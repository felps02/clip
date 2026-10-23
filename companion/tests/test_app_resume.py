"""Resuming across runs: a resumed session keeps its own segment length, a session an earlier
run ended (still "ending" on the server) is waited for, not reported as a conflict, and a session
whose final segment was already queued is ended, never resumed."""

from __future__ import annotations

import asyncio
import dataclasses
import json
import shutil
from pathlib import Path

import pytest

from clipforge_companion.app import EXIT_ABORTED, EXIT_OK, CompanionApp, RecordingMark, _path_key
from clipforge_companion.segmenter import Segment
from clipforge_companion.status import fmt_clock
from clipforge_companion.uploader import EndingRegistry, QueueItem, SessionStore
from tests.fake_api import BASE_URL, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.media import cluster_offsets
from tests.test_app import Console, file_config, make_app, obs_config, wait_until
from tests.test_app_sessions import assert_complete, leave_unfinished

MKV_MAGIC = bytes([0x1A, 0x45, 0xDF, 0xA3])

# -- a resumed session is cut with its own segment length -----------------------------------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_a_resumed_session_keeps_its_segment_length(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    """Keyframes at 0, 8.4 and 16.7 s. Cut every 5 s the file gives 3 segments; the first run
    queued 2 of them. Cut every 10 s it would give 2 segments, both taken for already sent:
    16.7-20 s would be skipped and deleted."""
    config = file_config(ffmpeg, tmp_path / "work", finished_mkv)  # 5 s segments
    fake = FakeLiveApi()
    store = await leave_unfinished(config, fake)
    assert [item.sequence for item in store.items] == [0, 1]
    console = Console()
    app = make_app(dataclasses.replace(config, segment_seconds=10), fake, console)
    code = await asyncio.wait_for(app.run(), 60)
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert len(fake.created) == 1  # resumed, not a new session
    accepted = assert_complete(fake, store.session_id, total=20.0)
    assert len(accepted) == 3
    assert "foi criada com segmentos de 5 s e continua com eles" in output
    assert "--segment-seconds 10 vale só para sessões novas" in output
    assert "em segmentos de 5 s" in output


def test_the_stored_segment_length_is_read_back(tmp_path: Path) -> None:
    store = SessionStore.create(
        tmp_path, "s1", mode="file", api_url=BASE_URL, device_id=None, segment_seconds=45
    )
    assert SessionStore.load(store.directory).segment_seconds == 45.0
    for invalid in (None, "60", 0, -5, True, float("nan")):
        store.data["segmentSeconds"] = invalid
        assert store.segment_seconds is None, invalid


# -- a session an earlier run ended is still "ending" ----------------------------------------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_a_restarted_companion_waits_for_the_session_an_earlier_run_ended(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    work_dir = tmp_path / "work"
    other = tmp_path / "other.mkv"
    shutil.copyfile(finished_mkv, other)
    fake = FakeLiveApi()
    fake.analysis_seconds = 4.0  # the server still analyses the last segments meanwhile
    first = make_app(file_config(ffmpeg, work_dir, finished_mkv), fake, Console())
    assert await asyncio.wait_for(first.run(), 60) == EXIT_OK
    await first.api.aclose()
    [old_id] = fake.sessions
    assert fake.sessions[old_id].status == "ending"

    console = Console()
    second = make_app(file_config(ffmpeg, work_dir, other), fake, console)
    code = await asyncio.wait_for(second.run(), 60)  # used to exit: "sessão ativa"
    await second.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "Aguardando a análise da sessão anterior" in output
    assert "Não foi possível criar a sessão" not in output
    _, new_id = fake.sessions
    assert_complete(fake, new_id, total=20.0)
    # The old session was asked about (heartbeat) and is forgotten once it is terminal.
    assert any(beat["session"] == old_id for beat in fake.heartbeats[-20:])
    [registry] = work_dir.glob("ending-*.json")
    assert EndingRegistry(registry, BASE_URL).load() == {new_id}


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_a_leftover_already_ending_on_the_server_is_not_resumed(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path
) -> None:
    """The run that ended the session stopped before writing that down: its queue.json says
    unfinished, the server says "ending". Nothing more is sent to it; a new session is made
    (after waiting for the old one to be analysed)."""
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    fake.analysis_seconds = 4.0
    async with fake.client() as api:
        old_id = str((await api.create_session(segment_seconds=5))["session"]["id"])
        await api.end_session(old_id)
    store = SessionStore.create(
        work_dir, old_id, mode="file", api_url=BASE_URL, device_id="dev-1", segment_seconds=5
    )
    store.set_source(finished_mkv, sequence_offset=0, time_offset=0.0)
    console = Console()
    app = make_app(file_config(ffmpeg, work_dir, finished_mkv), fake, console)
    code = await asyncio.wait_for(app.run(), 60)
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "já foi encerrada e está em análise no servidor; ela não é retomada" in output
    _, new_id = fake.sessions
    assert fake.accepted(old_id) == []
    assert_complete(fake, new_id, total=20.0)
    assert SessionStore.find_unfinished(work_dir) == []


# -- a session whose final segment was queued is never resumed --------------------------------
#
# Ctrl+C once (or a stop from the Lives page) makes FFmpeg close the segment it writes early,
# and that segment is queued flagged final. If the run then dies before /end (Ctrl+C again
# while the API is offline, a crash), resuming the session would cut that segment again whole,
# skip it as already sent and queue segments after the final one: the part the short copy
# lacks would never be sent, and two segments would be flagged final.


def growing_recording(live_mkv: Path, path: Path) -> bytes:
    """Writes the first ~12 s of ``live_mkv`` to ``path`` (a recording still being written);
    returns the rest."""
    data = live_mkv.read_bytes()
    cut = next(offset for offset in cluster_offsets(live_mkv) if offset > len(data) * 0.6)
    path.write_bytes(data[:cut])
    return data[cut:]


async def stop_twice_after_the_final_segment(
    app: CompanionApp, task: asyncio.Task[int], fake: FakeLiveApi
) -> QueueItem:
    """Ctrl+C once while the API is offline for uploads, then Ctrl+C again once the final
    segment is queued: the run leaves before /end. Returns that final segment."""
    await wait_until(
        lambda: app.session is not None and app.session.store.items, what="first segment"
    )
    app.request_stop()  # Ctrl+C once: FFmpeg closes the segment it writes
    await wait_until(
        lambda: app.session is not None and app.session.store.final_queued,
        what="final segment queued",
    )
    app.request_stop()  # Ctrl+C again: the queue cannot be sent
    assert await asyncio.wait_for(task, 30) == EXIT_ABORTED
    await app.api.aclose()
    assert fake.ends == [] and fake.accepted() == []
    [store] = SessionStore.find_unfinished(app.config.work_dir)
    assert [(item.sequence, item.final) for item in store.items] == [(0, False), (1, True)]
    assert RecordingMark.from_json(store.recording_mark) is not None  # kept with the final
    return store.items[-1]


def assert_ended_and_continued(
    fake: FakeLiveApi, old_id: str, final: QueueItem, output: str
) -> None:
    """The old session ended with its short final segment; a new session starts with that
    segment, whole, and goes to the end of the recording: nothing missing, one final each."""
    ended = assert_complete(fake, old_id)
    assert [u["sequence"] for u in ended] == [0, 1]
    assert ended[-1]["duration"] == pytest.approx(final.duration)
    [new_id] = [session_id for session_id in fake.sessions if session_id != old_id]
    resumed = assert_complete(fake, new_id)
    assert resumed[0]["duration"] > final.duration + 1.0  # the segment cut short, now whole
    assert final.start + sum(u["duration"] for u in resumed) == pytest.approx(20.0, abs=0.3)
    assert "já estava sendo encerrada" in output
    assert "Retomando a sessão" not in output
    assert f"já foi enviado até {fmt_clock(final.start)} numa sessão anterior" in output


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(150)
async def test_a_session_whose_final_segment_was_queued_is_ended_not_resumed(
    ffmpeg: str, live_mkv: Path, tmp_path: Path
) -> None:
    recording = tmp_path / "rec.mkv"
    rest = growing_recording(live_mkv, recording)
    config = file_config(ffmpeg, tmp_path / "work", recording)
    fake = FakeLiveApi()
    fake.always_fail_segments = 503  # the API is offline for uploads
    first = make_app(config, fake, Console())
    final = await stop_twice_after_the_final_segment(first, asyncio.create_task(first.run()), fake)
    [old_id] = fake.sessions
    with recording.open("ab") as handle:  # the recording went on
        handle.write(rest)
    fake.always_fail_segments = None

    console = Console()
    app = make_app(config, fake, console)
    code = await asyncio.wait_for(app.run(), 90)
    await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert len(fake.sessions) == 2
    assert_ended_and_continued(fake, old_id, final, output)
    assert SessionStore.find_unfinished(config.work_dir) == []


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(150)
async def test_obs_mode_ends_such_a_session_at_startup_while_obs_still_records_its_file(
    ffmpeg: str, live_mkv: Path, tmp_path: Path
) -> None:
    """The same in OBS mode, OBS still recording that file when the companion starts again:
    the session is ended right at startup, not left for the main loop to resume, and the
    recording goes on in a new session."""
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 20-00-00.mkv"
    rest = growing_recording(live_mkv, recording)
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    fake.always_fail_segments = 503
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        first = make_app(obs_config(ffmpeg, work_dir, server.url), fake, Console())
        task = asyncio.create_task(first.run())
        await wait_until(lambda: first.obs_synced, what="OBS sync")
        await server.start_recording(str(recording))
        final = await stop_twice_after_the_final_segment(first, task, fake)
        [old_id] = fake.sessions
        with recording.open("ab") as handle:  # OBS went on recording it
            handle.write(rest)
        fake.always_fail_segments = None

        console = Console()
        app = make_app(obs_config(ffmpeg, work_dir, server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: len(fake.sessions) == 2, what="new session", timeout=60)
        await wait_until(lambda: console.has("parou de crescer"), what="rest captured", timeout=60)
        await server.stop_recording()
        await wait_until(lambda: len(fake.ends) == 2, what="both sessions ended")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert_ended_and_continued(fake, old_id, final, output)
    # Ended before the new session was even asked for, not by the main loop.
    assert output.index("já estava sendo encerrada") < output.index("Sessão criada")
    assert SessionStore.find_unfinished(work_dir) == []


def final_leftover(work_dir: Path, session_id: str, recording: Path) -> SessionStore:
    """A session of an earlier run with two segments queued, the second flagged final, and
    the mark of its recording (as _capture leaves it)."""
    store = SessionStore.create(
        work_dir, session_id, mode="obs", api_url=BASE_URL, device_id="dev-1", segment_seconds=5
    )
    store.set_source(recording, sequence_offset=0, time_offset=0.0)
    run_dir = store.new_run_dir()
    for sequence in range(2):
        path = run_dir / f"seg_{sequence:05d}.mkv"
        path.write_bytes(MKV_MAGIC + bytes([sequence]) * 2000)
        segment = Segment(sequence, path, start=sequence * 5.0, end=sequence * 5.0 + 5.0)
        store.add(segment, final=sequence == 1)
    mark = RecordingMark(sequence=1, start=5.0, segment_seconds=5.0, size=recording.stat().st_size)
    store.set_recording_mark(mark.to_json(recording))
    return store


@pytest.mark.async_timeout(60)
async def test_obs_mode_only_ends_such_a_session_when_its_recording_is_over(
    tmp_path: Path,
) -> None:
    """Its recording finished while no companion ran (still on disk): the session is ended
    with what it had queued. Resuming it would capture the rest of the file after its final
    segment (FFmpeg is not even available here: nothing may be captured)."""
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 20-00-00.mkv"
    recording.write_bytes(MKV_MAGIC + b"\0" * 4096)
    work_dir = tmp_path / "work"
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = str((await api.create_session())["session"]["id"])
    store = final_leftover(work_dir, session_id, recording)
    mark = RecordingMark.from_json(store.recording_mark)
    console = Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        app = make_app(obs_config("ffmpeg-not-needed", work_dir, server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("Aguardando a próxima gravação"), what="idle")
        app.request_stop()
        code = await asyncio.wait_for(task, 20)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "já estava sendo encerrada" in output
    assert "Concluindo a sessão anterior" not in output
    assert len(assert_complete(fake, session_id, total=10.0)) == 2
    assert len(fake.sessions) == 1
    # Should OBS record that file again in this run, a new session goes on from the mark.
    assert mark is not None and app.capture_marks == {_path_key(recording): mark[1]}
    assert SessionStore.find_unfinished(work_dir) == []


def test_the_recording_mark_kept_with_the_final_segment_is_read_back(tmp_path: Path) -> None:
    recording = tmp_path / "rec.mkv"
    mark = RecordingMark(sequence=3, start=25.0, segment_seconds=5.0, size=123_456)
    store = SessionStore.create(
        tmp_path / "work", "s1", mode="obs", api_url=BASE_URL, device_id=None, segment_seconds=5
    )
    assert store.recording_mark is None
    store.set_recording_mark(mark.to_json(recording))
    loaded = SessionStore.load(store.directory)
    assert RecordingMark.from_json(loaded.recording_mark) == (str(recording), mark)
    valid = mark.to_json(recording)
    for invalid in (
        None,
        [],
        {**valid, "path": ""},
        {**valid, "sequence": -1},
        {**valid, "sequence": 1.5},
        {**valid, "size": True},
        {**valid, "start": float("nan")},
        {**valid, "start": -1.0},
        {**valid, "segmentSeconds": 0},
        {key: value for key, value in valid.items() if key != "size"},
    ):
        assert RecordingMark.from_json(invalid) is None, invalid


def test_ending_registry_round_trip(tmp_path: Path) -> None:
    path = tmp_path / "ending-dev-1.json"
    assert EndingRegistry(path, "http://a").load() == set()
    EndingRegistry(path, "http://a").save({"s1", "s2"})
    other = EndingRegistry(path, "http://b")
    assert other.load() == set()  # another API's sessions are not this one's
    other.save({"x"})
    again = EndingRegistry(path, "http://a")
    assert again.load() == {"s1", "s2"}  # kept by the other API's save
    again.save({"s2"})
    assert EndingRegistry(path, "http://a").load() == {"s2"}
    assert EndingRegistry(path, "http://b").load() == {"x"}
    assert EndingRegistry(path, "http://a", max_age=0.0).load() == set()  # too old to wait for

    data = json.loads(path.read_text(encoding="utf-8"))
    assert {entry["sessionId"] for entry in data["sessions"]} == {"s2", "x"}
    path.write_text("{not json", encoding="utf-8")
    broken = EndingRegistry(path, "http://a")
    assert broken.load() == set()  # unreadable: ignored, never raised
    broken.save([])
    assert not path.exists()


async def test_only_a_session_reported_ending_is_waited_for(tmp_path: Path) -> None:
    """A recorded session that is "ending" makes a 409 worth retrying; one that is not being
    ended (a stale record) or is already terminal is dropped from the record."""
    fake = FakeLiveApi()
    fake.analysis_seconds = 600.0  # "ending" until finish_analysis()
    async with fake.client() as api:
        ending = str((await api.create_session())["session"]["id"])
        await api.end_session(ending)
    app = make_app(file_config("ffmpeg", tmp_path / "work", tmp_path / "rec.mkv"), fake, Console())
    try:
        app.ending_sessions = {ending}
        assert await app._previous_session_ending() is True
        assert app.ending_sessions == {ending}
        fake.finish_analysis()
        assert await app._previous_session_ending() is True  # just turned terminal: retry once
        assert app.ending_sessions == set()

        async with fake.client() as api:
            live = str((await api.create_session())["session"]["id"])
        app.ending_sessions = {live}  # "waiting": not being ended
        assert await app._previous_session_ending() is False
        assert app.ending_sessions == set()
    finally:
        await app.api.aclose()
