from __future__ import annotations

import itertools
import re
import shutil
import subprocess
from pathlib import Path

import pytest

from app.analysis.transcription import Word
from app.config import Settings
from app.media.ffmpeg import FfmpegTools
from app.render.subtitles import (
    ALIGNMENT,
    STYLE_PRESETS,
    TITLE_PRESET,
    build_ass,
    caption_margins,
    chunk_words,
    escape_ass_text,
    estimated_title_lines,
    filter_path,
    format_ass_time,
    subtitles_filter,
)


def words(n: int, start: float = 10.0, step: float = 0.3, length: float = 0.25) -> list[Word]:
    return [Word(start + i * step, start + i * step + length, f" w{i}") for i in range(n)]


def test_time_format() -> None:
    assert format_ass_time(0) == "0:00:00.00"
    assert format_ass_time(61.5) == "0:01:01.50"
    assert format_ass_time(3661.257) == "1:01:01.26"
    assert format_ass_time(-3) == "0:00:00.00"


def test_escaping_of_braces_backslash_and_newlines() -> None:
    text = escape_ass_text("a{b}c\\d\r\ne  \n f")
    assert "{" not in text and "}" not in text
    assert text == "a(b)c/d\\Ne\\Nf"
    assert text.count("\\") == text.count("\\N")  # the only backslashes are line breaks
    assert escape_ass_text("x\x00y\x07z") == "xyz"


def test_chunking_limits_words_and_duration() -> None:
    caps = chunk_words(words(10), 10.0, 20.0)
    assert caps
    for cap in caps:
        assert 1 <= len(cap.text.split()) <= 4
        assert cap.end - cap.start <= 2.0 + 1e-9
        assert cap.start >= 0 and cap.end <= 10.0
    for a, b in itertools.pairwise(caps):
        assert a.end <= b.start
    assert caps[0].start == pytest.approx(0.0)
    assert caps[0].text == "w0 w1 w2 w3"


def test_chunking_splits_on_long_gaps_and_duration() -> None:
    long_words = [Word(10.0, 10.3, "um"), Word(10.4, 10.7, "dois"), Word(12.5, 12.8, "tres")]
    caps = chunk_words(long_words, 10.0, 15.0)
    assert [c.text for c in caps] == ["um dois", "tres"]
    slow = [Word(10.0, 10.5, "a"), Word(11.0, 11.5, "b"), Word(11.8, 12.6, "c")]
    caps = chunk_words(slow, 10.0, 15.0)
    assert [c.text for c in caps] == ["a b", "c"]


def test_chunking_ignores_words_outside_the_clip() -> None:
    caps = chunk_words(words(10), 11.0, 12.0)
    assert caps
    assert all(0 <= c.start < c.end <= 1.0 for c in caps)
    assert chunk_words(words(10), 50.0, 60.0) == []


def test_build_ass_styles_title_and_events() -> None:
    caps = chunk_words(words(6), 10.0, 20.0)
    ass = build_ass(
        caps,
        title="Título {x}\\y",
        style="bold",
        position="bottom",
        play_res_x=1080,
        play_res_y=1920,
        clip_duration=10.0,
    )
    assert "[Script Info]" in ass and "PlayResX: 1080" in ass and "PlayResY: 1920" in ass
    assert "Style: Caption,DejaVu Sans Bold," in ass
    assert "Style: Title,DejaVu Sans Bold," in ass
    title_lines = [line for line in ass.splitlines() if line.startswith("Dialogue: 1,")]
    assert len(title_lines) == 1
    assert title_lines[0].endswith(",Title,,0,0,0,,Título (x)/y")
    assert "0:00:00.00,0:00:10.00,Title" in title_lines[0]
    caption_lines = [line for line in ass.splitlines() if line.startswith("Dialogue: 0,")]
    assert len(caption_lines) == len(caps)
    assert re.search(
        r"Dialogue: 0,\d:\d\d:\d\d\.\d\d,\d:\d\d:\d\d\.\d\d,Caption,,0,0,0,,w0 w1 w2 w3", ass
    )


@pytest.mark.parametrize("style", list(STYLE_PRESETS))
@pytest.mark.parametrize("position", list(ALIGNMENT))
def test_style_presets_and_positions(style: str, position: str) -> None:
    ass = build_ass(
        chunk_words(words(4), 10.0, 20.0),
        style=style,
        position=position,
        play_res_x=1080,
        play_res_y=1920,
    )
    caption_style = next(line for line in ass.splitlines() if line.startswith("Style: Caption,"))
    fields = caption_style.split(",")
    assert fields[1] == STYLE_PRESETS[style].fontname
    assert int(fields[15]) == STYLE_PRESETS[style].border_style
    assert int(fields[18]) == ALIGNMENT[position]
    if style == "boxed":
        assert int(fields[15]) == 3


def test_title_only_ass_has_no_caption_events() -> None:
    ass = build_ass([], title="Só título", clip_duration=5.0)
    assert "Dialogue: 1," in ass
    assert "Dialogue: 0," not in ass


def test_filter_path_escaping() -> None:
    # backslashes are escaped (not rewritten), so the path reaches ffmpeg unchanged
    assert filter_path("C:\\work\\a b\\x.ass") == "C\\:\\\\work\\\\a b\\\\x.ass"
    assert filter_path("/srv/a\\b/x.ass") == "/srv/a\\\\b/x.ass"  # a backslash in a POSIX name
    # a quote must still be an escaped quote after ffmpeg's first parsing level: the
    # shell-style "'\\''" leaves a bare quote for the second level, which drops it and
    # swallows the rest of the option string ("...oneil/x.ass:fontsdir=...")
    assert filter_path("/tmp/o'neil/x.ass") == "/tmp/o\\'\\''neil/x.ass"
    assert filter_path("/tmp/x''y'/x.ass") == "/tmp/x\\'\\''\\'\\''y\\'\\''/x.ass"
    assert filter_path("/tmp/a:b/x.ass") == "/tmp/a\\:b/x.ass"
    flt = subtitles_filter("C:\\w\\s.ass", "C:\\fonts")
    assert flt == "subtitles=filename='C\\:\\\\w\\\\s.ass':fontsdir='C\\:\\\\fonts'"


def av_get_token(buf: str, term: str) -> tuple[str, str]:
    """Port of libavutil's ``av_get_token``, the tokenizer ffmpeg applies both when parsing
    the filter graph (terminators ``[],;``) and when splitting a filter's ``key=value``
    options (terminator ``:``): ``\\`` escapes the next character, ``'...'`` is literal."""
    out: list[str] = []
    i = 0
    while i < len(buf) and buf[i] not in term:
        c = buf[i]
        i += 1
        if c == "\\" and i < len(buf):
            out.append(buf[i])
            i += 1
        elif c == "'":
            while i < len(buf) and buf[i] != "'":
                out.append(buf[i])
                i += 1
            if i < len(buf):
                i += 1
        else:
            out.append(c)
    return "".join(out), buf[i:]


@pytest.mark.parametrize(
    "ass_path",
    [
        "C:\\Users\\O'Neil\\AppData\\Local\\ClipForge\\work\\job-1\\clips\\clip-01.ass",
        "/var/lib/clipforge/work/x''y'/clip's.ass",
        "/tmp/a [b],c;d=e f/clip-01.ass",
        "/srv/time 12:30/back\\slash/o'neil: x/clip-01.ass",
        "C:\\Users\\José d'Ávila\\work dir\\clip-01.ass",
    ],
)
def test_filter_path_survives_both_ffmpeg_parsing_levels(ass_path: str) -> None:
    fonts_dir = "C:\\checkout\\d'souza [x],y;z\\processing\\assets\\fonts"
    flt = subtitles_filter(ass_path, fonts_dir)
    # level 1: the graph parser reads the filter name, then its option string
    name, rest = av_get_token(flt, "=,;[")
    assert name == "subtitles" and rest.startswith("=")
    opts, rest = av_get_token(rest[1:], "[],;")
    assert rest == ""
    # level 2: the filter's option parser splits key=value pairs on ':'
    parsed: dict[str, str] = {}
    while opts:
        key, opts = opts.split("=", 1)
        value, opts = av_get_token(opts, ":")
        parsed[key] = value
        opts = opts[1:]  # skip the ':' separator
    assert parsed == {"filename": ass_path, "fontsdir": fonts_dir}


TRICKY_DIR = "o'neil [x],y;z=w %20 (a&b) d'souza"


def _subtitles_probe(tools: FfmpegTools, flt: str) -> subprocess.CompletedProcess[str]:
    """Run the subtitles filter on one generated frame with ``-loglevel info`` (libass logs
    the font files it loads from ``fontsdir`` at that level)."""
    return subprocess.run(
        [
            tools.ffmpeg_path,
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "info",
            "-f",
            "lavfi",
            "-i",
            "color=c=black:s=320x180:d=1",
            "-filter_complex",
            f"[0:v]{flt}[v]",
            "-map",
            "[v]",
            "-frames:v",
            "1",
            "-f",
            "null",
            "-",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=120,
        check=False,
    )


def test_ffmpeg_opens_the_ass_file_and_fonts_from_directories_with_special_characters(
    tools: FfmpegTools, base_settings: Settings, tmp_path: Path
) -> None:
    """Real ffmpeg, both parsing levels: apostrophes, spaces, brackets, commas, semicolons,
    ``=``, ``%``, ``&`` and the drive colon / backslashes of an absolute Windows path, in
    the ASS file's directory and in ``fontsdir`` alike."""
    work = tmp_path / TRICKY_DIR / "clips"
    fonts = tmp_path / "fonts d'souza [1],x;y"
    shutil.copytree(base_settings.fonts_dir, fonts)
    ass = work / "clip's 01.ass"
    ass.parent.mkdir(parents=True)
    ass.write_text(
        build_ass(chunk_words(words(4), 10.0, 12.0), play_res_x=320, play_res_y=180),
        encoding="utf-8",
    )
    proc = _subtitles_probe(tools, subtitles_filter(ass, fonts))
    assert proc.returncode == 0, proc.stderr[-1500:]
    assert "Unable to open" not in proc.stderr
    # libass loaded the bundled fonts from the exact fontsdir (not from a mangled path)
    loaded = [ln for ln in proc.stderr.splitlines() if "Loading font file" in ln]
    assert any(str(fonts / "DejaVuSans-Bold.ttf") in ln for ln in loaded), proc.stderr[-1500:]


def test_ffmpeg_parses_a_colon_inside_a_directory_name_exactly(
    tools: FfmpegTools, tmp_path: Path
) -> None:
    """A colon cannot be part of a Windows file name, so the file cannot exist here: the
    file name ffmpeg reports as unopenable proves both parsing levels kept the whole path
    (and did not split the option string at the colon)."""
    missing = str(tmp_path) + ("\\" if "\\" in str(tmp_path) else "/") + "a:b c'd [x]"
    missing += ("\\" if "\\" in str(tmp_path) else "/") + "clip.ass"
    proc = _subtitles_probe(tools, subtitles_filter(missing, tmp_path))
    assert proc.returncode != 0
    unable = [ln for ln in proc.stderr.splitlines() if "Unable to open" in ln]
    assert unable and unable[0].endswith(missing), proc.stderr[-1500:]


def margins_of(ass: str) -> dict[str, tuple[int, int]]:
    """``{style name: (alignment, MarginV)}`` from the ``[V4+ Styles]`` section."""
    out: dict[str, tuple[int, int]] = {}
    for line in ass.splitlines():
        if line.startswith("Style: "):
            fields = line[len("Style: ") :].split(",")
            out[fields[0]] = (int(fields[18]), int(fields[21]))
    return out


@pytest.mark.parametrize(
    ("aspect", "size", "bottom", "top", "title"),
    [
        # 9:16 (TikTok/Reels/Shorts): bottom captions ~20 % up, top captions >= 12 %, title ~8 %
        ("9:16", (1080, 1920), 384, 230, 154),
        ("9:16", (720, 1280), 256, 154, 102),
        # 16:9 and 1:1 keep the previous margins: 8 % for captions, 5 % for the title
        ("16:9", (1920, 1080), 86, 86, 54),
        ("16:9", (1280, 720), 58, 58, 36),
        ("1:1", (1080, 1080), 86, 86, 54),
        ("1:1", (720, 720), 58, 58, 36),
    ],
)
def test_caption_and_title_margins_per_aspect_ratio(
    aspect: str, size: tuple[int, int], bottom: int, top: int, title: int
) -> None:
    w, h = size
    caps = chunk_words(words(4), 10.0, 20.0)
    for inferred in (aspect, None):  # explicit aspect, or inferred from the play resolution
        styles = margins_of(
            build_ass(caps, position="bottom", play_res_x=w, play_res_y=h, aspect=inferred)
        )
        assert styles["Caption"] == (2, bottom)
        assert styles["Title"] == (8, title)
        styles = margins_of(
            build_ass(caps, position="top", play_res_x=w, play_res_y=h, aspect=inferred)
        )
        assert styles["Caption"] == (8, top)
        # center captions use the middle alignment (libass ignores MarginV): unchanged
        styles = margins_of(
            build_ass(caps, position="center", play_res_x=w, play_res_y=h, aspect=inferred)
        )
        assert styles["Caption"][0] == 5


def test_bottom_captions_of_9x16_clear_the_platform_overlay() -> None:
    caption, title = caption_margins("bottom", title="x", play_res_x=1080, play_res_y=1920)
    assert caption >= 0.19 * 1920 and title == pytest.approx(0.08 * 1920, abs=1)
    caption_16x9, title_16x9 = caption_margins(
        "bottom", title="x", play_res_x=1920, play_res_y=1080, aspect="16:9"
    )
    assert (caption_16x9, title_16x9) == (86, 54)


@pytest.mark.parametrize(
    ("aspect", "size"), [("9:16", (1080, 1920)), ("16:9", (1920, 1080)), ("1:1", (1080, 1080))]
)
@pytest.mark.parametrize(
    "title",
    ["Clipe de teste", "Que jogada absurda do O'Neil no final da partida de ontem à noite"],
)
def test_top_captions_go_below_the_title(aspect: str, size: tuple[int, int], title: str) -> None:
    w, h = size
    caption, title_margin = caption_margins(
        "top", title=title, play_res_x=w, play_res_y=h, aspect=aspect
    )
    font = round(h * TITLE_PRESET.size_ratio)
    lines = estimated_title_lines(title, font, w - 2 * round(w * 0.05))
    assert caption >= title_margin + lines * font  # never overlaps the title block
    minimum = 0.12 if aspect == "9:16" else 0.15
    assert caption >= round(h * minimum)
    if aspect != "9:16" and lines == 1:
        assert caption == round(h * 0.15)  # 16:9 / 1:1 keep the previous 15 %


def test_estimated_title_lines() -> None:
    assert estimated_title_lines("curto", 96, 972) == 1
    long_title = "uma frase bem longa que não cabe numa linha só de jeito nenhum"
    assert estimated_title_lines(long_title, 96, 972) >= 3
    assert estimated_title_lines("a\nb", 96, 972) == 2
