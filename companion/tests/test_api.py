"""HTTP client: headers, typed errors and the streamed multipart upload."""

from __future__ import annotations

import json
import ssl
from datetime import UTC, datetime
from pathlib import Path

import httpx
import pytest

from clipforge_companion.api import (
    AuthError,
    CompanionApi,
    PlanLimitError,
    RateLimiter,
    RejectedError,
    RetryableError,
    SegmentFileError,
    SessionClosed,
    parse_retry_after,
)
from tests.fake_api import BASE_URL, TOKEN, FakeLiveApi, parse_multipart


def api_returning(response: httpx.Response) -> CompanionApi:
    return CompanionApi(BASE_URL, TOKEN, transport=httpx.MockTransport(lambda request: response))


async def test_me_sends_bearer_token_and_user_agent() -> None:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(
            200, json={"device": {"id": "d", "name": "PC"}, "plan": {"code": "pro"}}
        )

    async with CompanionApi(BASE_URL + "/", TOKEN, transport=httpx.MockTransport(handler)) as api:
        me = await api.me()
    assert me["device"]["name"] == "PC"
    request = seen[0]
    assert request.url == httpx.URL(BASE_URL + "/api/live/companion/me")
    assert request.headers["authorization"] == f"Bearer {TOKEN}"
    assert request.headers["user-agent"].startswith("clipforge-companion/")


async def test_create_session_body_and_plan_limit() -> None:
    fake = FakeLiveApi()
    async with fake.client() as api:
        created = await api.create_session(
            title="Live de sexta",
            segment_seconds=60,
            obs={"version": "31.0.2", "recordingPath": None},
        )
        assert created["session"]["segmentSeconds"] == 60
        assert fake.created == [
            {"title": "Live de sexta", "segmentSeconds": 60, "obs": {"version": "31.0.2"}}
        ]
        with pytest.raises(PlanLimitError) as info:
            await api.create_session()
    assert info.value.status == 409 and info.value.code == "plan_limit"
    assert "sessão ativa" in (info.value.detail or "")


@pytest.mark.parametrize(
    ("status", "error"),
    [(401, AuthError), (403, AuthError), (409, SessionClosed), (404, SessionClosed)],
)
async def test_session_endpoint_errors_are_typed(status: int, error: type[Exception]) -> None:
    response = httpx.Response(status, json={"status": status, "code": "x", "detail": "d"})
    async with api_returning(response) as api:
        with pytest.raises(error):
            await api.heartbeat("s1", {"recording": True})


@pytest.mark.parametrize("status", [500, 502, 503, 504, 408, 429])
async def test_temporary_errors_are_retryable(status: int) -> None:
    async with api_returning(httpx.Response(status, headers={"Retry-After": "7"})) as api:
        with pytest.raises(RetryableError) as info:
            await api.end_session("s1", 3)
    assert info.value.status == status
    assert info.value.retry_after == 7.0


@pytest.mark.parametrize("status", [400, 413, 415, 422])
async def test_other_client_errors_are_rejected(status: int) -> None:
    async with api_returning(httpx.Response(status, json={"code": "invalid_media"})) as api:
        with pytest.raises(RejectedError) as info:
            await api.end_session("s1")
    assert info.value.code == "invalid_media"


async def test_network_errors_are_retryable() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    async with CompanionApi(BASE_URL, TOKEN, transport=httpx.MockTransport(handler)) as api:
        with pytest.raises(RetryableError, match="falha de rede"):
            await api.me()


async def test_redirect_is_reported_not_followed() -> None:
    response = httpx.Response(301, headers={"Location": "https://api.test/"})
    async with api_returning(response) as api:
        with pytest.raises(RejectedError, match="redirecionamento"):
            await api.me()


async def test_upload_is_streamed_multipart_with_content_length(tmp_path: Path) -> None:
    segment = tmp_path / "seg_00003.mkv"
    payload = bytes(range(256)) * 1000 + b"\r\n--not-a-boundary\r\n"
    segment.write_bytes(payload)
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = (await api.create_session())["session"]["id"]
        result = await api.upload_segment(
            session_id, sequence=3, start_offset=180.0, duration=60.033, path=segment, final=True
        )
        again = await api.upload_segment(
            session_id, sequence=3, start_offset=180.0, duration=60.033, path=segment
        )
    assert result["duplicate"] is False
    assert again["duplicate"] is True
    upload = fake.uploads[0]
    assert upload["transfer_encoding"] is None  # not chunked
    assert int(upload["content_length_header"]) == upload["body_length"]
    assert upload["sequence"] == 3
    assert upload["start"] == 180.0
    assert upload["duration"] == pytest.approx(60.033)
    assert upload["final"] is True
    assert upload["file"]["filename"] == "seg_00003.mkv"
    assert upload["file"]["content_type"] == "video/x-matroska"
    assert upload["file"]["content"] == payload
    assert fake.uploads[1]["final"] is False


async def test_multipart_fields_are_parseable_by_an_independent_parser(tmp_path: Path) -> None:
    from clipforge_companion.api import MultipartFileBody

    path = tmp_path / "x.mkv"
    path.write_bytes(b"abc")
    body = MultipartFileBody(
        [("sequence", "0"), ("final", "false")],
        file_field="file",
        path=path,
        content_type="video/x-matroska",
    )
    data = b"".join([chunk async for chunk in body])
    assert len(data) == body.content_length
    fields, files = parse_multipart(body.content_type, data)
    assert fields == {"sequence": "0", "final": "false"}
    assert files["file"]["content"] == b"abc"


async def test_rate_limiter_paces_bytes() -> None:
    now = [100.0]
    sleeps: list[float] = []

    async def fake_sleep(delay: float) -> None:
        sleeps.append(delay)
        now[0] += delay

    limiter = RateLimiter.from_mbps(8, clock=lambda: now[0], sleep=fake_sleep)  # 1 MB/s
    for _ in range(4):
        await limiter.consume(250_000)
    # First chunk goes immediately, then one chunk every 0.25 s.
    assert sleeps == pytest.approx([0.25, 0.25, 0.25])
    now[0] += 10  # idle time does not build up a burst
    await limiter.consume(250_000)
    await limiter.consume(250_000)
    assert sleeps[-1] == pytest.approx(0.25)


async def test_throttled_upload_takes_the_expected_time(tmp_path: Path) -> None:
    import time

    path = tmp_path / "seg_00000.mkv"
    path.write_bytes(b"\0" * 200_000)
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = (await api.create_session())["session"]["id"]
        started = time.monotonic()
        await api.upload_segment(
            session_id,
            sequence=0,
            start_offset=0,
            duration=1,
            path=path,
            limiter=RateLimiter.from_mbps(1.6),  # 200 KB/s
        )
        elapsed = time.monotonic() - started
    # 200 KB at 200 KB/s, minus the first 64 KiB chunk sent immediately.
    assert elapsed >= (200_000 - 65_536) / 200_000 - 0.05


def test_retry_after_http_date() -> None:
    now = datetime(2026, 10, 23, 20, 0, 0, tzinfo=UTC)
    assert parse_retry_after("Fri, 23 Oct 2026 20:00:30 GMT", now=now) == 30.0
    assert parse_retry_after("12", now=now) == 12.0
    assert parse_retry_after("soon", now=now) is None
    assert parse_retry_after(None) is None


def test_problem_json_detail_is_kept() -> None:
    from clipforge_companion.api import error_from_response

    response = httpx.Response(
        409, content=json.dumps({"code": "quota_exceeded", "detail": "Minutos esgotados"}).encode()
    )
    error = error_from_response(response, conflict=SessionClosed)
    assert isinstance(error, SessionClosed)
    assert error.code == "quota_exceeded"
    assert "Minutos esgotados" in error.message


TLS_RECORD_ERROR = ssl.SSLError(
    1, "[SSL: DECRYPTION_FAILED_OR_BAD_RECORD_MAC] decryption failed or bad record mac"
)


@pytest.mark.parametrize(
    "error",
    [
        TLS_RECORD_ERROR,  # raised as is by anyio below httpx (antivirus/proxy scanning HTTPS)
        ssl.SSLError(1, "[SSL: TLSV1_ALERT_INTERNAL_ERROR] tlsv1 alert internal error"),
        ConnectionResetError(10054, "connection reset by peer"),
        httpx.DecodingError("invalid gzip"),
        httpx.TooManyRedirects("redirect loop"),
    ],
)
async def test_errors_raised_below_httpx_are_retryable(error: Exception) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        raise error

    async with CompanionApi(BASE_URL, TOKEN, transport=httpx.MockTransport(handler)) as api:
        with pytest.raises(RetryableError, match="falha de rede") as info:
            await api.heartbeat("s1", {"recording": True})
    assert info.value.__cause__ is error


async def test_segment_file_problems_are_not_network_errors(tmp_path: Path) -> None:
    fake = FakeLiveApi()
    async with fake.client() as api:
        session_id = (await api.create_session())["session"]["id"]
        with pytest.raises(SegmentFileError) as info:
            await api.upload_segment(
                session_id, sequence=0, start_offset=0, duration=5, path=tmp_path / "gone.mkv"
            )
    assert info.value.reason == "missing" and not info.value.transient
    assert fake.uploads == []


async def test_a_segment_that_shrinks_or_is_locked_while_sent(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from clipforge_companion.api import MultipartFileBody

    path = tmp_path / "seg_00000.mkv"
    path.write_bytes(b"x" * 1000)
    body = MultipartFileBody([], file_field="file", path=path, content_type="video/x-matroska")
    path.write_bytes(b"x" * 10)  # truncated after the size was announced
    with pytest.raises(SegmentFileError) as shrunk:
        _ = [chunk async for chunk in body]
    assert shrunk.value.reason == "shrunk" and not shrunk.value.transient

    body = MultipartFileBody([], file_field="file", path=path, content_type="video/x-matroska")
    real_open = Path.open

    def locked_open(self: Path, *args: object, **kwargs: object):  # type: ignore[no-untyped-def]
        if self == path:
            raise PermissionError(13, "The process cannot access the file", str(self))
        return real_open(self, *args, **kwargs)  # type: ignore[arg-type]

    monkeypatch.setattr(Path, "open", locked_open)
    with pytest.raises(SegmentFileError) as locked:
        _ = [chunk async for chunk in body]
    assert locked.value.reason == "locked" and locked.value.transient
