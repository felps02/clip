from __future__ import annotations

from app.analysis.lexicon import default_lexicon, normalize_text, tokenize


def categories(text: str) -> set[str]:
    return {h.category for h in default_lexicon().match_text(text)}


def test_normalisation_strips_accents_case_and_punctuation() -> None:
    assert normalize_text("Que JOGADA incrível!!!") == "que jogada incrivel"
    assert normalize_text("Não acredito...") == "nao acredito"
    assert tokenize("Let's GO, bora!") == ["lets", "go", "bora"]


def test_elongations_and_laughs_collapse() -> None:
    assert normalize_text("goooooool") == "gol"
    assert normalize_text("vamoooos") == "vamos"
    assert normalize_text("kkkkkkkk") == "kkk"
    assert normalize_text("hahahahaha") == "haha"
    assert normalize_text("rsrsrsrs") == "rsrs"


def test_pt_br_matches_with_and_without_accents() -> None:
    lex = default_lexicon()
    hits = lex.match_text("que jogada incrível, não acredito")
    phrases = [h.phrase for h in hits]
    assert "incrivel" in phrases
    assert "nao acredito" in phrases
    assert lex.score("INCRIVEL") == lex.score("incrível") > 0
    assert "reaction" in categories("nao acredito nisso")


def test_multiword_phrases_are_matched_greedily() -> None:
    lex = default_lexicon()
    hits = lex.match_text("ai meu deus do céu")
    assert hits[0].phrase == "ai meu deus"
    assert hits[0].length == 3
    assert hits[0].index == 0


def test_english_keywords() -> None:
    assert "hype" in categories("let's go, that was insane")
    assert "gameplay" in categories("what a clutch, ace!")
    assert "reaction" in categories("oh my god no way")
    assert "funny" in categories("lmao that was hilarious")


def test_categories_cover_the_documented_set() -> None:
    assert default_lexicon().categories == {
        "hype",
        "gameplay",
        "funny",
        "tension",
        "reaction",
        "quote",
    }


def test_kkk_is_funny_and_weights_are_positive() -> None:
    hits = default_lexicon().match_text("kkkkkkk rachei")
    assert {h.category for h in hits} == {"funny"}
    assert all(h.weight > 0 for h in hits)


def test_match_tokens_positions() -> None:
    tokens = tokenize("isso foi um clutch absurdo")
    hits = default_lexicon().match_tokens(tokens)
    by_phrase = {h.phrase: h for h in hits}
    assert by_phrase["clutch"].index == 3
    assert by_phrase["absurdo"].index == 4


def test_no_hits_on_neutral_text() -> None:
    assert default_lexicon().match_text("a reunião começa às dez horas") == []


def test_custom_keywords_match_like_the_lexicon_with_weight_3() -> None:
    from app.analysis.lexicon import CUSTOM_CATEGORY, CUSTOM_WEIGHT, with_custom_keywords

    base = default_lexicon()
    lex = with_custom_keywords(base, ["Patrocínio", "que isso", "clutch", "boss fight"])
    hits = {
        h.phrase: h
        for h in lex.match_text("Valeu o PATROCINIO! Que isso, que clutch no boss fight")
    }
    assert hits["patrocinio"].category == CUSTOM_CATEGORY and hits["patrocinio"].custom
    assert hits["patrocinio"].weight == CUSTOM_WEIGHT == 3.0
    # already lexicon phrases: their category stays (it still drives the moment category),
    # with weight max(3, theirs), flagged custom
    assert hits["que isso"].category == "reaction" and hits["que isso"].weight == 3.0
    assert hits["que isso"].custom
    assert hits["clutch"].category == "gameplay" and hits["clutch"].custom
    # the longest phrase wins over the lexicon's "boss"
    assert hits["boss fight"].custom and "boss" not in hits
    assert with_custom_keywords(base, []) is base
    assert not any(h.custom for h in base.match_text("que isso clutch"))  # base untouched
