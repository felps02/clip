"""LLM assist stage (round 3, optional): runs after fusion when an LLM provider is set.

* **Re-rank** — when the user gave instructions (``settings.instructions``) or focus
  categories, the transcript excerpts of the top ``MAX_CANDIDATES`` (20) candidates are
  rated 0–10 against them, and each rated candidate's rank priority moves by
  ``2 × (rating − 5)`` (−10..+10). The stored score never changes (it stays the internal
  estimate computed from the signals).
* **Titles** — a short pt-BR title for every candidate that will be rendered (the top
  ``autoRenderTop`` within those 20), accepted only when it passes :func:`validate_title`
  (≤ 60 characters, grounded in the excerpt — most of its content words come from it —, no
  hashtag, mention, link, emoji or laughter); otherwise the clip keeps the title it would
  have had. An accepted title is drawn at the top of the clip, so a title the small model
  invented must never pass.

Both tasks share the requests: the excerpts are numbered in one block (a byte-stable
prefix with the system prompt, so the server's prompt cache serves every request of the
job) and asked for in batches of ``BATCH_SIZE``; the answer is a JSON object whose keys are
the excerpt numbers (schema-constrained, so no excerpt is skipped or invented). The stage
is bounded by ``LLM_BUDGET_SECONDS``: each request gets at most what is left of it and the
stage stops at the first failure (timeout, connection, invalid answer), keeping whatever
it already obtained — the analysis always continues.
"""

from __future__ import annotations

import math
import re
import time
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

from app.analysis.fusion import Candidate, rank_candidates
from app.analysis.lexicon import normalize_text
from app.analysis.llm import JsonLlmClient, LlmError, call_cancellable
from app.logging_setup import get_logger
from app.models import AnalysisSettings
from app.worker.errors import CancelToken, JobCancelled

log = get_logger("llm_assist")

MAX_CANDIDATES = 20
BATCH_SIZE = 10
EXCERPT_MAX_CHARS = 300
MIN_EXCERPT_WORDS = 3
TITLE_MIN_CHARS = 3
TITLE_MAX_CHARS = 60
TITLE_MAX_WORDS = 10  # asked for 3 to 8; a longer one is a copied sentence
# grounding: at least this share of the title's content words must come from the excerpt
# (and at least 2 of them when the title has 3 or more)
GROUNDED_SHARE = 0.6
RELEVANCE_MAX = 10
RANK_BONUS_PER_POINT = 2  # (rating − 5) × 2 → −10..+10 rank priority
MIN_REQUEST_SECONDS = 5.0  # a request is not started with less budget than this
TOKENS_PER_TITLE = 48
TOKENS_PER_RATING = 12

# Byte-stable (no ids, dates or settings in it): the server caches its tokens.
SYSTEM_PROMPT = (
    "Você ajuda a escolher e nomear clipes curtos (TikTok, Reels, Shorts) cortados de uma "
    "live. Você recebe trechos numerados da transcrição. Responda somente com o JSON pedido.\n"
    'Quando o JSON pedir "titulo": escreva um título em português do Brasil com 3 a 8 '
    "palavras (no máximo 60 caracteres), começando com letra maiúscula, que resuma o que "
    "acontece no trecho usando as palavras dele. Não copie a frase inteira; sem hashtags, "
    "links, emojis, aspas ou kkk; não invente nada que não esteja no trecho.\n"
    'Quando o JSON pedir "nota": de 0 a 10, o quanto o trecho atende ao pedido e ao foco do '
    "usuário (10 = é exatamente o que foi pedido, 5 = mais ou menos, 0 = não tem relação). "
    "Julgue só pelo que é dito no trecho.\n"
    "Exemplo com o pedido 'momentos engraçados': 'kkkk ele caiu da ponte de novo' -> "
    '{"titulo": "Caiu da ponte de novo", "nota": 9}; \'valeu pelo sub pessoal\' -> '
    '{"titulo": "Valeu pelo sub", "nota": 0}; \'três kills com a pistola\' -> '
    '{"titulo": "Três kills com a pistola", "nota": 2}.'
)

FOCUS_LABELS = {
    "highlight": "destaques",
    "reaction": "reações",
    "funny": "momentos engraçados",
    "hype": "hype e comemorações",
    "tension": "tensão",
    "gameplay": "jogadas",
    "quote": "frases marcantes",
}

_URL_RE = re.compile(
    r"(https?://|www\.|\b[\w-]+\.(?:com|net|org|br|io|gg|tv|ly|me|app|link)\b)", re.IGNORECASE
)
_LAUGH_RE = re.compile(r"\b(?:kkk+|(?:ha){2,}h?|(?:rs){2,}|lol|lmao)\b")
_DIGITS_RE = re.compile(r"\d")
_QUOTES = "\"'“”‘’«»`"
_STOPWORDS = frozenset(
    {
        # pt
        "para", "como", "isso", "esse", "essa", "este", "esta", "isto", "aqui", "mais",
        "muito", "muita", "quando", "onde", "porque", "pela", "pelo", "pelas", "pelos",
        "sobre", "entre", "depois", "antes", "ainda", "agora", "tudo", "todo", "toda",
        "nada", "voce", "voces", "gente", "galera", "pessoal", "entao", "tambem", "mesmo",
        "dele", "dela", "eles", "elas", "minha", "meus", "minhas", "seus", "suas", "nossa",
        "nosso", "momento", "clipe", "video", "live", "trecho",
        "dessa", "desse", "disso", "nessa", "nesse", "nisso", "aquele", "aquela", "aquilo",
        "esses", "essas", "outro", "outra", "outros", "outras", "algum", "alguma", "alguem",
        "hoje", "nunca", "sempre", "estou", "estava", "estamos", "tava", "tamo",
        # spoken fillers and generic verbs: on their own they ground nothing ("Cara ganha um
        # carro novo" is not grounded by "cara, eu nem sei o que falar")
        "vamos", "vamo", "bora", "cara", "caras", "mano", "manos", "olha", "olhem", "olhar",
        "tipo", "assim", "coisa", "coisas", "fazer", "falar", "fala", "falou", "falando",
        "sabe", "sabia", "acho", "achei", "quero", "queria", "pode", "podia", "posso",
        "ficar", "fica", "ficou",
        # en
        "that", "this", "with", "from", "have", "they", "your", "just", "about", "there",
        "their", "will", "would", "been", "were", "what", "when", "then", "than", "into",
        "here", "some", "like", "okay", "yeah", "gonna", "wanna", "guys", "dude", "really",
        "know", "think", "thing", "things", "stuff",
    }
)  # fmt: skip


@dataclass(slots=True)
class AssistOutcome:
    """What the stage did, reported in ``signals.llm`` (job level) and per candidate."""

    provider: str
    model: str | None
    budget_seconds: float = 0.0
    seconds: float = 0.0
    requests: int = 0
    answered: int = 0
    error: str | None = None
    budget_exhausted: bool = False
    rerank_status: str = "skipped"  # skipped | applied | partial | failed
    rerank_requested: int = 0
    rerank_rated: int = 0
    titles_status: str = "skipped"
    titles_requested: int = 0
    titles_accepted: int = 0
    titles_rejected: dict[str, int] = field(default_factory=dict)

    @property
    def available(self) -> bool:
        """At least one request was answered with valid JSON."""
        return self.answered > 0

    def to_json(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "available": self.available,
            "provider": self.provider,
            "model": self.model,
            "seconds": round(self.seconds, 1),
            "budgetSeconds": round(self.budget_seconds, 1),
            "requests": self.requests,
            "rerank": {
                "status": self.rerank_status,
                "requested": self.rerank_requested,
                "rated": self.rerank_rated,
                "rankBonusPerPoint": RANK_BONUS_PER_POINT,
            },
            "titles": {
                "status": self.titles_status,
                "requested": self.titles_requested,
                "accepted": self.titles_accepted,
                "rejected": dict(self.titles_rejected),
            },
        }
        if self.error:
            data["error"] = self.error
        if self.budget_exhausted:
            data["budgetExhausted"] = True
        return data

    def notice(self) -> tuple[str, str, dict[str, Any]] | None:
        """A warning job event when the stage stopped early (``None`` otherwise)."""
        label = f"Modelo de linguagem ({self.provider})"
        if self.error:
            message = f"{label} interrompido: {self.error}. A análise seguiu sem ele"
        elif self.budget_exhausted:
            message = (
                f"{label} parou no limite de {self.budget_seconds:.0f} s. "
                "A análise seguiu sem o restante"
            )
        else:
            return None
        done = []
        if self.rerank_rated:
            done.append(f"{self.rerank_rated} momentos avaliados")
        if self.titles_accepted:
            done.append(f"{self.titles_accepted} títulos sugeridos")
        if done:
            message += " (" + ", ".join(done) + ")"
        return "warning", message + ".", {"signal": "llm", **self.to_json()}


def excerpt_for(text: str, limit: int = EXCERPT_MAX_CHARS) -> str:
    """The candidate's transcript on one line, cut at a word boundary."""
    flat = " ".join((text or "").split())
    if len(flat) <= limit:
        return flat
    cut = flat[:limit].rsplit(" ", 1)[0]
    return (cut or flat[:limit]) + "…"


def _content_tokens(text: str) -> set[str]:
    return {
        tok
        for tok in normalize_text(text).split()
        if tok not in _STOPWORDS and (len(tok) >= 4 or _DIGITS_RE.search(tok))
    }


def _same_word(a: str, b: str) -> bool:
    """Equal, or one is the start of the other (``test``/``teste``, ``jogada``/``jogadas``)."""
    short, long_ = sorted((a, b), key=len)
    return a == b or (len(short) >= 4 and long_.startswith(short))


def grounded_needed(n_title_tokens: int) -> int:
    """How many of a title's content words must come from the excerpt: ``GROUNDED_SHARE``
    of them rounded up, at least 1, and at least 2 when the title has 3 or more."""
    needed = max(1, math.ceil(round(GROUNDED_SHARE * n_title_tokens, 6)))
    return max(needed, 2) if n_title_tokens >= 3 else needed


def is_grounded(title: str, excerpt: str) -> bool:
    """Most of the title's content words come from the excerpt (accents and case ignored; a
    word and its longer form count, see :func:`_same_word`; how many: :func:`grounded_needed`).
    Content words have 4+ characters (or a digit) and are not stopwords or spoken fillers, so
    one shared filler (``vamos``, ``cara``, ``olha``) or one shared term (``clutch``) never
    grounds an otherwise invented title. A title without content words is not grounded."""
    title_tokens = _content_tokens(title)
    if not title_tokens:
        return False
    excerpt_tokens = _content_tokens(excerpt)
    matched = sum(1 for a in title_tokens if any(_same_word(a, b) for b in excerpt_tokens))
    return matched >= grounded_needed(len(title_tokens))


def _has_emoji(text: str) -> bool:
    return any(
        ord(ch) > 0xFFFF or unicodedata.category(ch) in ("So", "Sk", "Cs", "Co", "Cn")
        for ch in text
    )


def _foreign_script(text: str) -> bool:
    """Letters outside the Latin script (a small model sometimes slips CJK or Cyrillic words
    into a Portuguese title)."""
    return any(ch.isalpha() and ord(ch) > 0x024F for ch in text)


def validate_title(title: Any, excerpt: str) -> tuple[str | None, str | None]:
    """``(title, None)`` when acceptable (whitespace collapsed, quotes and trailing dots
    stripped, first letter upper-case), otherwise ``(None, reason)``."""
    if not isinstance(title, str):
        return None, "missing"
    text = " ".join(title.split()).strip(_QUOTES + " ").rstrip(" .;,:-–—").strip(_QUOTES + " ")
    if len(text) < TITLE_MIN_CHARS:
        return None, "empty"
    if len(text) > TITLE_MAX_CHARS or len(text.split()) > TITLE_MAX_WORDS:
        return None, "too_long"
    if "#" in text or "@" in text:
        return None, "hashtag"
    if _URL_RE.search(text):
        return None, "link"
    if _has_emoji(text):
        return None, "emoji"
    if _foreign_script(text):
        return None, "script"
    if _LAUGH_RE.search(normalize_text(text)):
        return None, "laughter"
    if not is_grounded(text, excerpt):
        return None, "not_grounded"
    return text[0].upper() + text[1:], None


def _relevance(value: Any) -> int | None:
    if isinstance(value, bool) or not isinstance(value, int | float | str):
        return None
    try:
        number = float(value)
    except ValueError:
        return None
    if number != number:  # NaN
        return None
    return int(round(min(float(RELEVANCE_MAX), max(0.0, number))))


@dataclass(slots=True)
class _Entry:
    number: int
    candidate: Candidate
    excerpt: str
    wants_rating: bool
    wants_title: bool


def _batch_schema(batch: list[_Entry]) -> tuple[dict[str, Any], int]:
    properties: dict[str, Any] = {}
    tokens = 24
    for entry in batch:
        item: dict[str, Any] = {}
        if entry.wants_title:
            item["titulo"] = {
                "type": "string",
                "minLength": TITLE_MIN_CHARS,
                "maxLength": TITLE_MAX_CHARS,
            }
            tokens += TOKENS_PER_TITLE
        if entry.wants_rating:
            item["nota"] = {"type": "integer", "minimum": 0, "maximum": RELEVANCE_MAX}
            tokens += TOKENS_PER_RATING
        properties[str(entry.number)] = {
            "type": "object",
            "properties": item,
            "required": list(item),
            "additionalProperties": False,
        }
    schema = {
        "type": "object",
        "properties": properties,
        "required": list(properties),
        "additionalProperties": False,
    }
    return schema, tokens


def _status(requested: int, done: int) -> str:
    if requested == 0:
        return "skipped"
    if done >= requested:
        return "applied"
    return "partial" if done > 0 else "failed"


def run_assist(
    client: JsonLlmClient,
    candidates: list[Candidate],
    settings: AnalysisSettings,
    *,
    budget_seconds: float,
    timeout_seconds: float,
    cancel: CancelToken | None = None,
    progress: Callable[[int, int], None] | None = None,
    clock: Callable[[], float] = time.monotonic,
) -> AssistOutcome:
    """Re-rank (in place: ``candidates`` is re-sorted and renumbered when ratings arrived)
    and suggest titles (``Candidate.title``); see the module docstring."""
    started = clock()
    discover = getattr(client, "discover_model", None)
    if callable(discover):
        discover()
    outcome = AssistOutcome(client.name, client.model, budget_seconds=float(budget_seconds))
    wants_rating = bool(settings.instructions or settings.focus_categories)
    to_title = {id(c) for c in candidates[: settings.render_top]}
    entries: list[_Entry] = []
    for cand in candidates[:MAX_CANDIDATES]:
        excerpt = excerpt_for(cand.transcript)
        title_needed = id(cand) in to_title
        if len(excerpt.split()) < MIN_EXCERPT_WORDS or not (wants_rating or title_needed):
            continue
        entries.append(_Entry(len(entries) + 1, cand, excerpt, wants_rating, title_needed))
    outcome.rerank_requested = sum(e.wants_rating for e in entries)
    outcome.titles_requested = sum(e.wants_title for e in entries)
    if not entries or budget_seconds <= 0:
        outcome.seconds = clock() - started
        if entries:
            outcome.budget_exhausted = True
            outcome.rerank_status = _status(outcome.rerank_requested, 0)
            outcome.titles_status = _status(outcome.titles_requested, 0)
        return outcome

    block = "Trechos:\n" + "\n".join(f"[{e.number}] {e.excerpt}" for e in entries)
    if wants_rating:
        focus = ", ".join(FOCUS_LABELS.get(c, c) for c in settings.focus_categories)
        block += (
            f"\n\nPedido do usuário: {settings.instructions or '(nenhum)'}"
            f"\nFoco: {focus or '(nenhum)'}"
        )
    ratings: dict[int, int] = {}
    titles: dict[int, str] = {}
    batches = [entries[i : i + BATCH_SIZE] for i in range(0, len(entries), BATCH_SIZE)]
    for index, batch in enumerate(batches):
        remaining = float(budget_seconds) - (clock() - started)
        if remaining < MIN_REQUEST_SECONDS:
            outcome.budget_exhausted = True
            break
        if progress is not None:
            progress(index + 1, len(batches))
        schema, max_tokens = _batch_schema(batch)
        numbers = ", ".join(str(e.number) for e in batch)
        user = f"{block}\n\nResponda para os trechos {numbers}."
        timeout = max(1.0, min(float(timeout_seconds), remaining))
        outcome.requests += 1
        try:
            answer = call_cancellable(
                lambda user=user, schema=schema, max_tokens=max_tokens, timeout=timeout: (
                    client.complete_json(
                        SYSTEM_PROMPT,
                        user,
                        schema,
                        schema_name="clip_moments",
                        max_tokens=max_tokens,
                        temperature=0.2,
                        timeout=timeout,
                    )
                ),
                cancel,
            )
        except JobCancelled:
            raise
        except LlmError as exc:
            outcome.error = str(exc)[:300]
            log.warning("llm.assist_failed", provider=client.name, error=outcome.error)
            break
        except Exception as exc:  # a provider bug must not fail the analysis
            outcome.error = f"{type(exc).__name__}: {exc}"[:300]
            log.warning("llm.assist_failed", provider=client.name, error=outcome.error)
            break
        outcome.answered += 1
        for entry in batch:
            item = answer.get(str(entry.number))
            if not isinstance(item, dict):
                continue
            if entry.wants_rating:
                rating = _relevance(item.get("nota"))
                if rating is not None:
                    ratings[entry.number] = rating
            if entry.wants_title:
                title, reason = validate_title(item.get("titulo"), entry.excerpt)
                if title is not None:
                    titles[entry.number] = title
                elif reason is not None:
                    outcome.titles_rejected[reason] = outcome.titles_rejected.get(reason, 0) + 1

    meta = {"provider": client.name, "model": client.model}
    outcome.model = client.model
    for entry in entries:
        signal = dict(entry.candidate.signals.get("llm") or {})
        rating = ratings.get(entry.number)
        title = titles.get(entry.number)
        if rating is not None:
            bonus = RANK_BONUS_PER_POINT * (rating - RELEVANCE_MAX // 2)
            entry.candidate.priority += bonus
            signal.update({"available": True, **meta, "relevance": rating, "rankBonus": bonus})
        if title is not None:
            entry.candidate.title = title
            signal.update({"available": True, **meta, "title": title})
        if rating is not None or title is not None:
            entry.candidate.signals["llm"] = signal
    if ratings:
        rank_candidates(candidates)
    outcome.rerank_rated = len(ratings)
    outcome.titles_accepted = len(titles)
    outcome.rerank_status = _status(outcome.rerank_requested, len(ratings))
    outcome.titles_status = _status(outcome.titles_requested, len(titles))
    outcome.seconds = clock() - started
    log.info(
        "llm.assist_done",
        provider=client.name,
        model=client.model,
        seconds=round(outcome.seconds, 1),
        requests=outcome.requests,
        rated=len(ratings),
        titles=len(titles),
        error=outcome.error,
    )
    return outcome
