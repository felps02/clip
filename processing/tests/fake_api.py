"""In-memory fake of the internal API + signed storage, served through ``httpx.MockTransport``.

Records every call so tests can assert the worker protocol
(claim -> probe -> statuses -> candidates -> upload-urls -> PUT -> clips -> analysis -> complete).

Jobs claimed through the fake are tracked the way the real API tracks them: the claim
itself moves the job to ``preparing_media`` and records its owner, ``/status`` validates
the transition like ``JobStateMachine`` (backwards or out of a terminal status -> 409;
re-posting the current running status is an idempotent progress update, like
``JobStateMachine.IsProgressUpdate``), ``/heartbeat``, ``/probe`` and ``/complete`` require
a running job, ``/fail`` an active one, ``/events`` rejects messages over 1000 characters
(422, like ``EventRequestValidator``), and the endpoints that identify the worker answer
409 when ``X-Worker-Id`` names another worker. Job ids that were never claimed through the
fake are handled leniently (client unit tests).

``answer_lost`` simulates a request the API applied whose answer never arrived (read
timeout): the request is processed, then ``httpx.ReadTimeout`` is raised.
"""

from __future__ import annotations

import json
import re
import time
import uuid
from collections.abc import Iterator
from pathlib import Path
from typing import Any

import httpx

STORAGE_BASE = "http://fake-storage"
API_BASE = "http://api.test"

_JOB_RE = re.compile(r"^/internal/jobs/([^/]+)/([a-z-]+)$")
_CLAIM_RE = re.compile(r"^/internal/workers/([^/]+)/claim$")

ANALYZE_CHAIN = [
    "pending",
    "queued",
    "preparing_media",
    "analyzing",
    "detecting_moments",
    "generating_clips",
    "finalizing",
    "completed",
]
RENDER_CHAIN = [
    "pending",
    "queued",
    "preparing_media",
    "generating_clips",
    "finalizing",
    "completed",
]
RUNNING_STATUSES = frozenset(
    {"preparing_media", "analyzing", "detecting_moments", "generating_clips", "finalizing"}
)
TERMINAL_STATUSES = frozenset({"completed", "failed", "cancelled"})
OWNER_CHECKED_ENDPOINTS = frozenset(
    {"heartbeat", "probe", "status", "render-result", "complete", "fail"}
)


def can_transition(kind: str, from_status: str, to_status: str) -> bool:
    """Port of ``JobStateMachine.CanTransition`` (backend ``ClipForge.Domain/Jobs``)."""
    if from_status == to_status or from_status in TERMINAL_STATUSES:
        return False
    if to_status in ("failed", "cancelled"):
        return True
    if to_status == "queued":
        return from_status == "pending"
    if to_status == "completed":
        return from_status in RUNNING_STATUSES
    chain = RENDER_CHAIN if kind == "render_clip" else ANALYZE_CHAIN
    if to_status not in chain:
        return False
    if from_status == "queued":
        return to_status == "preparing_media"
    if from_status == "pending" or from_status not in chain:
        return False
    return chain.index(to_status) > chain.index(from_status)


def problem(status: int, code: str, detail: str) -> httpx.Response:
    body = {"type": "about:blank", "title": code, "status": status, "detail": detail, "code": code}
    return httpx.Response(status, json=body, headers={"content-type": "application/problem+json"})


class _SlowStream(httpx.SyncByteStream):
    """Body streamed in small chunks with a pause between them (slow download)."""

    def __init__(self, data: bytes, chunk: int, delay: float) -> None:
        self._data = data
        self._chunk = max(1, chunk)
        self._delay = delay

    def __iter__(self) -> Iterator[bytes]:
        for offset in range(0, len(self._data), self._chunk):
            time.sleep(self._delay)
            yield self._data[offset : offset + self._chunk]


class FakeInternalApi:
    def __init__(
        self,
        *,
        payloads: list[dict[str, Any]] | None = None,
        files: dict[str, Path | bytes] | None = None,
        json_files: dict[str, Any] | None = None,
        cancel_at_heartbeat: int | None = None,
        lease_lost_at_heartbeat: int | None = None,
        probe_conflict: tuple[str, str] | None = None,
        api_key: str = "test-key",
        flaky: dict[str, list[int]] | None = None,
        stream_delay: float = 0.0,
        stream_chunk: int = 64 * 1024,
        storage_errors: dict[str, int] | None = None,
        answer_lost: dict[str, int] | None = None,
        keep_uploads: bool = False,
    ) -> None:
        self.payloads = list(payloads or [])
        self.files = dict(files or {})
        self.json_files = dict(json_files or {})
        self.cancel_at_heartbeat = cancel_at_heartbeat
        self.lease_lost_at_heartbeat = lease_lost_at_heartbeat
        self.probe_conflict = probe_conflict
        self.api_key = api_key
        self.flaky = {
            k: list(v) for k, v in (flaky or {}).items()
        }  # endpoint -> status codes to return first
        self.stream_delay = stream_delay
        self.stream_chunk = stream_chunk
        self.storage_errors = dict(storage_errors or {})  # file name -> HTTP status (GET/PUT)
        # endpoint -> number of requests applied whose answer is then lost (ReadTimeout)
        self.answer_lost = dict(answer_lost or {})
        self.clips_created = 0  # clips registered through /clips (duplicates show here)
        self.events_rejected = 0
        self.calls: list[tuple[str, str, Any]] = []
        self.worker_ids: list[str | None] = []  # X-Worker-Id of every internal call
        self.jobs: dict[str, dict[str, Any]] = {}  # claimed jobs: status, kind, worker
        self.uploads: dict[str, int] = {}
        self.upload_headers: dict[str, dict[str, str]] = {}
        # with keep_uploads: the last body PUT under each file name (rendered clips, JSON)
        self.keep_uploads = keep_uploads
        self.upload_bodies: dict[str, bytes] = {}
        self.heartbeats = 0
        self.cancel_answered_at: int | None = None  # heartbeat that first answered cancelRequested
        self.candidate_ids: list[str] = []
        self.bad_key_calls = 0

    # ------------------------------------------------------------------ helpers ----
    def transport(self) -> httpx.MockTransport:
        return httpx.MockTransport(self.handler)

    def sequence(self) -> list[str]:
        """Endpoint names in call order (``claim``, ``probe``, ``status:analyzing``...)."""
        out: list[str] = []
        for _method, path, body in self.calls:
            if path.endswith("/claim"):
                out.append("claim")
            elif path.startswith("/storage/upload/"):
                out.append("PUT:" + path.rsplit("/", 1)[-1])
            elif path.startswith("/storage/download/"):
                out.append("GET:" + path.rsplit("/", 1)[-1])
            else:
                match = _JOB_RE.match(path)
                name = match.group(2) if match else path
                if name == "status" and isinstance(body, dict):
                    out.append(f"status:{body.get('status')}")
                else:
                    out.append(name)
        return out

    def bodies(self, endpoint: str) -> list[Any]:
        result: list[Any] = []
        for _method, path, body in self.calls:
            match = _JOB_RE.match(path)
            if match and match.group(2) == endpoint:
                result.append(body)
        return result

    def statuses(self) -> list[str]:
        return [b.get("status") for b in self.bodies("status")]

    def job_status(self, job_id: str) -> str | None:
        """Server-side status of a job claimed through the fake."""
        job = self.jobs.get(job_id)
        return job["status"] if job else None

    # ------------------------------------------------------------------ handler ----
    def handler(self, request: httpx.Request) -> httpx.Response:
        url = request.url
        path = url.path
        if url.host == "fake-storage":
            return self._storage(request, path)
        if request.headers.get("X-Internal-Api-Key") != self.api_key:
            self.bad_key_calls += 1
            return problem(401, "unauthorized", "missing or invalid internal key")
        worker_id = request.headers.get("X-Worker-Id") or None
        self.worker_ids.append(worker_id)
        body: Any = None
        if request.content:
            try:
                body = json.loads(request.content)
            except ValueError:
                body = request.content
        self.calls.append((request.method, path, body))

        endpoint_name = None
        match = _JOB_RE.match(path)
        claim = _CLAIM_RE.match(path)
        if match:
            endpoint_name = match.group(2)
        elif claim:
            endpoint_name = "claim"
        if endpoint_name and self.flaky.get(endpoint_name):
            status = self.flaky[endpoint_name].pop(0)
            return problem(status, "server_error", "simulated failure")

        if claim:
            if not self.payloads:
                response = httpx.Response(204)
            else:
                payload = self.payloads.pop(0)
                # The claim performs queued -> preparing_media and records the owner.
                self.jobs[str(payload.get("jobId"))] = {
                    "status": "preparing_media",
                    "kind": payload.get("kind"),
                    "worker": claim.group(1),
                }
                response = httpx.Response(200, json=payload)
        elif not match:
            return problem(404, "not_found", path)
        else:
            job_id, endpoint = match.group(1), match.group(2)
            response = self._job_endpoint(job_id, endpoint, body, worker_id)
        if endpoint_name and self.answer_lost.get(endpoint_name, 0) > 0:
            self.answer_lost[endpoint_name] -= 1  # applied above; the answer never arrives
            raise httpx.ReadTimeout("simulated: answer lost after the request was applied")
        return response

    def _job_endpoint(
        self, job_id: str, endpoint: str, body: Any, worker_id: str | None
    ) -> httpx.Response:
        job = self.jobs.get(job_id)
        if job is not None:
            if endpoint in OWNER_CHECKED_ENDPOINTS and worker_id and job["worker"] != worker_id:
                return problem(
                    409, "conflict", f"O job está reservado para outro worker ({job['worker']})."
                )
            if endpoint in ("heartbeat", "probe", "complete") and (
                job["status"] not in RUNNING_STATUSES
            ):
                return problem(
                    409, "conflict", f"O job não está em execução (status atual: {job['status']})."
                )
            if endpoint == "fail" and job["status"] in TERMINAL_STATUSES:
                return problem(
                    409, "conflict", f"O job já foi finalizado (status atual: {job['status']})."
                )

        if endpoint == "heartbeat":
            self.heartbeats += 1
            if (
                self.lease_lost_at_heartbeat is not None
                and self.heartbeats >= self.lease_lost_at_heartbeat
            ):
                return problem(409, "conflict", "O job está reservado para outro worker (other).")
            cancel = (
                self.cancel_at_heartbeat is not None and self.heartbeats >= self.cancel_at_heartbeat
            )
            if cancel and self.cancel_answered_at is None:
                self.cancel_answered_at = self.heartbeats
            return httpx.Response(
                200, json={"cancelRequested": cancel, "leaseExpiresAt": "2030-01-01T00:00:00Z"}
            )
        if endpoint == "probe":
            if self.probe_conflict:
                code, detail = self.probe_conflict
                if job is not None:
                    job["status"] = "failed"
                return problem(409, code, detail)
            return httpx.Response(200, json={"ok": True, "remainingProcessingSeconds": 3600})
        if endpoint == "status":
            target = body.get("status") if isinstance(body, dict) else None
            if job is not None and target == job["status"] and target in RUNNING_STATUSES:
                # JobStateMachine.IsProgressUpdate: the current running status re-posted
                return httpx.Response(
                    200, json={"id": job_id, "status": target, "progress": body.get("progress")}
                )
            if job is not None:
                if not isinstance(target, str) or not can_transition(
                    job["kind"], job["status"], target
                ):
                    return problem(
                        409,
                        "conflict",
                        f"Transição de status inválida: {job['status']} → {target}.",
                    )
                job["status"] = target
            return httpx.Response(
                200,
                json={"id": job_id, "status": target, "progress": body.get("progress")},
            )
        if endpoint == "events":
            message = body.get("message") if isinstance(body, dict) else None
            if not isinstance(message, str) or not message or len(message) > 1000:
                self.events_rejected += 1  # EventRequestValidator: NotEmpty, MaximumLength(1000)
                return problem(422, "validation_failed", "message: 1 a 1000 caracteres")
            return httpx.Response(201)
        if endpoint == "upload-urls":
            files = []
            for f in body.get("files", []):
                name = f["fileName"]
                files.append(
                    {
                        "fileName": name,
                        "key": f"users/u1/jobs/{job_id}/{name}",
                        "url": f"{STORAGE_BASE}/storage/upload/{name}",
                        "method": "PUT",
                        "headers": {"x-test-signed": "1"},
                    }
                )
            return httpx.Response(200, json={"files": files})
        if endpoint == "analysis":
            return httpx.Response(200, json={})
        if endpoint == "candidates":
            self.candidate_ids = [str(uuid.uuid4()) for _ in body.get("candidates", [])]
            return httpx.Response(200, json={"ids": self.candidate_ids})
        if endpoint == "clips":
            self.clips_created += len(body.get("clips", []))
            clips = [
                {
                    "candidateId": c["candidateId"],
                    "clipId": str(uuid.uuid4()),
                    "renderId": str(uuid.uuid4()),
                }
                for c in body.get("clips", [])
            ]
            return httpx.Response(200, json={"clips": clips})
        if endpoint == "render-result":
            return httpx.Response(200, json={})
        if endpoint == "complete":
            if job is not None:
                job["status"] = "completed"
            return httpx.Response(200, json={"id": job_id, "status": "completed"})
        if endpoint == "fail":
            if job is not None:
                job["status"] = "failed"
            return httpx.Response(200, json={"id": job_id, "status": "failed"})
        return problem(404, "not_found", endpoint)

    def _storage(self, request: httpx.Request, path: str) -> httpx.Response:
        name = path.rsplit("/", 1)[-1]
        if name in self.storage_errors and request.method in ("GET", "PUT"):
            self.calls.append((request.method, path, None))
            return problem(self.storage_errors[name], "storage_error", f"simulated for {name}")
        if request.method == "GET" and path.startswith("/storage/download/"):
            self.calls.append(("GET", path, None))
            if name in self.json_files:
                return httpx.Response(200, json=self.json_files[name])
            source = self.files.get(name)
            if source is None:
                return problem(404, "not_found", name)
            data = source if isinstance(source, bytes) else Path(source).read_bytes()
            headers = {"content-length": str(len(data))}
            if self.stream_delay > 0:
                return httpx.Response(
                    200,
                    stream=_SlowStream(data, self.stream_chunk, self.stream_delay),
                    headers=headers,
                )
            return httpx.Response(200, content=data, headers=headers)
        if request.method == "PUT" and path.startswith("/storage/upload/"):
            data = request.read()
            self.calls.append(("PUT", path, None))
            self.uploads[name] = len(data)
            self.upload_headers[name] = dict(request.headers)
            if self.keep_uploads:
                self.upload_bodies[name] = bytes(data)
            return httpx.Response(200)
        return problem(405, "method_not_allowed", request.method)


def analyze_payload(
    *,
    job_id: str = "job-analyze-1",
    download_url: str,
    size_bytes: int | None = None,
    file_name: str = "source.mp4",
    settings: dict[str, Any] | None = None,
    limits: dict[str, Any] | None = None,
    lease_seconds: int = 180,
) -> dict[str, Any]:
    base_settings = {
        "minClipSeconds": 5,
        "maxClipSeconds": 12,
        "targetClipSeconds": 8,
        "sensitivity": 50,
        "maxCandidates": 10,
        "autoRenderTop": 1,
        "aspectRatio": "9:16",
        "subtitles": {"enabled": True, "style": "bold", "position": "bottom"},
        "language": "auto",
        "framing": {"mode": "auto"},
        "title": None,
    }
    base_settings.update(settings or {})
    base_limits = {
        "maxRenderHeight": 480,
        "maxSourceDurationSeconds": 21600,
        "remainingProcessingSeconds": 3600,
        "transcriptionModel": "base",
        "allowWebcamLayout": True,
    }
    base_limits.update(limits or {})
    return {
        "jobId": job_id,
        "kind": "analyze_source",
        "attempt": 1,
        "userId": "u1",
        "projectId": "p1",
        "leaseSeconds": lease_seconds,
        "source": {
            "assetId": "a1",
            "downloadUrl": download_url,
            "fileName": file_name,
            "contentType": "video/mp4",
            "sizeBytes": size_bytes,
            "durationSeconds": None,
        },
        "settings": base_settings,
        "limits": base_limits,
    }


def render_payload(
    *,
    job_id: str = "job-render-1",
    download_url: str,
    size_bytes: int | None = None,
    transcript_url: str | None = None,
    clip: dict[str, Any] | None = None,
    webcam_region: dict[str, float] | None = None,
) -> dict[str, Any]:
    base_clip = {
        "clipId": "c1",
        "renderId": "r2",
        "version": 2,
        "title": "Jogada incrível",
        "startSeconds": 40.0,
        "endSeconds": 46.0,
        "aspectRatio": "9:16",
        "subtitles": {"enabled": True, "style": "boxed", "position": "center"},
        "framing": {"mode": "auto", "focusX": 0.5, "focusY": 0.5, "webcamRegion": None},
    }
    base_clip.update(clip or {})
    return {
        "jobId": job_id,
        "kind": "render_clip",
        "attempt": 1,
        "userId": "u1",
        "projectId": "p1",
        "leaseSeconds": 180,
        "source": {
            "assetId": "a1",
            "downloadUrl": download_url,
            "fileName": "source.mp4",
            "contentType": "video/mp4",
            "sizeBytes": size_bytes,
            "durationSeconds": 90.0,
        },
        "clip": base_clip,
        "analysis": {"transcriptUrl": transcript_url, "webcamRegion": webcam_region},
        "limits": {"maxRenderHeight": 480, "allowWebcamLayout": True},
    }


LIVE_SETTINGS: dict[str, Any] = {
    "minClipSeconds": 5,
    "maxClipSeconds": 12,
    "targetClipSeconds": 8,
    "sensitivity": 50,
    "maxCandidates": 10,
    "autoRenderTop": 1,
    "aspectRatio": "9:16",
    "subtitles": {"enabled": True, "style": "bold", "position": "bottom"},
    "language": "auto",
    "framing": {"mode": "auto"},
    "title": None,
}


def live_segment_payload(
    *,
    sequence: int,
    file_name: str,
    start: float,
    duration: float,
    size_bytes: int | None = None,
    final: bool = False,
    previous: dict[str, Any] | None = None,
    context: dict[str, Any] | None = None,
    settings: dict[str, Any] | None = None,
    limits: dict[str, Any] | None = None,
    session_id: str = "session-1",
    job_id: str | None = None,
) -> dict[str, Any]:
    """A ``live_segment`` claim payload (``LIVE_CAPTURE.md`` section 3); ``previous`` is
    ``{fileName, start, duration}`` of the previous segment (served by the fake storage)."""
    base_limits = {
        "maxRenderHeight": 480,
        "maxSourceDurationSeconds": 21600,
        "remainingProcessingSeconds": 3600,
        "transcriptionModel": "base",
        "allowWebcamLayout": True,
    }
    base_limits.update(limits or {})
    return {
        "jobId": job_id or f"job-live-{sequence}",
        "kind": "live_segment",
        "attempt": 1,
        "userId": "u1",
        "projectId": "p-live",
        "leaseSeconds": 180,
        "settings": {**LIVE_SETTINGS, **(settings or {})},
        "limits": base_limits,
        "live": {
            "sessionId": session_id,
            "segmentId": f"segment-{sequence}",
            "sequence": sequence,
            "startOffsetSeconds": start,
            "durationSeconds": duration,
            "final": final,
            "segmentSeconds": 25,
        },
        "source": {
            "assetId": f"asset-{sequence}",
            "downloadUrl": f"{STORAGE_BASE}/storage/download/{file_name}",
            "fileName": file_name,
            "contentType": "video/x-matroska",
            "sizeBytes": size_bytes,
            "durationSeconds": duration,
        },
        "previous": (
            {
                "segmentId": f"segment-{sequence - 1}",
                "sequence": sequence - 1,
                "downloadUrl": f"{STORAGE_BASE}/storage/download/{previous['fileName']}",
                "startOffsetSeconds": previous["start"],
                "durationSeconds": previous["duration"],
            }
            if previous is not None
            else None
        ),
        "context": context,
    }
