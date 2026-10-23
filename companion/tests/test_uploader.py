"""Persistent in-order upload queue against a fake API (httpx.MockTransport)."""

from __future__ import annotations

import asyncio
import json
import ssl
from pathlib import Path

import pytest

from clipforge_companion.api import AuthError, SegmentFileError, SessionClosed
from clipforge_companion.segmenter import Segment
from clipforge_companion.uploader import QUEUE_FILE, SessionStore, Uploader
from tests.fake_api import BASE_URL, FakeLiveApi

MKV_MAGIC = bytes([0x1A, 0x45, 0xDF, 0xA3])


class FakeSleep:
    def __init__(self) -> None:
        self.delays: list[float] = []

    async def __call__(self, delay: float) -> None:
        self.delays.append(delay)
        await asyncio.sleep(0)


def make_segments(directory: Path, count: int, *, seconds: float = 60.0) -> list[Segment]:
    run_dir = directory / "run-01"
    run_dir.mkdir(parents=True, exist_ok=True)
    segments = []
    for sequence in range(count):
        path = run_dir / f"seg_{sequence:05d}.mkv"
        path.write_bytes(MKV_MAGIC + bytes([sequence]) * 1000)
        segments.append(
            Segment(sequence, path, start=sequence * seconds, end=(sequence + 1) * seconds)
        )
    return segments


async def new_store(fake: FakeLiveApi, work_dir: Path) -> SessionStore:
    async with fake.client() as api:
        session_id = (await api.create_session(segment_seconds=60))["session"]["id"]
    return SessionStore.create(
        work_dir, session_id, mode="file", api_url=BASE_URL, device_id="dev-1", segment_seconds=60
    )


async def run_uploader(uploader: Uploader, timeout: float = 10.0) -> None:
    uploader.close()
    await asyncio.wait_for(uploader.run(), timeout)


async def test_uploads_in_order_with_final_flag_and_deletes_files(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 4)
    async with fake.client() as api:
        uploader = Uploader(api, store)
        for segment in segments:
            uploader.enqueue(segment, final=segment.sequence == 3)
        await run_uploader(uploader)
    accepted = fake.accepted(store.session_id)
    assert [u["sequence"] for u in accepted] == [0, 1, 2, 3]
    assert [u["final"] for u in accepted] == [False, False, False, True]
    assert [u["start"] for u in accepted] == [0.0, 60.0, 120.0, 180.0]
    assert all(u["duration"] == 60.0 for u in accepted)
    assert accepted[2]["file"]["content"] == MKV_MAGIC + bytes([2]) * 1000
    assert not any(s.path.exists() for s in segments)  # deleted after upload
    assert store.items == [] and store.uploaded == 4 and store.last_acked_sequence == 3


async def test_keep_segments_keeps_files(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    async with fake.client() as api:
        uploader = Uploader(api, store, keep_segments=True)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert all(s.path.exists() for s in segments)
    assert [u["file"]["content"] for u in fake.accepted()] == [
        s.path.read_bytes() for s in segments
    ]


async def test_retries_503_with_exponential_backoff_then_succeeds(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    fake.segment_failures = [503, 503, 503, 429]
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    sleep = FakeSleep()
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=sleep, backoff_initial=2, backoff_max=60)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert sleep.delays == [2, 4, 8, 16]
    assert [u["sequence"] for u in fake.uploads] == [0, 0, 0, 0, 0, 1]
    assert [u["sequence"] for u in fake.accepted()] == [0, 1]
    assert uploader.last_error is None and uploader.online


async def test_backoff_is_capped_at_sixty_seconds(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    fake.segment_failures = [500] * 8
    store = await new_store(fake, tmp_path)
    sleep = FakeSleep()
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=sleep)
        uploader.enqueue(make_segments(store.directory, 1)[0], final=True)
        await run_uploader(uploader)
    assert sleep.delays == [2, 4, 8, 16, 32, 60, 60, 60]


async def test_duplicate_200_is_treated_as_success(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    async with fake.client() as api:
        # The API already has sequence 0 (e.g. uploaded before a crash, ack not persisted).
        await api.upload_segment(
            store.session_id, sequence=0, start_offset=0, duration=60, path=segments[0].path
        )
        events: list[tuple[str, dict]] = []
        uploader = Uploader(api, store, on_status=lambda e, d: events.append((e, d)))
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert [u["status"] for u in fake.uploads] == [201, 200, 201]
    assert store.uploaded == 2 and store.items == []
    uploaded = [d for e, d in events if e == "uploaded"]
    assert [d["duplicate"] for d in uploaded] == [True, False]
    assert not segments[0].path.exists()


@pytest.mark.parametrize(
    ("status", "error"), [(401, AuthError), (403, AuthError), (409, SessionClosed)]
)
async def test_stops_on_auth_and_conflict_errors(tmp_path: Path, status: int, error: type) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 3)
    fake.segment_failures = [status]
    sleep = FakeSleep()
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=sleep)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await asyncio.wait_for(uploader.run(), 5)  # returns without close(): fatal
    assert isinstance(uploader.fatal, error)
    assert uploader.last_error and str(status) in uploader.last_error
    assert sleep.delays == []  # no retries
    assert [u["sequence"] for u in fake.uploads] == [0]
    assert [i.sequence for i in store.items] == [0, 1, 2]  # nothing lost
    assert all(s.path.exists() for s in segments)


async def test_session_revoked_401_from_fake_api(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    fake.revoked = True
    async with fake.client() as api:
        uploader = Uploader(api, store)
        uploader.enqueue(make_segments(store.directory, 1)[0], final=True)
        await asyncio.wait_for(uploader.run(), 5)
    assert isinstance(uploader.fatal, AuthError)


async def test_rejected_segment_is_skipped(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    fake.segment_failures = [422]
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    async with fake.client() as api:
        uploader = Uploader(api, store)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert [u["sequence"] for u in fake.accepted()] == [1]
    assert store.data["skipped"] == 1
    assert uploader.fatal is None


async def test_queue_is_persisted_and_resumed_after_restart(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 3)
    fake.always_fail_segments = 503
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=FakeSleep())
        for segment in segments:
            uploader.enqueue(segment, final=segment.sequence == 2)
        task = asyncio.create_task(uploader.run())
        while len(fake.uploads) < 2:
            await asyncio.sleep(0.01)
        task.cancel()  # the companion crashes / is killed mid-retry
        with pytest.raises(asyncio.CancelledError):
            await task

    saved = json.loads((store.directory / QUEUE_FILE).read_text(encoding="utf-8"))
    assert [i["sequence"] for i in saved["items"]] == [0, 1, 2]
    assert saved["items"][2]["final"] is True
    assert saved["items"][0]["file"] == "run-01/seg_00000.mkv"

    # Next run: reload from disk with a fresh uploader; the API works again.
    fake.always_fail_segments = None
    [reloaded] = SessionStore.find_unfinished(tmp_path)
    assert reloaded.session_id == store.session_id
    async with fake.client() as api:
        uploader = Uploader(api, reloaded)
        await run_uploader(uploader)
    accepted = fake.accepted(store.session_id)
    assert [u["sequence"] for u in accepted] == [0, 1, 2]
    assert [u["final"] for u in accepted] == [False, False, True]
    assert SessionStore.load(store.directory).items == []


async def test_missing_segment_file_is_skipped(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    segments[0].path.unlink()
    async with fake.client() as api:
        uploader = Uploader(api, store)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert [u["sequence"] for u in fake.accepted()] == [1]


def test_find_unfinished_ignores_ended_and_corrupt_sessions(tmp_path: Path) -> None:
    ended = SessionStore.create(
        tmp_path, "a", mode="file", api_url=BASE_URL, device_id=None, segment_seconds=60
    )
    ended.mark_ended("ended")
    SessionStore.create(
        tmp_path, "b", mode="obs", api_url=BASE_URL, device_id=None, segment_seconds=60
    )
    (tmp_path / "c").mkdir()
    (tmp_path / "c" / QUEUE_FILE).write_text("{not json", encoding="utf-8")
    assert [s.session_id for s in SessionStore.find_unfinished(tmp_path)] == ["b"]


@pytest.mark.parametrize(
    "error",
    [
        ssl.SSLError(1, "[SSL: DECRYPTION_FAILED_OR_BAD_RECORD_MAC] decryption failed"),
        ConnectionResetError(10054, "connection reset by peer"),
        RuntimeError("nobody mapped this"),
    ],
)
async def test_network_and_unexpected_errors_never_drop_a_segment(
    tmp_path: Path, error: Exception
) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    fake.segment_exceptions = [error, error]
    sleep = FakeSleep()
    events: list[tuple[str, dict]] = []
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=sleep, on_status=lambda e, d: events.append((e, d)))
        for segment in segments:
            uploader.enqueue(segment, final=segment.sequence == 1)
        await run_uploader(uploader)
    assert [u["sequence"] for u in fake.accepted()] == [0, 1]  # retried, then sent
    assert [u["final"] for u in fake.accepted()] == [False, True]
    assert store.data["skipped"] == 0 and store.uploaded == 2
    assert sleep.delays == [2, 4]
    assert [e for e, _ in events].count("retry") == 2
    assert not any(e in ("rejected", "skipped") for e, _ in events)


def lock_file(monkeypatch: pytest.MonkeyPatch, path: Path, times: int) -> list[int]:
    """Opening ``path`` fails with a sharing violation the first ``times`` times."""
    real_open = Path.open
    attempts: list[int] = []

    def open_(self: Path, *args: object, **kwargs: object):  # type: ignore[no-untyped-def]
        if self == path:
            attempts.append(1)
            if len(attempts) <= times:
                raise PermissionError(13, "The process cannot access the file", str(self))
        return real_open(self, *args, **kwargs)  # type: ignore[arg-type]

    monkeypatch.setattr(Path, "open", open_)
    return attempts


async def test_a_briefly_locked_segment_is_retried(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    [segment] = make_segments(store.directory, 1)
    lock_file(monkeypatch, segment.path, times=2)  # antivirus scanning the new file
    sleep = FakeSleep()
    async with fake.client() as api:
        uploader = Uploader(api, store, sleep=sleep)
        uploader.enqueue(segment, final=True)
        await run_uploader(uploader)
    assert [u["sequence"] for u in fake.accepted()] == [0]
    assert sleep.delays == [2, 4] and store.data["skipped"] == 0


async def test_a_segment_that_stays_locked_is_skipped_but_kept_on_disk(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    attempts = lock_file(monkeypatch, segments[0].path, times=100)
    sleep = FakeSleep()
    events: list[tuple[str, dict]] = []
    async with fake.client() as api:
        uploader = Uploader(
            api, store, sleep=sleep, local_retries=3, on_status=lambda e, d: events.append((e, d))
        )
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert len(attempts) == 4  # first try + 3 retries
    assert [u["sequence"] for u in fake.accepted()] == [1]  # the queue moved on
    assert store.data["skipped"] == 1
    assert segments[0].path.exists()  # never deleted: it was not sent
    assert [d["item"].sequence for e, d in events if e == "skipped"] == [0]


async def test_a_segment_that_shrinks_while_sent_is_skipped(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    store = await new_store(fake, tmp_path)
    segments = make_segments(store.directory, 2)
    async with fake.client() as api:
        real_upload = api.upload_segment

        async def upload(session_id: str, **kwargs: object) -> dict:
            if kwargs["sequence"] == 0:
                raise SegmentFileError(segments[0].path, "shrunk", "encolheu durante o envio")
            return await real_upload(session_id, **kwargs)  # type: ignore[arg-type]

        api.upload_segment = upload  # type: ignore[method-assign]
        sleep = FakeSleep()
        uploader = Uploader(api, store, sleep=sleep)
        for segment in segments:
            uploader.enqueue(segment, final=False)
        await run_uploader(uploader)
    assert sleep.delays == []  # not retried: the file is broken for good
    assert [u["sequence"] for u in fake.accepted()] == [1]
    assert store.data["skipped"] == 1
