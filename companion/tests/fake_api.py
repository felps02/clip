"""In-memory fake of the companion endpoints, served through ``httpx.MockTransport``.

It follows the contract: device token auth (401 otherwise), sessions per device, heartbeats
with ``stopRequested``/``nextSequence``, idempotent segment uploads (``201`` new, ``200
{duplicate: true}`` repeated) and ``end``, which moves a session to ``ending`` (still
non-terminal: it blocks a new session of the device) and to ``ended`` once its segments are
"analysed" (``analysis_seconds`` later). Failures can be scripted per endpoint, as HTTP
statuses or as exceptions raised by the transport (network/TLS errors).
"""

from __future__ import annotations

import email.parser
import email.policy
import itertools
import json
import re
import time
from dataclasses import dataclass, field
from typing import Any

import httpx

from clipforge_companion.api import CompanionApi

TOKEN = "cfd_" + "A1b2C3d4E5" * 4
BASE_URL = "http://api.test"


def parse_multipart(content_type: str, body: bytes) -> tuple[dict[str, str], dict[str, Any]]:
    """Independent multipart parser (stdlib email package)."""
    message = email.parser.BytesParser(policy=email.policy.HTTP).parsebytes(
        b"Content-Type: " + content_type.encode("ascii") + b"\r\nMIME-Version: 1.0\r\n\r\n" + body
    )
    fields: dict[str, str] = {}
    files: dict[str, Any] = {}
    for part in message.iter_parts():
        name = part.get_param("name", header="content-disposition")
        filename = part.get_param("filename", header="content-disposition")
        payload = part.get_payload(decode=True) or b""
        if filename:
            files[str(name)] = {
                "filename": filename,
                "content": payload,
                "content_type": part.get_content_type(),
            }
        else:
            fields[str(name)] = payload.decode("utf-8")
    return fields, files


@dataclass
class FakeSession:
    id: str
    title: str
    segment_seconds: int
    obs: dict[str, Any] | None
    status: str = "waiting"
    stop_requested: bool = False
    segments: dict[int, dict[str, Any]] = field(default_factory=dict)
    ended_with: dict[str, Any] | None = None
    ending_until: float | None = None


class FakeLiveApi:
    def __init__(self, token: str = TOKEN) -> None:
        self.token = token
        self.sessions: dict[str, FakeSession] = {}
        self.created: list[dict[str, Any]] = []
        self.uploads: list[dict[str, Any]] = []  # every segment request that reached the API
        self.heartbeats: list[dict[str, Any]] = []
        self.ends: list[dict[str, Any]] = []
        self.requests: list[tuple[str, str]] = []
        self.auth_headers: list[str | None] = []
        # Scripted responses: lists of status codes consumed by the next requests.
        self.segment_failures: list[int] = []
        self.heartbeat_failures: list[int] = []
        self.end_failures: list[int] = []
        self.always_fail_segments: int | None = None
        self.stop_after_segments: int | None = None
        # Exceptions raised by the transport instead of answering (network / TLS errors).
        self.segment_exceptions: list[BaseException] = []
        self.heartbeat_exceptions: list[BaseException] = []
        # How long an ended session stays "ending" (its segments being analysed).
        self.analysis_seconds = 0.3
        self.revoked = False
        self._ids = itertools.count(1)

    def client(self) -> CompanionApi:
        return CompanionApi(BASE_URL, self.token, transport=httpx.MockTransport(self.handle))

    # -- helpers for assertions ----------------------------------------------------------------

    def finish_analysis(self) -> None:
        """Every ``ending`` session becomes ``ended`` now."""
        for session in self.sessions.values():
            if session.status == "ending":
                session.status = "ended"

    def _refresh(self) -> None:
        now = time.monotonic()
        for session in self.sessions.values():
            if session.status == "ending" and (session.ending_until or 0.0) <= now:
                session.status = "ended"

    def accepted(self, session_id: str | None = None) -> list[dict[str, Any]]:
        """Segments stored by the API (first upload of each sequence), in arrival order."""
        return [
            u
            for u in self.uploads
            if u["status"] == 201 and (session_id is None or u["session"] == session_id)
        ]

    # -- transport handler ---------------------------------------------------------------------

    def handle(self, request: httpx.Request) -> httpx.Response:
        path = request.url.path
        self.requests.append((request.method, path))
        self.auth_headers.append(request.headers.get("authorization"))
        if self.revoked or request.headers.get("authorization") != f"Bearer {self.token}":
            return self._problem(401, "unauthorized", "Token inválido.")
        self._refresh()
        if request.method == "GET" and path == "/api/live/companion/me":
            return httpx.Response(
                200,
                json={
                    "device": {"id": "dev-1", "name": "PC do estúdio"},
                    "user": {"displayName": "Criadora"},
                    "plan": {"code": "pro", "name": "Pro"},
                },
            )
        if request.method == "POST" and path == "/api/live/sessions":
            return self._create(json.loads(request.content or b"{}"))
        match = re.fullmatch(r"/api/live/sessions/([^/]+)/(heartbeat|segments|end)", path)
        if request.method == "POST" and match:
            session = self.sessions.get(match.group(1))
            if session is None:
                return self._problem(404, "not_found", "Sessão não encontrada.")
            action = match.group(2)
            if action == "heartbeat":
                if self.heartbeat_exceptions:
                    raise self.heartbeat_exceptions.pop(0)
                return self._heartbeat(session, json.loads(request.content or b"{}"))
            if action == "segments":
                if self.segment_exceptions:
                    raise self.segment_exceptions.pop(0)
                return self._segment(session, request)
            return self._end(session, json.loads(request.content or b"{}"))
        return self._problem(404, "not_found", "Rota desconhecida.")

    def _create(self, body: dict[str, Any]) -> httpx.Response:
        active = [s for s in self.sessions.values() if s.status in ("waiting", "live", "ending")]
        if active:
            return self._problem(409, "plan_limit", "O dispositivo já tem uma sessão ativa.")
        session_id = f"00000000-0000-0000-0000-{next(self._ids):012d}"
        session = FakeSession(
            id=session_id,
            title=body.get("title") or "Live de 23/10 20:00",
            segment_seconds=int(body.get("segmentSeconds") or 60),
            obs=body.get("obs"),
        )
        self.sessions[session_id] = session
        self.created.append(body)
        return httpx.Response(201, json={"session": self._view(session), "settings": {}})

    def _heartbeat(self, session: FakeSession, body: dict[str, Any]) -> httpx.Response:
        self.heartbeats.append({"session": session.id, **body})
        if self.heartbeat_failures:
            return self._problem(self.heartbeat_failures.pop(0), "error", "falha simulada")
        if session.status in ("ended", "error"):
            return self._problem(409, "conflict", "Sessão encerrada.")
        state = body.get("state") or {}
        if session.status == "waiting" and state.get("recording"):
            session.status = "live"
        next_sequence = max(session.segments, default=-1) + 1
        return httpx.Response(
            200,
            json={
                "status": session.status,
                "stopRequested": session.stop_requested,
                "nextSequence": next_sequence,
            },
        )

    def _segment(self, session: FakeSession, request: httpx.Request) -> httpx.Response:
        content_type = request.headers.get("content-type", "")
        body = request.content
        record: dict[str, Any] = {
            "session": session.id,
            "content_length_header": request.headers.get("content-length"),
            "transfer_encoding": request.headers.get("transfer-encoding"),
            "body_length": len(body),
        }
        fields, files = parse_multipart(content_type, body)
        record.update(
            sequence=int(fields["sequence"]),
            start=float(fields["startOffsetSeconds"]),
            duration=float(fields["durationSeconds"]),
            final=fields.get("final") == "true",
            file=files.get("file"),
        )
        if self.segment_failures:
            record["status"] = self.segment_failures.pop(0)
            self.uploads.append(record)
            return self._problem(record["status"], "error", "falha simulada")
        if self.always_fail_segments is not None:
            record["status"] = self.always_fail_segments
            self.uploads.append(record)
            return self._problem(record["status"], "error", "falha simulada")
        if session.status in ("ended", "error"):
            record["status"] = 409
            self.uploads.append(record)
            return self._problem(409, "conflict", "Sessão encerrada.")
        if record["sequence"] in session.segments:
            record["status"] = 200
            self.uploads.append(record)
            return httpx.Response(
                200, json={"segment": session.segments[record["sequence"]], "duplicate": True}
            )
        segment = {
            "id": f"seg-{session.id[-4:]}-{record['sequence']}",
            "sequence": record["sequence"],
            "startOffsetSeconds": record["start"],
            "durationSeconds": record["duration"],
            "status": "received",
        }
        session.segments[record["sequence"]] = segment
        if session.status == "waiting":
            session.status = "live"
        record["status"] = 201
        self.uploads.append(record)
        if (
            self.stop_after_segments is not None
            and len(session.segments) >= self.stop_after_segments
        ):
            session.stop_requested = True
        return httpx.Response(201, json={"segment": segment, "duplicate": False})

    def _end(self, session: FakeSession, body: dict[str, Any]) -> httpx.Response:
        self.ends.append({"session": session.id, **body})
        if self.end_failures:
            return self._problem(self.end_failures.pop(0), "error", "falha simulada")
        if session.status in ("ended", "error"):
            return self._problem(409, "conflict", "Sessão encerrada.")
        if session.status != "ending":
            # Contract: "ending" until every segment job is terminal, then "ended".
            session.status = "ending"
            session.ending_until = time.monotonic() + self.analysis_seconds
            session.ended_with = body
        return httpx.Response(200, json={"session": self._view(session)})

    @staticmethod
    def _view(session: FakeSession) -> dict[str, Any]:
        return {
            "id": session.id,
            "title": session.title,
            "status": session.status,
            "segmentSeconds": session.segment_seconds,
            "stopRequested": session.stop_requested,
        }

    @staticmethod
    def _problem(status: int, code: str, detail: str) -> httpx.Response:
        return httpx.Response(
            status,
            json={
                "type": "about:blank",
                "title": "Erro",
                "status": status,
                "code": code,
                "detail": detail,
            },
            headers={"Content-Type": "application/problem+json"},
        )
