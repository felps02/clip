"""End-to-end orchestration: real FFmpeg, fake API (MockTransport) and fake obs-websocket."""

from __future__ import annotations

import asyncio
import io
import itertools
import shutil
import struct
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from clipforge_companion.app import EXIT_ABORTED, EXIT_OK, CompanionApp
from clipforge_companion.config import Config
from clipforge_companion.logsetup import Redactor
from clipforge_companion.obs import OUTPUT_STOPPED
from clipforge_companion.status import StatusReporter
from clipforge_companion.uploader import SessionStore
from tests.fake_api import BASE_URL, TOKEN, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.media import cluster_offsets

pytestmark = pytest.mark.ffmpeg


class Console(io.StringIO):
    def has(self, text: str, count: int = 1) -> bool:
        return self.getvalue().count(text) >= count


async def wait_until(
    predicate: Callable[[], object], timeout: float = 30.0, what: str = ""
) -> None:
    async def wait() -> None:
        while not predicate():
            await asyncio.sleep(0.05)

    try:
        await asyncio.wait_for(wait(), timeout)
    except TimeoutError:
        raise AssertionError(f"timed out waiting for {what or predicate}") from None


def make_app(config: Config, fake: FakeLiveApi, console: Console, **kwargs: Any) -> CompanionApp:
    options: dict[str, Any] = {
        "settle_seconds": 0.3,
        "poll_interval": 0.1,
        "upload_backoff": (0.05, 0.2),
        "api_backoff": (0.05, 0.2),
        "ending_wait": (0.1, 0.3),
        "leftover_grace_seconds": 3.0,
    }
    options.update(kwargs)
    return CompanionApp(
        config,
        api=fake.client(),
        status=StatusReporter(console, redactor=Redactor([TOKEN])),
        install_signal_handlers=False,
        **options,
    )


def file_config(ffmpeg: str, work_dir: Path, recording: Path) -> Config:
    return Config(
        api_url=BASE_URL,
        token=TOKEN,
        file=recording,
        segment_seconds=5,
        work_dir=work_dir,
        ffmpeg=ffmpeg,
        rw_timeout=1.0,
        heartbeat_interval=0.2,
    )


def obs_config(ffmpeg: str, work_dir: Path, url: str, *, auto_record: bool = False) -> Config:
    return Config(
        api_url=BASE_URL,
        token=TOKEN,
        obs_url=url,
        auto_record=auto_record,
        segment_seconds=5,
        work_dir=work_dir,
        ffmpeg=ffmpeg,
        rw_timeout=1.0,
        heartbeat_interval=0.2,
    )


def assert_session_upload(fake: FakeLiveApi, session_id: str, total: float) -> list[dict]:
    accepted = fake.accepted(session_id)
    sequences = [u["sequence"] for u in accepted]
    assert sequences == list(range(len(accepted))), sequences  # every sequence once, in order
    assert [u["final"] for u in accepted] == [False] * (len(accepted) - 1) + [True]
    assert accepted[0]["start"] == 0.0
    for previous, current in itertools.pairwise(accepted):
        assert current["start"] == pytest.approx(previous["start"] + previous["duration"], abs=0.05)
    assert sum(u["duration"] for u in accepted) == pytest.approx(total, abs=0.3)
    assert all(u["file"]["content"][:4] == bytes([0x1A, 0x45, 0xDF, 0xA3]) for u in accepted)
    assert fake.ends == [{"session": session_id, "lastSequence": len(accepted) - 1}]
    return accepted


@pytest.mark.async_timeout(60)
async def test_file_mode_uploads_every_segment_and_ends_the_session(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    fake, console = FakeLiveApi(), Console()
    config = file_config(ffmpeg, tmp_path / "work", finished_mkv)
    app = make_app(config, fake, console)
    code = await app.run()
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    [session_id] = fake.sessions
    assert fake.created == [{"segmentSeconds": 5}]
    accepted = assert_session_upload(fake, session_id, total=20.0)
    assert len(accepted) == 3
    states = [h["state"] for h in fake.heartbeats]
    assert any(s["recording"] for s in states) and not any(s["obsConnected"] for s in states)
    assert not (config.work_dir / session_id).exists()  # local state removed after the end
    assert "Segmento 2 enviado" in output and "último" in output
    assert TOKEN not in output


@pytest.mark.async_timeout(60)
async def test_ctrl_c_finishes_the_segment_sends_the_queue_and_ends(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    fake, console = FakeLiveApi(), Console()
    app = make_app(file_config(ffmpeg, tmp_path / "work", finished_mkv), fake, console)
    task = asyncio.create_task(app.run())
    await wait_until(lambda: fake.accepted(), what="first upload")
    app.request_stop()  # Ctrl+C once
    code = await task
    await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    assert console.has("Encerrando: finalizando o segmento atual")
    [session_id] = fake.sessions
    assert_session_upload(fake, session_id, total=20.0)


@pytest.mark.async_timeout(60)
async def test_stop_requested_by_the_website_ends_the_session(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    fake, console = FakeLiveApi(), Console()
    fake.stop_after_segments = 1
    app = make_app(file_config(ffmpeg, tmp_path / "work", finished_mkv), fake, console)
    code = await app.run()
    await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    assert console.has("Encerramento solicitado pelo site")
    [session_id] = fake.sessions
    accepted = fake.accepted(session_id)
    assert accepted[-1]["final"] is True
    assert fake.ends[0]["lastSequence"] == accepted[-1]["sequence"]


@pytest.mark.async_timeout(90)
async def test_second_ctrl_c_keeps_the_queue_and_next_run_resumes_the_session(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    fake, console = FakeLiveApi(), Console()
    fake.always_fail_segments = 503  # API down for uploads
    config = file_config(ffmpeg, work_dir, finished_mkv)
    app = make_app(config, fake, console)
    task = asyncio.create_task(app.run())
    await wait_until(
        lambda: app.session is not None and len(app.session.store.items) >= 2,
        what="queued segments",
    )
    app.request_stop()
    app.request_stop()  # Ctrl+C twice: leave now
    code = await task
    await app.api.aclose()
    assert code == EXIT_ABORTED
    assert fake.ends == []
    assert console.has("serão enviados na próxima execução")
    [store] = SessionStore.find_unfinished(work_dir)
    pending = [item.sequence for item in store.items]
    assert pending[:2] == [0, 1]

    # Next run: the API is back. The same session is resumed (no new session is created),
    # the persisted queue is sent first and the capture continues after the last sequence.
    fake.always_fail_segments = None
    console2 = Console()
    app2 = make_app(config, fake, console2)
    code2 = await app2.run()
    await app2.api.aclose()
    assert code2 == EXIT_OK, console2.getvalue()
    assert console2.has(f"Retomando a sessão {store.session_id[:8]}")
    assert len(fake.sessions) == 1 and len(fake.created) == 1
    assert_session_upload(fake, store.session_id, total=20.0)
    assert SessionStore.find_unfinished(work_dir) == []


@pytest.mark.async_timeout(120)
async def test_obs_mode_follows_recording_through_a_stall_until_obs_stops(
    ffmpeg: str, live_mkv: Path, tmp_path: Path
) -> None:
    data = live_mkv.read_bytes()
    cut = next(offset for offset in cluster_offsets(live_mkv) if offset > len(data) * 0.6)
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 20-00-00.mkv"
    recording.write_bytes(data[:cut])  # OBS has written the first ~12 s so far
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        app = make_app(obs_config(ffmpeg, tmp_path / "work", server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        assert fake.sessions == {}  # no session while OBS is idle
        await server.start_recording(str(recording))
        await wait_until(lambda: console.has("parou de crescer"), what="first stall")
        before_growth = [u["sequence"] for u in fake.accepted()]
        with recording.open("ab") as handle:  # OBS writes again (pause ended)
            handle.write(data[cut:])
        await wait_until(lambda: console.has("voltou a crescer"), what="growth")
        await wait_until(lambda: console.has("parou de crescer", 2), what="second stall")
        await server.stop_recording()
        await wait_until(lambda: fake.ends, what="session end")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    [session_id] = fake.sessions
    assert fake.created[0]["obs"] == {"version": "31.0.2", "recordingPath": str(recording)}
    accepted = assert_session_upload(fake, session_id, total=20.0)
    assert len(accepted) >= 3
    assert before_growth and before_growth == list(range(len(before_growth)))
    states = [h["state"] for h in fake.heartbeats]
    assert any(s["obsConnected"] and s["recording"] for s in states)
    assert "GetRecordStatus" in server.requests and "GetVersion" in server.requests


@pytest.mark.async_timeout(90)
async def test_streaming_without_recording_warns_and_auto_record_starts_it(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    obs_dir = tmp_path / "obs"
    (obs_dir / "sub").mkdir(parents=True)
    recording = obs_dir / "sub" / "2026-10-23 21-00-00.mkv"
    shutil.copyfile(finished_mkv, recording)
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        server.streaming = True
        server.next_record_path = None  # RecordStateChanged without outputPath: use the folder
        app = make_app(
            obs_config(ffmpeg, tmp_path / "work", server.url, auto_record=True), fake, console
        )
        task = asyncio.create_task(app.run())
        await wait_until(lambda: "StartRecord" in server.requests, what="StartRecord")
        await wait_until(lambda: console.has("parou de crescer"), what="capture finished")
        server.streaming = False
        await server.emit(
            "StreamStateChanged", {"outputActive": False, "outputState": OUTPUT_STOPPED}
        )
        await server.stop_recording()
        await wait_until(lambda: fake.ends, what="session end")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert server.requests.count("StartRecord") == 1
    assert "GetRecordDirectory" in server.requests
    assert "não está gravando" in output
    [session_id] = fake.sessions
    assert fake.created[0]["obs"] == {"version": "31.0.2"}  # created before the recording existed
    first_state = fake.heartbeats[0]["state"]
    assert first_state["streaming"] is True and first_state["recording"] is False
    assert "não está gravando" in (first_state["lastError"] or "")
    assert_session_upload(fake, session_id, total=20.0)


@pytest.mark.async_timeout(60)
async def test_plain_mp4_recording_is_refused_without_creating_a_session(tmp_path: Path) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 22-00-00.mp4"
    ftyp = struct.pack(">I4s", 16, b"ftyp") + b"isom\x00\x00\x02\x00"
    recording.write_bytes(
        ftyp + struct.pack(">I4s", 8, b"free") + struct.pack(">I4s", 0, b"mdat") + b"\0" * 512
    )
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        config = obs_config("ffmpeg-not-needed", tmp_path / "work", server.url)
        app = make_app(config, fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(recording))
        await wait_until(lambda: console.has("MP4 comum"), what="refusal")
        await server.stop_recording()
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    assert code == EXIT_OK
    assert fake.sessions == {}
    assert "Configurações > Saída > Gravação" in console.getvalue()
