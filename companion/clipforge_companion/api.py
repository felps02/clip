"""HTTP client for the companion endpoints of the API (device token auth).

| Method | Path                                   |
|--------|----------------------------------------|
| GET    | /api/live/companion/me                 |
| POST   | /api/live/sessions                     |
| POST   | /api/live/sessions/{id}/heartbeat      |
| POST   | /api/live/sessions/{id}/segments       |
| POST   | /api/live/sessions/{id}/end            |

Errors are mapped to typed exceptions so that callers can decide what to do:

* :class:`AuthError` (401/403): the token is invalid or was revoked; stop.
* :class:`SessionClosed` (409, or 404 on a session URL): the session is over; stop.
* :class:`PlanLimitError` (409 when creating a session).
* :class:`RetryableError`: network errors (including TLS errors and socket errors that escape
  httpx's own mapping), timeouts, 5xx, 408 and 429 (``retry_after``).
* :class:`RejectedError`: any other 4xx (validation, 413 too big, 422 invalid media).

A problem with the local segment file (missing, locked, shrinking while it is sent) is not an
API error: it is raised as :class:`SegmentFileError`, so that a network failure is never taken
for a bad file (and the other way round).
"""

from __future__ import annotations

import asyncio
import email.utils
import json
import platform
import secrets
import time
from collections.abc import AsyncIterator, Callable
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import quote

import httpx

from clipforge_companion import __version__


class ApiError(Exception):
    """Base class; ``status`` is the HTTP status (None for network errors)."""

    def __init__(
        self,
        message: str,
        *,
        status: int | None = None,
        code: str | None = None,
        detail: str | None = None,
    ) -> None:
        super().__init__(message)
        self.message = message
        self.status = status
        self.code = code
        self.detail = detail


class AuthError(ApiError):
    """401/403: invalid or revoked device token."""


class ConflictError(ApiError):
    """409 (base class)."""


class SessionClosed(ConflictError):
    """The session is terminal (ended, error, quota exceeded) or no longer exists."""


class PlanLimitError(ConflictError):
    """A session cannot be created (active session for the device, plan limits)."""


class RetryableError(ApiError):
    """Temporary failure: retry later."""

    def __init__(self, message: str, *, retry_after: float | None = None, **kwargs: Any) -> None:
        super().__init__(message, **kwargs)
        self.retry_after = retry_after


class RejectedError(ApiError):
    """The API refused this request for good (other 4xx)."""


class SegmentFileError(Exception):
    """The segment file could not be read (a local problem, not the network).

    ``reason`` is ``"missing"`` (the file is gone), ``"shrunk"`` (it got smaller while it was
    sent), ``"locked"`` (Windows sharing violation, antivirus) or ``"unreadable"`` (other I/O
    errors). Only the first two are definite: the others may go away by themselves.
    """

    def __init__(self, path: Path, reason: str, message: str) -> None:
        super().__init__(message)
        self.path = path
        self.reason = reason
        self.message = message

    @property
    def transient(self) -> bool:
        return self.reason not in ("missing", "shrunk")

    @classmethod
    def from_os_error(cls, path: Path, exc: OSError) -> SegmentFileError:
        if isinstance(exc, FileNotFoundError):
            reason = "missing"
        elif isinstance(exc, PermissionError):
            reason = "locked"
        else:
            reason = "unreadable"
        detail = exc.strerror or str(exc) or exc.__class__.__name__
        return cls(path, reason, f"não foi possível ler {path.name}: {detail}")


def parse_retry_after(value: str | None, *, now: datetime | None = None) -> float | None:
    if not value:
        return None
    value = value.strip()
    try:
        return max(0.0, float(value))
    except ValueError:
        pass
    try:
        when = email.utils.parsedate_to_datetime(value)
    except (TypeError, ValueError):
        return None
    if when.tzinfo is None:
        when = when.replace(tzinfo=UTC)
    return max(0.0, (when - (now or datetime.now(UTC))).total_seconds())


def _problem(response: httpx.Response) -> dict[str, Any]:
    try:
        data = response.json()
    except (ValueError, UnicodeDecodeError):
        return {}
    return data if isinstance(data, dict) else {}


def error_from_response(response: httpx.Response, *, conflict: type[ConflictError]) -> ApiError:
    """Builds the typed error for a non-2xx response."""
    status = response.status_code
    problem = _problem(response)
    code = problem.get("code") if isinstance(problem.get("code"), str) else None
    detail = problem.get("detail") or problem.get("title")
    detail = str(detail) if detail else None
    message = f"HTTP {status}"
    if code:
        message += f" {code}"
    if detail:
        message += f": {detail}"
    kwargs: dict[str, Any] = {"status": status, "code": code, "detail": detail}
    if status in (401, 403):
        return AuthError(message, **kwargs)
    if status == 409:
        return conflict(message, **kwargs)
    if status == 404 and conflict is SessionClosed:
        return SessionClosed(message, **kwargs)
    if status in (408, 425, 429) or status >= 500:
        retry_after = parse_retry_after(response.headers.get("Retry-After"))
        return RetryableError(message, retry_after=retry_after, **kwargs)
    if 300 <= status < 400:
        location = response.headers.get("Location", "")
        return RejectedError(
            f"A API respondeu com redirecionamento (HTTP {status}) para {location or '?'}: "
            "use o endereço final em --api.",
            **kwargs,
        )
    return RejectedError(message, **kwargs)


class RateLimiter:
    """Paces byte streams to at most ``bytes_per_second`` (one chunk of burst)."""

    def __init__(
        self,
        bytes_per_second: float,
        *,
        clock: Callable[[], float] = time.monotonic,
        sleep: Callable[[float], Any] = asyncio.sleep,
    ) -> None:
        if bytes_per_second <= 0:
            raise ValueError("bytes_per_second must be positive")
        self.bytes_per_second = bytes_per_second
        self._clock = clock
        self._sleep = sleep
        self._next: float | None = None

    @classmethod
    def from_mbps(cls, mbps: float, **kwargs: Any) -> RateLimiter:
        return cls(mbps * 1_000_000 / 8, **kwargs)

    async def consume(self, num_bytes: int) -> None:
        now = self._clock()
        if self._next is None or self._next < now:
            self._next = now
        delay = self._next - now
        self._next += num_bytes / self.bytes_per_second
        if delay > 0:
            await self._sleep(delay)


CONTENT_TYPES = {
    ".mkv": "video/x-matroska",
    ".webm": "video/webm",
    ".mp4": "video/mp4",
    ".mov": "video/quicktime",
    ".ts": "video/mp2t",
}


def _format_number(value: float) -> str:
    return f"{value:.6f}"


class MultipartFileBody:
    """``multipart/form-data`` body that streams the file from disk in chunks (never loaded
    whole), optionally paced by a :class:`RateLimiter`. Content-Length is known upfront."""

    def __init__(
        self,
        fields: list[tuple[str, str]],
        *,
        file_field: str,
        path: Path,
        content_type: str,
        limiter: RateLimiter | None = None,
        chunk_size: int = 64 * 1024,
    ) -> None:
        self.boundary = "companion-" + secrets.token_hex(16)
        self.path = path
        self.limiter = limiter
        self.chunk_size = chunk_size
        self.bytes_sent = 0
        boundary = self.boundary.encode("ascii")
        parts = []
        for name, value in fields:
            parts.append(
                b"--" + boundary + b"\r\n"
                + f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode()
                + value.encode("utf-8") + b"\r\n"
            )  # fmt: skip
        filename = path.name.replace('"', "_")
        parts.append(
            b"--" + boundary + b"\r\n"
            + f'Content-Disposition: form-data; name="{file_field}"; filename="{filename}"\r\n'
            .encode()
            + f"Content-Type: {content_type}\r\n\r\n".encode()
        )  # fmt: skip
        self._preamble = b"".join(parts)
        self._epilogue = b"\r\n--" + boundary + b"--\r\n"
        try:
            self.file_size = path.stat().st_size
        except OSError as exc:
            raise SegmentFileError.from_os_error(path, exc) from exc
        self.content_length = len(self._preamble) + self.file_size + len(self._epilogue)

    @property
    def content_type(self) -> str:
        return f"multipart/form-data; boundary={self.boundary}"

    async def __aiter__(self) -> AsyncIterator[bytes]:
        # File errors are raised as SegmentFileError (not OSError): an OSError coming out of the
        # request is a network problem (see CompanionApi._send).
        yield self._preamble
        remaining = self.file_size
        try:
            handle = self.path.open("rb")
        except OSError as exc:
            raise SegmentFileError.from_os_error(self.path, exc) from exc
        with handle:
            while remaining > 0:
                try:
                    chunk = handle.read(min(self.chunk_size, remaining))
                except OSError as exc:
                    raise SegmentFileError.from_os_error(self.path, exc) from exc
                if not chunk:
                    raise SegmentFileError(
                        self.path, "shrunk", f"o arquivo {self.path.name} encolheu durante o envio"
                    )
                remaining -= len(chunk)
                if self.limiter is not None:
                    await self.limiter.consume(len(chunk))
                self.bytes_sent += len(chunk)
                yield chunk
        yield self._epilogue


def default_user_agent() -> str:
    return (
        f"clipforge-companion/{__version__} "
        f"(Python {platform.python_version()}; {platform.system()} {platform.release()})"
    )


class CompanionApi:
    """Async client for the companion endpoints. The token is only ever put in the
    ``Authorization`` header."""

    def __init__(
        self,
        base_url: str,
        token: str,
        *,
        transport: httpx.AsyncBaseTransport | None = None,
        timeout: float = 30.0,
        upload_timeout: float = 120.0,
        user_agent: str | None = None,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self._upload_timeout = upload_timeout
        self._client = httpx.AsyncClient(
            base_url=self.base_url,
            headers={
                "Authorization": f"Bearer {token}",
                "User-Agent": user_agent or default_user_agent(),
                "Accept": "application/json",
            },
            timeout=httpx.Timeout(timeout, connect=15.0),
            transport=transport,
            follow_redirects=False,
        )

    async def __aenter__(self) -> CompanionApi:
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.aclose()

    async def aclose(self) -> None:
        await self._client.aclose()

    async def me(self) -> dict[str, Any]:
        return await self._json("GET", "/api/live/companion/me", conflict=ConflictError)

    async def create_session(
        self,
        *,
        title: str | None = None,
        segment_seconds: float | None = None,
        settings: dict[str, Any] | None = None,
        obs: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        body: dict[str, Any] = {}
        if title:
            body["title"] = title
        if segment_seconds is not None:
            body["segmentSeconds"] = round(segment_seconds)
        if settings:
            body["settings"] = settings
        if obs:
            body["obs"] = {k: v for k, v in obs.items() if v is not None}
        return await self._json(
            "POST", "/api/live/sessions", json_body=body, conflict=PlanLimitError
        )

    async def heartbeat(self, session_id: str, state: dict[str, Any]) -> dict[str, Any]:
        return await self._json(
            "POST", f"{self._session_path(session_id)}/heartbeat", json_body={"state": state}
        )

    async def end_session(
        self, session_id: str, last_sequence: int | None = None
    ) -> dict[str, Any]:
        body: dict[str, Any] = {}
        if last_sequence is not None:
            body["lastSequence"] = last_sequence
        return await self._json("POST", f"{self._session_path(session_id)}/end", json_body=body)

    async def upload_segment(
        self,
        session_id: str,
        *,
        sequence: int,
        start_offset: float,
        duration: float,
        path: Path,
        final: bool = False,
        limiter: RateLimiter | None = None,
    ) -> dict[str, Any]:
        """Uploads one segment (multipart, streamed). Returns ``{segment, duplicate}``; a
        ``200 {duplicate: true}`` means the API already had this sequence."""
        body = MultipartFileBody(
            [
                ("sequence", str(int(sequence))),
                ("startOffsetSeconds", _format_number(start_offset)),
                ("durationSeconds", _format_number(duration)),
                ("final", "true" if final else "false"),
            ],
            file_field="file",
            path=path,
            content_type=CONTENT_TYPES.get(path.suffix.lower(), "application/octet-stream"),
            limiter=limiter,
        )
        response = await self._send(
            "POST",
            f"{self._session_path(session_id)}/segments",
            content=body,
            headers={
                "Content-Type": body.content_type,
                "Content-Length": str(body.content_length),
            },
            timeout=httpx.Timeout(self._upload_timeout, connect=15.0),
            conflict=SessionClosed,
        )
        data = self._decode(response)
        data.setdefault("duplicate", response.status_code == 200)
        return data

    # -- internals ---------------------------------------------------------------------------

    @staticmethod
    def _session_path(session_id: str) -> str:
        return f"/api/live/sessions/{quote(str(session_id), safe='')}"

    async def _json(
        self,
        method: str,
        path: str,
        *,
        json_body: dict[str, Any] | None = None,
        conflict: type[ConflictError] = SessionClosed,
    ) -> dict[str, Any]:
        headers = None
        content = None
        if json_body is not None:
            content = json.dumps(json_body).encode("utf-8")
            headers = {"Content-Type": "application/json"}
        response = await self._send(
            method, path, content=content, headers=headers, conflict=conflict
        )
        return self._decode(response)

    async def _send(
        self,
        method: str,
        path: str,
        *,
        content: Any = None,
        headers: dict[str, str] | None = None,
        timeout: httpx.Timeout | None = None,
        conflict: type[ConflictError],
    ) -> httpx.Response:
        try:
            response = await self._client.request(
                method,
                path,
                content=content,
                headers=headers,
                timeout=timeout if timeout is not None else httpx.USE_CLIENT_DEFAULT,
            )
        except httpx.TimeoutException as exc:
            raise RetryableError(f"tempo esgotado ({exc.__class__.__name__})") from exc
        except httpx.RequestError as exc:
            # TransportError (connect/read/write/protocol), DecodingError, TooManyRedirects...
            raise RetryableError(f"falha de rede ({exc.__class__.__name__}: {exc})") from exc
        except OSError as exc:
            # Raised as is by the network stack below httpx: a TLS record that fails to decrypt
            # (ssl.SSLError, e.g. an antivirus or proxy scanning HTTPS), a TLS alert, a reset
            # socket. Segment file problems arrive as SegmentFileError, never as OSError.
            raise RetryableError(f"falha de rede ({exc.__class__.__name__}: {exc})") from exc
        if not response.is_success:
            raise error_from_response(response, conflict=conflict)
        return response

    @staticmethod
    def _decode(response: httpx.Response) -> dict[str, Any]:
        if not response.content:
            return {}
        try:
            data = response.json()
        except (ValueError, UnicodeDecodeError) as exc:
            raise RetryableError(
                f"resposta inválida da API (HTTP {response.status_code})",
                status=response.status_code,
            ) from exc
        return data if isinstance(data, dict) else {"data": data}
