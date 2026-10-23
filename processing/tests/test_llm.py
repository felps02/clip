from __future__ import annotations

import json
import threading
import time

import httpx
import numpy as np
import pytest

from app.analysis.llm import (
    AnthropicProvider,
    GeminiClient,
    LlmError,
    LlmRating,
    LlmResult,
    LlmTimeout,
    LlmWindow,
    LocalLlmClient,
    NoneProvider,
    build_llm_client,
    build_llm_provider,
    build_windows,
    call_cancellable,
    llm_series,
)
from app.analysis.transcription import Segment, Transcript, Word
from app.config import settings_for_tests
from app.worker.errors import CancelToken, JobCancelled


def test_none_provider_is_default_and_unavailable() -> None:
    provider = build_llm_provider(settings_for_tests())
    assert isinstance(provider, NoneProvider)
    result = provider.rate_windows([LlmWindow(0, 0, 30, "x")])
    assert result.available is False and result.provider == "none"
    assert llm_series(result, 100) is None


def test_anthropic_provider_is_built_only_with_key() -> None:
    assert isinstance(
        build_llm_provider(settings_for_tests(llm_provider="anthropic")), NoneProvider
    )
    provider = build_llm_provider(
        settings_for_tests(
            llm_provider="anthropic", anthropic_api_key="sk-test", llm_model="claude-haiku-5-5"
        )
    )
    assert isinstance(provider, AnthropicProvider)
    assert provider.model == "claude-haiku-5-5"
    assert provider.rate_windows([]).available is True


def test_windows_and_series() -> None:
    words = [Word(i * 2.0, i * 2.0 + 1.0, f" palavra{i}") for i in range(60)]
    transcript = Transcript(
        available=True,
        model="m",
        language="pt",
        segments=[Segment(0, 120, "...", words)],
        duration=120,
    )
    windows = build_windows(transcript, 120, 30, max_windows=3)
    assert len(windows) == 3
    assert [w.id for w in windows] == [0, 1, 2]
    assert windows[0].start == 0 and windows[0].end == 30
    result = LlmResult(
        True, "anthropic", "m", [LlmRating(0, 30, 0.9, "Título"), LlmRating(60, 90, 0.2)]
    )
    series = llm_series(result, 120)
    assert series is not None and series[10] == 0.9 and series[70] == 0.2 and series[100] == 0
    assert result.title_for(5, 20) == "Título"
    assert result.title_for(100, 110) is None
    assert np.isfinite(series).all()


def test_anthropic_provider_honours_the_cancel_token_between_batches() -> None:
    pytest.importorskip("anthropic")
    provider = AnthropicProvider("sk-test", "claude-haiku-5-5")
    cancel = CancelToken()
    batches: list[int] = []

    def fake_batch(batch: list[LlmWindow], language: str | None) -> list[dict[str, object]]:
        batches.append(len(batch))
        cancel.cancel()  # the user cancels while the first batch is being answered
        return [{"id": w.id, "score": 0.5, "title": "t", "reason": "r"} for w in batch]

    provider._rate_batch = fake_batch  # type: ignore[method-assign]
    windows = [LlmWindow(i, i * 30.0, i * 30.0 + 30.0, "palavra " * 8) for i in range(60)]
    with pytest.raises(JobCancelled):
        provider.rate_windows(windows, language="pt", cancel=cancel)
    assert batches == [25]  # the second batch was never requested
    result = provider.rate_windows(windows[:3], language="pt", cancel=CancelToken())
    assert result.available is True and len(result.ratings) == 3


# --------------------------------------------------------------------------------------
# round 3: JSON clients (local llama-server, Gemini) and their selection
# --------------------------------------------------------------------------------------

SCHEMA = {
    "type": "object",
    "properties": {"1": {"type": "integer", "minimum": 0, "maximum": 10}},
    "required": ["1"],
    "additionalProperties": False,
}


def _local(handler: object, **kwargs: object) -> LocalLlmClient:
    return LocalLlmClient(
        "http://llm.test/",
        transport=httpx.MockTransport(handler),
        **kwargs,  # type: ignore[arg-type]
    )


def test_local_client_sends_a_schema_constrained_request_without_thinking() -> None:
    seen: list[dict[str, object]] = []

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/v1/models":
            return httpx.Response(
                200, json={"data": [{"id": "C:/models/llm/Qwen3-1.7B-Q4_K_M.gguf"}]}
            )
        assert request.url.path == "/v1/chat/completions"
        seen.append(json.loads(request.content))
        return httpx.Response(
            200,
            json={
                "choices": [{"message": {"content": '{"1": 7}'}, "finish_reason": "stop"}],
                "timings": {"prompt_n": 12, "cache_n": 600},
            },
        )

    client = _local(handler)
    assert client.discover_model() == "Qwen3-1.7B-Q4_K_M" and client.model == "Qwen3-1.7B-Q4_K_M"
    answer = client.complete_json("sys", "user", SCHEMA, schema_name="clip_moments", max_tokens=50)
    assert answer == {"1": 7} and client.last_timings == {"prompt_n": 12, "cache_n": 600}
    body = seen[0]
    assert body["response_format"] == {
        "type": "json_schema",
        "json_schema": {"name": "clip_moments", "strict": True, "schema": SCHEMA},
    }
    assert body["chat_template_kwargs"] == {"enable_thinking": False}
    assert body["cache_prompt"] is True and body["max_tokens"] == 50
    assert body["messages"] == [
        {"role": "system", "content": "sys"},
        {"role": "user", "content": "user"},
    ]
    assert _local(handler, model="minha-ia").discover_model() == "minha-ia"  # label wins


@pytest.mark.parametrize(
    ("response", "message"),
    [
        (httpx.Response(500, text="boom"), "HTTP 500"),
        (
            httpx.Response(
                200, json={"choices": [{"message": {"content": "{"}, "finish_reason": "length"}]}
            ),
            "truncated",
        ),
        (
            httpx.Response(
                200, json={"choices": [{"message": {"content": "[1]"}, "finish_reason": "stop"}]}
            ),
            "not a JSON object",
        ),
        (httpx.Response(200, json={"unexpected": True}), "unexpected answer"),
    ],
)
def test_local_client_errors_are_llm_errors(response: httpx.Response, message: str) -> None:
    client = _local(lambda request: response)
    with pytest.raises(LlmError, match=message):
        client.complete_json("s", "u", SCHEMA, schema_name="x", max_tokens=10)


def test_local_client_timeout_and_connection_errors() -> None:
    def slow(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("slow", request=request)

    with pytest.raises(LlmTimeout):
        _local(slow).complete_json("s", "u", SCHEMA, schema_name="x", max_tokens=10, timeout=2)

    def refused(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("refused", request=request)

    with pytest.raises(LlmError, match="connection error"):
        _local(refused).complete_json("s", "u", SCHEMA, schema_name="x", max_tokens=10)
    assert _local(refused).discover_model() is None  # unknown label, no exception


def test_gemini_client_uses_generate_content_with_a_response_schema() -> None:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(
            200,
            json={
                "candidates": [
                    {
                        "content": {"parts": [{"text": '{"1": '}, {"text": "4}"}]},
                        "finishReason": "STOP",
                    }
                ]
            },
        )

    client = GeminiClient("secret-key", transport=httpx.MockTransport(handler))
    assert client.model == "gemini-2.5-flash"
    assert client.complete_json("sys", "user", SCHEMA, schema_name="x", max_tokens=99) == {"1": 4}
    request = seen[0]
    assert request.url.path == "/v1beta/models/gemini-2.5-flash:generateContent"
    assert (
        "secret-key" not in str(request.url) and request.headers["x-goog-api-key"] == "secret-key"
    )
    body = json.loads(request.content)
    assert body["systemInstruction"] == {"parts": [{"text": "sys"}]}
    assert body["contents"] == [{"role": "user", "parts": [{"text": "user"}]}]
    config = body["generationConfig"]
    assert config["responseMimeType"] == "application/json" and config["maxOutputTokens"] == 99
    assert config["thinkingConfig"] == {"thinkingBudget": 0}
    assert config["responseSchema"] == {
        "type": "OBJECT",
        "properties": {"1": {"type": "INTEGER", "minimum": 0, "maximum": 10}},
        "propertyOrdering": ["1"],
        "required": ["1"],
    }


def test_gemini_errors_never_leak_the_key() -> None:
    def limited(request: httpx.Request) -> httpx.Response:
        return httpx.Response(429, json={"error": {"message": "Quota exceeded"}})

    client = GeminiClient("secret-key", transport=httpx.MockTransport(limited))
    with pytest.raises(LlmError) as info:
        client.complete_json("s", "u", SCHEMA, schema_name="x", max_tokens=10)
    assert "rate limited 429" in str(info.value) and "secret-key" not in str(info.value)

    def blocked(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"promptFeedback": {"blockReason": "SAFETY"}})

    with pytest.raises(LlmError, match="SAFETY"):
        GeminiClient("k", transport=httpx.MockTransport(blocked)).complete_json(
            "s", "u", SCHEMA, schema_name="x", max_tokens=10
        )


def test_llm_client_is_built_per_provider() -> None:
    assert build_llm_client(settings_for_tests()) is None
    local = build_llm_client(settings_for_tests(llm_provider="local"))
    assert isinstance(local, LocalLlmClient) and local.base_url == "http://127.0.0.1:8090"
    assert local.timeout == 60.0
    assert build_llm_client(settings_for_tests(llm_provider="gemini")) is None  # no key
    gemini = build_llm_client(
        settings_for_tests(llm_provider="gemini", gemini_api_key="k", gemini_model="gemini-x-flash")
    )
    assert isinstance(gemini, GeminiClient) and gemini.model == "gemini-x-flash"
    assert build_llm_client(settings_for_tests(llm_provider="anthropic")) is None  # opt-in + key
    settings = settings_for_tests(llm_provider="anthropic", anthropic_api_key="sk-test")
    provider = build_llm_provider(settings)
    assert build_llm_client(settings, provider=provider) is provider
    # only the opt-in anthropic provider rates transcript windows
    assert isinstance(build_llm_provider(settings_for_tests(llm_provider="local")), NoneProvider)


def test_call_cancellable_returns_or_raises_at_once() -> None:
    assert call_cancellable(lambda: 5, None) == 5
    assert call_cancellable(lambda: 6, CancelToken()) == 6
    with pytest.raises(LlmError):
        call_cancellable(lambda: (_ for _ in ()).throw(LlmError("x")), CancelToken())
    cancel = CancelToken()
    release = threading.Event()

    def blocked() -> int:
        release.wait(10)
        return 1

    threading.Timer(0.2, cancel.cancel).start()
    started = time.monotonic()
    with pytest.raises(JobCancelled):
        call_cancellable(blocked, cancel, poll_seconds=0.05)
    assert time.monotonic() - started < 2.0  # not the request's own timeout
    release.set()
