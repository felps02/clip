"""OBS mode with more than one recording file (automatic file splitting) and reconnects."""

from __future__ import annotations

import asyncio
import io
import os
import sys
import time
from pathlib import Path
from types import SimpleNamespace
from typing import Any

import pytest

from clipforge_companion.app import EXIT_OK, CompanionApp, recent_recordings
from clipforge_companion.config import Config
from clipforge_companion.logsetup import Redactor
from clipforge_companion.status import StatusReporter
from tests.fake_api import BASE_URL, TOKEN, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.media import media_duration, run_ffmpeg
from tests.test_app import Console, assert_session_upload, make_app, obs_config, wait_until


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(90)
async def test_record_file_changed_continues_the_same_session(
    ffmpeg: str, ffprobe: str, fixture_video: Path, tmp_path: Path
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    first = obs_dir / "2026-10-23 20-00-00.mkv"
    second = obs_dir / "2026-10-23 20-00-10.mkv"
    run_ffmpeg(
        ffmpeg, "-i", str(fixture_video), "-t", "10", "-c", "copy", "-f", "matroska", str(first)
    )
    run_ffmpeg(
        ffmpeg, "-ss", "10", "-i", str(fixture_video), "-t", "10", "-c", "copy", "-f", "matroska",
        str(second),
    )  # fmt: skip
    total = media_duration(ffprobe, first) + media_duration(ffprobe, second)
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        app = make_app(obs_config(ffmpeg, tmp_path / "work", server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(first))
        await wait_until(lambda: console.has("Capturando"), what="capture start")
        # OBS splits the recording: same recording, new file (obs-websocket 5.5+ event).
        await server.emit("RecordFileChanged", {"newOutputPath": str(second)})
        await wait_until(
            lambda: console.has(f"Continuando a captura em {second.name}"), what="switch"
        )
        await wait_until(lambda: console.has("parou de crescer"), what="second file done")
        await server.stop_recording()
        await wait_until(lambda: fake.ends, what="session end")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    [session_id] = fake.sessions
    assert len(fake.created) == 1  # one session for the whole recording
    accepted = assert_session_upload(fake, session_id, total=total)
    assert len(accepted) >= 3


class StubObs:
    def __init__(self, directory: Path, *, connect_count: int = 1, recording: bool = True) -> None:
        self.directory = directory
        self.connect_count = connect_count
        self.recording = recording

    async def get_record_directory(self) -> str:
        return str(self.directory)

    async def get_version(self) -> dict[str, Any]:
        return {"obsVersion": "31.0.2", "obsWebSocketVersion": "5.5.4"}

    async def get_record_status(self) -> dict[str, Any]:
        return {"outputActive": self.recording, "outputPaused": False}

    async def get_stream_status(self) -> dict[str, Any]:
        return {"outputActive": False}


def stub_app(tmp_path: Path) -> CompanionApp:
    config = Config(api_url=BASE_URL, token=TOKEN, work_dir=tmp_path / "work")
    return CompanionApp(
        config,
        api=FakeLiveApi().client(),
        status=StatusReporter(io.StringIO(), redactor=Redactor([TOKEN])),
        install_signal_handlers=False,
        split_growth_seconds=0.4,
    )


async def grow(*paths: Path, times: int = 10) -> None:
    for _ in range(times):
        await asyncio.sleep(0.1)
        for path in paths:
            with path.open("ab") as handle:
                handle.write(b"\0" * 4096)


async def test_split_detection_needs_a_newer_file_that_grows(tmp_path: Path) -> None:
    current = tmp_path / "2026-10-23 20-00-00.mkv"
    current.write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 1000)
    replay = tmp_path / "Replay 2026-10-23 20-05-00.mkv"  # replay buffer save: never grows
    replay.write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 5000)
    app = stub_app(tmp_path)
    app.obs = StubObs(tmp_path)  # type: ignore[assignment]
    session = SimpleNamespace(captured_paths=[str(current)])
    try:
        assert await app._find_next_recording(session, current) is None  # type: ignore[arg-type]

        continuation = tmp_path / "2026-10-23 20-05-01.mkv"
        continuation.write_bytes(b"\x1a\x45\xdf\xa3")
        # OBS stopped writing the current file when it created this one.
        created = continuation.stat().st_mtime
        os.utime(current, (created, created))
        grower = asyncio.create_task(grow(continuation))
        found = await app._find_next_recording(session, current)  # type: ignore[arg-type]
        await grower
        assert found == (continuation, True)  # the next part of the same recording
    finally:
        await app.api.aclose()


@pytest.mark.skipif(
    not hasattr(os.stat_result, "st_birthtime") and sys.platform != "win32",
    reason="the file system does not report creation times here",
)
async def test_split_detection_ignores_growing_files_that_cannot_be_the_next_part(
    tmp_path: Path,
) -> None:
    current = tmp_path / "2026-10-23 20-00-00.mkv"
    current.write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 1000)
    last_write = time.time() - 40  # paused 40 s ago
    os.utime(current, (last_write, last_write))
    candidates = [
        tmp_path / "Replay 2026-10-23 20-05-00.mkv",  # Replay Buffer save being written
        tmp_path / "2026-10-23 20-05-02.mp4",  # automatic remux / Game Bar: other container
        tmp_path / "2026-10-23 20-05-03.mkv",  # created long after the recording paused
    ]
    for path in candidates:
        path.write_bytes(b"\x1a\x45\xdf\xa3")
    app = stub_app(tmp_path)
    app.obs = StubObs(tmp_path)  # type: ignore[assignment]
    session = SimpleNamespace(captured_paths=[str(current)])
    try:
        grower = asyncio.create_task(grow(*candidates))
        found = await app._find_next_recording(session, current)  # type: ignore[arg-type]
        await grower
    finally:
        await app.api.aclose()
    assert found is None


async def test_split_files_are_only_searched_when_obs_may_not_report_them(
    tmp_path: Path,
) -> None:
    app = stub_app(tmp_path)
    try:
        app.obs = StubObs(tmp_path, connect_count=1)  # type: ignore[assignment]
        app.obs_version = {"obsWebSocketVersion": "5.5.4"}  # sends RecordFileChanged
        assert not app._split_scan_needed(connect_count=1)
        assert app._split_scan_needed(connect_count=0)  # reconnected since: event maybe missed
        app.obs_version = {"obsWebSocketVersion": "5.4.2"}
        assert app._split_scan_needed(connect_count=1)
        app.obs_version = {}
        assert app._split_scan_needed(connect_count=1)
    finally:
        await app.api.aclose()


async def test_reconnect_forgets_a_possibly_stale_recording_path(tmp_path: Path) -> None:
    app = stub_app(tmp_path)
    try:
        app.obs = StubObs(tmp_path, connect_count=1)  # type: ignore[assignment]
        app.record_path = "C:/OBS/a.mkv"
        await app._sync_obs()
        assert app.record_path == "C:/OBS/a.mkv"  # first connection: keep the event's path
        app.obs = StubObs(tmp_path, connect_count=2)  # type: ignore[assignment]
        await app._sync_obs()
        assert app.record_path is None  # reconnect: events may have been missed
        assert app.obs_recording and app.obs_synced
    finally:
        await app.api.aclose()


def test_recent_recordings_order_and_filters(tmp_path: Path) -> None:
    (tmp_path / "sub").mkdir()
    files = {
        "a.mkv": time.time() - 10,
        "sub/b.mp4": time.time() - 5,
        "c.txt": time.time(),
        "old.mkv": time.time() - 600,
    }
    for name, mtime in files.items():
        path = tmp_path / name
        path.write_bytes(b"x")
        os.utime(path, (mtime, mtime))
    found = recent_recordings(tmp_path)
    assert [p.name for p in found] == ["b.mp4", "a.mkv"]
    assert recent_recordings(tmp_path, exclude={str(tmp_path / "sub" / "b.mp4")}) == [
        tmp_path / "a.mkv"
    ]
