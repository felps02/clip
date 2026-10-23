"""FFmpeg runs that fail: counted per recording file, and only while they reach no new data.

A failed run in OBS mode waits for the recording to grow and reads it again from the start.
When FFmpeg fails at the same spot every time (damaged data, full disk), re-creating the
segment held from the previous run must not count as progress, or the recording would be
re-read from the beginning forever. A run that does reach new data resets the count, whether
it failed further on or ended cleanly (FFmpeg exits 0 when the followed file stops growing)."""

from __future__ import annotations

import asyncio
import contextlib
import sys
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

import pytest

import clipforge_companion.app as app_module
from clipforge_companion.app import EXIT_ERROR, MAX_FAILED_RUNS, CompanionApp, LiveSession, Outcome
from clipforge_companion.segmenter import Segmenter
from clipforge_companion.uploader import SessionStore
from tests.fake_api import BASE_URL, FakeLiveApi
from tests.test_app import Console, make_app, obs_config

MKV_MAGIC = bytes([0x1A, 0x45, 0xDF, 0xA3])

# Stand-in for FFmpeg: lists the given segments (start-end pairs), writes their files and exits
# with the given code: 1 like FFmpeg giving up on the recording at the end of the last one, 0
# like FFmpeg 9 when the followed file stops growing (it logs a demuxing I/O error, exits 0).
SCRIPTED_FFMPEG = r"""
import sys
from pathlib import Path
list_path, code = Path(sys.argv[1]), int(sys.argv[2])
for number, bounds in enumerate(sys.argv[3:]):
    start, end = bounds.split("-")
    name = f"seg_{number:05d}.mkv"
    (list_path.parent / name).write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 64)
    with open(list_path, "a", encoding="utf-8") as handle:
        handle.write(f"{name},{float(start):.6f},{float(end):.6f}\n")
if code:
    sys.stderr.write("Invalid data found when processing input\n")
else:
    sys.stderr.write("Error during demuxing: I/O error\n")
sys.exit(code)
"""

Plan = Callable[[int], list[str]]


def scripted_segmenter(
    runs: list[Segmenter], plan: Plan, exit_code: Callable[[int], int]
) -> type[Segmenter]:
    """A Segmenter whose n-th run (1-based) lists ``plan(n)`` and exits with ``exit_code(n)``."""

    class Scripted(Segmenter):
        def __init__(self, **kwargs: object) -> None:
            super().__init__(**kwargs)  # type: ignore[arg-type]
            runs.append(self)
            self.bounds = plan(len(runs))
            self.exit_code = exit_code(len(runs))

        @property
        def command(self) -> list[str]:
            return [
                sys.executable, "-c", SCRIPTED_FFMPEG, str(self.list_path), str(self.exit_code),
                *self.bounds,
            ]  # fmt: skip

    return Scripted


async def keep_growing(path: Path) -> None:
    """OBS keeps writing the recording: every run is followed by a new attempt."""
    while True:
        await asyncio.sleep(0.2)
        with path.open("ab") as handle:
            handle.write(b"\0" * 1024)


@dataclass
class Captured:
    outcome: Outcome
    runs: list[Segmenter]
    app: CompanionApp
    session: LiveSession
    store: SessionStore
    output: str


async def capture_with(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    plan: Plan,
    *,
    exit_code: Callable[[int], int] = lambda n: 1,
    stop_after: int | None = None,
) -> Captured:
    """Captures a recording OBS keeps writing with the scripted FFmpeg runs; OBS stops
    recording during run ``stop_after`` (that run is the last one)."""
    runs: list[Segmenter] = []
    segmenter_class = scripted_segmenter(runs, plan, exit_code)
    console = Console()
    config = obs_config("ffmpeg-stand-in", tmp_path / "work", "ws://127.0.0.1:1")
    app = make_app(config, FakeLiveApi(), console)
    if stop_after is not None:

        class Stopping(segmenter_class):  # type: ignore[misc, valid-type]
            def __init__(self, **kwargs: object) -> None:
                super().__init__(**kwargs)
                if len(runs) >= stop_after:
                    app.obs_recording = False  # OBS stops: this run is the last one

        monkeypatch.setattr(app_module, "Segmenter", Stopping)
    else:
        monkeypatch.setattr(app_module, "Segmenter", segmenter_class)
    app.obs_connected = app.obs_synced = app.obs_recording = True
    recording = tmp_path / "obs" / "2026-10-23 20-00-00.mkv"
    recording.parent.mkdir()
    recording.write_bytes(MKV_MAGIC + b"\0" * 4096)
    store = SessionStore.create(
        config.work_dir, "s1", mode="obs", api_url=BASE_URL, device_id=None, segment_seconds=5
    )
    session = LiveSession(app, store)
    grower = asyncio.create_task(keep_growing(recording))
    try:
        outcome = await app._capture(session, recording, obs_mode=True)
    finally:
        grower.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await grower
        await app.api.aclose()
    return Captured(outcome, runs, app, session, store, console.getvalue())


@pytest.mark.async_timeout(90)
async def test_ffmpeg_failing_at_the_same_spot_stops_after_the_cap(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Every run gets through 0-7.5 s and fails: run 1 makes progress (segment 1 is held for
    # the next run), the later ones only re-create segment 1 (sequence 0 is skipped).
    result = await capture_with(tmp_path, monkeypatch, lambda n: ["0-5", "5-7.5"])
    output = result.output
    assert result.outcome is Outcome.REFUSED, output
    assert len(result.runs) == 1 + MAX_FAILED_RUNS, output
    assert f"O FFmpeg falhou {MAX_FAILED_RUNS} vezes ao ler" in output
    assert "sem conseguir avançar na gravação" in output
    assert result.app.exit_code == EXIT_ERROR
    # What was captured is kept, the newest copy of the held segment flagged final.
    store = result.store
    assert [(item.sequence, item.final) for item in store.items] == [(0, False), (1, True)]
    assert all(store.path_of(item).is_file() for item in store.items)


@pytest.mark.async_timeout(90)
async def test_failing_runs_that_still_advance_do_not_stop_the_capture(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Run n gets 5 s further than run n-1 before failing: more failures than the cap, but
    # never two without new data. OBS stops recording during the 5th run.
    runs_wanted = MAX_FAILED_RUNS + 2
    result = await capture_with(
        tmp_path,
        monkeypatch,
        lambda n: [f"{5 * i}-{5 * (i + 1)}" for i in range(n + 1)],
        stop_after=runs_wanted,
    )
    output = result.output
    assert result.outcome is Outcome.ENDED, output
    assert len(result.runs) == runs_wanted
    assert "vezes ao ler" not in output and result.app.exit_code != EXIT_ERROR
    sequences = [item.sequence for item in result.store.items]
    assert sequences == list(range(runs_wanted + 1))
    assert [item.final for item in result.store.items] == [False] * runs_wanted + [True]


@pytest.mark.async_timeout(90)
async def test_clean_runs_that_reach_new_data_reset_the_failure_count(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A long live with a few FFmpeg failures far apart: each failing run gets nowhere, but
    the clean run after it (FFmpeg exits 0 at a pause) reaches new data. The failures add up
    to the cap over the live, never in a row: the capture must go on, and the error reported
    for a failure is cleared once a run gets further."""
    runs_wanted = 2 * MAX_FAILED_RUNS + 1

    def plan(n: int) -> list[str]:
        # Runs 2k-1 (clean) and 2k (failing) list k whole segments and stop 2.5 s into the
        # next one: 0-7.5 s, 0-7.5 s, 0-12.5 s, 0-12.5 s, 0-17.5 s...
        k = (n + 1) // 2
        return [f"{5 * i}-{5 * (i + 1)}" for i in range(k)] + [f"{5 * k}-{5 * k + 2.5}"]

    result = await capture_with(
        tmp_path,
        monkeypatch,
        plan,
        exit_code=lambda n: 1 if n % 2 == 0 else 0,
        stop_after=runs_wanted,
    )
    output = result.output
    assert result.outcome is Outcome.ENDED, output
    assert [run.returncode for run in result.runs] == [0, 1] * MAX_FAILED_RUNS + [0]
    assert output.count("O FFmpeg terminou com código 1") == MAX_FAILED_RUNS
    assert "vezes ao ler" not in output and result.app.exit_code != EXIT_ERROR, output
    # Every segment once, the last one final: nothing was lost to the failures.
    store = result.store
    assert [item.sequence for item in store.items] == list(range(MAX_FAILED_RUNS + 2))
    assert [item.final for item in store.items] == [False] * (MAX_FAILED_RUNS + 1) + [True]
    # The last run went further: the heartbeat no longer reports the last failure.
    assert result.session.error is None
    assert result.app.companion_state(result.session)["lastError"] is None
