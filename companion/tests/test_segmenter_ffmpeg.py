"""The segmenter with the real FFmpeg binary."""

from __future__ import annotations

import asyncio
import itertools
import time
from pathlib import Path

import pytest

from clipforge_companion.segmenter import (
    SEGMENT_LIST_NAME,
    Segment,
    Segmenter,
    SegmenterError,
    build_command,
)
from tests.media import media_duration, video_packets

pytestmark = pytest.mark.ffmpeg


async def collect(segmenter: Segmenter) -> list[Segment]:
    await segmenter.start()
    try:
        return [segment async for segment in segmenter.segments()]
    finally:
        await segmenter.close()


def assert_contiguous(segments: list[Segment], first_sequence: int, first_start: float) -> None:
    assert [s.sequence for s in segments] == list(
        range(first_sequence, first_sequence + len(segments))
    )
    assert segments[0].start == pytest.approx(first_start, abs=0.05)
    for previous, current in itertools.pairwise(segments):
        assert current.start == pytest.approx(previous.end, abs=0.05), (previous, current)
        assert current.duration > 0
    assert [s.last for s in segments] == [False] * (len(segments) - 1) + [True]


def test_command_uses_argument_list_and_escapes_percent(tmp_path: Path) -> None:
    out_dir = tmp_path / "100%" / "run-01"
    command = build_command("ffmpeg", Path("D:/OBS/live 1.mkv"), out_dir, 60, 15)
    assert command[:2] == ["ffmpeg", "-hide_banner"]
    assert "-nostdin" not in command  # stdin is the private pipe used for "q"
    assert command[command.index("-follow") + 1] == "1"
    assert command[command.index("-rw_timeout") + 1] == "15000000"
    assert command[command.index("-i") + 1] == "file:" + str(Path("D:/OBS/live 1.mkv"))
    assert command[command.index("-segment_time") + 1] == "60"
    assert command[command.index("-segment_list") + 1] == str(out_dir / SEGMENT_LIST_NAME)
    assert command[-1].endswith("seg_%05d.mkv")
    assert "100%%" in command[-1]
    first_map = command.index("-map")
    assert command[first_map : first_map + 6] == ["-map", "0:v:0", "-map", "0:a:0?", "-c", "copy"]


@pytest.mark.async_timeout(120)
async def test_segments_are_emitted_while_the_recording_grows(
    ffmpeg: str, ffprobe: str, fixture_video: Path, tmp_path: Path
) -> None:
    growing = tmp_path / "growing.mkv"
    writer = await asyncio.create_subprocess_exec(
        ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-re", "-i", str(fixture_video),
        "-t", "30", "-c", "copy", "-f", "matroska", str(growing),
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.DEVNULL,
        stderr=asyncio.subprocess.DEVNULL,
    )  # fmt: skip
    emitted: list[tuple[Segment, bool]] = []
    try:
        deadline = time.monotonic() + 30
        while not (growing.exists() and growing.stat().st_size > 0):
            assert time.monotonic() < deadline, "writer did not start"
            await asyncio.sleep(0.05)
        segmenter = Segmenter(
            ffmpeg=ffmpeg,
            recording=growing,
            out_dir=tmp_path / "out",
            segment_seconds=5,
            rw_timeout=8,  # > the writer's cluster interval (~5 s): no frames lost
            settle_seconds=1.0,
            poll_interval=0.2,
        )
        await segmenter.start()
        try:
            async for segment in segmenter.segments():
                emitted.append((segment, writer.returncode is None))
                if len(emitted) == 2:
                    # Graceful stop while data still flows: "q" closes the current segment.
                    segmenter.request_stop()
        finally:
            await segmenter.close()
    finally:
        if writer.returncode is None:
            writer.terminate()
        await writer.wait()

    segments = [segment for segment, _ in emitted]
    assert len(segments) >= 3
    # Incremental: the first segment arrived while the writer was still recording.
    assert emitted[0][1] is True
    assert emitted[1][1] is True
    assert_contiguous(segments, first_sequence=0, first_start=0.0)
    assert segmenter.returncode == 0
    assert segmenter.error is None
    assert segmenter.stop_requested

    # Every segment is a valid file whose duration matches the list.
    for segment in segments:
        assert segment.path.is_file() and segment.path.stat().st_size > 0
        assert media_duration(ffprobe, segment.path) == pytest.approx(segment.duration, abs=0.25)

    # No frame was lost while following the growing file: the frames of the complete segments
    # are exactly the source frames before the keyframe where the last segment starts.
    source = video_packets(ffprobe, growing)
    counts = [len(video_packets(ffprobe, s.path)) for s in segments]
    before_last = sum(counts[:-1])
    pts, is_key = source[before_last]
    assert is_key, (
        f"segments hold {before_last} frames but source frame {before_last} is no keyframe"
    )
    assert pts == pytest.approx(segments[-1].start, abs=0.2)


@pytest.mark.async_timeout(60)
async def test_finished_recording_ends_naturally_with_all_frames(
    ffmpeg: str, ffprobe: str, finished_mkv: Path, tmp_path: Path
) -> None:
    segmenter = Segmenter(
        ffmpeg=ffmpeg,
        recording=finished_mkv,
        out_dir=tmp_path / "out",
        segment_seconds=5,
        rw_timeout=1,
        settle_seconds=0.5,
        poll_interval=0.1,
    )
    segments = await collect(segmenter)
    assert_contiguous(segments, first_sequence=0, first_start=0.0)
    assert [round(s.start, 1) for s in segments] == [0.0, 8.4, 16.7]
    assert segments[-1].end == pytest.approx(media_duration(ffprobe, finished_mkv), abs=0.2)
    assert segmenter.returncode == 0 and segmenter.error is None
    assert sum(len(video_packets(ffprobe, s.path)) for s in segments) == len(
        video_packets(ffprobe, finished_mkv)
    )


@pytest.mark.async_timeout(60)
async def test_offsets_and_skip_map_to_session_sequences(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    out_dir = tmp_path / "out"
    segmenter = Segmenter(
        ffmpeg=ffmpeg,
        recording=finished_mkv,
        out_dir=out_dir,
        segment_seconds=5,
        rw_timeout=1,
        sequence_offset=10,
        time_offset=100.0,
        skip_through=10,  # sequence 10 was handled by an earlier run
        settle_seconds=0.5,
        poll_interval=0.1,
    )
    segments = await collect(segmenter)
    assert [s.sequence for s in segments] == [11, 12]
    assert segments[0].start == pytest.approx(108.4, abs=0.05)
    assert segments[-1].last
    assert not (out_dir / "seg_00000.mkv").exists()  # skipped segment deleted
    assert segmenter.skipped == 1


async def test_missing_ffmpeg_raises_clear_error(tmp_path: Path) -> None:
    segmenter = Segmenter(
        ffmpeg=str(tmp_path / "no-ffmpeg.exe"),
        recording=tmp_path / "rec.mkv",
        out_dir=tmp_path / "out",
        segment_seconds=60,
    )
    with pytest.raises(SegmenterError, match="FFmpeg não encontrado"):
        await segmenter.start()


@pytest.mark.async_timeout(60)
async def test_ffmpeg_failure_is_reported(ffmpeg: str, tmp_path: Path) -> None:
    bogus = tmp_path / "bogus.mkv"
    bogus.write_bytes(b"this is not a video" * 100)
    segmenter = Segmenter(
        ffmpeg=ffmpeg,
        recording=bogus,
        out_dir=tmp_path / "out",
        segment_seconds=5,
        rw_timeout=1,
        settle_seconds=0.2,
        poll_interval=0.1,
    )
    segments = await collect(segmenter)
    assert segments == []
    assert segmenter.returncode not in (0, None)
    assert segmenter.error is not None and "FFmpeg terminou" in segmenter.error
