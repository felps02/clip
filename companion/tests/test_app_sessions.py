"""Sessions across runs and instances: locks, leftovers, rejected answers, "ending" sessions."""

from __future__ import annotations

import asyncio
import itertools
import shutil
from pathlib import Path
from typing import Any

import pytest

from clipforge_companion.api import RejectedError
from clipforge_companion.app import (
    EXIT_ABORTED,
    EXIT_ERROR,
    EXIT_OK,
    SESSION_LOCK_FILE,
    LiveSession,
)
from clipforge_companion.config import Config
from clipforge_companion.locking import FileLock
from clipforge_companion.segmenter import Segment
from clipforge_companion.uploader import SessionStore
from tests.fake_api import BASE_URL, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.test_app import Console, file_config, make_app, obs_config, wait_until

MKV_MAGIC = bytes([0x1A, 0x45, 0xDF, 0xA3])


def assert_complete(fake: FakeLiveApi, session_id: str, total: float | None = None) -> list[dict]:
    """Every sequence once, in order, contiguous, the last one final, and ended with it."""
    accepted = fake.accepted(session_id)
    assert accepted, f"nothing uploaded for {session_id}"
    assert [u["sequence"] for u in accepted] == list(range(len(accepted)))
    assert [u["final"] for u in accepted] == [False] * (len(accepted) - 1) + [True]
    assert accepted[0]["start"] == 0.0
    for previous, current in itertools.pairwise(accepted):
        assert current["start"] == pytest.approx(previous["start"] + previous["duration"], abs=0.05)
    if total is not None:
        assert sum(u["duration"] for u in accepted) == pytest.approx(total, abs=0.3)
    assert {"session": session_id, "lastSequence": len(accepted) - 1} in fake.ends
    return accepted


async def leave_unfinished(config: Config, fake: FakeLiveApi, *, items: int = 2) -> SessionStore:
    """A first run during which the API refuses every upload (503) and the user presses
    Ctrl+C twice: the session stays behind with its queue on disk."""
    fake.always_fail_segments = 503
    app = make_app(config, fake, Console())
    task = asyncio.create_task(app.run())
    await wait_until(
        lambda: app.session is not None and len(app.session.store.items) >= items,
        what="queued segments",
    )
    app.request_stop()
    app.request_stop()
    assert await task == EXIT_ABORTED
    await app.api.aclose()
    fake.always_fail_segments = None
    [store] = SessionStore.find_unfinished(config.work_dir)
    return store


# -- one companion per device and work dir (a second instance used to end the live) -----------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(90)
async def test_a_second_companion_leaves_the_running_one_alone(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    fake.always_fail_segments = 503  # the first companion still has its queue to send
    console_a, console_b = Console(), Console()
    app_a = make_app(file_config(ffmpeg, work_dir, finished_mkv), fake, console_a)
    task_a = asyncio.create_task(app_a.run())
    await wait_until(
        lambda: app_a.session is not None and app_a.session.store.items, what="first capture"
    )
    other = tmp_path / "other.mkv"
    shutil.copyfile(finished_mkv, other)
    app_b = make_app(file_config(ffmpeg, work_dir, other), fake, console_b)
    code_b = await asyncio.wait_for(app_b.run(), 30)
    await app_b.api.aclose()
    assert code_b == EXIT_ERROR
    assert console_b.has("Outro companion já está rodando")
    assert fake.ends == [] and len(fake.created) == 1  # nothing ended, nothing created

    fake.always_fail_segments = None
    code_a = await asyncio.wait_for(task_a, 60)
    await app_a.api.aclose()
    assert code_a == EXIT_OK, console_a.getvalue()
    [session_id] = fake.sessions
    assert_complete(fake, session_id, total=20.0)


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(90)
async def test_a_session_locked_by_another_companion_is_not_taken_for_a_leftover(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    store = await leave_unfinished(file_config(ffmpeg, work_dir, finished_mkv), fake)
    pending = [item.sequence for item in store.items]
    holder = FileLock(store.directory / SESSION_LOCK_FILE)  # someone else works on it
    assert holder.acquire()
    try:
        other = tmp_path / "other.mkv"
        shutil.copyfile(finished_mkv, other)
        console = Console()
        app = make_app(file_config(ffmpeg, work_dir, other), fake, console)
        code = await asyncio.wait_for(app.run(), 30)
        await app.api.aclose()
    finally:
        holder.release()
    assert code == EXIT_ERROR  # the device still has that active session on the server
    assert console.has("em uso por outro companion")
    assert fake.ends == [] and fake.accepted() == []
    [still] = SessionStore.find_unfinished(work_dir)
    assert still.session_id == store.session_id
    assert [item.sequence for item in still.items] == pending
    assert all(still.path_of(item).is_file() for item in still.items)


# -- rejected answers while closing or resuming a leftover -------------------------------------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_end_rejected_for_a_leftover_does_not_crash_every_start(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    leftover = await leave_unfinished(file_config(ffmpeg, work_dir, finished_mkv), fake)
    other = tmp_path / "other.mkv"
    shutil.copyfile(finished_mkv, other)
    config = file_config(ffmpeg, work_dir, other)

    fake.end_failures = [422]  # e.g. lastSequence validation
    console = Console()
    app = make_app(config, fake, console)
    code = await asyncio.wait_for(app.run(), 60)  # used to raise RejectedError
    await app.api.aclose()
    assert code == EXIT_ERROR, console.getvalue()  # its session is still active on the server
    assert console.has("recusou o encerramento da sessão anterior")
    assert SessionStore.find_unfinished(work_dir) == []  # not picked up again next time

    fake.sessions[leftover.session_id].status = "ended"  # ended on the Lives page
    console = Console()
    app = make_app(config, fake, console)
    code = await asyncio.wait_for(app.run(), 60)
    await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    assert len(fake.sessions) == 2


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_resume_rejected_closes_the_leftover_and_starts_a_new_session(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    config = file_config(ffmpeg, tmp_path / "work", finished_mkv)
    fake = FakeLiveApi()
    # Long enough to still be "ending" when the new session is requested, even on a busy machine.
    fake.analysis_seconds = 2.0
    leftover = await leave_unfinished(config, fake)
    queued = len(leftover.items)
    fake.heartbeat_failures = [422]  # the heartbeat that resumes the session
    console = Console()
    app = make_app(config, fake, console)
    code = await asyncio.wait_for(app.run(), 60)
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "recusou retomar a sessão anterior" in output
    # The new session had to wait until the old one was analysed (409 plan_limit meanwhile).
    assert "Aguardando a análise da sessão anterior" in output
    old_id, new_id = fake.sessions
    assert old_id == leftover.session_id
    assert len(assert_complete(fake, old_id)) == queued
    assert_complete(fake, new_id, total=20.0)


async def test_an_unexpected_api_error_is_reported_not_raised(tmp_path: Path) -> None:
    recording = tmp_path / "rec.mkv"
    recording.write_bytes(MKV_MAGIC + b"\0" * 100)
    console = Console()
    app = make_app(file_config("ffmpeg", tmp_path / "work", recording), FakeLiveApi(), console)

    async def boom() -> int:
        raise RejectedError("HTTP 410 gone: Sessão removida", status=410)

    app._run_file_mode = boom  # type: ignore[method-assign]
    code = await app.run()
    await app.api.aclose()
    assert code == EXIT_ERROR
    assert console.has("HTTP 410 gone")


# -- 409 plan_limit while the previous session is still "ending" -------------------------------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_new_session_waits_for_the_previous_one_to_be_analysed(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    # Long enough to still be "ending" when the new session is requested, even on a busy machine.
    fake.analysis_seconds = 3.0
    leftover = await leave_unfinished(file_config(ffmpeg, work_dir, finished_mkv), fake)
    other = tmp_path / "other.mkv"
    shutil.copyfile(finished_mkv, other)
    console = Console()
    app = make_app(file_config(ffmpeg, work_dir, other), fake, console)
    code = await asyncio.wait_for(app.run(), 60)
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "Encerrando a sessão anterior" in output
    assert "Aguardando a análise da sessão anterior" in output
    old_id, new_id = fake.sessions
    assert old_id == leftover.session_id
    assert_complete(fake, old_id)
    assert_complete(fake, new_id, total=20.0)
    # Its heartbeat told when the old session became terminal.
    assert any(h["session"] == old_id and not h["state"]["recording"] for h in fake.heartbeats)


async def test_plan_limit_of_another_session_still_exits_with_a_message(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    async with fake.client() as api:
        await api.create_session(title="aberta em outro lugar")
    recording = tmp_path / "rec.mkv"
    recording.write_bytes(MKV_MAGIC + b"\0" * 100)
    console = Console()
    app = make_app(file_config("ffmpeg", tmp_path / "work", recording), fake, console)
    code = await asyncio.wait_for(app.run(), 20)
    await app.api.aclose()
    assert code == EXIT_ERROR
    assert console.has("Não foi possível criar a sessão")
    assert console.has("sessão ativa")


# -- OBS mode: leftovers are handled at startup ------------------------------------------------


@pytest.mark.async_timeout(60)
async def test_obs_mode_sends_a_leftover_queue_at_startup(tmp_path: Path) -> None:
    """The recording of the leftover is gone: its queue is sent right away (with heartbeats
    meanwhile) and the session ended, without waiting for OBS to record again."""
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = str((await api.create_session())["session"]["id"])
    store = SessionStore.create(
        work_dir, session_id, mode="obs", api_url=BASE_URL, device_id="dev-1", segment_seconds=5
    )
    store.set_source(tmp_path / "apagada.mkv", sequence_offset=0, time_offset=0.0)
    run_dir = store.new_run_dir()
    for sequence in range(3):
        path = run_dir / f"seg_{sequence:05d}.mkv"
        path.write_bytes(MKV_MAGIC + bytes([sequence]) * 2000)
        segment = Segment(sequence, path, start=sequence * 5.0, end=(sequence + 1) * 5.0)
        store.add(segment, final=False)
    console = Console()
    async with FakeObsServer(record_directory=str(tmp_path / "obs")) as server:
        app = make_app(obs_config("ffmpeg-not-needed", work_dir, server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: fake.ends, what="leftover ended")
        await wait_until(lambda: console.has("Encerrando a sessão anterior"), what="message")
        app.request_stop()
        code = await asyncio.wait_for(task, 20)
        await app.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    accepted = assert_complete(fake, session_id, total=15.0)
    assert len(accepted) == 3
    leftover_beats = [h for h in fake.heartbeats if h["session"] == session_id]
    assert leftover_beats and not any(h["state"]["recording"] for h in leftover_beats)
    assert SessionStore.find_unfinished(work_dir) == []
    assert not server.recording  # nothing happened in OBS meanwhile


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_obs_mode_finishes_the_previous_recording_at_startup(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    """Killed during a live; the recording stopped meanwhile. The next start resumes that
    session, captures the rest of the file, flags the last segment and ends the session."""
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 20-00-00.mkv"
    shutil.copyfile(finished_mkv, recording)
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    fake.always_fail_segments = 503
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        first = make_app(obs_config(ffmpeg, work_dir, server.url), fake, Console())
        task = asyncio.create_task(first.run())
        await wait_until(lambda: first.obs_synced, what="OBS sync")
        await server.start_recording(str(recording))
        await wait_until(
            lambda: first.session is not None and len(first.session.store.items) >= 2,
            what="queued segments",
        )
        first.request_stop()
        first.request_stop()  # killed
        assert await asyncio.wait_for(task, 20) == EXIT_ABORTED
        await first.api.aclose()
        server.recording = False  # the recording stopped while no companion was running
        fake.always_fail_segments = None

        console = Console()
        second = make_app(obs_config(ffmpeg, work_dir, server.url), fake, console)
        task = asyncio.create_task(second.run())
        await wait_until(lambda: fake.ends, what="previous session ended")
        await wait_until(lambda: console.has("Aguardando a próxima gravação"), what="idle")
        assert not second._obs_activity()
        second.request_stop()
        code = await asyncio.wait_for(task, 20)
        await second.api.aclose()
    assert code == EXIT_OK, console.getvalue()
    assert console.has("Concluindo a sessão anterior")
    [session_id] = fake.sessions  # resumed, not a new one
    assert_complete(fake, session_id, total=20.0)


# -- the heartbeat task is restarted if it ever dies -------------------------------------------


@pytest.mark.async_timeout(30)
async def test_a_heartbeat_task_that_dies_is_restarted(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = str((await api.create_session())["session"]["id"])
    config = file_config("ffmpeg", tmp_path / "work", tmp_path / "rec.mkv")
    app = make_app(config, fake, Console())
    store = SessionStore.create(
        config.work_dir, session_id, mode="file", api_url=BASE_URL, device_id=None,
        segment_seconds=5,
    )  # fmt: skip
    session = LiveSession(app, store)
    original = session.heartbeat.run
    calls: list[float] = []

    async def flaky_run(*, initial_delay: float = 0.0) -> Any:
        calls.append(initial_delay)
        if len(calls) == 1:
            raise RuntimeError("bug in the heartbeat loop")
        return await original(initial_delay=initial_delay)

    session.heartbeat.run = flaky_run  # type: ignore[method-assign]
    session.start()
    try:
        await wait_until(lambda: fake.heartbeats, what="heartbeat after the restart")
    finally:
        await session.close()
        await app.api.aclose()
    assert session.heartbeat_restarts == 1
    assert calls[0] == 0.0 and calls[1] > 0
