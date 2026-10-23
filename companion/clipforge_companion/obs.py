"""Minimal asyncio client for obs-websocket v5 (bundled with OBS Studio 28+).

Protocol summary (obs-websocket ``docs/generated/protocol.md``):

* the server sends ``Hello`` (op 0), with ``authentication: {challenge, salt}`` when a password
  is configured;
* the client answers ``Identify`` (op 1) with ``rpcVersion``, the authentication string and the
  event subscription bitmask, and waits for ``Identified`` (op 2);
* requests are op 6 and their responses op 7, correlated by ``requestId``;
* events are op 5.

The client reconnects with exponential backoff (1 s doubling up to 30 s) whenever the socket
closes, until :meth:`ObsClient.close` is called. Authentication problems are fatal because
retrying with the same password cannot succeed.
"""

from __future__ import annotations

import asyncio
import base64
import contextlib
import hashlib
import json
import logging
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from enum import IntEnum
from typing import Any

from websockets.asyncio.client import ClientConnection, connect
from websockets.exceptions import ConnectionClosed, InvalidHandshake, InvalidURI
from websockets.typing import Subprotocol

log = logging.getLogger(__name__)

SUBPROTOCOL = Subprotocol("obswebsocket.json")
RPC_VERSION = 1
# EventSubscription.Outputs: StreamStateChanged, RecordStateChanged, RecordFileChanged, ...
EVENT_SUBSCRIPTION_OUTPUTS = 1 << 6

OUTPUT_STARTING = "OBS_WEBSOCKET_OUTPUT_STARTING"
OUTPUT_STARTED = "OBS_WEBSOCKET_OUTPUT_STARTED"
OUTPUT_STOPPING = "OBS_WEBSOCKET_OUTPUT_STOPPING"
OUTPUT_STOPPED = "OBS_WEBSOCKET_OUTPUT_STOPPED"
OUTPUT_RECONNECTING = "OBS_WEBSOCKET_OUTPUT_RECONNECTING"
OUTPUT_RECONNECTED = "OBS_WEBSOCKET_OUTPUT_RECONNECTED"
OUTPUT_PAUSED = "OBS_WEBSOCKET_OUTPUT_PAUSED"
OUTPUT_RESUMED = "OBS_WEBSOCKET_OUTPUT_RESUMED"


class OpCode(IntEnum):
    HELLO = 0
    IDENTIFY = 1
    IDENTIFIED = 2
    REIDENTIFY = 3
    EVENT = 5
    REQUEST = 6
    REQUEST_RESPONSE = 7
    REQUEST_BATCH = 8
    REQUEST_BATCH_RESPONSE = 9


class CloseCode(IntEnum):
    """WebSocket close codes used by obs-websocket."""

    UNKNOWN_REASON = 4000
    MESSAGE_DECODE_ERROR = 4002
    MISSING_DATA_FIELD = 4003
    INVALID_DATA_FIELD_TYPE = 4004
    INVALID_DATA_FIELD_VALUE = 4005
    UNKNOWN_OP_CODE = 4006
    NOT_IDENTIFIED = 4007
    ALREADY_IDENTIFIED = 4008
    AUTHENTICATION_FAILED = 4009
    UNSUPPORTED_RPC_VERSION = 4010
    SESSION_INVALIDATED = 4011
    UNSUPPORTED_FEATURE = 4012


def compute_authentication(password: str, salt: str, challenge: str) -> str:
    """``base64(sha256(base64(sha256(password + salt)) + challenge))`` as documented."""
    secret = base64.b64encode(hashlib.sha256((password + salt).encode("utf-8")).digest())
    auth = hashlib.sha256(secret + challenge.encode("utf-8")).digest()
    return base64.b64encode(auth).decode("ascii")


class ObsError(Exception):
    """Base class of OBS errors (messages are user-facing, pt-BR)."""


class ObsFatalError(ObsError):
    """Retrying cannot help (wrong password, unsupported protocol version, bad URL)."""


class ObsAuthError(ObsFatalError):
    """Missing or wrong obs-websocket password."""


class ObsProtocolError(ObsError):
    """Unexpected message from the server."""


class ObsNotConnected(ObsError):
    """No identified connection to OBS right now."""


class ObsRequestTimeout(ObsError):
    """OBS did not answer a request in time."""


class ObsRequestError(ObsError):
    """OBS answered a request with ``requestStatus.result = false``."""

    def __init__(self, request_type: str, code: int | None, comment: str | None) -> None:
        detail = f": {comment}" if comment else ""
        super().__init__(f"O OBS recusou {request_type} (código {code}){detail}")
        self.request_type = request_type
        self.code = code
        self.comment = comment


@dataclass(frozen=True, slots=True)
class ObsHello:
    obs_websocket_version: str | None
    rpc_version: int
    challenge: str | None = None
    salt: str | None = None
    obs_studio_version: str | None = None

    @property
    def requires_auth(self) -> bool:
        return self.challenge is not None and self.salt is not None


EventCallback = Callable[[str, dict[str, Any]], None]
ConnectionCallback = Callable[[bool], None]
RetryCallback = Callable[[float, str], None]


def _close_code(exc: ConnectionClosed) -> int | None:
    rcvd = getattr(exc, "rcvd", None)
    if rcvd is not None:
        return rcvd.code
    return None


def describe_error(exc: BaseException) -> str:
    """Short pt-BR description of a connection failure."""
    if isinstance(exc, ConnectionRefusedError):
        return "conexão recusada (o OBS está aberto e o servidor WebSocket está ativado?)"
    if isinstance(exc, TimeoutError):
        return "tempo esgotado ao conectar"
    if isinstance(exc, ConnectionClosed):
        code = _close_code(exc)
        return f"conexão encerrada pelo OBS (código {code})" if code else "conexão encerrada"
    if isinstance(exc, InvalidHandshake):
        return f"o endereço não parece ser um obs-websocket ({exc})"
    if isinstance(exc, OSError):
        return f"erro de rede ({exc.strerror or exc})"
    return str(exc) or exc.__class__.__name__


class ObsClient:
    """obs-websocket v5 client with request correlation, event dispatch and reconnection.

    ``on_event(event_type, event_data)`` and ``on_connection_change(connected)`` are called from
    the receive loop and must not block (schedule a task for slow work).
    """

    def __init__(
        self,
        url: str,
        password: str | None = None,
        *,
        on_event: EventCallback | None = None,
        on_connection_change: ConnectionCallback | None = None,
        on_retry: RetryCallback | None = None,
        event_subscriptions: int = EVENT_SUBSCRIPTION_OUTPUTS,
        request_timeout: float = 10.0,
        handshake_timeout: float = 10.0,
        backoff_initial: float = 1.0,
        backoff_max: float = 30.0,
    ) -> None:
        self.url = url
        self._password = password
        self._on_event = on_event
        self._on_connection_change = on_connection_change
        self._on_retry = on_retry
        self._event_subscriptions = event_subscriptions
        self._request_timeout = request_timeout
        self._handshake_timeout = handshake_timeout
        self._backoff_initial = backoff_initial
        self._backoff_max = backoff_max
        self._ws: ClientConnection | None = None
        self._identified = asyncio.Event()
        self._close_requested = asyncio.Event()
        self._pending: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._session_identified = False
        self.hello: ObsHello | None = None
        self.connect_count = 0
        self.last_error: str | None = None
        self.retry_delays: list[float] = []

    @property
    def connected(self) -> bool:
        return self._ws is not None and self._identified.is_set()

    async def wait_connected(self, timeout: float | None = None) -> bool:
        try:
            await asyncio.wait_for(self._identified.wait(), timeout)
        except TimeoutError:
            return False
        return True

    async def run(self) -> None:
        """Connects and keeps reconnecting until :meth:`close`. Raises :class:`ObsFatalError`."""
        delay = self._backoff_initial
        while not self._close_requested.is_set():
            self._session_identified = False
            try:
                await self._session()
                reason = "conexão encerrada pelo OBS"
            except ObsFatalError:
                raise
            except InvalidURI as exc:
                raise ObsFatalError(f"Endereço do obs-websocket inválido: {self.url}") from exc
            except (OSError, TimeoutError, ConnectionClosed, InvalidHandshake, ObsError) as exc:
                reason = describe_error(exc)
            if self._close_requested.is_set():
                break
            if self._session_identified:
                delay = self._backoff_initial
            self.last_error = reason
            self.retry_delays.append(delay)
            log.info("OBS: %s; nova tentativa em %.0f s", reason, delay)
            if self._on_retry is not None:
                try:
                    self._on_retry(delay, reason)
                except Exception:
                    log.exception("on_retry callback failed")
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(self._close_requested.wait(), delay)
            delay = min(delay * 2, self._backoff_max)

    async def close(self) -> None:
        self._close_requested.set()
        ws = self._ws
        if ws is not None:
            with contextlib.suppress(Exception):
                await ws.close()

    async def request(
        self, request_type: str, data: dict[str, Any] | None = None, *, timeout: float | None = None
    ) -> dict[str, Any]:
        """Sends a request (op 6) and returns ``responseData`` of the matching op 7."""
        ws = self._ws
        if ws is None or not self._identified.is_set():
            raise ObsNotConnected("OBS não conectado")
        request_id = uuid.uuid4().hex
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future
        payload: dict[str, Any] = {"requestType": request_type, "requestId": request_id}
        if data:
            payload["requestData"] = data
        try:
            await ws.send(json.dumps({"op": int(OpCode.REQUEST), "d": payload}))
            response = await asyncio.wait_for(future, timeout or self._request_timeout)
        except TimeoutError as exc:
            raise ObsRequestTimeout(f"O OBS não respondeu a {request_type} a tempo") from exc
        except ConnectionClosed as exc:
            raise ObsNotConnected("conexão com o OBS perdida") from exc
        finally:
            self._pending.pop(request_id, None)
        status = response.get("requestStatus") or {}
        if not status.get("result"):
            raise ObsRequestError(request_type, status.get("code"), status.get("comment"))
        return response.get("responseData") or {}

    async def get_version(self) -> dict[str, Any]:
        return await self.request("GetVersion")

    async def get_record_status(self) -> dict[str, Any]:
        return await self.request("GetRecordStatus")

    async def get_stream_status(self) -> dict[str, Any]:
        return await self.request("GetStreamStatus")

    async def get_record_directory(self) -> str:
        data = await self.request("GetRecordDirectory")
        return str(data.get("recordDirectory") or "")

    async def start_record(self) -> None:
        await self.request("StartRecord")

    # -- internals ---------------------------------------------------------------------------

    async def _session(self) -> None:
        async with connect(
            self.url,
            subprotocols=[SUBPROTOCOL],
            open_timeout=self._handshake_timeout,
            close_timeout=2,
            max_size=16 * 1024 * 1024,
            compression=None,
            proxy=None,
        ) as ws:
            hello = self._parse_hello(await self._recv(ws))
            identify: dict[str, Any] = {
                "rpcVersion": RPC_VERSION,
                "eventSubscriptions": self._event_subscriptions,
            }
            if hello.requires_auth:
                if not self._password:
                    raise ObsAuthError(
                        "O obs-websocket exige senha: informe --obs-password ou defina "
                        "OBS_WEBSOCKET_PASSWORD."
                    )
                identify["authentication"] = compute_authentication(
                    self._password, hello.salt or "", hello.challenge or ""
                )
            await ws.send(json.dumps({"op": int(OpCode.IDENTIFY), "d": identify}))
            try:
                message = await self._recv(ws)
            except ConnectionClosed as exc:
                code = _close_code(exc)
                if code == CloseCode.AUTHENTICATION_FAILED:
                    raise ObsAuthError(
                        "Senha do obs-websocket incorreta. Confira em OBS > Ferramentas > "
                        "Configurações do servidor WebSocket."
                    ) from exc
                if code == CloseCode.UNSUPPORTED_RPC_VERSION:
                    raise ObsFatalError(
                        "Versão do obs-websocket incompatível: use o OBS 28 ou mais recente."
                    ) from exc
                raise
            if message.get("op") != OpCode.IDENTIFIED:
                raise ObsProtocolError(f"resposta inesperada ao Identify (op {message.get('op')})")
            self.hello = hello
            self._ws = ws
            self._session_identified = True
            self.connect_count += 1
            self.last_error = None
            self._identified.set()
            self._notify_connection(True)
            try:
                async for raw in ws:
                    self._dispatch(raw)
            finally:
                self._identified.clear()
                self._ws = None
                self._fail_pending()
                self._notify_connection(False)

    async def _recv(self, ws: ClientConnection) -> dict[str, Any]:
        raw = await asyncio.wait_for(ws.recv(), self._handshake_timeout)
        try:
            message = json.loads(raw)
        except ValueError as exc:
            raise ObsProtocolError("mensagem inválida do obs-websocket") from exc
        if not isinstance(message, dict):
            raise ObsProtocolError("mensagem inválida do obs-websocket")
        return message

    @staticmethod
    def _parse_hello(message: dict[str, Any]) -> ObsHello:
        if message.get("op") != OpCode.HELLO or not isinstance(message.get("d"), dict):
            raise ObsProtocolError("o servidor não enviou Hello (é mesmo o obs-websocket v5?)")
        data = message["d"]
        auth = data.get("authentication")
        return ObsHello(
            obs_websocket_version=data.get("obsWebSocketVersion"),
            rpc_version=int(data.get("rpcVersion") or RPC_VERSION),
            challenge=auth.get("challenge") if isinstance(auth, dict) else None,
            salt=auth.get("salt") if isinstance(auth, dict) else None,
            obs_studio_version=data.get("obsStudioVersion"),
        )

    def _dispatch(self, raw: str | bytes) -> None:
        try:
            message = json.loads(raw)
        except ValueError:
            log.warning("mensagem inválida ignorada do obs-websocket")
            return
        if not isinstance(message, dict):
            return
        op = message.get("op")
        data = message.get("d") if isinstance(message.get("d"), dict) else {}
        if op == OpCode.REQUEST_RESPONSE:
            future = self._pending.get(str(data.get("requestId")))
            if future is not None and not future.done():
                future.set_result(data)
        elif op == OpCode.EVENT:
            event_type = str(data.get("eventType") or "")
            event_data = data.get("eventData") if isinstance(data.get("eventData"), dict) else {}
            log.debug("OBS event %s %s", event_type, event_data)
            if self._on_event is not None:
                try:
                    self._on_event(event_type, event_data)
                except Exception:
                    log.exception("OBS event handler failed for %s", event_type)
        else:
            log.debug("OBS op %s ignored", op)

    def _fail_pending(self) -> None:
        for future in self._pending.values():
            if not future.done():
                future.set_exception(ObsNotConnected("conexão com o OBS perdida"))
        self._pending.clear()

    def _notify_connection(self, connected: bool) -> None:
        if self._on_connection_change is None:
            return
        try:
            self._on_connection_change(connected)
        except Exception:
            log.exception("OBS connection callback failed")
