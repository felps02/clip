"""LLM assist stage (round 3): re-rank against instructions/focus, validated titles, budget."""

from __future__ import annotations

from typing import Any

import pytest

from app.analysis.fusion import Candidate, rank_candidates
from app.analysis.llm import LlmError, LlmTimeout
from app.analysis.llm_assist import (
    MAX_CANDIDATES,
    SYSTEM_PROMPT,
    excerpt_for,
    grounded_needed,
    is_grounded,
    run_assist,
    validate_title,
)
from app.models import AnalysisSettings
from app.worker.errors import CancelToken, JobCancelled

TEXTS = [
    "nossa que jogada absurda no último segundo do round",
    "kkkk ele caiu da ponte de novo eu avisei",
    "valeu pelo sub pessoal bem-vindos à live de hoje",
    "três kills com a pistola meu deus que mira",
]


def make_candidates(texts: list[str]) -> list[Candidate]:
    candidates = [
        Candidate(
            rank=0,
            start=i * 40.0,
            end=i * 40.0 + 30.0,
            peak=i * 40.0 + 10.5,
            composite=3.0 - i * 0.1,
            score=90 - i,
            category="highlight",
            reasoning="r",
            transcript=text,
            signals={"llm": {"available": False}},
            priority=float(90 - i),
        )
        for i, text in enumerate(texts)
    ]
    rank_candidates(candidates)
    return candidates


class FakeClient:
    """Answers from a ``replies`` callback; records every request."""

    name = "local"

    def __init__(self, reply: Any, model: str | None = "Qwen3-1.7B-Q4_K_M") -> None:
        self.reply = reply
        self._model = model
        self.requests: list[dict[str, Any]] = []

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
        self.requests.append(
            {
                "system": system,
                "user": user,
                "schema": schema,
                "timeout": timeout,
                "max": max_tokens,
            }
        )
        return self.reply(schema)


def test_rerank_moves_the_rank_priority_but_never_the_score() -> None:
    candidates = make_candidates(TEXTS)
    ratings = {"1": 2, "2": 10, "3": 0, "4": 5}
    titles = {
        "1": "Jogada absurda no último segundo",
        "2": "Caiu da ponte de novo",
        "3": "Bem-vindos à live #sub",  # hashtag: rejected
        "4": "Três kills com a pistola",
    }
    client = FakeClient(
        lambda schema: {k: {"titulo": titles[k], "nota": ratings[k]} for k in schema["properties"]}
    )
    settings = AnalysisSettings(instructions="quero os momentos engraçados", max_candidates=4)
    outcome = run_assist(client, candidates, settings, budget_seconds=100, timeout_seconds=60)
    assert outcome.available and outcome.requests == 1
    assert outcome.rerank_status == "applied" and outcome.rerank_rated == 4
    assert outcome.titles_status == "partial" and outcome.titles_accepted == 3
    assert outcome.titles_rejected == {"hashtag": 1}
    # 2 x (rating - 5): +10 for the funny one, -10 for the "sub" one
    # priorities: 90-6, 89+10, 88-10, 87+0
    assert [c.transcript for c in candidates] == [TEXTS[1], TEXTS[3], TEXTS[0], TEXTS[2]]
    assert [c.rank for c in candidates] == [1, 2, 3, 4]
    funny = candidates[0]
    assert funny.score == 89 and funny.priority == 99  # the stored score is untouched
    assert funny.signals["llm"] == {
        "available": True,
        "provider": "local",
        "model": "Qwen3-1.7B-Q4_K_M",
        "relevance": 10,
        "rankBonus": 10,
        "title": "Caiu da ponte de novo",
    }
    sub = next(c for c in candidates if c.transcript == TEXTS[2])
    assert sub.title is None and sub.signals["llm"]["rankBonus"] == -10
    assert "title" not in sub.signals["llm"]
    request = client.requests[0]
    assert request["system"] == SYSTEM_PROMPT
    assert "Pedido do usuário: quero os momentos engraçados" in request["user"]
    assert "[2] kkkk ele caiu da ponte de novo eu avisei" in request["user"]
    assert request["user"].endswith("Responda para os trechos 1, 2, 3, 4.")
    assert request["schema"]["required"] == ["1", "2", "3", "4"]
    item = request["schema"]["properties"]["1"]
    assert item["required"] == ["titulo", "nota"]
    assert item["properties"]["titulo"]["maxLength"] == 60
    data = outcome.to_json()
    assert data["rerank"]["status"] == "applied" and data["titles"]["accepted"] == 3
    assert outcome.notice() is None


def test_without_instructions_or_focus_only_titles_are_asked() -> None:
    candidates = make_candidates(TEXTS)
    client = FakeClient(
        lambda schema: {k: {"titulo": "Jogada absurda"} for k in schema["properties"]}
    )
    settings = AnalysisSettings(max_candidates=4, auto_render_top=2)
    outcome = run_assist(client, candidates, settings, budget_seconds=100, timeout_seconds=60)
    assert outcome.rerank_status == "skipped" and outcome.titles_requested == 2
    schema = client.requests[0]["schema"]
    assert list(schema["properties"]) == ["1", "2"]  # the two clips that will be rendered
    assert schema["properties"]["1"]["required"] == ["titulo"]
    assert "Pedido do usuário" not in client.requests[0]["user"]
    assert candidates[0].title == "Jogada absurda"  # grounded in TEXTS[0]
    assert candidates[1].title is None  # "Jogada absurda" is not grounded in the fall
    assert outcome.titles_rejected == {"not_grounded": 1}
    assert [c.transcript for c in candidates] == TEXTS  # no rating: order untouched


def test_nothing_to_ask_makes_no_request() -> None:
    client = FakeClient(lambda schema: {})
    outcome = run_assist(
        client,
        make_candidates(TEXTS),
        AnalysisSettings(auto_render_top=0),
        budget_seconds=100,
        timeout_seconds=60,
    )
    assert client.requests == [] and outcome.available is False
    assert outcome.rerank_status == "skipped" and outcome.titles_status == "skipped"
    assert outcome.to_json()["available"] is False


def test_failures_keep_what_was_obtained_and_stop_the_stage() -> None:
    texts = [f"{TEXTS[i % 4]} parte {i}" for i in range(25)]
    candidates = make_candidates(texts)
    calls = {"n": 0}

    def reply(schema: dict[str, Any]) -> dict[str, Any]:
        calls["n"] += 1
        if calls["n"] == 2:
            raise LlmTimeout("no answer within 60s")
        return {k: {"titulo": "Jogada absurda", "nota": 9} for k in schema["properties"]}

    client = FakeClient(reply)
    settings = AnalysisSettings(focus_categories=["gameplay"], max_candidates=25)
    outcome = run_assist(client, candidates, settings, budget_seconds=100, timeout_seconds=60)
    assert len(client.requests) == 2  # batches of 10 out of the top 20, stopped at the failure
    assert outcome.rerank_requested == MAX_CANDIDATES == 20 and outcome.rerank_rated == 10
    assert outcome.rerank_status == "partial" and outcome.error == "no answer within 60s"
    level, message, data = outcome.notice() or ("", "", {})
    assert level == "warning" and message.startswith("Modelo de linguagem (local) interrompido")
    assert "10 momentos avaliados" in message and data["signal"] == "llm"
    assert "Foco: jogadas" in client.requests[0]["user"]
    # the cached prefix is the same for both requests: system prompt + excerpts + request
    first, second = (r["user"] for r in client.requests)
    assert first.rsplit("\n", 1)[0] == second.rsplit("\n", 1)[0]


def test_the_budget_bounds_every_request() -> None:
    now = [0.0]
    client = FakeClient(lambda schema: {})

    def reply(schema: dict[str, Any]) -> dict[str, Any]:
        now[0] += 80.0  # a slow answer
        return {k: {"titulo": "x", "nota": 5} for k in schema["properties"]}

    client.reply = reply
    candidates = make_candidates([f"{TEXTS[0]} {i}" for i in range(20)])
    settings = AnalysisSettings(instructions="jogadas", max_candidates=20)
    outcome = run_assist(
        client,
        candidates,
        settings,
        budget_seconds=100,
        timeout_seconds=60,
        clock=lambda: now[0],
    )
    assert client.requests[0]["timeout"] == 60  # min(timeout, remaining budget)
    assert len(client.requests) == 2 and client.requests[1]["timeout"] == 20
    now[0] = 0.0
    client.requests.clear()
    outcome = run_assist(
        client,
        make_candidates([f"{TEXTS[0]} {i}" for i in range(20)]),
        settings,
        budget_seconds=60,
        timeout_seconds=60,
        clock=lambda: now[0],
    )
    assert len(client.requests) == 1 and outcome.budget_exhausted  # 80 s spent: no 2nd request
    assert outcome.rerank_status == "partial"
    assert "parou no limite de 60 s" in (outcome.notice() or ("", "", {}))[1]


def test_invalid_answers_and_provider_errors_never_raise() -> None:
    candidates = make_candidates(TEXTS)

    def broken(schema: dict[str, Any]) -> dict[str, Any]:
        raise LlmError("HTTP 500: boom")

    outcome = run_assist(
        FakeClient(broken),
        candidates,
        AnalysisSettings(instructions="x"),
        budget_seconds=100,
        timeout_seconds=60,
    )
    assert outcome.available is False and outcome.rerank_status == "failed"
    assert all(c.title is None for c in candidates)
    junk = run_assist(
        FakeClient(lambda schema: {"1": "x", "2": {"nota": "dez", "titulo": 5}, "3": {"nota": 99}}),
        make_candidates(TEXTS),
        AnalysisSettings(instructions="x"),
        budget_seconds=100,
        timeout_seconds=60,
    )
    assert junk.available and junk.rerank_rated == 1  # only the "99" (clamped to 10) parses


def test_a_cancelled_job_stops_the_stage() -> None:
    cancel = CancelToken()
    cancel.cancel()
    with pytest.raises(JobCancelled):
        run_assist(
            FakeClient(lambda schema: {}),
            make_candidates(TEXTS),
            AnalysisSettings(instructions="x"),
            budget_seconds=100,
            timeout_seconds=60,
            cancel=cancel,
        )


EXCERPT = "ele caiu da ponte de novo, três kills com a pistola, this is a test, que jogada absurda"


@pytest.mark.parametrize(
    ("title", "expected", "reason", "excerpt"),
    [
        ("  caiu da ponte de novo.  ", "Caiu da ponte de novo", None, EXCERPT),
        ('"Jogada absurda"', "Jogada absurda", None, EXCERPT),
        ("Três kills com a pistola", "Três kills com a pistola", None, EXCERPT),
        ("Teste da pistola", "Teste da pistola", None, EXCERPT),  # test/teste
        ("Caiu da ponte #fail", None, "hashtag", EXCERPT),
        ("Caiu da ponte @fulano", None, "hashtag", EXCERPT),
        ("Caiu da ponte em www.site.com", None, "link", EXCERPT),
        ("Caiu da ponte 😂", None, "emoji", EXCERPT),
        ("Caiu da ponte kkkk", None, "laughter", EXCERPT),
        ("x" * 61, None, "too_long", EXCERPT),
        ("caiu da ponte de novo e de novo e de novo eu falei", None, "too_long", EXCERPT),
        ("Valeu pelo sub, bem-vindos a直播", None, "script", EXCERPT),
        ("ok", None, "empty", EXCERPT),
        ("Vitória no campeonato", None, "not_grounded", EXCERPT),
        # one shared word no longer grounds an invented title (it would be burned into
        # the clip): "test" alone, and the spoken fillers of the review of round 3
        ("Teste de reconhecimento facial", None, "not_grounded", EXCERPT),
        (
            "Vamos vencer o campeonato mundial",
            None,
            "not_grounded",
            "agora vamos para a proxima partida pessoal, bora",
        ),
        (
            "Cara ganha um carro novo",
            None,
            "not_grounded",
            "cara eu nem sei o que falar sobre isso",
        ),
        ("Olha o presidente chegando", None, "not_grounded", "olha so quem apareceu no chat"),
        (None, None, "missing", EXCERPT),
    ],
)
def test_title_validation(
    title: Any, expected: str | None, reason: str | None, excerpt: str
) -> None:
    assert validate_title(title, excerpt) == (expected, reason)


def test_excerpt_and_grounding_helpers() -> None:
    assert excerpt_for("  a\n b  ") == "a b"
    long = " ".join(["palavra"] * 100)
    cut = excerpt_for(long)
    assert len(cut) <= 301 and cut.endswith("…") and not cut.endswith(" …")
    # one shared term out of three content words is not enough
    assert not is_grounded("Clutch no último round", "what a CLUTCH")
    assert is_grounded("Clutch no último round", "que CLUTCH no último round")
    assert is_grounded("Clutch no último round", "clutch no round final")  # 2 of 3
    assert not is_grounded("Muito bom isso", "muito bom isso aqui")  # stopwords only
    assert not is_grounded("Cara, olha isso", "cara olha isso")  # fillers only
    assert is_grounded("3 abates seguidos", "foram 3 abates seguidos")  # numbers count
    assert not is_grounded("3 kills seguidas", "foram 3 abates")  # only the number
    assert is_grounded("Jogadas absurdas", "que jogada absurda")  # jogada/jogadas
    # most of the content words must come from the excerpt
    excerpt = "o boss tem mais uma fase e a luta não acaba"
    assert is_grounded("Boss ganha fase nova na luta", excerpt)  # boss, fase, luta: 3 of 5
    assert not is_grounded("Boss ganha fase secreta", excerpt)  # boss, fase: 2 of 4
    assert not is_grounded("Boss ganha poder novo absurdo", excerpt)  # boss: 1 of 5


def test_grounding_threshold() -> None:
    """At least 60 % of the title's content words, rounded up; at least 2 from 3 words."""
    assert [grounded_needed(n) for n in range(1, 11)] == [1, 2, 2, 3, 3, 4, 5, 5, 6, 6]
