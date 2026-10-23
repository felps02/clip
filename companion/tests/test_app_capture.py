"""Capture edge cases: stopping while FFmpeg re-reads a recording, a held segment after a
pause, and a new OBS recording started while FFmpeg still drains the previous one."""

from __future__ import annotations

import asyncio
import itertools
import shutil
import sys
from pathlib import Path

import pytest

import clipforge_companion.app as app_module
from clipforge_companion.app import EXIT_ABORTED, EXIT_OK, LiveSession
from clipforge_companion.segmenter import Segment, Segmenter
from clipforge_companion.uploader import SessionStore
from tests.fake_api import BASE_URL, FakeLiveApi
from tests.fake_obs import FakeObsServer
from tests.media import cluster_offsets
from tests.test_app import Console, file_config, make_app, obs_config, wait_until


class SlowSegmenter(Segmenter):
    """FFmpeg reading at 4x real time. Stream copy is so fast that FFmpeg re-reads a test
    recording before it looks at stdin (every 0.5 s): slowed down, a stop request reliably
    arrives while it is still re-reading the part that was already sent, as it does with a
    real multi-hour recording."""

    @property
    def command(self) -> list[str]:
        command = super().command
        index = command.index("-i")
        return [*command[:index], "-readrate", "4", *command[index:]]


def assert_contiguous_and_final(accepted: list[dict]) -> None:
    assert [u["sequence"] for u in accepted] == list(range(len(accepted)))
    assert [u["final"] for u in accepted] == [False] * (len(accepted) - 1) + [True]
    for previous, current in itertools.pairwise(accepted):
        assert current["start"] == pytest.approx(previous["start"] + previous["duration"], abs=0.05)


# -- _consume with a held segment (scripted stand-in for FFmpeg) -------------------------------

SCRIPT = r"""
import sys
path, mode = sys.argv[1], sys.argv[2]
def line(text):
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(text + "\n")
if mode == "short":    # stops inside the skipped range (file gone, FFmpeg failed...)
    line("seg_00000.mkv,0.000000,8.333000")
elif mode == "longer":  # re-creates segment 1 with the new data and goes on
    line("seg_00000.mkv,0.000000,8.333000")
    line("seg_00001.mkv,8.333000,16.666000")
    line("seg_00002.mkv,16.666000,20.000000")
elif mode == "inside":  # ends inside segment 1, before the old copy's end
    line("seg_00000.mkv,0.000000,8.333000")
    line("seg_00001.mkv,8.333000,9.000000")
"""  # exits right after its last line, like FFmpeg writing the list entry of its last segment


class Scripted(Segmenter):
    def __init__(self, mode: str, out_dir: Path, **kwargs: object) -> None:
        super().__init__(
            ffmpeg=sys.executable,
            recording=out_dir / "unused.mkv",
            out_dir=out_dir,
            segment_seconds=5,
            poll_interval=0.05,
            settle_seconds=2.0,
            **kwargs,  # type: ignore[arg-type]
        )
        self.mode = mode

    @property
    def command(self) -> list[str]:
        return [sys.executable, "-c", SCRIPT, str(self.list_path), self.mode]


@pytest.mark.parametrize(
    ("mode", "expected_last"),
    [("short", "held"), ("longer", (2, 20.0)), ("inside", "held")],
)
async def test_a_held_segment_is_only_replaced_by_a_longer_copy(
    tmp_path: Path, mode: str, expected_last: object
) -> None:
    config = file_config("ffmpeg", tmp_path / "work", tmp_path / "rec.mkv")
    app = make_app(config, FakeLiveApi(), Console())
    store = SessionStore.create(
        config.work_dir, "s1", mode="file", api_url=BASE_URL, device_id=None, segment_seconds=5
    )
    session = LiveSession(app, store)
    old_run = store.new_run_dir()
    held = Segment(1, old_run / "seg_00001.mkv", start=8.333, end=12.0, last=True)
    held.path.write_bytes(b"\x1a\x45\xdf\xa3old")
    segmenter = Scripted(mode, store.new_run_dir(), skip_through=0)
    try:
        await segmenter.start()
        last, interrupted = await app._consume(session, segmenter, held=held)
    finally:
        await app.api.aclose()
    assert interrupted is None and last is not None
    if expected_last == "held":
        assert last is held and held.path.exists()  # still the newest data
        assert [item.sequence for item in store.items] == []
    else:
        assert (last.sequence, last.end) == expected_last
        assert not held.path.exists()  # superseded by the longer copy
        assert [(item.sequence, item.duration) for item in store.items] == [(1, 8.333)]


# -- stopping while FFmpeg re-reads the recording (real FFmpeg) --------------------------------


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_stop_right_after_a_paused_recording_resumes_keeps_the_tail(
    ffmpeg: str, live_mkv: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(app_module, "Segmenter", SlowSegmenter)
    data = live_mkv.read_bytes()
    cut = next(offset for offset in cluster_offsets(live_mkv) if offset > len(data) * 0.6)
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    recording = obs_dir / "2026-10-23 20-00-00.mkv"
    recording.write_bytes(data[:cut])  # ~12 s written, then OBS pauses
    fake, console = FakeLiveApi(), Console()
    async with FakeObsServer(record_directory=str(obs_dir)) as server:
        app = make_app(obs_config(ffmpeg, tmp_path / "work", server.url), fake, console)
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(recording))
        await wait_until(lambda: console.has("parou de crescer"), what="pause", timeout=60)
        sent_before_pause = max(u["start"] + u["duration"] for u in fake.accepted())
        with recording.open("ab") as handle:  # the pause ends
            handle.write(data[cut:])
        await wait_until(lambda: console.has("voltou a crescer"), what="growth")
        app.request_stop()  # Ctrl+C while FFmpeg reads the recording from the start again
        code = await asyncio.wait_for(task, 60)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert "relendo a parte da gravação que já foi enviada" in output
    [session_id] = fake.sessions
    accepted = fake.accepted(session_id)
    assert_contiguous_and_final(accepted)
    total = sum(u["duration"] for u in accepted)
    # Nothing captured before the pause is lost, and the segment cut by the pause was
    # re-created whole (to the keyframe at 16.7 s) from the data written after it.
    assert total > sent_before_pause and total >= 16.6, (total, sent_before_pause, output)
    assert fake.ends == [{"session": session_id, "lastSequence": accepted[-1]["sequence"]}]


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(120)
async def test_stop_while_a_resumed_capture_catches_up_still_flags_a_final_segment(
    ffmpeg: str, finished_mkv: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = file_config(ffmpeg, tmp_path / "work", finished_mkv)
    fake = FakeLiveApi()
    fake.always_fail_segments = 503
    first = make_app(config, fake, Console())
    task = asyncio.create_task(first.run())
    await wait_until(
        lambda: first.session is not None and len(first.session.store.items) >= 2,
        what="queued segments",
    )
    first.request_stop()
    first.request_stop()
    assert await task == EXIT_ABORTED
    await first.api.aclose()
    [store] = SessionStore.find_unfinished(config.work_dir)
    queued = [item.sequence for item in store.items]
    fake.always_fail_segments = None

    monkeypatch.setattr(app_module, "Segmenter", SlowSegmenter)
    console = Console()
    second = make_app(config, fake, console)
    second.request_stop()  # Ctrl+C right away: the resumed capture is still re-reading
    code = await asyncio.wait_for(second.run(), 60)
    await second.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    accepted = fake.accepted(store.session_id)
    assert_contiguous_and_final(accepted)
    assert len(accepted) > len(queued), output  # captured past what was sent before
    assert fake.ends[-1]["lastSequence"] == accepted[-1]["sequence"]


# -- a new recording started while FFmpeg drains the previous one ------------------------------


async def write_growing(path: Path, source: Path, interval: float = 0.25) -> None:
    """Writes ``source`` (a live-mode MKV) to ``path`` cluster by cluster, like OBS."""
    data = source.read_bytes()
    cuts = [*cluster_offsets(source)[1:], len(data)]
    position = 0
    for cut in cuts:
        with path.open("ab") as handle:
            handle.write(data[position:cut])
        position = cut
        await asyncio.sleep(interval)


@pytest.mark.ffmpeg
@pytest.mark.async_timeout(150)
async def test_a_recording_started_while_ffmpeg_drains_the_previous_one_gets_its_own_session(
    ffmpeg: str, finished_mkv: Path, live_mkv: Path, tmp_path: Path
) -> None:
    obs_dir = tmp_path / "obs"
    obs_dir.mkdir()
    first = obs_dir / "2026-10-23 20-00-00.mkv"
    shutil.copyfile(finished_mkv, first)
    second = obs_dir / "2026-10-23 20-00-30.mkv"
    fake, console = FakeLiveApi(), Console()
    # obs-websocket 5.4 (no RecordFileChanged): the folder is searched for split files, so
    # only the recording generation keeps the new recording out of the previous session.
    async with FakeObsServer(record_directory=str(obs_dir), websocket_version="5.4.2") as server:
        app = make_app(
            obs_config(ffmpeg, tmp_path / "work", server.url),
            fake,
            console,
            split_scan_seconds=0.5,
            split_growth_seconds=0.5,
        )
        task = asyncio.create_task(app.run())
        await wait_until(lambda: console.has("aguardando gravação"), what="OBS sync")
        await server.start_recording(str(first))
        await wait_until(lambda: console.has("Capturando"), what="first capture")
        # Stop and start again at once: FFmpeg still needs ~3 x rw_timeout to notice that
        # the first file stopped growing.
        await server.stop_recording()
        writer = asyncio.create_task(write_growing(second, live_mkv))
        await server.start_recording(str(second))
        await wait_until(lambda: len(fake.sessions) == 2, what="second session", timeout=60)
        await writer
        await wait_until(lambda: console.has("parou de crescer"), what="second file written")
        await server.stop_recording()
        await wait_until(lambda: len(fake.ends) == 2, what="both sessions ended")
        app.request_stop()
        code = await asyncio.wait_for(task, 30)
        await app.api.aclose()
    output = console.getvalue()
    assert code == EXIT_OK, output
    assert output.count("parou de crescer") == 1  # never said about the first recording
    first_id, second_id = fake.sessions
    for session_id in (first_id, second_id):
        accepted = fake.accepted(session_id)
        assert_contiguous_and_final(accepted)
        assert accepted[0]["start"] == 0.0  # each recording has its own timeline
        assert sum(u["duration"] for u in accepted) == pytest.approx(20.0, abs=0.3)
        assert {"session": session_id, "lastSequence": accepted[-1]["sequence"]} in fake.ends
    assert fake.created[1]["obs"]["recordingPath"] == str(second)


async def test_only_a_file_change_of_the_captured_recording_continues_it(tmp_path: Path) -> None:
    app = make_app(
        obs_config("ffmpeg", tmp_path / "work", "ws://127.0.0.1:1"), FakeLiveApi(), Console()
    )
    app.obs_connected = app.obs_synced = True

    def record(state: str, path: Path) -> None:
        state = f"OBS_WEBSOCKET_OUTPUT_{state}"
        data = {"outputActive": True, "outputState": state, "outputPath": str(path)}
        app.on_obs_event("RecordStateChanged", data)

    first, split, second, second_split = (tmp_path / f"{name}.mkv" for name in "abcd")
    try:
        record("STARTED", first)
        generation = app.record_generation
        app.on_obs_event("RecordFileChanged", {"newOutputPath": str(split)})
        assert app._after_run(first, True, generation) == "file_changed"  # same recording

        app.file_changed_to = None
        record("STOPPED", first)
        record("STARTED", second)
        app.on_obs_event("RecordFileChanged", {"newOutputPath": str(second_split)})
        # The first recording's file was still being drained: it ends here, the split file
        # belongs to the second recording.
        assert app._after_run(split, True, generation) == "final"
        assert app._after_run(second, True, app.record_generation) == "file_changed"
    finally:
        await app.api.aclose()
