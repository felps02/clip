"""A small fake obs-websocket v5 server (``websockets.serve``) for tests.

It implements the handshake (Hello with or without authentication, Identify validation,
Identified), answers the requests the companion uses and can emit events or drop clients.
The authentication string is checked with its own implementation (hashlib.new + binascii),
independent from the client code under test.
"""

from __future__ import annotations

import asyncio
import binascii
import contextlib
import hashlib
import json
from typing import Any

from websockets.asyncio.server import Server, ServerConnection, serve
from websockets.exceptions import ConnectionClosed
from websockets.typing import Subprotocol

SALT = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI="
CHALLENGE = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY="


def expected_auth(password: str, salt: str, challenge: str) -> str:
    def b64_sha256(text: str) -> str:
        digest = hashlib.new("sha256", text.encode("utf-8")).digest()
        return binascii.b2a_base64(digest, newline=False).decode("ascii")

    return b64_sha256(b64_sha256(password + salt) + challenge)


class FakeObsServer:
    def __init__(
        self,
        *,
        password: str | None = None,
        record_directory: str = "C:/OBS",
        obs_version: str = "31.0.2",
        websocket_version: str = "5.5.4",
        ignore_requests: tuple[str, ...] = (),
        port: int = 0,
    ) -> None:
        self.password = password
        self.record_directory = record_directory
        self.obs_version = obs_version
        self.websocket_version = websocket_version
        self.ignore_requests = set(ignore_requests)
        self.port = port
        self.recording = False
        self.paused = False
        self.streaming = False
        self.record_path: str | None = None
        self.next_record_path: str | None = None
        self.identify_messages: list[dict[str, Any]] = []
        self.requests: list[str] = []
        self.connections: set[ServerConnection] = set()
        self.connection_count = 0
        self.identified_count = 0
        self.rejected_auth = 0
        self._server: Server | None = None

    @property
    def url(self) -> str:
        return f"ws://127.0.0.1:{self.port}"

    async def start(self) -> None:
        self._server = await serve(
            self._handle,
            "127.0.0.1",
            self.port,
            subprotocols=[Subprotocol("obswebsocket.json")],
            compression=None,
        )
        self.port = self._server.sockets[0].getsockname()[1]

    async def stop(self) -> None:
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()
            self._server = None

    async def __aenter__(self) -> FakeObsServer:
        await self.start()
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.stop()

    async def wait_identified(self, count: int = 1, timeout: float = 10.0) -> None:
        async def wait() -> None:
            while self.identified_count < count:
                await asyncio.sleep(0.02)

        await asyncio.wait_for(wait(), timeout)

    async def emit(self, event_type: str, data: dict[str, Any]) -> None:
        message = json.dumps(
            {"op": 5, "d": {"eventType": event_type, "eventIntent": 64, "eventData": data}}
        )
        for ws in list(self.connections):
            with contextlib.suppress(ConnectionClosed):
                await ws.send(message)

    async def start_recording(self, path: str | None) -> None:
        self.recording = True
        self.paused = False
        self.record_path = path
        await self.emit(
            "RecordStateChanged",
            {
                "outputActive": False,
                "outputState": "OBS_WEBSOCKET_OUTPUT_STARTING",
                "outputPath": None,
            },
        )
        await self.emit(
            "RecordStateChanged",
            {
                "outputActive": True,
                "outputState": "OBS_WEBSOCKET_OUTPUT_STARTED",
                "outputPath": path,
            },
        )

    async def stop_recording(self) -> None:
        self.recording = False
        await self.emit(
            "RecordStateChanged",
            {
                "outputActive": False,
                "outputState": "OBS_WEBSOCKET_OUTPUT_STOPPED",
                "outputPath": self.record_path,
            },
        )

    async def drop_connections(self, code: int = 1001, reason: str = "OBS closed") -> None:
        for ws in list(self.connections):
            with contextlib.suppress(Exception):
                await ws.close(code, reason)

    async def _handle(self, ws: ServerConnection) -> None:
        self.connection_count += 1
        self.connections.add(ws)
        try:
            hello: dict[str, Any] = {
                "obsWebSocketVersion": self.websocket_version,
                "rpcVersion": 1,
                "obsStudioVersion": self.obs_version,
            }
            if self.password is not None:
                hello["authentication"] = {"challenge": CHALLENGE, "salt": SALT}
            await ws.send(json.dumps({"op": 0, "d": hello}))
            identify = json.loads(await ws.recv())
            self.identify_messages.append(identify)
            data = identify.get("d") or {}
            if identify.get("op") != 1:
                await ws.close(4007, "Not identified")
                return
            if data.get("rpcVersion") != 1:
                await ws.close(4010, "Unsupported rpc version")
                return
            if self.password is not None and data.get("authentication") != expected_auth(
                self.password, SALT, CHALLENGE
            ):
                self.rejected_auth += 1
                await ws.close(4009, "Authentication failed.")
                return
            await ws.send(json.dumps({"op": 2, "d": {"negotiatedRpcVersion": 1}}))
            self.identified_count += 1
            async for raw in ws:
                message = json.loads(raw)
                if message.get("op") == 6:
                    await self._handle_request(ws, message.get("d") or {})
        except ConnectionClosed:
            pass
        finally:
            self.connections.discard(ws)

    async def _handle_request(self, ws: ServerConnection, data: dict[str, Any]) -> None:
        request_type = str(data.get("requestType"))
        self.requests.append(request_type)
        if request_type in self.ignore_requests:
            return
        status: dict[str, Any] = {"result": True, "code": 100}
        response: dict[str, Any] = {}
        start_recording = False
        if request_type == "GetVersion":
            response = {
                "obsVersion": self.obs_version,
                "obsWebSocketVersion": self.websocket_version,
                "rpcVersion": 1,
                "availableRequests": ["GetVersion", "GetRecordStatus", "StartRecord"],
                "platform": "windows",
            }
        elif request_type == "GetRecordStatus":
            response = {
                "outputActive": self.recording,
                "outputPaused": self.paused,
                "outputTimecode": "00:00:00.000",
                "outputDuration": 0,
                "outputBytes": 0,
            }
        elif request_type == "GetStreamStatus":
            response = {"outputActive": self.streaming, "outputReconnecting": False}
        elif request_type == "GetRecordDirectory":
            response = {"recordDirectory": self.record_directory}
        elif request_type == "StartRecord":
            if self.recording:
                status = {"result": False, "code": 500, "comment": "Recording already active"}
            else:
                start_recording = True
        else:
            status = {"result": False, "code": 204, "comment": f"Unknown request {request_type}"}
        await ws.send(
            json.dumps(
                {
                    "op": 7,
                    "d": {
                        "requestType": request_type,
                        "requestId": data.get("requestId"),
                        "requestStatus": status,
                        "responseData": response,
                    },
                }
            )
        )
        if start_recording:
            await self.start_recording(self.next_record_path)
