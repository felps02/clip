"""OBS mode with a recording that cannot be captured, reported with a Qt-style path.

OBS on Windows reports ``outputPath`` with forward slashes (``C:/Users/x/Videos/a.mp4``) while
``Path`` gives backslashes: a refused recording must still be recognised as handled, otherwise
the OBS loop spins without ever suspending and starves the event loop (OBS events, heartbeats
and Ctrl+C included).
"""

from __future__ import annotations

import asyncio
import struct
import time
from pathlib import Path

import pytest

from clipforge_companion.app import EXIT_OK, CompanionApp, Outcome
from clipforge_companion.obs import OUTPUT_STARTED, OUTPUT_STOPPED
from tests.fake_api import FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.test_app import Console, make_app, obs_config, wait_until


def plain_mp4(path: Path) -> Path:
    """A plain MP4 still being written: ftyp + open-ended mdat, no moov yet."""
    ftyp = struct.pack(">I4s", 16, b"ftyp") + b"isom\x00\x00\x02\x00"
    path.write_bytes(
        ftyp + struct.pack(">I4s", 8, b"free") + struct.pack(">I4s", 0, b"mdat") + b"\0" * 512
    )
    return path


class LoopLag:
    """Measures how late a 50 ms ticker wakes up (a starved event loop shows up here)."""

    def __init__(self) -> None:
        self.worst = 0.0
        self._task: asyncio.Task[None] | None = None

    async def _tick(self) -> None:
        while True:
            started = time.monotonic()
            await asyncio.sleep(0.05)
            self.worst = max(self.worst, time.monotonic() - started - 0.05)

    def __enter__(self) -> LoopLag:
        self._task = asyncio.create_task(self._tick())
        return self

    def __exit__(self, *exc: object) -> None:
        if self._task is not None:
            self._task.cancel()


def count_sessions(app: CompanionApp, limit: int = 5) -> list[Outcome]:
    """Records every OBS activity round; fails loudly (instead of hanging) if the OBS loop
    starts spinning."""
    outcomes: list[Outcome] = []
    original = app._obs_activity_session

    async def counted() -> Outcome:
        if len(outcomes) >= limit:
            raise AssertionError(f"the OBS loop spins: {len(outcomes)} rounds")
        outcome = await original()
        outcomes.append(outcome)
        return outcome

    app._obs_activity_session = counted  # type: ignore[method-assign]
    return outcomes


@pytest.mark.async_timeout(60)
@pytest.mark.parametrize("kind", ["plain-mp4", "not-on-this-machine"])
async def test_refused_recording_with_a_qt_style_path_does_not_spin(
    tmp_path: Path, kind: str
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    if kind == "plain-mp4":
        recording = plain_mp4(obs_dir / "2026-10-23 22-00-00.mp4")
        refusal = "MP4 comum"
    else:  # e.g. OBS on another computer, recording to a disk this one cannot see
        recording = obs_dir / "outro-pc" / "2026-10-23 22-00-00.mkv"
        refusal = "não apareceu no disco"
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        config = obs_config("ffmpeg-not-needed", tmp_path / "work", server.url)
        app = make_app(config, fake, console, record_wait_timeout=0.5)
        rounds = count_sessions(app)
        with LoopLag() as lag:
            task = asyncio.create_task(app.run())
            await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
            await server.start_recording(recording.as_posix())  # as OBS reports it on Windows
            await wait_until(lambda: console.has(refusal), what="refusal")
            await asyncio.sleep(2.5)  # a few idle cycles of the OBS loop
            assert rounds == [Outcome.REFUSED]
            assert console.getvalue().count("Aguardando a próxima gravação") <= 1
            assert app._obs_activity() is False  # handled, although the separators differ
            app.request_stop()  # Ctrl+C is honoured right away
            code = await asyncio.wait_for(task, 10)
            await app.api.aclose()
        assert lag.worst < 1.0, f"event loop starved for {lag.worst:.2f} s"
    assert code == EXIT_OK, console.getvalue()
    assert fake.sessions == {}


@pytest.mark.async_timeout(60)
async def test_streaming_with_a_refused_recording_reports_it_and_ends_with_the_stream(
    tmp_path: Path,
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = plain_mp4(obs_dir / "2026-10-23 22-30-00.mp4")
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        server.streaming = True
        config = obs_config("ffmpeg-not-needed", tmp_path / "work", server.url)
        app = make_app(config, fake, console)
        rounds = count_sessions(app)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: len(fake.sessions) == 1, what="session for the stream")
        [session_id] = fake.sessions
        await server.start_recording(recording.as_posix())
        await wait_until(lambda: console.has("MP4 comum"), what="refusal")
        # The Lives page learns why nothing arrives although OBS records.
        await wait_until(
            lambda: any(
                "MP4 comum" in (h["state"]["lastError"] or "")
                for h in fake.heartbeats
                if h["state"]["recording"]
            ),
            what="lastError in a heartbeat",
        )
        server.streaming = False
        await server.emit(
            "StreamStateChanged", {"outputActive": False, "outputState": OUTPUT_STOPPED}
        )
        # The stream is over: the session ends although OBS still writes the refused file.
        await wait_until(lambda: fake.ends, what="session end")
        assert app.obs_recording
        await asyncio.sleep(1.5)
        assert rounds == [Outcome.ENDED]
        app.request_stop()
        code = await asyncio.wait_for(task, 10)
        await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    assert fake.ends == [{"session": session_id}]  # nothing was uploaded: no lastSequence
    assert fake.accepted() == []


async def test_record_paths_from_events_are_compared_like_the_file_system(
    tmp_path: Path,
) -> None:
    """Unit level: the handled sets ignore separator (and, on Windows, case) differences."""
    fake = FakeLiveApi()
    app = make_app(obs_config("ffmpeg", tmp_path / "work", "ws://127.0.0.1:1"), fake, Console())
    try:
        app.obs_connected = app.obs_synced = True
        recording = tmp_path / "Videos" / "2026-10-23 22-00-00.mp4"
        app.on_obs_event(
            "RecordStateChanged",
            {
                "outputActive": True,
                "outputState": OUTPUT_STARTED,
                "outputPath": recording.as_posix(),
            },
        )
        assert app._obs_activity()
        app._mark_unusable(str(recording))  # stored the way Path() spells it
        assert not app._obs_activity()
        assert app._is_handled(recording.as_posix())
    finally:
        await app.api.aclose()


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(90)
async def test_a_recording_ffmpeg_cannot_read_does_not_spin_either(
    ffmpeg: str, tmp_path: Path
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 23-00-00.mkv"
    recording.write_bytes(b"\x1a\x45\xdf\xa3" + b"not really a matroska file" * 200)
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        app = make_app(obs_config(ffmpeg, tmp_path / "work", server.url), fake, console)
        rounds = count_sessions(app)
        with LoopLag() as lag:
            task = asyncio.create_task(app.run())
            await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
            await server.start_recording(recording.as_posix())
            await wait_until(lambda: fake.ends, what="session ended after the FFmpeg failure")
            await asyncio.sleep(2.5)
            assert rounds == [Outcome.ENDED]  # handled once, then idle
            app.request_stop()
            await asyncio.wait_for(task, 10)
            await app.api.aclose()
        assert lag.worst < 1.0, f"event loop starved for {lag.worst:.2f} s"
    assert console.has("O FFmpeg terminou com código")
    assert len(fake.sessions) == 1
