"""A minimal HTTPS server for tests that can corrupt encrypted response records.

A TLS record that fails to decrypt (an antivirus or proxy scanning HTTPS, a flaky middlebox)
makes the client's ssl module raise ``ssl.SSLError [DECRYPTION_FAILED_OR_BAD_RECORD_MAC]``,
which anyio re-raises as is below httpx. The server terminates TLS itself with
``ssl.MemoryBIO`` so it can flip a byte in the ciphertext of chosen responses. The throwaway
certificate is generated with the ``openssl`` command line (tests skip when it is missing).
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import shutil
import ssl
import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

Handler = Callable[[str, str, bytes], tuple[int, dict[str, Any]]]


def make_certificate(directory: Path) -> tuple[Path, Path] | None:
    openssl = shutil.which("openssl")
    if openssl is None:
        return None
    cert, key = directory / "cert.pem", directory / "key.pem"
    result = subprocess.run(
        [
            openssl, "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1",
            "-nodes", "-keyout", str(key), "-out", str(cert), "-days", "2",
            "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1",
        ],
        capture_output=True,
        timeout=60,
        check=False,
    )  # fmt: skip
    if result.returncode != 0 or not cert.is_file():
        return None
    return cert, key


class CorruptingTlsServer:
    """Answers HTTP/1.1 requests with ``handler(method, path, body) -> (status, json)``; the
    first ``corrupt`` responses are sent with one ciphertext byte flipped."""

    def __init__(self, cert: Path, key: Path, handler: Handler, *, corrupt: int = 1) -> None:
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.context.load_cert_chain(cert, key)
        self.handler = handler
        self.corrupt = corrupt
        self.requests: list[tuple[str, str]] = []
        self.corrupted = 0
        self._server: asyncio.Server | None = None
        self.port = 0

    @property
    def url(self) -> str:
        return f"https://127.0.0.1:{self.port}"

    async def __aenter__(self) -> CorruptingTlsServer:
        self._server = await asyncio.start_server(self._serve, "127.0.0.1", 0)
        self.port = self._server.sockets[0].getsockname()[1]
        return self

    async def __aexit__(self, *exc: object) -> None:
        if self._server is not None:
            self._server.close()
            await self._server.wait_closed()

    async def _serve(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        incoming, outgoing = ssl.MemoryBIO(), ssl.MemoryBIO()
        tls = self.context.wrap_bio(incoming, outgoing, server_side=True)
        try:
            while True:  # handshake
                try:
                    tls.do_handshake()
                    break
                except ssl.SSLWantReadError:
                    writer.write(outgoing.read())
                    await writer.drain()
                    data = await reader.read(65536)
                    if not data:
                        return
                    incoming.write(data)
            writer.write(outgoing.read())  # last flight, session tickets
            await writer.drain()
            while True:
                request = await self._read_request(tls, incoming, outgoing, reader, writer)
                if request is None:
                    return
                method, path, body = request
                self.requests.append((method, path))
                status, payload = self.handler(method, path, body)
                content = json.dumps(payload).encode()
                tls.write(
                    f"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\n"
                    f"Content-Length: {len(content)}\r\n\r\n".encode()
                    + content
                )
                records = bytearray(outgoing.read())
                if self.corrupted < self.corrupt:
                    self.corrupted += 1
                    records[-1] ^= 0xFF  # inside the authentication tag of the last record
                writer.write(bytes(records))
                await writer.drain()
        except (ssl.SSLError, ConnectionError):
            return
        finally:
            with contextlib.suppress(Exception):
                writer.close()

    @staticmethod
    async def _read_request(
        tls: ssl.SSLObject,
        incoming: ssl.MemoryBIO,
        outgoing: ssl.MemoryBIO,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
    ) -> tuple[str, str, bytes] | None:
        buffer = b""
        while True:
            while True:  # everything already received (headers and body are separate records)
                try:
                    chunk = tls.read(65536)
                except ssl.SSLWantReadError:
                    break
                if not chunk:
                    return None
                buffer += chunk
            if b"\r\n\r\n" in buffer:
                head, _, rest = buffer.partition(b"\r\n\r\n")
                lines = head.decode("latin-1").split("\r\n")
                method, path, _ = lines[0].split(" ", 2)
                headers = dict(line.split(": ", 1) for line in lines[1:] if ": " in line)
                length = int({k.lower(): v for k, v in headers.items()}.get("content-length", 0))
                if len(rest) >= length:
                    return method, path, rest[:length]
            pending = outgoing.read()
            if pending:
                writer.write(pending)
                await writer.drain()
            data = await reader.read(65536)
            if not data:
                return None
            incoming.write(data)
