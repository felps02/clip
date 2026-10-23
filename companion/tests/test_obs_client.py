"""The real ObsClient against a fake obs-websocket v5 server."""

from __future__ import annotations

import asyncio
import contextlib
from typing import Any

import pytest

from clipforge_companion.obs import (
    EVENT_SUBSCRIPTION_OUTPUTS,
    OUTPUT_STARTED,
    OUTPUT_STOPPED,
    ObsAuthError,
    ObsClient,
    ObsNotConnected,
    ObsRequestError,
    ObsRequestTimeout,
    describe_error,
)
from tests.fake_obs import CHALLENGE, SALT, FakeObsServer, expected_auth


class Recorder:
    def __init__(self) -> None:
        self.events: list[tuple[str, dict[str, Any]]] = []
        self.connection: list[bool] = []
        self.changed = asyncio.Event()

    def on_event(self, event_type: str, data: dict[str, Any]) -> None:
        self.events.append((event_type, data))
        self.changed.set()

    def on_connection(self, connected: bool) -> None:
        self.connection.append(connected)
        self.changed.set()

    async def wait_for(self, predicate: Any, timeout: float = 5.0) -> None:
        async def wait() -> None:
            while not predicate():
                self.changed.clear()
                with contextlib.suppress(TimeoutError):
                    await asyncio.wait_for(self.changed.wait(), 0.1)

        await asyncio.wait_for(wait(), timeout)


@contextlib.asynccontextmanager
async def running(client: ObsClient):  # type: ignore[no-untyped-def]
    task = asyncio.create_task(client.run())
    try:
        yield task
    finally:
        await client.close()
        if not task.done():
            task.cancel()
        with contextlib.suppress(BaseException):
            await task


def make_client(
    server: FakeObsServer, recorder: Recorder, password: str | None = None, **kwargs: Any
) -> ObsClient:
    kwargs.setdefault("backoff_initial", 0.05)
    kwargs.setdefault("backoff_max", 0.2)
    return ObsClient(
        server.url,
        password,
        on_event=recorder.on_event,
        on_connection_change=recorder.on_connection,
        **kwargs,
    )


async def test_hello_without_authentication_identify_and_requests() -> None:
    recorder = Recorder()
    async with FakeObsServer(record_directory="D:/Gravações") as server:
        server.recording = True
        client = make_client(server, recorder)
        async with running(client):
            assert await client.wait_connected(5)
            identify = server.identify_messages[0]
            assert identify["op"] == 1
            assert identify["d"]["rpcVersion"] == 1
            assert identify["d"]["eventSubscriptions"] == EVENT_SUBSCRIPTION_OUTPUTS == 64
            assert "authentication" not in identify["d"]
            assert client.hello is not None and not client.hello.requires_auth
            assert client.hello.obs_websocket_version == "5.5.4"

            version = await client.get_version()
            assert version["obsVersion"] == "31.0.2"
            status = await client.get_record_status()
            assert status["outputActive"] is True
            assert await client.get_record_directory() == "D:/Gravações"
            assert server.requests == ["GetVersion", "GetRecordStatus", "GetRecordDirectory"]
        assert recorder.connection == [True, False]


async def test_hello_with_authentication_sends_documented_auth_string() -> None:
    recorder = Recorder()
    async with FakeObsServer(password="supersecretpassword") as server:
        client = make_client(server, recorder, password="supersecretpassword")
        async with running(client):
            assert await client.wait_connected(5)
            sent = server.identify_messages[0]["d"]["authentication"]
            assert sent == expected_auth("supersecretpassword", SALT, CHALLENGE)
            assert sent == "1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4="
            assert client.hello is not None and client.hello.requires_auth
            assert server.rejected_auth == 0


async def test_wrong_password_is_fatal() -> None:
    async with FakeObsServer(password="right") as server:
        client = ObsClient(server.url, "wrong", backoff_initial=0.05)
        with pytest.raises(ObsAuthError, match="incorreta"):
            await asyncio.wait_for(client.run(), 5)
        assert server.rejected_auth == 1
        assert server.connection_count == 1  # no reconnect storm with a wrong password


async def test_missing_password_is_fatal_without_identify() -> None:
    async with FakeObsServer(password="secret") as server:
        client = ObsClient(server.url, None, backoff_initial=0.05)
        with pytest.raises(ObsAuthError, match="exige senha"):
            await asyncio.wait_for(client.run(), 5)
        assert server.identify_messages == []


async def test_record_state_events_are_dispatched_in_order() -> None:
    recorder = Recorder()
    async with FakeObsServer() as server:
        client = make_client(server, recorder)
        async with running(client):
            assert await client.wait_connected(5)
            await server.start_recording("C:/OBS/2026-10-23 20-00-00.mkv")
            await server.stop_recording()
            await recorder.wait_for(lambda: len(recorder.events) >= 3)
    states = [(name, data["outputState"]) for name, data in recorder.events]
    assert states == [
        ("RecordStateChanged", "OBS_WEBSOCKET_OUTPUT_STARTING"),
        ("RecordStateChanged", OUTPUT_STARTED),
        ("RecordStateChanged", OUTPUT_STOPPED),
    ]
    assert recorder.events[1][1]["outputPath"] == "C:/OBS/2026-10-23 20-00-00.mkv"
    assert recorder.events[2][1]["outputPath"] == "C:/OBS/2026-10-23 20-00-00.mkv"


async def test_request_error_and_timeout() -> None:
    recorder = Recorder()
    async with FakeObsServer(ignore_requests=("GetStreamStatus",)) as server:
        server.recording = True
        client = make_client(server, recorder, request_timeout=0.3)
        async with running(client):
            assert await client.wait_connected(5)
            with pytest.raises(ObsRequestError) as info:
                await client.start_record()
            assert info.value.code == 500
            with pytest.raises(ObsRequestTimeout):
                await client.get_stream_status()
            # The connection is still usable after a timeout.
            assert (await client.get_record_status())["outputActive"] is True


async def test_request_without_connection_raises() -> None:
    client = ObsClient("ws://127.0.0.1:9")
    with pytest.raises(ObsNotConnected):
        await client.get_version()


async def test_reconnects_after_server_closes_the_socket() -> None:
    recorder = Recorder()
    async with FakeObsServer() as server:
        client = make_client(server, recorder)
        async with running(client):
            assert await client.wait_connected(5)
            await server.drop_connections()
            await recorder.wait_for(lambda: recorder.connection.count(True) >= 2)
            assert client.connected
            assert client.connect_count == 2
            assert server.identified_count == 2
            # Requests work on the new connection.
            assert (await client.get_version())["obsWebSocketVersion"] == "5.5.4"
    assert recorder.connection[:3] == [True, False, True]


async def test_backoff_grows_while_obs_is_down_and_reconnects_when_back() -> None:
    recorder = Recorder()
    server = FakeObsServer()
    await server.start()
    port = server.port
    client = make_client(server, recorder, backoff_initial=0.05, backoff_max=0.4)
    async with running(client):
        assert await client.wait_connected(5)
        await server.stop()  # OBS closed
        # While OBS is down the port drops every connection (fast failures; a refused
        # connection on Windows takes ~2 s to fail, which only slows the test down).
        dead = await asyncio.start_server(lambda reader, writer: writer.close(), "127.0.0.1", port)
        try:
            await recorder.wait_for(lambda: len(client.retry_delays) >= 5, timeout=10)
        finally:
            dead.close()
            await dead.wait_closed()
        # First retry right after an established connection, then doubling up to the cap.
        assert client.retry_delays[:5] == [0.05, 0.1, 0.2, 0.4, 0.4]
        assert not client.connected
        restarted = FakeObsServer(port=port)
        await restarted.start()
        try:
            await recorder.wait_for(lambda: client.connected, timeout=10)
            assert client.connect_count == 2
            assert restarted.identified_count == 1
        finally:
            await client.close()
            await restarted.stop()


def test_connection_errors_are_described_in_portuguese() -> None:
    assert "conexão recusada" in describe_error(ConnectionRefusedError())
    assert "tempo esgotado" in describe_error(TimeoutError())


def test_default_backoff_is_one_to_thirty_seconds() -> None:
    client = ObsClient("ws://127.0.0.1:4455")
    assert client._backoff_initial == 1.0
    assert client._backoff_max == 30.0
