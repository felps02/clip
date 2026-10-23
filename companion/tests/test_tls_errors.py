"""Real TLS: a response record that fails to decrypt must be a retryable network error.

Below httpx, anyio re-raises such ``ssl.SSLError`` as is (it is neither an EOF nor mapped by
httpcore). Unmapped, it used to end the heartbeat task silently (the session then showed
offline, went to error after the server's timeout and every upload got 409) and made the
uploader drop and delete the segment.
"""

from __future__ import annotations

import asyncio
import ssl
from pathlib import Path
from typing import Any

import httpx
import pytest

from clipforge_companion.api import CompanionApi, RetryableError
from clipforge_companion.heartbeat import Heartbeat
from clipforge_companion.uploader import SessionStore, Uploader
from tests.fake_api import TOKEN
from tests.test_uploader import FakeSleep, make_segments
from tests.tls_server import CorruptingTlsServer, make_certificate


@pytest.fixture(scope="module")
def certificate(tmp_path_factory: pytest.TempPathFactory) -> tuple[Path, Path]:
    made = make_certificate(tmp_path_factory.mktemp("tls"))
    if made is None:
        pytest.skip("openssl is needed to create a test certificate")
    return made


def answer(method: str, path: str, body: bytes) -> tuple[int, dict[str, Any]]:
    if path.endswith("/heartbeat"):
        return 200, {"status": "live", "stopRequested": False, "nextSequence": 0}
    if path.endswith("/segments"):
        return 201, {"segment": {"id": "seg"}, "duplicate": False}
    return 404, {"code": "not_found"}


def https_client(server: CorruptingTlsServer, cert: Path) -> CompanionApi:
    context = ssl.create_default_context(cafile=str(cert))
    return CompanionApi(server.url, TOKEN, transport=httpx.AsyncHTTPTransport(verify=context))


async def test_a_record_that_fails_to_decrypt_is_retryable(certificate: tuple[Path, Path]) -> None:
    cert, key = certificate
    async with (
        CorruptingTlsServer(cert, key, answer, corrupt=1) as server,
        https_client(server, cert) as api,
    ):
        with pytest.raises(RetryableError, match="falha de rede") as info:
            await api.heartbeat("s1", {"recording": True})
        assert isinstance(info.value.__cause__, ssl.SSLError)  # raised as is below httpx
        again = await api.heartbeat("s1", {"recording": True})  # a new connection works
    assert again["status"] == "live" and server.corrupted == 1


@pytest.mark.async_timeout(30)
async def test_the_heartbeat_keeps_beating_after_a_corrupted_record(
    certificate: tuple[Path, Path],
) -> None:
    cert, key = certificate
    changes: list[bool] = []
    fatal: list[Exception] = []
    async with (
        CorruptingTlsServer(cert, key, answer, corrupt=1) as server,
        https_client(server, cert) as api,
    ):
        heartbeat = Heartbeat(
            api,
            "s1",
            lambda: {"recording": True},
            interval=0.1,
            min_spacing=0.0,
            on_fatal=fatal.append,
            on_online_change=changes.append,
        )
        task = asyncio.create_task(heartbeat.run())
        while heartbeat.sent < 3:
            assert not task.done(), task
            await asyncio.sleep(0.02)
        heartbeat.stop()
        await asyncio.wait_for(task, 5)
    assert heartbeat.failures == 1 and fatal == []
    assert changes == [False, True]


@pytest.mark.async_timeout(30)
async def test_a_segment_is_retried_not_dropped_after_a_corrupted_record(
    certificate: tuple[Path, Path], tmp_path: Path
) -> None:
    cert, key = certificate
    store = SessionStore.create(
        tmp_path, "s1", mode="file", api_url="https://127.0.0.1", device_id=None, segment_seconds=60
    )
    segments = make_segments(store.directory, 2)
    events: list[str] = []
    async with (
        CorruptingTlsServer(cert, key, answer, corrupt=1) as server,
        https_client(server, cert) as api,
    ):
        uploader = Uploader(api, store, sleep=FakeSleep(), on_status=lambda e, d: events.append(e))
        for segment in segments:
            uploader.enqueue(segment, final=segment.sequence == 1)
        uploader.close()
        await asyncio.wait_for(uploader.run(), 20)
    posts = [path for method, path in server.requests if method == "POST"]
    assert posts == ["/api/live/sessions/s1/segments"] * 3  # segment 0 sent twice
    assert store.uploaded == 2 and store.data["skipped"] == 0
    assert "retry" in events and "rejected" not in events and "skipped" not in events
    assert not any(segment.path.exists() for segment in segments)  # deleted after success
