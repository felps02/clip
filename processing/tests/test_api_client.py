from __future__ import annotations

import json
import threading
from collections.abc import Callable
from pathlib import Path

import httpx
import pytest

from app.api_client import (
    CHUNK_SIZE,
    ApiConflictError,
    ApiError,
    ApiUnavailableError,
    InternalApiClient,
    InvalidJobPayload,
    TransferAborted,
    TransferError,
    _bounded,
)
from app.models import (
    ClipReport,
    FramingSettings,
    ProbeReport,
    RenderInfo,
    SubtitleSettings,
    UploadTarget,
)
from app.worker.errors import CancelToken
from tests.fake_api import API_BASE, STORAGE_BASE, FakeInternalApi, analyze_payload

PROBE = ProbeReport(
    duration_seconds=10, width=640, height=360, fps=25, has_audio=True, has_video=True
)


def make_client(
    fake: FakeInternalApi, sleeps: list[float] | None = None, **kwargs: object
) -> InternalApiClient:
    return InternalApiClient(
        API_BASE,
        "test-key",
        "worker-test",
        transport=fake.transport(),
        sleep=(sleeps.append if sleeps is not None else (lambda _s: None)),
        backoff_base=0.01,
        **kwargs,  # type: ignore[arg-type]
    )


def test_headers_and_claim() -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")]
    )
    with make_client(fake) as client:
        payload = client.claim(["analyze_source", "render_clip"])
        assert payload is not None and payload.kind == "analyze_source"
        assert payload.settings.aspect_ratio == "9:16"
        assert client.claim(["analyze_source"]) is None  # 204
    assert fake.bad_key_calls == 0
    method, path, body = fake.calls[0]
    assert (method, path) == ("POST", "/internal/workers/worker-test/claim")
    assert body["kinds"] == ["analyze_source", "render_clip"]
    assert fake.worker_ids == ["worker-test", "worker-test"]  # X-Worker-Id on every call


def test_calls_for_a_lease_owned_by_another_worker_are_conflicts() -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")]
    )
    with make_client(fake) as client:
        payload = client.claim(["analyze_source"])
        assert payload is not None
        assert client.heartbeat(payload.job_id, 1, "ok").cancel_requested is False
    other = InternalApiClient(
        API_BASE, "test-key", "worker-other", transport=fake.transport(), sleep=lambda _s: None
    )
    with other:
        with pytest.raises(ApiConflictError):
            other.heartbeat(payload.job_id, 2, "stale worker")
        with pytest.raises(ApiConflictError):
            other.fail(payload.job_id, "worker_error", "stale worker", retryable=True)
    assert fake.job_status(payload.job_id) == "preparing_media"  # the stale worker changed nothing


def test_wrong_key_is_an_api_error() -> None:
    fake = FakeInternalApi(api_key="other")
    with make_client(fake) as client, pytest.raises(ApiError) as info:
        client.claim(["analyze_source"])
    assert info.value.status == 401
    assert fake.bad_key_calls == 1


def test_retries_on_5xx_with_backoff() -> None:
    fake = FakeInternalApi(flaky={"probe": [503, 502]})
    sleeps: list[float] = []
    with make_client(fake, sleeps) as client:
        response = client.probe("job-1", PROBE)
    assert response.ok is True
    assert len(fake.bodies("probe")) == 3
    assert len(sleeps) == 2
    assert sleeps[1] >= sleeps[0]


def test_retries_exhausted_raise_unavailable() -> None:
    fake = FakeInternalApi(flaky={"probe": [500] * 10})
    with make_client(fake, max_retries=2) as client, pytest.raises(ApiUnavailableError):
        client.probe("job-1", PROBE)
    assert len(fake.bodies("probe")) == 3


def test_retries_on_connection_errors() -> None:
    attempts = {"n": 0}
    fake = FakeInternalApi()

    def handler(request: httpx.Request) -> httpx.Response:
        attempts["n"] += 1
        if attempts["n"] <= 2:
            raise httpx.ConnectError("refused", request=request)
        return fake.handler(request)

    client = InternalApiClient(
        API_BASE, "test-key", "w", transport=httpx.MockTransport(handler), sleep=lambda _s: None
    )
    client.heartbeat("job-1", 10, "x")
    assert attempts["n"] == 3
    assert fake.heartbeats == 1


def test_409_raises_conflict_with_code_without_retry() -> None:
    fake = FakeInternalApi(probe_conflict=("quota_exceeded", "sem minutos"))
    with make_client(fake) as client, pytest.raises(ApiConflictError) as info:
        client.probe("job-1", PROBE)
    assert info.value.code == "quota_exceeded"
    assert info.value.detail == "sem minutos"
    assert info.value.status == 409
    assert len(fake.bodies("probe")) == 1


def test_other_4xx_are_not_retried() -> None:
    fake = FakeInternalApi(flaky={"status": [422]})
    with make_client(fake) as client, pytest.raises(ApiError) as info:
        client.status("job-1", "analyzing", 10, "x")
    assert info.value.status == 422
    assert len(fake.bodies("status")) == 1


def test_report_bodies_are_camel_case() -> None:
    fake = FakeInternalApi()
    with make_client(fake) as client:
        client.status("job-1", "analyzing", 12, "Transcrevendo")
        client.event("job-1", "warning", "x", {"a": 1})
        client.complete("job-1", 90.0, {"candidatesFound": 3})
        client.fail(
            "job-1", "invalid_media", "arquivo sem vídeo", retryable=False, processed_seconds=0
        )
    assert fake.bodies("status") == [
        {"status": "analyzing", "progress": 12, "message": "Transcrevendo"}
    ]
    assert fake.bodies("events") == [{"level": "warning", "message": "x", "data": {"a": 1}}]
    assert fake.bodies("complete") == [
        {"processedSeconds": 90.0, "summary": {"candidatesFound": 3}}
    ]
    assert fake.bodies("fail") == [
        {
            "code": "invalid_media",
            "message": "arquivo sem vídeo",
            "retryable": False,
            "processedSeconds": 0.0,
        }
    ]


def test_download_streams_to_file_with_progress(tmp_path: Path) -> None:
    data = bytes(range(256)) * 5000
    fake = FakeInternalApi(files={"big.bin": data})
    seen: list[tuple[int, int | None]] = []
    with make_client(fake) as client:
        dest = tmp_path / "sub" / "source.bin"
        written = client.download_to_file(
            f"{STORAGE_BASE}/storage/download/big.bin",
            dest,
            progress=lambda w, t: seen.append((w, t)),
        )
    assert written == len(data)
    assert dest.read_bytes() == data
    assert not dest.with_name("source.bin.part").exists()
    assert seen and seen[-1] == (len(data), len(data))


def test_download_stopped_by_the_cancel_token_raises_transfer_aborted(tmp_path: Path) -> None:
    fake = FakeInternalApi(files={"big.bin": b"x" * (2 * CHUNK_SIZE + 1)})
    dest = tmp_path / "x.bin"
    polls = {"n": 0}

    def cancelled_after_the_first_chunk() -> bool:
        polls["n"] += 1
        return polls["n"] > 1  # 1: before the attempt, 2: once the first chunk arrived

    with make_client(fake) as client, pytest.raises(TransferAborted) as info:
        client.download_to_file(
            f"{STORAGE_BASE}/storage/download/big.bin",
            dest,
            should_stop=cancelled_after_the_first_chunk,
        )
    assert isinstance(info.value, TransferError) and info.value.retryable is False
    assert not dest.exists() and not dest.with_name("x.bin.part").exists()
    assert fake.sequence() == ["GET:big.bin"]  # an aborted transfer is not retried
    # a job cancelled before the transfer starts never issues the request
    with make_client(fake) as client, pytest.raises(TransferAborted):
        client.download_to_file(
            f"{STORAGE_BASE}/storage/download/big.bin", dest, should_stop=lambda: True
        )
    assert fake.sequence() == ["GET:big.bin"]


def test_download_404_is_not_resent_but_retryable_for_the_job(tmp_path: Path) -> None:
    """A 404 is not retried with the same signed URL, but it is a storage problem: the job
    is re-queued (retryable) and a fresh claim signs a new URL."""
    fake = FakeInternalApi()
    sleeps: list[float] = []
    with make_client(fake, sleeps) as client, pytest.raises(TransferError) as info:
        client.download_to_file(f"{STORAGE_BASE}/storage/download/missing.mp4", tmp_path / "x.mp4")
    assert info.value.retryable is True
    assert info.value.status == 404
    assert fake.sequence() == ["GET:missing.mp4"] and sleeps == []


def test_upload_streams_with_length_and_signed_headers_only(tmp_path: Path) -> None:
    payload = tmp_path / "clip.mp4"
    payload.write_bytes(b"x" * 10_000)
    fake = FakeInternalApi()
    target = UploadTarget(
        file_name="clip.mp4",
        key="k",
        url=f"{STORAGE_BASE}/storage/upload/clip.mp4",
        headers={"x-test-signed": "1"},
    )
    with make_client(fake) as client:
        client.upload_file(target, payload, "video/mp4")
    assert fake.uploads["clip.mp4"] == 10_000
    headers = fake.upload_headers["clip.mp4"]
    assert headers["content-length"] == "10000"
    assert headers["content-type"] == "video/mp4"
    assert headers["x-test-signed"] == "1"
    assert "x-internal-api-key" not in headers


def test_download_json_and_problem_parsing() -> None:
    fake = FakeInternalApi(json_files={"transcript.json": {"available": True, "segments": []}})
    with make_client(fake) as client:
        assert client.download_json(f"{STORAGE_BASE}/storage/download/transcript.json") == {
            "available": True,
            "segments": [],
        }
        urls = client.upload_urls("job-1", [])
        assert urls == []
        ids = client.candidates("job-1", [])
        assert ids == []
    assert json.loads(json.dumps(fake.bodies("candidates"))) == [{"candidates": []}]


def test_claim_with_an_invalid_payload_fails_the_job_and_raises() -> None:
    """The claim already leased the job to us: contract drift (an aspect ratio this worker
    does not know) must fail that one job on the API, not poison the loop."""
    payload = analyze_payload(
        download_url=f"{STORAGE_BASE}/storage/download/x.mp4", settings={"aspectRatio": "4:5"}
    )
    fake = FakeInternalApi(payloads=[payload])
    with make_client(fake) as client, pytest.raises(InvalidJobPayload) as info:
        client.claim(["analyze_source"])
    assert info.value.job_id == "job-analyze-1" and info.value.reported is True
    assert "settings.aspectRatio" in info.value.summary
    assert fake.sequence() == ["claim", "fail"]
    fail = fake.bodies("fail")[0]
    assert fail["code"] == "invalid_payload" and fail["retryable"] is False
    assert "settings.aspectRatio" in fail["message"] and "processedSeconds" not in fail
    assert fake.job_status("job-analyze-1") == "failed"
    assert fake.worker_ids == ["worker-test", "worker-test"]


def test_claim_with_an_invalid_payload_and_no_usable_job_id_only_raises() -> None:
    payload = analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")
    payload["jobId"] = "../evil"
    del payload["source"]
    fake = FakeInternalApi(payloads=[payload])
    with make_client(fake) as client, pytest.raises(InvalidJobPayload) as info:
        client.claim(["analyze_source"])
    assert info.value.job_id is None and info.value.reported is False
    assert "source" in info.value.summary
    assert fake.sequence() == ["claim"]  # an unsafe id never reaches a URL


def test_upload_stopped_by_the_cancel_token_raises_transfer_aborted(tmp_path: Path) -> None:
    clip = tmp_path / "clip.mp4"
    clip.write_bytes(b"x" * (2 * CHUNK_SIZE + 1))
    fake = FakeInternalApi()
    target = UploadTarget(
        file_name="clip.mp4", key="k", url=f"{STORAGE_BASE}/storage/upload/clip.mp4"
    )
    polls = {"n": 0}

    def cancelled_after_the_first_chunk() -> bool:
        polls["n"] += 1
        return polls["n"] > 2  # 1: before the attempt, 2: before chunk 1, 3: before chunk 2

    sleeps: list[float] = []
    with make_client(fake, sleeps) as client, pytest.raises(TransferAborted) as info:
        client.upload_file(target, clip, "video/mp4", should_stop=cancelled_after_the_first_chunk)
    assert info.value.retryable is False and "upload" in str(info.value)
    assert "clip.mp4" not in fake.uploads  # the body never reached the storage
    assert sleeps == []  # an aborted transfer is not retried


def test_download_is_not_retried_once_the_job_is_cancelled(tmp_path: Path) -> None:
    cancel = CancelToken()

    def handler(request: httpx.Request) -> httpx.Response:
        cancel.cancel()  # the user cancels while the connection keeps failing
        raise httpx.ReadError("connection reset", request=request)

    sleeps: list[float] = []
    client = InternalApiClient(
        API_BASE,
        "test-key",
        "w",
        transport=httpx.MockTransport(handler),
        sleep=sleeps.append,
        backoff_base=0.01,
    )
    with client, pytest.raises(TransferAborted):
        client.download_to_file(
            f"{STORAGE_BASE}/storage/download/x.mp4",
            tmp_path / "x.mp4",
            should_stop=lambda: cancel.cancelled,
        )
    assert sleeps == []


def test_download_registers_a_cancel_hook_that_closes_the_stream(tmp_path: Path) -> None:
    fake = FakeInternalApi(
        files={"slow.bin": b"x" * (4 * CHUNK_SIZE)}, stream_delay=0.05, stream_chunk=64 * 1024
    )
    cancel = CancelToken()
    hooks: list[Callable[[], None]] = []

    def register(callback: Callable[[], None]) -> None:
        hooks.append(callback)
        cancel.on_cancel(callback)

    threading.Timer(0.3, cancel.cancel).start()
    dest = tmp_path / "x.bin"
    with make_client(fake) as client, pytest.raises(TransferAborted):
        client.download_to_file(
            f"{STORAGE_BASE}/storage/download/slow.bin",
            dest,
            should_stop=lambda: cancel.cancelled,
            on_cancel=register,
        )
    assert len(hooks) == 1 and cancel.cancelled
    assert not dest.exists() and not dest.with_name("x.bin.part").exists()


@pytest.mark.parametrize("status", [401, 403, 400, 404, 410, 413])
def test_transfer_4xx_are_never_retried_here_and_always_retryable_for_the_api(
    tmp_path: Path, status: int
) -> None:
    """A 4xx from a signed URL is not retried with the same URL (the answer will not
    change), but it is a storage/infrastructure problem: always retryable for the job (a
    fresh claim issues new signed URLs; the API bounds the attempts)."""
    retryable = True
    fake = FakeInternalApi(storage_errors={"clip.mp4": status, "src.mp4": status})
    clip = tmp_path / "clip.mp4"
    clip.write_bytes(b"x" * 100)
    target = UploadTarget(
        file_name="clip.mp4", key="k", url=f"{STORAGE_BASE}/storage/upload/clip.mp4"
    )
    sleeps: list[float] = []
    with make_client(fake, sleeps) as client:
        with pytest.raises(TransferError) as up:
            client.upload_file(target, clip, "video/mp4")
        with pytest.raises(TransferError) as down:
            client.download_to_file(
                f"{STORAGE_BASE}/storage/download/src.mp4", tmp_path / "src.mp4"
            )
    assert (up.value.status, up.value.retryable) == (status, retryable)
    assert (down.value.status, down.value.retryable) == (status, retryable)
    assert fake.sequence() == ["PUT:clip.mp4", "GET:src.mp4"] and sleeps == []


@pytest.mark.parametrize("status", [500, 503])
def test_transfer_5xx_and_network_errors_are_retried_then_retryable(
    tmp_path: Path, status: int
) -> None:
    fake = FakeInternalApi(storage_errors={"src.mp4": status})
    sleeps: list[float] = []
    with make_client(fake, sleeps, max_retries=2) as client, pytest.raises(TransferError) as info:
        client.download_to_file(f"{STORAGE_BASE}/storage/download/src.mp4", tmp_path / "s.mp4")
    assert info.value.retryable is True and info.value.status == status
    assert fake.sequence() == ["GET:src.mp4"] * 3 and len(sleeps) == 2


# ---------------------------------------------------------------- retry policies ----


def claimed(fake: FakeInternalApi, client: InternalApiClient) -> str:
    payload = client.claim(["analyze_source"])
    assert payload is not None
    return payload.job_id


def test_claim_is_not_resent_when_the_api_may_have_leased_a_job() -> None:
    """A read timeout after the claim reached the API: resending would lease a second job
    to this worker while the first one waits for the lease reaper."""
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")],
        answer_lost={"claim": 1},
    )
    with make_client(fake) as client, pytest.raises(ApiUnavailableError) as info:
        client.claim(["analyze_source"])
    assert info.value.maybe_applied is True
    assert fake.sequence() == ["claim"]
    assert fake.job_status("job-analyze-1") == "preparing_media"  # leased: the reaper's job


def test_claim_is_resent_when_the_request_never_reached_the_api() -> None:
    attempts = {"n": 0}
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")],
        flaky={"claim": [503]},  # refused before running the handler: safe to resend
    )

    def handler(request: httpx.Request) -> httpx.Response:
        attempts["n"] += 1
        if attempts["n"] == 1:
            raise httpx.ConnectError("refused", request=request)
        return fake.handler(request)

    client = InternalApiClient(
        API_BASE, "test-key", "w", transport=httpx.MockTransport(handler), sleep=lambda _s: None
    )
    with client:
        payload = client.claim(["analyze_source"])
    assert payload is not None and attempts["n"] == 3


def test_claim_is_not_resent_after_a_500() -> None:
    fake = FakeInternalApi(flaky={"claim": [500, 500]})
    with make_client(fake) as client, pytest.raises(ApiUnavailableError) as info:
        client.claim(["analyze_source"])
    assert info.value.maybe_applied is True and info.value.status == 500
    assert fake.sequence() == ["claim"]


def test_status_is_resent_after_a_lost_answer_and_the_repeat_is_harmless() -> None:
    """The API applied ``analyzing`` but the answer timed out: the resend is the current
    running status again, which the API accepts as a progress update."""
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")],
        answer_lost={"status": 1},
    )
    with make_client(fake) as client:
        job_id = claimed(fake, client)
        client.status(job_id, "analyzing", 10, "Analisando áudio…")
        client.status(job_id, "detecting_moments", 60, "Detectando momentos…")
    assert fake.statuses() == ["analyzing", "analyzing", "detecting_moments"]
    assert fake.job_status(job_id) == "detecting_moments"


@pytest.mark.parametrize("endpoint", ["status", "complete", "fail"])
def test_a_409_answering_a_resend_of_an_applied_request_counts_as_applied(endpoint: str) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")],
        answer_lost={endpoint: 1},
    )
    with make_client(fake) as client:
        job_id = claimed(fake, client)
        if endpoint == "status":
            client.status(job_id, "cancelled", None, "Cancelado pelo usuário")
            expected = "cancelled"
        elif endpoint == "complete":
            client.complete(job_id, 10.0, None)
            expected = "completed"
        else:
            client.fail(job_id, "worker_error", "boom", retryable=True)
            expected = "failed"
    assert len(fake.bodies(endpoint)) == 2  # resent once, the 409 did not raise
    assert fake.job_status(job_id) == expected


def test_a_409_on_a_first_attempt_is_still_a_conflict() -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")]
    )
    with make_client(fake) as client:
        job_id = claimed(fake, client)
        client.status(job_id, "analyzing", 10, "x")
        with pytest.raises(ApiConflictError):
            client.status(job_id, "preparing_media", 10, "x")  # backwards


def test_clips_are_never_resent_when_they_may_have_been_created() -> None:
    fake = FakeInternalApi(answer_lost={"clips": 1})
    report = ClipReport(
        candidate_id="c1",
        aspect_ratio="9:16",
        start_seconds=1.0,
        end_seconds=9.0,
        subtitles=SubtitleSettings(),
        framing=FramingSettings(),
        render=RenderInfo(
            storage_key="k", width=720, height=1280, duration_seconds=8.0, size_bytes=10
        ),
    )
    with make_client(fake) as client, pytest.raises(ApiUnavailableError) as info:
        client.clips("job-1", [report])
    assert info.value.maybe_applied is True
    assert fake.clips_created == 1 and len(fake.bodies("clips")) == 1  # no duplicate


def test_events_are_bounded_and_never_duplicated() -> None:
    fake = FakeInternalApi(answer_lost={"events": 1})
    with make_client(fake) as client:
        with pytest.raises(ApiUnavailableError):
            client.event("job-1", "warning", "x")
        client.event("job-1", "warning", "é" * 1500, {"signal": "visual"})
    bodies = fake.bodies("events")
    assert len(bodies) == 2 and fake.events_rejected == 0
    assert len(bodies[1]["message"]) == 1000 and bodies[1]["message"].endswith("…")


def test_heartbeat_is_resent_after_a_lost_answer() -> None:
    fake = FakeInternalApi(answer_lost={"heartbeat": 2})
    with make_client(fake) as client:
        assert client.heartbeat("job-1", 5, "m" * 900).cancel_requested is False
    assert fake.heartbeats == 3
    assert all(len(b["message"]) == 500 for b in fake.bodies("heartbeat"))


def test_bounded_messages_count_utf16_units_like_the_api() -> None:
    assert _bounded("abc", 10) == "abc"
    assert _bounded(None, 10) is None
    cut = _bounded("😀" * 600, 1000)  # 1200 UTF-16 code units
    assert cut is not None and cut.endswith("…")
    assert len(cut.encode("utf-16-le")) // 2 <= 1000
