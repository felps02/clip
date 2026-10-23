"""Optional LLM providers.

Two uses, both optional (the pipeline is fully functional without an LLM, and any failure
only marks the signal unavailable, never fails the job):

* **JSON completion clients** (:class:`JsonLlmClient`) used by the round-3 assist stage
  (``app/analysis/llm_assist.py``: re-rank the top candidates against the user's
  instructions/focus, suggest pt-BR titles): :class:`LocalLlmClient` — the product's own
  model behind an OpenAI-compatible ``llama-server`` (``LLM_PROVIDER=local``,
  ``LLM_BASE_URL``), :class:`GeminiClient` — Google's REST ``generateContent`` with a
  ``responseSchema`` (``LLM_PROVIDER=gemini`` + ``GEMINI_API_KEY``, free tier), and
  :class:`AnthropicProvider` (paid API, opt-in only: ``LLM_PROVIDER=anthropic`` +
  ``ANTHROPIC_API_KEY``).
* **Transcript window rating** (``LlmProvider.rate_windows``, adds the ``llm_score``
  series to the composite): only the opt-in :class:`AnthropicProvider`, as before.

A cancelled job is honoured between requests, and while one is in flight
(:func:`call_cancellable`).
"""

from __future__ import annotations

import json
import math
import threading
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any, Protocol, runtime_checkable

import httpx
import numpy as np

from app.analysis.transcription import Transcript
from app.logging_setup import get_logger
from app.worker.errors import CancelToken

log = get_logger("llm")

GEMINI_API_BASE = "https://generativelanguage.googleapis.com/v1beta"


class LlmError(Exception):
    """The provider could not answer (connection, HTTP status, invalid or truncated JSON).
    The message never contains credentials."""


class LlmTimeout(LlmError):
    """The request took longer than its timeout."""


@runtime_checkable
class JsonLlmClient(Protocol):
    """One JSON-schema constrained completion."""

    name: str

    @property
    def model(self) -> str | None: ...

    def complete_json(
        self,
        system: str,
        user: str,
        schema: dict[str, Any],
        *,
        schema_name: str,
        max_tokens: int,
        temperature: float = 0.2,
        timeout: float | None = None,
    ) -> dict[str, Any]: ...


def call_cancellable[T](
    fn: Callable[[], T], cancel: CancelToken | None, *, poll_seconds: float = 0.2
) -> T:
    """Run a blocking request in a daemon thread and wait for it while honouring the cancel
    token: a cancelled job raises ``JobCancelled`` at once; the abandoned request ends by
    itself within its own timeout and its answer is discarded."""
    if cancel is None:
        return fn()
    cancel.raise_if_cancelled()
    box: dict[str, Any] = {}
    done = threading.Event()

    def run() -> None:
        try:
            box["value"] = fn()
        except BaseException as exc:  # handed to the waiting thread
            box["error"] = exc
        finally:
            done.set()

    threading.Thread(target=run, name="llm-request", daemon=True).start()
    while not done.wait(poll_seconds):
        cancel.raise_if_cancelled()
    if "error" in box:
        raise box["error"]
    return box["value"]


def _parse_json_object(text: str) -> dict[str, Any]:
    try:
        data = json.loads(text)
    except (TypeError, ValueError) as exc:
        raise LlmError(f"invalid JSON answer: {exc}"[:300]) from exc
    if not isinstance(data, dict):
        raise LlmError("the answer is not a JSON object")
    return data


def _model_label(model_id: str) -> str:
    """``C:/models/llm/Qwen3-1.7B-Q4_K_M.gguf`` -> ``Qwen3-1.7B-Q4_K_M``."""
    name = model_id.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
    return name[:-5] if name.lower().endswith(".gguf") else name


class LocalLlmClient:
    """OpenAI-compatible ``/v1/chat/completions`` (llama.cpp ``llama-server``): JSON-schema
    constrained output (``response_format: json_schema``), thinking disabled for Qwen3
    (``chat_template_kwargs.enable_thinking=false``) and prompt caching (the system prompt
    and the excerpt block are a byte-stable prefix shared by the requests of a job)."""

    name = "local"

    def __init__(
        self,
        base_url: str,
        *,
        model: str = "",
        timeout: float = 60.0,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.timeout = float(timeout)
        self._label = model.strip() or None
        self._discovered = False
        self._transport = transport
        self.last_timings: dict[str, Any] | None = None

    @property
    def model(self) -> str | None:
        return self._label

    def _client(self, timeout: float) -> httpx.Client:
        return httpx.Client(
            base_url=self.base_url,
            timeout=httpx.Timeout(timeout, connect=min(5.0, timeout)),
            transport=self._transport,
        )

    def discover_model(self) -> str | None:
        """Model id the server reports (``/v1/models``), cached; the configured label wins."""
        if self._label or self._discovered:
            return self._label
        try:
            with self._client(5.0) as client:
                response = client.get("/v1/models")
            data = response.json().get("data") or []
            if response.status_code == 200 and data and data[0].get("id"):
                self._label = _model_label(str(data[0]["id"]))
            self._discovered = True  # answered: do not ask again (an unreachable server is)
        except (httpx.HTTPError, ValueError, AttributeError) as exc:
            log.info("llm.local.model_unknown", error=str(exc)[:200])
        return self._label

    def complete_json(
        self,
        system: str,
        user: str,
        schema: dict[str, Any],
        *,
        schema_name: str,
        max_tokens: int,
        temperature: float = 0.2,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        timeout = float(timeout or self.timeout)
        body = {
            "model": self._label or "local",
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            "response_format": {
                "type": "json_schema",
                "json_schema": {"name": schema_name, "strict": True, "schema": schema},
            },
            "chat_template_kwargs": {"enable_thinking": False},
            "temperature": temperature,
            "max_tokens": int(max_tokens),
            "cache_prompt": True,
            "stream": False,
        }
        try:
            with self._client(timeout) as client:
                response = client.post("/v1/chat/completions", json=body)
        except httpx.TimeoutException as exc:
            raise LlmTimeout(f"no answer within {timeout:.0f}s") from exc
        except httpx.HTTPError as exc:
            raise LlmError(f"connection error: {type(exc).__name__}: {exc}"[:300]) from exc
        if response.status_code != 200:
            raise LlmError(f"HTTP {response.status_code}: {response.text[:200]}")
        try:
            data = response.json()
            choice = data["choices"][0]
            content = choice["message"]["content"]
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            raise LlmError(f"unexpected answer: {exc}"[:300]) from exc
        self.last_timings = data.get("timings") if isinstance(data, dict) else None
        if choice.get("finish_reason") == "length":
            raise LlmError("the answer was truncated (max_tokens)")
        return _parse_json_object(content)


def gemini_schema(schema: dict[str, Any]) -> dict[str, Any]:
    """JSON schema -> the OpenAPI subset of Gemini's ``responseSchema``: upper-case types,
    ``propertyOrdering`` kept from the properties, keywords it does not take dropped (the
    answer is validated afterwards anyway)."""
    out: dict[str, Any] = {}
    kind = schema.get("type")
    if isinstance(kind, str):
        out["type"] = kind.upper()
    for key in ("description", "enum", "minimum", "maximum", "minItems", "maxItems"):
        if key in schema:
            out[key] = schema[key]
    properties = schema.get("properties")
    if isinstance(properties, dict):
        out["properties"] = {k: gemini_schema(v) for k, v in properties.items()}
        out["propertyOrdering"] = list(properties)
    if isinstance(schema.get("required"), list):
        out["required"] = list(schema["required"])
    if isinstance(schema.get("items"), dict):
        out["items"] = gemini_schema(schema["items"])
    return out


class GeminiClient:
    """Google Gemini API (``models/{model}:generateContent``, REST) with a
    ``responseSchema``. Optional free cloud provider: the free tier has low limits and
    Google may use its inputs/outputs to improve its products (docs/PROCESSING_PIPELINE.md).
    The key goes in the ``x-goog-api-key`` header, never in the URL or the logs."""

    name = "gemini"

    def __init__(
        self,
        api_key: str,
        model: str = "gemini-2.5-flash",
        *,
        timeout: float = 60.0,
        base_url: str = GEMINI_API_BASE,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        if not api_key.strip():
            raise ValueError("GEMINI_API_KEY is required for the gemini provider")
        self._api_key = api_key.strip()
        self._model = model.strip() or "gemini-2.5-flash"
        self.timeout = float(timeout)
        self.base_url = base_url.rstrip("/")
        self._transport = transport

    @property
    def model(self) -> str | None:
        return self._model

    def complete_json(
        self,
        system: str,
        user: str,
        schema: dict[str, Any],
        *,
        schema_name: str,
        max_tokens: int,
        temperature: float = 0.2,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        timeout = float(timeout or self.timeout)
        config: dict[str, Any] = {
            "responseMimeType": "application/json",
            "responseSchema": gemini_schema(schema),
            "temperature": temperature,
            "maxOutputTokens": int(max_tokens),
        }
        if "flash" in self._model:
            config["thinkingConfig"] = {"thinkingBudget": 0}  # Flash: no thinking tokens
        body = {
            "systemInstruction": {"parts": [{"text": system}]},
            "contents": [{"role": "user", "parts": [{"text": user}]}],
            "generationConfig": config,
        }
        url = f"{self.base_url}/models/{self._model}:generateContent"
        try:
            with httpx.Client(
                timeout=httpx.Timeout(timeout, connect=min(10.0, timeout)),
                transport=self._transport,
            ) as client:
                response = client.post(url, json=body, headers={"x-goog-api-key": self._api_key})
        except httpx.TimeoutException as exc:
            raise LlmTimeout(f"no answer within {timeout:.0f}s") from exc
        except httpx.HTTPError as exc:
            raise LlmError(f"connection error: {type(exc).__name__}") from exc
        if response.status_code != 200:
            message = ""
            try:
                message = str(response.json().get("error", {}).get("message", ""))
            except ValueError, AttributeError:
                message = response.text
            label = "rate limited" if response.status_code == 429 else "HTTP"
            raise LlmError(f"{label} {response.status_code}: {message[:200]}")
        try:
            data = response.json()
            candidate = (data.get("candidates") or [None])[0]
            if candidate is None:
                reason = (data.get("promptFeedback") or {}).get("blockReason") or "no candidate"
                raise LlmError(f"no answer ({reason})")
            if candidate.get("finishReason") == "MAX_TOKENS":
                raise LlmError("the answer was truncated (MAX_TOKENS)")
            parts = (candidate.get("content") or {}).get("parts") or []
            text = "".join(str(p.get("text", "")) for p in parts if isinstance(p, dict))
        except (ValueError, AttributeError, TypeError) as exc:
            raise LlmError(f"unexpected answer: {exc}"[:300]) from exc
        return _parse_json_object(text)


def _anthropic_schema(schema: dict[str, Any]) -> dict[str, Any]:
    """Structured outputs take plain JSON schema without numeric/length constraints."""
    out = {
        k: v
        for k, v in schema.items()
        if k not in ("minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems")
    }
    if isinstance(out.get("properties"), dict):
        out["properties"] = {k: _anthropic_schema(v) for k, v in out["properties"].items()}
    if isinstance(out.get("items"), dict):
        out["items"] = _anthropic_schema(out["items"])
    return out


MAX_WINDOWS_PER_REQUEST = 25
MIN_WORDS_PER_WINDOW = 6

SYSTEM_PROMPT = """You help an editor pick moments from a livestream recording that would work as
standalone short clips (TikTok/Reels/Shorts). You receive numbered transcript windows
with their start/end seconds. Rate each window from 0 to 1 for clip potential: 1 means a
self-contained, surprising, funny, tense or quotable moment that makes sense without the
rest of the stream; 0 means filler (silence, logistics, repetitive game callouts).
Also suggest a short catchy title in Brazilian Portuguese (max 60 characters) for the
windows you rate 0.5 or higher. Return only the JSON that matches the schema."""

OUTPUT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "ratings": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "id": {"type": "integer"},
                    "score": {"type": "number"},
                    "title": {"type": "string"},
                    "reason": {"type": "string"},
                },
                "required": ["id", "score", "title", "reason"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["ratings"],
    "additionalProperties": False,
}


@dataclass(slots=True)
class LlmWindow:
    id: int
    start: float
    end: float
    text: str


@dataclass(slots=True)
class LlmRating:
    start: float
    end: float
    score: float
    title: str | None = None
    reason: str | None = None

    def to_json(self) -> dict[str, Any]:
        return {
            "start": round(self.start, 2),
            "end": round(self.end, 2),
            "score": round(self.score, 3),
            "title": self.title,
            "reason": self.reason,
        }


@dataclass(slots=True)
class LlmResult:
    available: bool
    provider: str
    model: str | None = None
    ratings: list[LlmRating] = field(default_factory=list)
    error: str | None = None

    def to_json(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "available": self.available,
            "provider": self.provider,
            "model": self.model,
        }
        if self.error:
            data["error"] = self.error
        if self.available:
            data["ratings"] = [r.to_json() for r in self.ratings]
        return data

    def title_for(self, start: float, end: float) -> str | None:
        best: LlmRating | None = None
        best_overlap = 0.0
        for r in self.ratings:
            overlap = min(end, r.end) - max(start, r.start)
            if overlap > best_overlap and r.title:
                best_overlap = overlap
                best = r
        return best.title if best else None


@runtime_checkable
class LlmProvider(Protocol):
    name: str

    def rate_windows(
        self,
        windows: list[LlmWindow],
        *,
        language: str | None = None,
        cancel: CancelToken | None = None,
    ) -> LlmResult: ...


class NoneProvider:
    name = "none"

    def rate_windows(
        self,
        windows: list[LlmWindow],
        *,
        language: str | None = None,
        cancel: CancelToken | None = None,
    ) -> LlmResult:
        return LlmResult(available=False, provider=self.name)


class AnthropicProvider:
    """Rates windows with Claude through the official SDK (``anthropic``)."""

    name = "anthropic"

    def __init__(
        self, api_key: str, model: str, *, timeout: float = 90.0, max_retries: int = 2
    ) -> None:
        if not api_key:
            raise ValueError("ANTHROPIC_API_KEY is required for the anthropic provider")
        self.api_key = api_key
        self.model = model
        self.timeout = timeout
        self.max_retries = max_retries
        self._client: Any = None

    def _get_client(self) -> Any:
        if self._client is None:
            import anthropic  # lazy: optional dependency at runtime

            self._client = anthropic.Anthropic(
                api_key=self.api_key, timeout=self.timeout, max_retries=self.max_retries
            )
        return self._client

    def complete_json(
        self,
        system: str,
        user: str,
        schema: dict[str, Any],
        *,
        schema_name: str,
        max_tokens: int,
        temperature: float = 0.2,
        timeout: float | None = None,
    ) -> dict[str, Any]:
        """One structured-output request (same call as the window rating)."""
        try:
            import anthropic
        except ImportError as exc:
            raise LlmError(f"anthropic SDK not installed: {exc}") from exc
        try:
            response = self._get_client().messages.create(
                model=self.model,
                max_tokens=int(max_tokens),
                system=system,
                messages=[{"role": "user", "content": user}],
                output_config={
                    "format": {"type": "json_schema", "schema": _anthropic_schema(schema)}
                },
                timeout=float(timeout or self.timeout),
            )
        except anthropic.APITimeoutError as exc:
            raise LlmTimeout("no answer within the timeout") from exc
        except anthropic.APIStatusError as exc:
            raise LlmError(f"API error {exc.status_code}: {exc.message}"[:300]) from exc
        except anthropic.APIConnectionError as exc:
            raise LlmError(f"connection error: {exc}"[:300]) from exc
        if response.stop_reason == "refusal":
            raise LlmError("the model declined the request (stop_reason=refusal)")
        if response.stop_reason == "max_tokens":
            raise LlmError("the answer was truncated (max_tokens)")
        return _parse_json_object(next((b.text for b in response.content if b.type == "text"), ""))

    @staticmethod
    def _prompt(windows: list[LlmWindow], language: str | None) -> str:
        lines = [
            "Language of the stream: " + (language or "unknown") + ".",
            "Rate every window below. Windows:",
        ]
        for w in windows:
            text = " ".join(w.text.split())[:1200]
            lines.append(f"[{w.id}] {w.start:.0f}s-{w.end:.0f}s: {text}")
        return "\n".join(lines)

    def _rate_batch(self, windows: list[LlmWindow], language: str | None) -> list[dict[str, Any]]:
        client = self._get_client()
        response = client.messages.create(
            model=self.model,
            max_tokens=4096,
            system=SYSTEM_PROMPT,
            messages=[{"role": "user", "content": self._prompt(windows, language)}],
            output_config={"format": {"type": "json_schema", "schema": OUTPUT_SCHEMA}},
        )
        if response.stop_reason == "refusal":
            raise RuntimeError("the model declined the request (stop_reason=refusal)")
        if response.stop_reason == "max_tokens":
            raise RuntimeError("the model answer was truncated (max_tokens)")
        text = next((b.text for b in response.content if b.type == "text"), "")
        data = json.loads(text)
        ratings = data.get("ratings", []) if isinstance(data, dict) else []
        return [r for r in ratings if isinstance(r, dict)]

    def rate_windows(
        self,
        windows: list[LlmWindow],
        *,
        language: str | None = None,
        cancel: CancelToken | None = None,
    ) -> LlmResult:
        if not windows:
            return LlmResult(available=True, provider=self.name, model=self.model)
        try:
            import anthropic
        except ImportError as exc:
            return LlmResult(
                False, self.name, self.model, error=f"anthropic SDK not installed: {exc}"
            )

        by_id = {w.id: w for w in windows}
        ratings: list[LlmRating] = []
        try:
            for i in range(0, len(windows), MAX_WINDOWS_PER_REQUEST):
                if cancel is not None:
                    # Each batch is a request of up to timeout x (1 + retries): a cancelled
                    # job must not start the next one.
                    cancel.raise_if_cancelled()
                batch = windows[i : i + MAX_WINDOWS_PER_REQUEST]
                for item in self._rate_batch(batch, language):
                    try:
                        window = by_id.get(int(item.get("id")))
                        score = float(item.get("score", 0.0))
                    except TypeError, ValueError:
                        continue
                    if window is None or not math.isfinite(score):
                        continue
                    title = str(item.get("title") or "").strip()[:60] or None
                    reason = str(item.get("reason") or "").strip()[:200] or None
                    ratings.append(
                        LlmRating(
                            window.start, window.end, min(1.0, max(0.0, score)), title, reason
                        )
                    )
        except anthropic.RateLimitError as exc:
            return LlmResult(
                False, self.name, self.model, error=f"rate limited: {exc.message}"[:300]
            )
        except anthropic.APIStatusError as exc:
            return LlmResult(
                False,
                self.name,
                self.model,
                error=f"API error {exc.status_code}: {exc.message}"[:300],
            )
        except anthropic.APIConnectionError as exc:
            return LlmResult(False, self.name, self.model, error=f"connection error: {exc}"[:300])
        except (json.JSONDecodeError, RuntimeError, ValueError) as exc:
            return LlmResult(False, self.name, self.model, error=f"invalid answer: {exc}"[:300])
        return LlmResult(True, self.name, self.model, ratings)


def build_llm_provider(settings: Any) -> LlmProvider:
    """Window rating (pre-fusion ``llm_score``): the opt-in Anthropic provider only."""
    if getattr(settings, "llm_provider", "none") == "anthropic" and getattr(
        settings, "anthropic_api_key", ""
    ):
        return AnthropicProvider(settings.anthropic_api_key, settings.llm_model)
    return NoneProvider()


def build_llm_client(settings: Any, provider: LlmProvider | None = None) -> JsonLlmClient | None:
    """JSON client of the assist stage for ``LLM_PROVIDER`` (``None`` when disabled or
    missing what it needs). ``provider``: the window-rating provider already built, reused
    for ``anthropic`` (one SDK client)."""
    kind = getattr(settings, "llm_provider", "none")
    timeout = float(getattr(settings, "llm_timeout_seconds", 60.0))
    if kind == "local" and getattr(settings, "llm_base_url", ""):
        return LocalLlmClient(
            settings.llm_base_url, model=getattr(settings, "llm_local_model", ""), timeout=timeout
        )
    if kind == "gemini" and getattr(settings, "gemini_api_key", "").strip():
        return GeminiClient(settings.gemini_api_key, settings.gemini_model, timeout=timeout)
    if kind == "anthropic" and getattr(settings, "anthropic_api_key", ""):
        if isinstance(provider, AnthropicProvider):
            return provider
        return AnthropicProvider(settings.anthropic_api_key, settings.llm_model, timeout=timeout)
    return None


def build_windows(
    transcript: Transcript, duration: float, window_seconds: float, max_windows: int
) -> list[LlmWindow]:
    """Consecutive transcript windows with enough words, capped at ``max_windows``."""
    if not transcript.available or not transcript.segments:
        return []
    window_seconds = max(10.0, float(window_seconds))
    n = int(math.ceil(max(duration, transcript.duration) / window_seconds))
    windows: list[LlmWindow] = []
    words = transcript.words
    for i in range(n):
        start = i * window_seconds
        end = min(start + window_seconds, max(duration, transcript.duration))
        text = " ".join(
            w.word.strip() for w in words if w.end > start and w.start < end and w.word.strip()
        )
        if len(text.split()) >= MIN_WORDS_PER_WINDOW:
            windows.append(LlmWindow(len(windows), start, end, text))
    if len(windows) > max_windows:
        windows.sort(key=lambda w: (-len(w.text.split()), w.start))
        windows = sorted(windows[:max_windows], key=lambda w: w.start)
        for index, w in enumerate(windows):
            w.id = index
    return windows


def llm_series(result: LlmResult, n_seconds: int) -> np.ndarray | None:
    """Per-second score (max of the covering windows) or ``None`` when unavailable."""
    if not result.available or not result.ratings:
        return None
    series = np.zeros(max(1, n_seconds))
    for r in result.ratings:
        s = max(0, int(r.start))
        e = min(len(series), int(math.ceil(r.end)))
        if e > s:
            series[s:e] = np.maximum(series[s:e], r.score)
    return series
