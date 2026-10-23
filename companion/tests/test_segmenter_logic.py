"""Segment emission rules, with a scripted stand-in for FFmpeg (no media needed).

The child process writes CSV lines with controlled timing, like the segment muxer does.
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

from clipforge_companion.segmenter import Segment, Segmenter

FAKE_FFMPEG = r"""
import sys, time
path, mode = sys.argv[1], sys.argv[2]

def write(line):
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(line + "\n")

if mode == "burst":
    write("seg_00000.mkv,0.000000,5.000000")
    write("seg_00001.mkv,5.000000,10.000000")
    time.sleep(1.5)
    write("seg_00002.mkv,10.000000,12.500000")
    time.sleep(0.05)
elif mode == "stop":
    write("seg_00000.mkv,0.000000,5.000000")
    key = sys.stdin.buffer.read(1)
    write("seg_00001.mkv,5.000000,7.500000" if key == b"q" else "seg_00001.mkv,5.0,5.5")
"""


class ScriptedSegmenter(Segmenter):
    def __init__(self, mode: str, out_dir: Path, **kwargs: object) -> None:
        super().__init__(
            ffmpeg=sys.executable,
            recording=out_dir / "unused.mkv",
            out_dir=out_dir,
            segment_seconds=5,
            poll_interval=0.05,
            **kwargs,  # type: ignore[arg-type]
        )
        self.mode = mode

    @property
    def command(self) -> list[str]:
        return [sys.executable, "-c", FAKE_FFMPEG, str(self.list_path), self.mode]


async def test_only_the_newest_entry_waits_and_the_final_one_is_last(tmp_path: Path) -> None:
    segmenter = ScriptedSegmenter("burst", tmp_path, settle_seconds=0.6)
    await segmenter.start()
    started = time.monotonic()
    timeline: list[tuple[float, Segment]] = []
    try:
        async for segment in segmenter.segments():
            timeline.append((time.monotonic() - started, segment))
    finally:
        await segmenter.close()
    assert [(s.sequence, s.start, s.end, s.last) for _, s in timeline] == [
        (0, 0.0, 5.0, False),
        (1, 5.0, 10.0, False),
        (2, 10.0, 12.5, True),
    ]
    first, second, third = (t for t, _ in timeline)
    # Segment 0 is followed by segment 1 in the same burst: emitted without the settle wait.
    assert first < second
    # Segment 1 is only known not to be last once the child keeps running after it.
    assert second >= 0.6
    # Segment 2 is emitted when the child exits, flagged as the last one.
    assert third >= 1.5
    assert segmenter.returncode == 0 and segmenter.error is None


async def test_graceful_stop_sends_q_and_flags_the_closing_segment(tmp_path: Path) -> None:
    segmenter = ScriptedSegmenter("stop", tmp_path, settle_seconds=0.2, stop_timeout=10)
    await segmenter.start()
    segments: list[Segment] = []
    try:
        async for segment in segmenter.segments():
            segments.append(segment)
            if segment.sequence == 0:
                segmenter.request_stop()
    finally:
        await segmenter.close()
    assert [(s.sequence, s.end, s.last) for s in segments] == [(0, 5.0, False), (1, 7.5, True)]
    assert segmenter.stop_requested and segmenter.error is None


async def test_watchdog_terminates_a_process_that_ignores_q(tmp_path: Path) -> None:
    class Stubborn(ScriptedSegmenter):
        @property
        def command(self) -> list[str]:
            return [sys.executable, "-c", "import time\nwhile True: time.sleep(1)"]

    segmenter = Stubborn("none", tmp_path, settle_seconds=0.1, stop_timeout=0.5)
    await segmenter.start()
    segmenter.request_stop()
    started = time.monotonic()
    segments = [segment async for segment in segmenter.segments()]
    await segmenter.close()
    assert segments == []
    assert time.monotonic() - started < 8
    assert segmenter.returncode not in (0, None)
    assert segmenter.error is None  # stopping was requested: not reported as an FFmpeg failure
