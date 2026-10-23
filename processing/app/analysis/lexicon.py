"""Keyword lexicon (pt-BR + en) with category weights, accent-insensitive matching.

Matching works on normalised tokens: lower-case, accents stripped (NFKD), punctuation
removed and elongations collapsed (``goooool`` -> ``gol``, ``kkkkkk`` -> ``kkk``).
Multi-word phrases are matched greedily (longest first) on the token sequence, so hits
can be attributed to the word timestamps of the transcript.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from functools import lru_cache

MAX_PHRASE_TOKENS = 4

_PUNCT_RE = re.compile(r"[^\w\s']+", re.UNICODE)
# collapse letters repeated 3+ times ("goooool" -> "gol"); 'k' is excluded so that the
# laugh token "kkk" survives the normalisation
_ELONGATION_RE = re.compile(r"([^\W\d_k])\1{2,}", re.UNICODE)
_K_LAUGH_RE = re.compile(r"\bk{3,}\b")
_HAHA_RE = re.compile(r"\b(?:ha|he|hu|ja|je){2,}h?\b")
_RS_RE = re.compile(r"\b(?:rs){2,}\b")
_WS_RE = re.compile(r"\s+")


MAX_PHRASE_TOKENS_CUSTOM = 8
# the user's own keywords (``settings.customKeywords``): matched like the lexicon, weight 3
CUSTOM_CATEGORY = "custom"
CUSTOM_WEIGHT = 3.0


@dataclass(frozen=True, slots=True)
class LexiconEntry:
    phrase: str
    category: str
    weight: float
    lang: str
    custom: bool = False  # one of the user's keywords


@dataclass(frozen=True, slots=True)
class KeywordHit:
    index: int
    length: int
    phrase: str
    category: str
    weight: float
    custom: bool = False


def strip_accents(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    return "".join(ch for ch in decomposed if not unicodedata.combining(ch))


def normalize_text(text: str) -> str:
    """Lower-case, accent-free, punctuation-free, elongations collapsed."""
    text = strip_accents(text).lower()
    text = text.replace("’", "'")
    text = _PUNCT_RE.sub(" ", text)
    text = _K_LAUGH_RE.sub("kkk", text)
    text = _HAHA_RE.sub("haha", text)
    text = _RS_RE.sub("rsrs", text)
    text = _ELONGATION_RE.sub(r"\1", text)
    text = text.replace("'", "")
    return _WS_RE.sub(" ", text).strip()


def tokenize(text: str) -> list[str]:
    normalised = normalize_text(text)
    return normalised.split() if normalised else []


_RAW_ENTRIES: list[tuple[str, str, float, str]] = [
    # phrase, category, weight, lang -------------------------------------------- hype
    ("vamos", "hype", 1.5, "pt"),
    ("vamo", "hype", 1.5, "pt"),
    ("bora", "hype", 1.5, "pt"),
    ("isso ai", "hype", 1.5, "pt"),
    ("ai sim", "hype", 2.0, "pt"),
    ("demais", "hype", 1.0, "pt"),
    ("insano", "hype", 2.5, "pt"),
    ("insana", "hype", 2.5, "pt"),
    ("absurdo", "hype", 2.5, "pt"),
    ("absurda", "hype", 2.5, "pt"),
    ("brabo", "hype", 2.0, "pt"),
    ("braba", "hype", 2.0, "pt"),
    ("monstro", "hype", 2.0, "pt"),
    ("sensacional", "hype", 2.0, "pt"),
    ("incrivel", "hype", 2.0, "pt"),
    ("lendario", "hype", 2.0, "pt"),
    ("epico", "hype", 2.0, "pt"),
    ("gol", "hype", 2.5, "pt"),
    ("ganhamos", "hype", 2.5, "pt"),
    ("vitoria", "hype", 2.0, "pt"),
    ("uau", "hype", 1.5, "pt"),
    ("lets go", "hype", 2.0, "en"),
    ("let's go", "hype", 2.0, "en"),
    ("go go go", "hype", 2.0, "en"),
    ("insane", "hype", 2.5, "en"),
    ("incredible", "hype", 2.0, "en"),
    ("amazing", "hype", 2.0, "en"),
    ("awesome", "hype", 1.5, "en"),
    ("epic", "hype", 2.0, "en"),
    ("legendary", "hype", 2.0, "en"),
    ("poggers", "hype", 2.0, "en"),
    ("pog", "hype", 1.5, "en"),
    ("hype", "hype", 1.5, "en"),
    ("we won", "hype", 2.5, "en"),
    ("victory", "hype", 2.0, "en"),
    ("goal", "hype", 2.0, "en"),
    ("wow", "hype", 1.5, "en"),
    ("winner winner", "hype", 2.5, "en"),
    # ------------------------------------------------------------------------ gameplay
    ("clutch", "gameplay", 3.0, "en"),
    ("ace", "gameplay", 3.0, "en"),
    ("headshot", "gameplay", 2.5, "en"),
    ("one tap", "gameplay", 2.0, "en"),
    ("no scope", "gameplay", 2.5, "en"),
    ("noscope", "gameplay", 2.5, "en"),
    ("triple kill", "gameplay", 2.5, "en"),
    ("double kill", "gameplay", 2.0, "en"),
    ("penta", "gameplay", 3.0, "en"),
    ("quadra", "gameplay", 2.5, "pt"),
    ("flick", "gameplay", 2.0, "en"),
    ("rush", "gameplay", 1.5, "en"),
    ("rushar", "gameplay", 1.5, "pt"),
    ("overtime", "gameplay", 1.5, "en"),
    ("combo", "gameplay", 1.5, "en"),
    ("ult", "gameplay", 1.5, "en"),
    ("ulti", "gameplay", 1.5, "pt"),
    ("speedrun", "gameplay", 2.0, "en"),
    ("world record", "gameplay", 3.0, "en"),
    ("recorde", "gameplay", 2.5, "pt"),
    ("record", "gameplay", 2.0, "en"),
    ("matei", "gameplay", 2.0, "pt"),
    ("mata ele", "gameplay", 2.0, "pt"),
    ("abate", "gameplay", 1.5, "pt"),
    ("kill", "gameplay", 1.5, "en"),
    ("kills", "gameplay", 1.5, "en"),
    ("gg", "gameplay", 1.5, "en"),
    ("ez", "gameplay", 1.5, "en"),
    ("easy", "gameplay", 1.0, "en"),
    ("ranked", "gameplay", 1.0, "en"),
    ("chicken dinner", "gameplay", 2.5, "en"),
    ("boss", "gameplay", 1.5, "en"),
    ("chefe", "gameplay", 1.5, "pt"),
    ("derrotamos", "gameplay", 2.0, "pt"),
    ("defuse", "gameplay", 2.0, "en"),
    ("defusar", "gameplay", 2.0, "pt"),
    ("plant", "gameplay", 1.0, "en"),
    ("frag", "gameplay", 1.5, "en"),
    ("spray", "gameplay", 1.0, "en"),
    ("jogada", "gameplay", 1.5, "pt"),
    ("play", "gameplay", 1.0, "en"),
    ("round", "gameplay", 1.0, "en"),
    ("partida", "gameplay", 1.0, "pt"),
    ("elo", "gameplay", 1.0, "pt"),
    # --------------------------------------------------------------------------- funny
    ("kkk", "funny", 2.5, "pt"),
    ("haha", "funny", 2.0, "en"),
    ("rsrs", "funny", 2.0, "pt"),
    ("lol", "funny", 1.5, "en"),
    ("lmao", "funny", 2.0, "en"),
    ("rindo", "funny", 2.0, "pt"),
    ("ri muito", "funny", 2.5, "pt"),
    ("rachei", "funny", 2.5, "pt"),
    ("morri de rir", "funny", 3.0, "pt"),
    ("piada", "funny", 1.5, "pt"),
    ("hilario", "funny", 2.5, "pt"),
    ("hilaria", "funny", 2.5, "pt"),
    ("engracado", "funny", 2.0, "pt"),
    ("engracada", "funny", 2.0, "pt"),
    ("zoeira", "funny", 2.0, "pt"),
    ("zoando", "funny", 1.5, "pt"),
    ("palhaco", "funny", 1.5, "pt"),
    ("comedia", "funny", 2.0, "pt"),
    ("trolei", "funny", 2.0, "pt"),
    ("trollado", "funny", 2.0, "pt"),
    ("troll", "funny", 1.5, "en"),
    ("meme", "funny", 1.5, "en"),
    ("bugou", "funny", 2.0, "pt"),
    ("bugado", "funny", 2.0, "pt"),
    ("bug", "funny", 1.5, "en"),
    ("glitch", "funny", 1.5, "en"),
    ("funny", "funny", 2.0, "en"),
    ("hilarious", "funny", 2.5, "en"),
    ("joke", "funny", 1.5, "en"),
    ("fail", "funny", 1.5, "en"),
    ("vergonha", "funny", 1.5, "pt"),
    # ------------------------------------------------------------------------- tension
    ("cuidado", "tension", 2.0, "pt"),
    ("calma", "tension", 1.5, "pt"),
    ("perigo", "tension", 2.0, "pt"),
    ("ultimo", "tension", 1.0, "pt"),
    ("ultima", "tension", 1.0, "pt"),
    ("ultimo round", "tension", 2.5, "pt"),
    ("last one", "tension", 2.0, "en"),
    ("last round", "tension", 2.5, "en"),
    ("careful", "tension", 2.0, "en"),
    ("medo", "tension", 2.0, "pt"),
    ("tenso", "tension", 2.5, "pt"),
    ("tensa", "tension", 2.5, "pt"),
    ("tensao", "tension", 2.5, "pt"),
    ("pressao", "tension", 2.0, "pt"),
    ("pressure", "tension", 2.0, "en"),
    ("nervoso", "tension", 2.0, "pt"),
    ("nervosa", "tension", 2.0, "pt"),
    ("nao erra", "tension", 2.5, "pt"),
    ("quase", "tension", 1.0, "pt"),
    ("por pouco", "tension", 2.0, "pt"),
    ("almost", "tension", 1.0, "en"),
    ("so close", "tension", 2.0, "en"),
    ("um de vida", "tension", 3.0, "pt"),
    ("one hp", "tension", 3.0, "en"),
    ("sem vida", "tension", 2.0, "pt"),
    ("morrendo", "tension", 1.5, "pt"),
    ("dying", "tension", 1.5, "en"),
    ("match point", "tension", 2.5, "en"),
    ("decisivo", "tension", 2.0, "pt"),
    ("decisiva", "tension", 2.0, "pt"),
    ("respira", "tension", 1.5, "pt"),
    ("silencio", "tension", 1.0, "pt"),
    ("socorro", "tension", 2.0, "pt"),
    ("corre", "tension", 1.5, "pt"),
    ("run", "tension", 1.0, "en"),
    # ------------------------------------------------------------------------ reaction
    ("nao acredito", "reaction", 3.0, "pt"),
    ("nao e possivel", "reaction", 3.0, "pt"),
    ("meu deus", "reaction", 2.5, "pt"),
    ("ai meu deus", "reaction", 3.0, "pt"),
    ("deus do ceu", "reaction", 2.5, "pt"),
    ("misericordia", "reaction", 2.5, "pt"),
    ("nossa", "reaction", 1.5, "pt"),
    ("nossa senhora", "reaction", 2.5, "pt"),
    ("caramba", "reaction", 2.0, "pt"),
    ("caraca", "reaction", 2.0, "pt"),
    ("caralho", "reaction", 2.5, "pt"),
    ("porra", "reaction", 2.0, "pt"),
    ("puta", "reaction", 2.0, "pt"),
    ("pqp", "reaction", 2.5, "pt"),
    ("mds", "reaction", 2.0, "pt"),
    ("eita", "reaction", 2.0, "pt"),
    ("mano do ceu", "reaction", 3.0, "pt"),
    ("mano", "reaction", 1.0, "pt"),
    ("velho", "reaction", 1.0, "pt"),
    ("serio", "reaction", 1.5, "pt"),
    ("que isso", "reaction", 2.0, "pt"),
    ("o que", "reaction", 1.0, "pt"),
    ("nao nao nao", "reaction", 3.0, "pt"),
    ("ah nao", "reaction", 2.0, "pt"),
    ("sim sim sim", "reaction", 2.5, "pt"),
    ("my god", "reaction", 2.5, "en"),
    ("oh my god", "reaction", 3.0, "en"),
    ("omg", "reaction", 2.5, "en"),
    ("no way", "reaction", 3.0, "en"),
    ("what", "reaction", 1.0, "en"),
    ("wtf", "reaction", 2.5, "en"),
    ("holy", "reaction", 2.0, "en"),
    ("holy shit", "reaction", 3.0, "en"),
    ("jesus", "reaction", 2.0, "en"),
    ("bro", "reaction", 1.0, "en"),
    ("dude", "reaction", 1.0, "en"),
    ("oh no", "reaction", 2.0, "en"),
    ("nope", "reaction", 1.5, "en"),
    ("serious", "reaction", 1.5, "en"),
    ("seriously", "reaction", 1.5, "en"),
    ("help", "reaction", 1.5, "en"),
    # --------------------------------------------------------------------------- quote
    ("a verdade e", "quote", 2.0, "pt"),
    ("eu acho que", "quote", 1.5, "pt"),
    ("na minha opiniao", "quote", 2.0, "pt"),
    ("o segredo e", "quote", 2.5, "pt"),
    ("a dica e", "quote", 2.5, "pt"),
    ("dica", "quote", 1.5, "pt"),
    ("conselho", "quote", 2.0, "pt"),
    ("licao", "quote", 2.0, "pt"),
    ("aprendi", "quote", 2.0, "pt"),
    ("honestly", "quote", 1.5, "en"),
    ("the truth is", "quote", 2.0, "en"),
    ("i think", "quote", 1.0, "en"),
    ("my advice", "quote", 2.5, "en"),
    ("lesson", "quote", 2.0, "en"),
    ("the secret is", "quote", 2.5, "en"),
]


class Lexicon:
    def __init__(self, entries: list[LexiconEntry]) -> None:
        self.entries = entries
        self._by_first: dict[str, list[tuple[tuple[str, ...], LexiconEntry]]] = {}
        for entry in entries:
            tokens = tuple(tokenize(entry.phrase))
            if not tokens:
                continue
            self._by_first.setdefault(tokens[0], []).append((tokens, entry))
        for options in self._by_first.values():
            options.sort(key=lambda item: -len(item[0]))

    @property
    def categories(self) -> set[str]:
        return {e.category for e in self.entries}

    def match_tokens(self, tokens: list[str]) -> list[KeywordHit]:
        """Greedy longest-match over normalised tokens."""
        hits: list[KeywordHit] = []
        i = 0
        n = len(tokens)
        while i < n:
            options = self._by_first.get(tokens[i])
            matched = False
            if options:
                for phrase_tokens, entry in options:
                    length = len(phrase_tokens)
                    if i + length <= n and tuple(tokens[i : i + length]) == phrase_tokens:
                        hits.append(
                            KeywordHit(
                                i,
                                length,
                                entry.phrase,
                                entry.category,
                                entry.weight,
                                entry.custom,
                            )
                        )
                        i += length
                        matched = True
                        break
            if not matched:
                i += 1
        return hits

    def match_text(self, text: str) -> list[KeywordHit]:
        return self.match_tokens(tokenize(text))

    def score(self, text: str) -> float:
        return float(sum(h.weight for h in self.match_text(text)))


@lru_cache(maxsize=1)
def default_lexicon() -> Lexicon:
    entries = [LexiconEntry(normalize_text(p), c, w, lang) for p, c, w, lang in _RAW_ENTRIES]
    return Lexicon(entries)


def with_custom_keywords(base: Lexicon, keywords: list[str] | tuple[str, ...]) -> Lexicon:
    """``base`` plus the user's keywords, matched the same way (accent/case-insensitive,
    longest phrase first) with weight 3 and flagged ``custom``. A keyword that already is a
    lexicon phrase keeps its category (so it still drives the moment's category) with
    weight ``max(3, its weight)``; any other keyword gets the category ``custom``, which
    no candidate category rule uses."""
    if not keywords:
        return base
    entries = list(base.entries)
    index = {entry.phrase: i for i, entry in enumerate(entries)}
    for keyword in keywords:
        tokens = tokenize(keyword)[:MAX_PHRASE_TOKENS_CUSTOM]
        if not tokens:
            continue
        phrase = " ".join(tokens)
        at = index.get(phrase)
        if at is not None:
            known = entries[at]
            entries[at] = LexiconEntry(
                phrase, known.category, max(CUSTOM_WEIGHT, known.weight), known.lang, True
            )
        else:
            index[phrase] = len(entries)
            entries.append(LexiconEntry(phrase, CUSTOM_CATEGORY, CUSTOM_WEIGHT, "custom", True))
    return Lexicon(entries)
