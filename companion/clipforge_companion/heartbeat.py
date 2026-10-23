"""Periodic heartbeat of a live session.

Every ``interval`` seconds (10 s by default, or right away after :meth:`Heartbeat.poke`) the
companion reports ``{obsConnected, streaming, recording, uploading, queueLength, lastError}``.
The response says whether the user asked to stop the session on the website
(``stopRequested``); that is reported once through ``on_stop_requested``.

Network problems never stop the heartbeat (the companion is just "offline" for a while), and
neither does any other unexpected exception: only :meth:`Heartbeat.stop` and 401/403/409 end
:meth:`Heartbeat.run`. The fatal ones are reported through ``on_fatal``.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import Callable
from typing import Any

from clipforge_companion.api import ApiError, AuthError, CompanionApi, RetryableError, SessionClosed

log = logging.getLogger(__name__)


class Heartbeat:
    def __init__(
        self,
        api: CompanionApi,
        session_id: str,
        state: Callable[[], dict[str, Any]],
        *,
        interval: float = 10.0,
        min_spacing: float = 1.0,
        on_stop_requested: Callable[[], None] | None = None,
        on_fatal: Callable[[ApiError], None] | None = None,
        on_online_change: Callable[[bool], None] | None = None,
    ) -> None:
        self.api = api
        self.session_id = session_id
        self._state = state
        self.interval = interval
        self.min_spacing = min_spacing
        self._on_stop_requested = on_stop_requested
        self._on_fatal = on_fatal
        self._on_online_change = on_online_change
        self._poke = asyncio.Event()
        self._stopped = asyncio.Event()
        self.stop_requested = False
        self.last_response: dict[str, Any] | None = None
        self.last_state: dict[str, Any] | None = None
        self.online: bool | None = None
        self.fatal: ApiError | None = None
        self.sent = 0
        self.failures = 0

    @property
    def next_sequence(self) -> int | None:
        value = (self.last_response or {}).get("nextSequence")
        return value if isinstance(value, int) else None

    def poke(self) -> None:
        """Send the next heartbeat as soon as possible (state changed)."""
        self._poke.set()

    def stop(self) -> None:
        self._stopped.set()
        self._poke.set()

    @property
    def stopped(self) -> bool:
        return self._stopped.is_set()

    async def beat_once(self) -> dict[str, Any] | None:
        """Sends one heartbeat. Raises :class:`AuthError` / :class:`SessionClosed`; any other
        failure counts as offline and returns None."""
        state = self._state()
        self.last_state = state
        try:
            response = await self.api.heartbeat(self.session_id, state)
        except (AuthError, SessionClosed) as exc:
            self.fatal = exc
            if self._on_fatal is not None:
                self._on_fatal(exc)
            raise
        except RetryableError as exc:
            self.failures += 1
            self._set_online(False)
            log.info("heartbeat failed: %s", exc.message)
            return None
        except ApiError as exc:
            self.failures += 1
            log.warning("heartbeat rejected: %s", exc.message)
            return None
        except Exception as exc:  # an error nobody mapped: still just "offline" for a while
            self.failures += 1
            self._set_online(False)
            log.warning("heartbeat failed: %s: %s", exc.__class__.__name__, exc)
            return None
        self.sent += 1
        self._set_online(True)
        self.last_response = response
        if response.get("stopRequested") and not self.stop_requested:
            self.stop_requested = True
            if self._on_stop_requested is not None:
                self._on_stop_requested()
        return response

    async def run(self, *, initial_delay: float = 0.0) -> None:
        """Beats until :meth:`stop` or a fatal error (after ``initial_delay`` seconds)."""
        if initial_delay > 0:
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(self._stopped.wait(), initial_delay)
        while not self._stopped.is_set():
            self._poke.clear()
            try:
                await self.beat_once()
            except (AuthError, SessionClosed):
                return
            except Exception:  # e.g. the state callback failed: report it and keep beating
                self.failures += 1
                log.exception("heartbeat failed unexpectedly")
            if self._stopped.is_set():
                return
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(self._stopped.wait(), self.min_spacing)
            if self._stopped.is_set():
                return
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(
                    self._poke.wait(), max(0.0, self.interval - self.min_spacing)
                )

    def _set_online(self, online: bool) -> None:
        if self.online == online:
            return
        self.online = online
        if self._on_online_change is not None:
            try:
                self._on_online_change(online)
            except Exception:
                log.exception("online callback failed")
