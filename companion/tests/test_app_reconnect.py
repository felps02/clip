"""OBS reconnects: the file OBS writes is looked up again, because OBS may have been restarted
(and be recording another file) while its events could not be received."""

from __future__ import annotations

import asyncio
import contextlib
import os
import shutil
import sys
import time
from collections.abc import Callable
from pathlib import Path

import pytest

import clipforge_companion.app as app_module
from clipforge_companion.app import (
    EXIT_OK,
    CompanionApp,
    LiveSession,
    Outcome,
    _path_key,
    _same_path,
)
from clipforge_companion.segmenter import Segmenter
from clipforge_companion.status import fmt_clock
from clipforge_companion.uploader import EndingRegistry, SessionStore
from tests.fake_api import BASE_URL, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.media import cluster_offsets
from tests.test_app import Console, make_app, obs_config, wait_until
from tests.test_app_capture import assert_contiguous_and_final, write_growing
from tests.test_app_obs_files import StubObs, grow, stub_app

MKV_MAGIC = bytes([0x1A, 0x45, 0xDF, 0xA3])

needs_creation_times = pytest.mark.skipif(
    not hasattr(os.stat_result, "st_birthtime") and sys.platform != "win32",
    reason="the file system does not report creation times here",
)


def capturing(app: CompanionApp, recording: Path) -> LiveSession:
    """A session whose capture of ``recording`` is in progress (FFmpeg not involved)."""
    store = SessionStore.create(
        app.config.work_dir, "s1", mode="obs", api_url=BASE_URL, device_id=None, segment_seconds=5
    )
    session = LiveSession(app, store)
    session.capturing = True
    session.captured_paths.append(str(recording))
    session.generation = app.record_generation
    app.session = session
    return session


def recording_file(directory: Path, name: str, *, written_ago: float = 0.0) -> Path:
    path = directory / name
    path.write_bytes(MKV_MAGIC + b"\0" * 1000)
    if written_ago:
        last_write = time.time() - written_ago
        os.utime(path, (last_write, last_write))
    return path


def reconnected(app: CompanionApp, directory: Path) -> None:
    """OBS was connected and recording; the connection dropped and came back (2nd time)."""
    app.obs = StubObs(directory, connect_count=2, recording=True)  # type: ignore[assignment]
    app.obs_connected = True
    app.obs_recording = True


def console_of(app: CompanionApp) -> str:
    return app.status.stream.getvalue()  # type: ignore[attr-defined]


@needs_creation_times
async def test_a_new_recording_started_while_obs_was_away_is_captured_next(
    tmp_path: Path,
) -> None:
    old = recording_file(tmp_path, "2026-10-23 20-00-00.mkv", written_ago=60)  # OBS crashed
    new = recording_file(tmp_path, "2026-10-23 20-01-30.mkv")  # recorded after the restart
    app = stub_app(tmp_path)
    try:
        reconnected(app, tmp_path)
        capturing(app, old)
        generation = app.record_generation
        grower = asyncio.create_task(grow(new))
        await app._sync_obs()  # GetRecordStatus says "recording", as before the disconnect
        await grower
        assert app.obs_synced
        assert app.record_generation == generation + 1  # not the recording being captured
        assert app._after_run(old, True, generation) == "final"  # its capture ends...
        assert app.record_path == str(new)
        app.session = None
        assert app._obs_activity()  # ...and the new file is what the main loop captures next
        assert "O OBS está gravando um arquivo novo" in console_of(app)
    finally:
        await app.api.aclose()


@needs_creation_times
async def test_the_next_file_of_a_split_recording_continues_the_capture(tmp_path: Path) -> None:
    old = recording_file(tmp_path, "2026-10-23 20-00-00.mkv")
    split = recording_file(tmp_path, "2026-10-23 20-30-00.mkv")  # created as old stopped
    app = stub_app(tmp_path)
    try:
        reconnected(app, tmp_path)
        capturing(app, old)
        generation = app.record_generation
        grower = asyncio.create_task(grow(split))
        await app._sync_obs()
        await grower
        assert app.record_generation == generation  # the same recording
        assert app._after_run(old, True, generation) == "file_changed"
        assert app.file_changed_to == str(split)
    finally:
        await app.api.aclose()


async def test_the_file_being_captured_still_growing_changes_nothing(tmp_path: Path) -> None:
    current = recording_file(tmp_path, "2026-10-23 20-00-00.mkv")
    other = recording_file(tmp_path, "2026-10-23 20-01-00.mkv")
    app = stub_app(tmp_path)
    try:
        reconnected(app, tmp_path)
        capturing(app, current)
        generation = app.record_generation
        grower = asyncio.create_task(grow(current, other))
        await app._sync_obs()
        await grower
        assert app.record_generation == generation
        assert app.record_path == str(current)
        assert app.file_changed_to is None
    finally:
        await app.api.aclose()


async def test_a_finished_recording_obs_still_writes_is_captured_again(tmp_path: Path) -> None:
    """Its capture ended while OBS was unreachable for too long; OBS is back and still writes
    the file: it is captured again (a new session goes on from the last segment sent)."""
    recording = recording_file(tmp_path, "2026-10-23 20-00-00.mkv")
    app = stub_app(tmp_path)
    try:
        reconnected(app, tmp_path)
        app.finished_recordings.add(_path_key(recording))
        grower = asyncio.create_task(grow(recording))
        await app._sync_obs()
        await grower
        assert app.record_path == str(recording)
        assert app._obs_activity()
        assert "a captura continua numa sessão nova" in console_of(app)
    finally:
        await app.api.aclose()


# -- a file resolved before a reconnect, captured after it ------------------------------------

ONE_SEGMENT = r"""
import sys
from pathlib import Path
list_path = Path(sys.argv[1])
(list_path.parent / "seg_00000.mkv").write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 64)
with open(list_path, "a", encoding="utf-8") as handle:
    handle.write("seg_00000.mkv,0.000000,5.000000\n")
"""


class OneSegment(Segmenter):
    """Stand-in for FFmpeg over a recording OBS no longer writes: one 5 s segment, exit 0."""

    @property
    def command(self) -> list[str]:
        return [sys.executable, "-c", ONE_SEGMENT, str(self.list_path)]


@needs_creation_times
@pytest.mark.async_timeout(60)
async def test_a_file_resolved_before_a_reconnect_ends_once_obs_records_another_one(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The file OBS records is resolved, but its session is created only later (the previous
    session still being analysed). Meanwhile OBS crashed, was opened again and records a new
    file: the reconnect finds no capture in progress, and OBS says "recording" before and
    after. The first file is still captured; once it stops growing, its capture finds the new
    file and ends, and the new file is what the main loop captures next. That capture used to
    wait for the first file to grow again until OBS stopped recording the new one."""
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    old = recording_file(obs_dir, "2026-10-23 20-00-00.mkv", written_ago=60)  # OBS crashed
    new = recording_file(obs_dir, "2026-10-23 20-01-30.mkv")  # recorded after the restart
    monkeypatch.setattr(app_module, "Segmenter", OneSegment)
    config = obs_config(sys.executable, tmp_path / "work", "ws://127.0.0.1:1")
    app = make_app(
        config, FakeLiveApi(), Console(), split_scan_seconds=0.2, split_growth_seconds=0.3
    )
    stub = StubObs(obs_dir, connect_count=1, recording=True)
    app.obs = stub  # type: ignore[assignment]
    app.obs_connected = app.obs_synced = app.obs_recording = True
    app.obs_version = {"obsWebSocketVersion": "5.5.4"}  # reports split files itself
    app.record_path = str(old)  # from RecordStateChanged
    grower: asyncio.Task[None] | None = None
    try:
        found = await app._usable_recording()
        assert found == (old, app.record_generation, 1)
        stub.connect_count = 2  # OBS was restarted: the companion reconnects
        grower = asyncio.create_task(grow(new, times=100))
        await app._sync_obs()
        assert app.record_path == str(new)
        assert app.record_generation == found.generation  # nothing says the old file is over
        store = SessionStore.create(
            config.work_dir, "s1", mode="obs", api_url=BASE_URL, device_id=None,
            segment_seconds=5,
        )  # fmt: skip
        session = LiveSession(app, store)
        outcome = await asyncio.wait_for(app._obs_session_body(session, found), 15)
    finally:
        if grower is not None:
            grower.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await grower
        await app.api.aclose()
    assert outcome is Outcome.ENDED
    assert [(item.sequence, item.final) for item in store.items] == [(0, True)]
    assert app.record_generation == found.generation + 1
    assert "O OBS está gravando um arquivo novo" in console_of(app)
    assert app.record_path == str(new)
    assert app._obs_activity()  # the new file is captured next


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(90)
async def test_a_new_session_goes_on_from_the_last_segment_of_the_ended_one(
    ffmpeg: str, live_mkv: Path, tmp_path: Path
) -> None:
    """A session captured the first ~12 s and ended (OBS unreachable for too long); OBS
    still writes the file. The new session starts with the last segment the ended one sent
    (it was cut short when the file stopped growing) instead of the whole recording."""
    data = live_mkv.read_bytes()
    cut = next(offset for offset in cluster_offsets(live_mkv) if offset > len(data) * 0.6)
    recording = tmp_path / "rec.mkv"
    recording.write_bytes(data[:cut])
    console = Console()
    config = obs_config(ffmpeg, tmp_path / "work", "ws://127.0.0.1:1")
    app = make_app(config, FakeLiveApi(), console)  # OBS not recording: each capture ends
    stores = [
        SessionStore.create(
            config.work_dir, name, mode="obs", api_url=BASE_URL, device_id=None, segment_seconds=5
        )
        for name in ("s1", "s2")
    ]
    try:
        first = await app._capture(LiveSession(app, stores[0]), recording, obs_mode=True)
        with recording.open("ab") as handle:  # OBS went on writing it
            handle.write(data[cut:])
        second = await app._capture(LiveSession(app, stores[1]), recording, obs_mode=True)
    finally:
        await app.api.aclose()
    assert first is second is Outcome.ENDED, console.getvalue()
    ended = stores[0].items
    resumed = stores[1].items
    assert [item.sequence for item in resumed] == list(range(len(resumed)))
    assert resumed[0].start == 0.0  # the new session has its own timeline...
    assert resumed[0].duration > ended[-1].duration  # ...and starts with that segment, whole
    assert [item.final for item in resumed] == [False] * (len(resumed) - 1) + [True]
    sent_before = ended[-1].start  # everything before that segment was sent already
    total = sent_before + sum(item.duration for item in resumed)
    assert total == pytest.approx(20.0, abs=0.3)
    assert f"já foi enviado até {fmt_clock(sent_before)}" in console.getvalue()
    assert (
        stores[1].source is not None and stores[1].source["sequenceOffset"] == -ended[-1].sequence
    )


# -- end to end: OBS restarted and recording a new file before the companion reconnects -------


@needs_creation_times
@pytest.mark.ffmpeg
@pytest.mark.async_timeout(150)
async def test_a_recording_started_while_obs_restarted_gets_captured(
    ffmpeg: str, finished_mkv: Path, live_mkv: Path, tmp_path: Path
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    first = obs_dir / "2026-10-23 20-00-00.mkv"
    shutil.copyfile(finished_mkv, first)
    crashed = time.time() - 60
    os.utime(first, (crashed, crashed))  # OBS stopped writing it when it crashed
    second = obs_dir / "2026-10-23 20-01-30.mkv"
    fake, console = FakeLiveApi(), Console()
    server = FakeObsServer(record_directory=str(obs_dir))
    await server.start()
    writer: asyncio.Task[None] | None = None
    try:
        app = make_app(obs_config(ffmpeg, tmp_path / "work", server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(first))
        await wait_until(lambda: console.has("parou de crescer"), what="first file read")
        # OBS crashes; it is opened again and records a new file, all before the companion
        # reconnects: no RecordStateChanged reaches it, OBS says "recording" before and after.
        await server.stop()
        server.recording, server.record_path = True, str(second)
        writer = asyncio.create_task(write_growing(second, live_mkv))
        await server.start()
        await wait_until(
            lambda: len(fake.sessions) == 2, what="session of the new file", timeout=60
        )
        await writer
        await wait_until(lambda: console.has("parou de crescer", 2), what="new file read")
        await server.stop_recording()
        await wait_until(lambda: len(fake.ends) == 2, what="both sessions ended")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    finally:
        if writer is not None and not writer.done():
            writer.cancel()
        await server.stop()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "O OBS está gravando um arquivo novo" in output
    first_id, second_id = fake.sessions
    for session_id in (first_id, second_id):
        accepted = fake.accepted(session_id)
        assert_contiguous_and_final(accepted)
        assert accepted[0]["start"] == 0.0
        assert sum(u["duration"] for u in accepted) == pytest.approx(20.0, abs=0.3)
        assert {"session": session_id, "lastSequence": accepted[-1]["sequence"]} in fake.ends
    assert fake.created[1]["obs"]["recordingPath"] == str(second)


async def write_growing_until(
    path: Path, source: Path, done: Callable[[], bool], interval: float = 0.3
) -> None:
    """Writes ``source`` (a live-mode MKV) to ``path`` like OBS: half of it cluster by cluster,
    then a trickle of small pieces until ``done()`` (the file keeps growing however long that
    takes), then the rest. Pieces arrive well within FFmpeg's read timeout."""
    data = source.read_bytes()
    clusters = cluster_offsets(source)
    half = clusters[len(clusters) // 2]
    cuts = [offset for offset in clusters[1:] if offset <= half]
    position = 0
    for cut in cuts:
        with path.open("ab") as handle:
            handle.write(data[position:cut])
        position = cut
        await asyncio.sleep(interval)
    while not done() and position < len(data) - 4096:
        with path.open("ab") as handle:
            handle.write(data[position : position + 2048])
        position += 2048
        await asyncio.sleep(interval)
    with path.open("ab") as handle:
        handle.write(data[position:])


@needs_creation_times
@pytest.mark.ffmpeg
@pytest.mark.async_timeout(150)
async def test_a_recording_obs_restarted_into_before_the_capture_started_gets_captured(
    ffmpeg: str, finished_mkv: Path, live_mkv: Path, tmp_path: Path
) -> None:
    """The session of the first file waits for the previous session to be analysed;
    meanwhile OBS crashes, is opened again and records a new file, all before the companion
    reconnects. The first file still gets its session, and the new file its own."""
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    first = obs_dir / "2026-10-23 20-00-00.mkv"
    shutil.copyfile(finished_mkv, first)
    crashed = time.time() - 60
    os.utime(first, (crashed, crashed))  # OBS stopped writing it when it crashed
    second = obs_dir / "2026-10-23 20-01-30.mkv"
    work_dir = tmp_path / "work"
    fake, console = FakeLiveApi(), Console()
    fake.analysis_seconds = 600.0  # the previous session is "ending" until finish_analysis()
    async with fake.client() as api:
        previous = str((await api.create_session())["session"]["id"])
        await api.end_session(previous)
    fake.analysis_seconds = 0.3
    EndingRegistry(work_dir / "ending-dev-1.json", BASE_URL).save({previous})  # ended earlier
    server = FakeObsServer(record_directory=str(obs_dir))
    await server.start()
    writer: asyncio.Task[None] | None = None
    try:
        app = make_app(
            obs_config(ffmpeg, work_dir, server.url),
            fake,
            console,
            split_scan_seconds=0.5,
            split_growth_seconds=0.5,
        )
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(first))
        await wait_until(
            lambda: console.has("Aguardando a análise da sessão anterior"),
            what="the session of the first file waits",
        )
        # OBS crashes; it is opened again and records a new file before the companion
        # reconnects: no RecordStateChanged reaches it, OBS says "recording" before and after.
        await server.stop()
        server.recording, server.record_path = True, str(second)
        # Still growing when the capture of the first file looks for it, however slow.
        writer = asyncio.create_task(
            write_growing_until(second, live_mkv, lambda: len(fake.ends) >= 2)
        )
        await server.start()
        await wait_until(
            lambda: app.obs_synced and _same_path(app.record_path, second), what="reconnect"
        )
        assert len(fake.sessions) == 1  # the session of the first file is not created yet
        fake.finish_analysis()  # now it can be
        await wait_until(
            lambda: len(fake.sessions) == 3, what="session of the new file", timeout=60
        )
        await writer
        await wait_until(lambda: console.has("parou de crescer", 2), what="new file read")
        await server.stop_recording()
        await wait_until(lambda: len(fake.ends) == 3, what="both sessions ended")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    finally:
        if writer is not None and not writer.done():
            writer.cancel()
        await server.stop()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "O OBS está gravando um arquivo novo" in output
    _, first_id, second_id = fake.sessions
    for session_id in (first_id, second_id):
        accepted = fake.accepted(session_id)
        assert_contiguous_and_final(accepted)
        assert accepted[0]["start"] == 0.0
        assert sum(u["duration"] for u in accepted) == pytest.approx(20.0, abs=0.3)
        assert {"session": session_id, "lastSequence": accepted[-1]["sequence"]} in fake.ends
    assert fake.created[1]["obs"]["recordingPath"] == str(first)
    assert fake.created[2]["obs"]["recordingPath"] == str(second)
