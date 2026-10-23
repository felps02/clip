"""Heartbeat loop: state payload, stopRequested, offline handling and fatal errors."""

from __future__ import annotations

import asyncio
import ssl

from clipforge_companion.api import AuthError, SessionClosed
from clipforge_companion.heartbeat import Heartbeat
from tests.fake_api import FakeLiveApi

STATE = {
    "obsConnected": True,
    "streaming": True,
    "recording": True,
    "uploading": False,
    "queueLength": 2,
    "lastError": None,
}


async def wait_until(predicate, timeout: float = 5.0) -> None:  # type: ignore[no-untyped-def]
    async def wait() -> None:
        while not predicate():
            await asyncio.sleep(0.01)

    await asyncio.wait_for(wait(), timeout)


async def start_session(fake: FakeLiveApi) -> str:
    async with fake.client() as api:
        return str((await api.create_session())["session"]["id"])


async def test_reports_state_and_honours_stop_requested_once() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    stops: list[int] = []
    async with fake.client() as api:
        heartbeat = Heartbeat(
            api,
            session_id,
            lambda: dict(STATE),
            interval=0.05,
            min_spacing=0.0,
            on_stop_requested=lambda: stops.append(1),
        )
        task = asyncio.create_task(heartbeat.run())
        await wait_until(lambda: len(fake.heartbeats) >= 2)
        assert stops == []
        fake.sessions[session_id].stop_requested = True
        await wait_until(lambda: heartbeat.stop_requested)
        await wait_until(lambda: len(fake.heartbeats) >= 6)
        heartbeat.stop()
        await asyncio.wait_for(task, 2)
    assert stops == [1]  # reported exactly once, although every response repeats it
    assert fake.heartbeats[0]["state"] == STATE
    assert set(fake.heartbeats[0]["state"]) == {
        "obsConnected", "streaming", "recording", "uploading", "queueLength", "lastError",
    }  # fmt: skip
    assert fake.sessions[session_id].status == "live"  # recording: true makes it live
    assert heartbeat.next_sequence == 0


async def test_network_failures_go_offline_and_recover() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    fake.heartbeat_failures = [503, 503]
    changes: list[bool] = []
    async with fake.client() as api:
        heartbeat = Heartbeat(
            api,
            session_id,
            lambda: dict(STATE),
            interval=0.02,
            min_spacing=0.0,
            on_online_change=changes.append,
        )
        task = asyncio.create_task(heartbeat.run())
        await wait_until(lambda: heartbeat.sent >= 1)
        heartbeat.stop()
        await asyncio.wait_for(task, 2)
    assert heartbeat.failures == 2
    assert changes == [False, True]


async def test_terminal_session_is_fatal() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    fake.sessions[session_id].status = "ended"
    fatal: list[Exception] = []
    async with fake.client() as api:
        heartbeat = Heartbeat(
            api, session_id, lambda: dict(STATE), interval=0.02, on_fatal=fatal.append
        )
        await asyncio.wait_for(heartbeat.run(), 2)  # returns by itself
    assert isinstance(fatal[0], SessionClosed)
    assert isinstance(heartbeat.fatal, SessionClosed)


async def test_revoked_token_is_fatal() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    fake.revoked = True
    async with fake.client() as api:
        heartbeat = Heartbeat(api, session_id, lambda: dict(STATE), interval=0.02)
        await asyncio.wait_for(heartbeat.run(), 2)
    assert isinstance(heartbeat.fatal, AuthError)


async def test_poke_sends_immediately() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    async with fake.client() as api:
        heartbeat = Heartbeat(api, session_id, lambda: dict(STATE), interval=30, min_spacing=0.0)
        task = asyncio.create_task(heartbeat.run())
        await wait_until(lambda: len(fake.heartbeats) == 1)
        heartbeat.poke()
        await wait_until(lambda: len(fake.heartbeats) == 2, timeout=2)
        heartbeat.stop()
        await asyncio.wait_for(task, 2)


async def test_errors_nobody_mapped_never_stop_the_heartbeat() -> None:
    """A TLS record that fails to decrypt (antivirus or proxy scanning HTTPS) or any other
    unexpected error only makes the companion offline for a while."""
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    fake.heartbeat_exceptions = [
        ssl.SSLError(1, "[SSL: DECRYPTION_FAILED_OR_BAD_RECORD_MAC] decryption failed"),
        RuntimeError("something nobody expected"),
    ]
    changes: list[bool] = []
    fatal: list[Exception] = []
    async with fake.client() as api:
        heartbeat = Heartbeat(
            api,
            session_id,
            lambda: dict(STATE),
            interval=0.02,
            min_spacing=0.0,
            on_fatal=fatal.append,
            on_online_change=changes.append,
        )
        task = asyncio.create_task(heartbeat.run())
        await wait_until(lambda: heartbeat.sent >= 2)
        assert not task.done()
        fake.sessions[session_id].stop_requested = True  # still listening to the website
        await wait_until(lambda: heartbeat.stop_requested)
        heartbeat.stop()
        await asyncio.wait_for(task, 2)
    assert task.exception() is None
    assert heartbeat.failures == 2 and fatal == []
    assert changes == [False, True]


async def test_a_failing_state_callback_does_not_stop_the_heartbeat() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    calls = 0

    def state() -> dict:
        nonlocal calls
        calls += 1
        if calls == 1:
            raise KeyError("bug")
        return dict(STATE)

    async with fake.client() as api:
        heartbeat = Heartbeat(api, session_id, state, interval=0.02, min_spacing=0.0)
        task = asyncio.create_task(heartbeat.run())
        await wait_until(lambda: heartbeat.sent >= 1)
        heartbeat.stop()
        await asyncio.wait_for(task, 2)
    assert heartbeat.failures == 1 and task.exception() is None


async def test_initial_delay_is_cut_short_by_stop() -> None:
    fake = FakeLiveApi()
    session_id = await start_session(fake)
    async with fake.client() as api:
        heartbeat = Heartbeat(api, session_id, lambda: dict(STATE), interval=0.02)
        task = asyncio.create_task(heartbeat.run(initial_delay=30))
        await asyncio.sleep(0.05)
        heartbeat.stop()
        await asyncio.wait_for(task, 1)
    assert fake.heartbeats == []
