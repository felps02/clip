"""httpx client for the internal API (``/internal``) and for signed storage URLs.

* every internal call carries ``X-Internal-Api-Key`` and ``X-Worker-Id`` (the id used to
  claim, so the API can reject calls for a lease that now belongs to another worker);
* connection errors, timeouts and 5xx answers are retried with exponential backoff, as
  long as resending cannot apply a change twice. After a read timeout, a dropped
  connection or a 5xx the API may already have applied the request, so each endpoint has a
  retry policy:

  - ``idempotent`` (heartbeat, probe, upload-urls, candidates — replaced as a whole —,
    analysis, render-result): always resent;
  - ``reconcile`` (status, complete, fail): resent, and a ``409`` answering a resend whose
    first attempt may have been applied is "already applied" (the transition happened);
  - ``once`` (claim, clips, events — they create rows): resent only when the request
    certainly never reached the API (connection refused / connect timeout); otherwise
    :class:`ApiUnavailableError` with ``maybe_applied=True`` is raised so the caller can
    reconcile (a claim that may have leased a job is left to the API's lease reaper);
* ``409`` raises :class:`ApiConflictError` (typed, carries the problem ``code``) so the
  worker can stop the job (quota/plan rejection at ``/probe``, lost lease at
  ``/heartbeat``);
* a claimed payload that does not match the contract is failed on the API right away
  (``/fail invalid_payload``: the job is already leased to this worker) and raises
  :class:`InvalidJobPayload`;
* message fields are bounded to the API's validation limits (events 1000 characters,
  heartbeat/status 500);
* transfers to/from signed URLs use a *separate* client without the internal headers;
  they poll ``should_stop`` so a cancelled job never waits for a slow transfer, and every
  failure is a retryable :class:`TransferError` (a fresh claim issues new signed URLs).
"""

from __future__ import annotations

import contextlib
import os
import random
import time
from collections.abc import Callable, Iterator, Sequence
from pathlib import Path
from typing import Any

import httpx
from pydantic import ValidationError

from app import __version__
from app.logging_setup import get_logger
from app.models import (
    SAFE_JOB_ID_RE,
    AnalysisReport,
    AnalysisSummary,
    CandidateInput,
    ClipReport,
    HeartbeatResponse,
    JobPayload,
    ProbeReport,
    ProbeResponse,
    RenderResultReport,
    UploadFileSpec,
    UploadTarget,
)

log = get_logger("api_client")

RETRYABLE_STATUS = frozenset({500, 502, 503, 504})
# Signed-URL transfers: besides 5xx, 408/429 are retried here with backoff. Other 4xx
# (401/403 expired or rejected signature, 404/410 missing object, 413 body over the signed
# size...) are not retried with the same URL. Every transfer failure is reported as
# retryable: it is a storage/infrastructure problem, and a fresh claim of the re-queued job
# issues new signed URLs (the API bounds the attempts).
TRANSFER_RETRY_STATUS = RETRYABLE_STATUS | {408, 429}
CHUNK_SIZE = 1024 * 1024

# Retry policies of the internal endpoints (module docstring).
IDEMPOTENT = "idempotent"
RECONCILE = "reconcile"
ONCE = "once"
# Transport errors raised before the request reached the API: resending is always safe.
NOT_SENT_ERRORS: tuple[type[Exception], ...] = (
    httpx.ConnectError,
    httpx.ConnectTimeout,
    httpx.PoolTimeout,
    httpx.UnsupportedProtocol,
    httpx.LocalProtocolError,
    httpx.ProxyError,
)
# Validation limits of the API (``Features/Internal/Dtos.cs``).
EVENT_MESSAGE_MAX = 1000
PROGRESS_MESSAGE_MAX = 500
FAIL_MESSAGE_MAX = 1000


class ApiError(Exception):
    """Non-retryable answer (4xx other than 409) or retries exhausted."""

    def __init__(
        self,
        message: str,
        *,
        status: int | None = None,
        code: str | None = None,
        detail: str | None = None,
        body: Any = None,
        maybe_applied: bool = False,
    ):
        super().__init__(message)
        self.status = status
        self.code = code
        self.detail = detail
        self.body = body
        # the API may have applied the request although no successful answer arrived
        self.maybe_applied = maybe_applied


class ApiConflictError(ApiError):
    """HTTP 409 (quota/plan rejection, invalid transition, lease owned by another worker)."""


class ApiUnavailableError(ApiError):
    """Connection problems / 5xx after all retries (or not resent: ``maybe_applied``)."""


class TransferError(Exception):
    """Download or upload through a signed URL failed (always retryable at the job level,
    except :class:`TransferAborted`, which is a cancellation)."""

    def __init__(self, message: str, *, status: int | None = None, retryable: bool = True):
        super().__init__(message)
        self.status = status
        self.retryable = retryable


class TransferAborted(TransferError):
    """The transfer was stopped by ``should_stop`` (the job was cancelled): not a media
    error, the caller must take its cancellation path."""

    def __init__(self, message: str = "download aborted"):
        super().__init__(message, retryable=False)


class InvalidJobPayload(Exception):
    """A claimed job whose payload does not match the contract (``JobPayload``).

    The API has already moved the job to ``preparing_media`` and leased it to this worker,
    so :meth:`InternalApiClient.claim` fails it (``/fail {code: invalid_payload,
    retryable: false}``: the payload will not change on a retry) before raising. The loop
    treats it as a finished job, not as an API outage.
    """

    def __init__(self, job_id: str | None, summary: str, *, reported: bool):
        super().__init__(f"invalid job payload ({job_id or '?'}): {summary}")
        self.job_id = job_id
        self.summary = summary
        self.reported = reported


def _validation_summary(exc: ValidationError, limit: int = 5) -> str:
    errors = exc.errors()
    parts = []
    for err in errors[:limit]:
        loc = ".".join(str(p) for p in err.get("loc", ())) or "payload"
        parts.append(f"{loc}: {err.get('msg', 'invalid')}")
    if len(errors) > limit:
        parts.append(f"(+{len(errors) - limit})")
    return "; ".join(parts)[:600]


def _utf16_units(ch: str) -> int:
    return 2 if ord(ch) > 0xFFFF else 1


def _bounded(message: str | None, limit: int) -> str | None:
    """Cut ``message`` to the API's ``MaximumLength(limit)``, which counts UTF-16 code
    units (C# strings): a character outside the BMP (an emoji in a title) counts twice."""
    if message is None:
        return None
    if sum(_utf16_units(ch) for ch in message) <= limit:
        return message
    out: list[str] = []
    used = 0
    for ch in message:
        units = _utf16_units(ch)
        if used + units > limit - 1:  # keep room for the ellipsis
            break
        out.append(ch)
        used += units
    return "".join(out) + "…"


def _check_stop(should_stop: Callable[[], bool] | None, message: str) -> None:
    if should_stop is not None and should_stop():
        raise TransferAborted(message)


def _close_all(responses: list[httpx.Response]) -> None:
    for response in list(responses):
        with contextlib.suppress(Exception):
            response.close()


def _problem(response: httpx.Response) -> tuple[str | None, str | None, Any]:
    try:
        body = response.json()
    except ValueError:
        return None, response.text[:500] or None, None
    if isinstance(body, dict):
        code = body.get("code")
        detail = body.get("detail") or body.get("title")
        return (str(code) if code else None), (str(detail) if detail else None), body
    return None, None, body


class InternalApiClient:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        worker_id: str,
        *,
        timeout: float = 30.0,
        max_retries: int = 4,
        backoff_base: float = 0.5,
        backoff_max: float = 8.0,
        transport: httpx.BaseTransport | None = None,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.worker_id = worker_id
        self.max_retries = max_retries
        self.backoff_base = backoff_base
        self.backoff_max = backoff_max
        self._sleep = sleep
        user_agent = f"clipforge-processing/{__version__}"
        self._client = httpx.Client(
            base_url=self.base_url,
            headers={
                "X-Internal-Api-Key": api_key,
                "X-Worker-Id": worker_id,
                "User-Agent": user_agent,
            },
            timeout=httpx.Timeout(timeout, connect=min(10.0, timeout)),
            transport=transport,
        )
        # Signed URLs (storage) never receive the internal key nor the worker id.
        self._transfer = httpx.Client(
            headers={"User-Agent": user_agent},
            timeout=httpx.Timeout(120.0, connect=15.0),
            transport=transport,
            follow_redirects=True,
        )

    def close(self) -> None:
        self._client.close()
        self._transfer.close()

    def __enter__(self) -> InternalApiClient:
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # ------------------------------------------------------------------ low level ----
    def _backoff(self, attempt: int) -> float:
        delay = min(self.backoff_max, self.backoff_base * (2**attempt))
        return delay + random.uniform(0, delay * 0.25)

    def _request(
        self,
        method: str,
        path: str,
        *,
        json: Any = None,
        retry: str = IDEMPOTENT,
    ) -> httpx.Response:
        """Send an internal request with the endpoint's retry policy (module docstring)."""
        last_error: str = ""
        maybe_applied = False  # an earlier attempt may have been applied by the API
        for attempt in range(self.max_retries + 1):
            try:
                response = self._client.request(method, path, json=json)
            except httpx.TransportError as exc:  # connect/read/write/timeout errors
                last_error = f"{type(exc).__name__}: {exc}"
                if not isinstance(exc, NOT_SENT_ERRORS):
                    maybe_applied = True
                    if retry == ONCE:
                        log.warning("api.not_resent", method=method, path=path, error=last_error)
                        raise ApiUnavailableError(
                            f"{method} {path} failed: {last_error} (not resent: the API may "
                            "have applied it)",
                            maybe_applied=True,
                        ) from exc
                if attempt < self.max_retries:
                    delay = self._backoff(attempt)
                    log.warning(
                        "api.retry", method=method, path=path, attempt=attempt + 1, error=last_error
                    )
                    self._sleep(delay)
                    continue
                raise ApiUnavailableError(
                    f"{method} {path} failed: {last_error}", maybe_applied=maybe_applied
                ) from exc

            if response.status_code in RETRYABLE_STATUS:
                last_error = f"HTTP {response.status_code}"
                code, detail, body = _problem(response)
                # 500/502/504: the handler may have run and committed part of the work before
                # the error (or the gateway gave up on it); 503 means the API (or a proxy in
                # front of it) refused the request, which is safe to resend
                unsure = response.status_code != 503
                maybe_applied = maybe_applied or unsure
                if (retry != ONCE or not unsure) and attempt < self.max_retries:
                    self._sleep(self._backoff(attempt))
                    log.warning(
                        "api.retry", method=method, path=path, attempt=attempt + 1, error=last_error
                    )
                    continue
                raise ApiUnavailableError(
                    f"{method} {path} failed: {last_error}",
                    status=response.status_code,
                    code=code,
                    detail=detail,
                    body=body,
                    maybe_applied=maybe_applied,
                )
            if response.status_code == 409:
                code, detail, body = _problem(response)
                if retry == RECONCILE and maybe_applied:
                    # an earlier attempt without a clear answer was applied: this resend
                    # repeats a transition that happened (or finishes a finished job)
                    log.info(
                        "api.conflict_after_resend",
                        method=method,
                        path=path,
                        code=code,
                        detail=detail,
                    )
                    return response
                raise ApiConflictError(
                    f"{method} {path} conflict ({code}): {detail}",
                    status=409,
                    code=code,
                    detail=detail,
                    body=body,
                )
            if response.status_code >= 400:
                code, detail, body = _problem(response)
                raise ApiError(
                    f"{method} {path} failed with HTTP {response.status_code} ({code}): {detail}",
                    status=response.status_code,
                    code=code,
                    detail=detail,
                    body=body,
                )
            return response
        raise ApiUnavailableError(f"{method} {path} failed: {last_error}")  # pragma: no cover

    @staticmethod
    def _json(response: httpx.Response) -> Any:
        if response.status_code == 204 or not response.content:
            return None
        try:
            return response.json()
        except ValueError:
            return None

    # ------------------------------------------------------------------ endpoints ----
    def claim(
        self, kinds: Sequence[str], capabilities: dict[str, Any] | None = None
    ) -> JobPayload | None:
        """Claim the next job. Not resent after a failure the API may have applied: a claim
        that did lease a job we never received is re-queued by the API's lease reaper,
        whereas a resend would lease a second job to this worker."""
        response = self._request(
            "POST",
            f"/internal/workers/{self.worker_id}/claim",
            json={"kinds": list(kinds), "capabilities": capabilities or {}},
            retry=ONCE,
        )
        data = self._json(response)
        if response.status_code == 204 or not data:
            return None
        try:
            return JobPayload.model_validate(data)
        except ValidationError as exc:
            # The job is already preparing_media and leased to us: fail it explicitly
            # (non-retryable, the payload will not change) instead of abandoning it to the
            # reaper, which would re-queue and re-claim it until the attempts run out.
            raw_id = data.get("jobId") if isinstance(data, dict) else None
            job_id = raw_id if isinstance(raw_id, str) and SAFE_JOB_ID_RE.match(raw_id) else None
            summary = _validation_summary(exc)
            reported = False
            if job_id is not None:
                try:
                    self.fail(
                        job_id,
                        "invalid_payload",
                        "O worker não reconheceu os dados do job (contrato incompatível): "
                        + summary,
                        retryable=False,
                    )
                    reported = True
                except ApiError as fail_exc:
                    log.warning(
                        "claim.invalid_payload_unreported",
                        job_id=job_id,
                        error=str(fail_exc)[:200],
                    )
            raise InvalidJobPayload(job_id, summary, reported=reported) from exc

    def heartbeat(
        self, job_id: str, progress: int | None = None, message: str | None = None
    ) -> HeartbeatResponse:
        body: dict[str, Any] = {}
        if progress is not None:
            body["progress"] = int(progress)
        if message:
            body["message"] = _bounded(message, PROGRESS_MESSAGE_MAX)
        response = self._request("POST", f"/internal/jobs/{job_id}/heartbeat", json=body)
        data = self._json(response) or {}
        return HeartbeatResponse.model_validate(data)

    def probe(self, job_id: str, report: ProbeReport) -> ProbeResponse:
        response = self._request("POST", f"/internal/jobs/{job_id}/probe", json=report.to_api())
        data = self._json(response) or {}
        return ProbeResponse.model_validate(data)

    def status(
        self,
        job_id: str,
        status: str,
        progress: int | None = None,
        message: str | None = None,
    ) -> Any:
        body: dict[str, Any] = {"status": status}
        if progress is not None:
            body["progress"] = int(progress)
        if message:
            body["message"] = _bounded(message, PROGRESS_MESSAGE_MAX)
        response = self._request(
            "POST", f"/internal/jobs/{job_id}/status", json=body, retry=RECONCILE
        )
        return self._json(response)

    def event(
        self, job_id: str, level: str, message: str, data: dict[str, Any] | None = None
    ) -> None:
        """Append a job event (best effort: not resent when it may have been stored, so a
        timeout never duplicates a line of the job's event log)."""
        body: dict[str, Any] = {"level": level, "message": _bounded(message, EVENT_MESSAGE_MAX)}
        if data:
            body["data"] = data
        self._request("POST", f"/internal/jobs/{job_id}/events", json=body, retry=ONCE)

    def upload_urls(self, job_id: str, files: Sequence[UploadFileSpec]) -> list[UploadTarget]:
        response = self._request(
            "POST",
            f"/internal/jobs/{job_id}/upload-urls",
            json={"files": [f.to_api() for f in files]},
        )
        data = self._json(response) or {}
        return [UploadTarget.model_validate(item) for item in data.get("files", [])]

    def analysis(self, job_id: str, report: AnalysisReport) -> None:
        self._request("POST", f"/internal/jobs/{job_id}/analysis", json=report.to_api())

    def candidates(self, job_id: str, candidates: Sequence[CandidateInput]) -> list[str]:
        response = self._request(
            "POST",
            f"/internal/jobs/{job_id}/candidates",
            json={"candidates": [c.to_api() for c in candidates]},
        )
        data = self._json(response) or {}
        return [str(i) for i in data.get("ids", [])]

    def clips(self, job_id: str, clips: Sequence[ClipReport]) -> list[dict[str, Any]]:
        """Register rendered clips. The API creates a clip per entry, so a request that may
        have been applied is never resent (it would duplicate the clips):
        :class:`ApiUnavailableError` with ``maybe_applied=True`` lets the caller decide."""
        response = self._request(
            "POST",
            f"/internal/jobs/{job_id}/clips",
            json={"clips": [c.to_api() for c in clips]},
            retry=ONCE,
        )
        data = self._json(response) or {}
        return list(data.get("clips", []))

    def render_result(self, job_id: str, report: RenderResultReport) -> None:
        self._request("POST", f"/internal/jobs/{job_id}/render-result", json=report.to_api())

    def complete(
        self,
        job_id: str,
        processed_seconds: float,
        summary: AnalysisSummary | dict[str, Any] | None,
    ) -> Any:
        body: dict[str, Any] = {"processedSeconds": round(float(processed_seconds), 3)}
        if summary is not None:
            body["summary"] = summary.to_api() if isinstance(summary, AnalysisSummary) else summary
        response = self._request(
            "POST", f"/internal/jobs/{job_id}/complete", json=body, retry=RECONCILE
        )
        return self._json(response)

    def fail(
        self,
        job_id: str,
        code: str,
        message: str,
        *,
        retryable: bool,
        processed_seconds: float | None = None,
    ) -> Any:
        body: dict[str, Any] = {
            "code": code,
            "message": _bounded(message, FAIL_MESSAGE_MAX),
            "retryable": bool(retryable),
        }
        if processed_seconds is not None:
            body["processedSeconds"] = round(float(processed_seconds), 3)
        response = self._request(
            "POST", f"/internal/jobs/{job_id}/fail", json=body, retry=RECONCILE
        )
        return self._json(response)

    # ------------------------------------------------------------------ transfers ----
    def download_to_file(
        self,
        url: str,
        dest: Path,
        *,
        progress: Callable[[int, int | None], None] | None = None,
        should_stop: Callable[[], bool] | None = None,
        expected_size: int | None = None,
        on_cancel: Callable[[Callable[[], None]], None] | None = None,
    ) -> int:
        """Stream a signed download URL to ``dest`` (written to ``dest.part`` first).

        ``should_stop`` is polled between chunks and before every attempt; when it returns
        ``True`` the transfer is abandoned with :class:`TransferAborted` (never retried).
        ``on_cancel`` (``CancelToken.on_cancel``) registers a callback that closes the
        in-flight response, so a stalled download does not sit in a blocking read until the
        read timeout once the job is cancelled.
        """
        dest.parent.mkdir(parents=True, exist_ok=True)
        part = dest.with_name(dest.name + ".part")
        attempts = self.max_retries + 1
        inflight: list[httpx.Response] = []
        if on_cancel is not None:
            on_cancel(lambda: _close_all(inflight))
        for attempt in range(attempts):
            _check_stop(should_stop, "download aborted")
            try:
                with self._transfer.stream("GET", url) as response:
                    inflight.append(response)
                    if response.status_code >= 400:
                        retryable = response.status_code in TRANSFER_RETRY_STATUS
                        if retryable and attempt < attempts - 1:
                            self._sleep(self._backoff(attempt))
                            continue
                        raise TransferError(
                            f"download failed with HTTP {response.status_code}",
                            status=response.status_code,
                            retryable=True,
                        )
                    total_header = response.headers.get("content-length")
                    total = int(total_header) if total_header and total_header.isdigit() else None
                    if total is None:
                        total = expected_size
                    written = 0
                    with part.open("wb") as fh:
                        for chunk in response.iter_bytes(CHUNK_SIZE):
                            if should_stop is not None and should_stop():
                                raise TransferAborted()
                            fh.write(chunk)
                            written += len(chunk)
                            if progress is not None:
                                progress(written, total)
                os.replace(part, dest)
                return written
            except (httpx.TransportError, httpx.StreamError) as exc:
                # A cancelled job closes the response from the heartbeat thread, which
                # surfaces here as a read/stream error: the cancellation is the outcome.
                _check_stop(should_stop, "download aborted")
                if attempt < attempts - 1:
                    self._sleep(self._backoff(attempt))
                    continue
                raise TransferError(f"download failed: {exc}", retryable=True) from exc
            finally:
                inflight.clear()
                if part.exists() and not dest.exists():
                    with contextlib.suppress(OSError):
                        part.unlink()
        raise TransferError("download failed", retryable=True)  # pragma: no cover

    def download_json(self, url: str, *, should_stop: Callable[[], bool] | None = None) -> Any:
        attempts = self.max_retries + 1
        for attempt in range(attempts):
            _check_stop(should_stop, "download aborted")
            try:
                response = self._transfer.get(url)
            except httpx.TransportError as exc:
                _check_stop(should_stop, "download aborted")
                if attempt < attempts - 1:
                    self._sleep(self._backoff(attempt))
                    continue
                raise TransferError(f"download failed: {exc}", retryable=True) from exc
            if response.status_code in TRANSFER_RETRY_STATUS and attempt < attempts - 1:
                self._sleep(self._backoff(attempt))
                continue
            if response.status_code >= 400:
                raise TransferError(
                    f"download failed with HTTP {response.status_code}",
                    status=response.status_code,
                    retryable=True,
                )
            return response.json()
        raise TransferError("download failed", retryable=True)  # pragma: no cover

    def upload_file(
        self,
        target: UploadTarget,
        path: Path,
        content_type: str,
        *,
        should_stop: Callable[[], bool] | None = None,
    ) -> None:
        """PUT a local file to a signed upload URL, streaming the body.

        ``should_stop`` is polled between the streamed chunks and before every attempt;
        ``True`` abandons the upload with :class:`TransferAborted` (never retried).
        """
        size = path.stat().st_size
        headers = {k: str(v) for k, v in (target.headers or {}).items()}
        headers.setdefault("Content-Type", content_type)
        headers["Content-Length"] = str(size)
        method = (target.method or "PUT").upper()
        attempts = self.max_retries + 1
        for attempt in range(attempts):
            _check_stop(should_stop, "upload aborted")
            try:
                response = self._transfer.request(
                    method, target.url, content=_iter_file(path, should_stop), headers=headers
                )
            except httpx.TransportError as exc:
                _check_stop(should_stop, "upload aborted")
                if attempt < attempts - 1:
                    self._sleep(self._backoff(attempt))
                    continue
                raise TransferError(f"upload failed: {exc}", retryable=True) from exc
            if response.status_code in TRANSFER_RETRY_STATUS and attempt < attempts - 1:
                self._sleep(self._backoff(attempt))
                continue
            if response.status_code >= 400:
                raise TransferError(
                    f"upload of {path.name} failed with HTTP {response.status_code}",
                    status=response.status_code,
                    retryable=True,
                )
            return
        raise TransferError("upload failed", retryable=True)  # pragma: no cover


def _iter_file(path: Path, should_stop: Callable[[], bool] | None = None) -> Iterator[bytes]:
    with path.open("rb") as fh:
        while True:
            _check_stop(should_stop, "upload aborted")
            chunk = fh.read(CHUNK_SIZE)
            if not chunk:
                break
            yield chunk
