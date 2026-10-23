"""ASS subtitle generation from word timestamps (burned with the ``subtitles`` filter).

Captions are chunks of <= 4 words / <= 2 s. Styles: ``bold`` (large white, black
outline, bottom), ``clean`` (medium, light outline), ``boxed`` (semi-transparent box).
The optional title is one event spanning the whole clip in a second style at the top.
User text is escaped: ``{`` ``}`` become parentheses (there is no ASS escape for override
braces), backslashes become ``/`` and newlines become ``\\N``.

Vertical margins follow the safe area of the target platform: 9:16 clips are published on
TikTok/Reels/Shorts, whose interface covers roughly the bottom fifth (description,
buttons) and the top tenth (tabs) of the screen, so their captions sit higher (bottom) or
lower (top) than in 16:9 / 1:1; a caption at the top never overlaps the title.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path

from app.analysis.transcription import Word

MAX_WORDS = 4
MAX_SECONDS = 2.0
MAX_GAP = 0.8
MIN_CAPTION_SECONDS = 0.35

ALIGNMENT = {"bottom": 2, "center": 5, "top": 8}
_CONTROL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


@dataclass(frozen=True, slots=True)
class SafeArea:
    """Vertical margins (``MarginV``) as fractions of the output height.

    ``top_with_title`` is the minimum margin of captions at the top when a title is shown:
    those captions always go below the (estimated) title block. libass ignores ``MarginV``
    for the middle alignment, so ``center`` captions are not affected.
    """

    bottom: float
    top: float
    top_with_title: float
    title: float


# 9:16 is the TikTok/Reels/Shorts format: captions stay out of the bottom ~20 % and the top
# ~12 % covered by the app interface, and the title sits about 8 % from the top.
SAFE_AREAS: dict[str, SafeArea] = {
    "9:16": SafeArea(bottom=0.20, top=0.12, top_with_title=0.12, title=0.08),
}
DEFAULT_SAFE_AREA = SafeArea(bottom=0.08, top=0.08, top_with_title=0.15, title=0.05)
# Title block estimate (DejaVu Sans Bold): average advance of a character and line height,
# in em, plus the gap kept between the title and captions placed below it.
TITLE_CHAR_WIDTH_EM = 0.62
TITLE_LINE_HEIGHT_EM = 1.2
TITLE_GAP_RATIO = 0.02


@dataclass(slots=True)
class Caption:
    start: float
    end: float
    text: str


@dataclass(frozen=True, slots=True)
class StylePreset:
    fontname: str
    size_ratio: float  # of PlayResY
    primary: str
    outline_colour: str
    back_colour: str
    bold: int
    border_style: int  # 1 = outline + shadow, 3 = opaque box
    outline_ratio: float
    shadow_ratio: float


STYLE_PRESETS: dict[str, StylePreset] = {
    "bold": StylePreset(
        "DejaVu Sans Bold", 0.058, "&H00FFFFFF", "&H00000000", "&H80000000", -1, 1, 0.0045, 0.002
    ),
    "clean": StylePreset(
        "DejaVu Sans", 0.046, "&H00FFFFFF", "&H40000000", "&H80000000", 0, 1, 0.0018, 0.0
    ),
    "boxed": StylePreset(
        "DejaVu Sans Bold", 0.05, "&H00FFFFFF", "&H80000000", "&H80000000", -1, 3, 0.006, 0.0
    ),
}
TITLE_PRESET = StylePreset(
    "DejaVu Sans Bold", 0.05, "&H00FFFFFF", "&H00000000", "&H80000000", -1, 1, 0.004, 0.0
)


def format_ass_time(seconds: float) -> str:
    """``H:MM:SS.cc`` (centiseconds), clamped at zero."""
    cs = max(0, int(round(float(seconds) * 100)))
    h, rem = divmod(cs, 360000)
    m, rem = divmod(rem, 6000)
    s, c = divmod(rem, 100)
    return f"{h}:{m:02d}:{s:02d}.{c:02d}"


def escape_ass_text(text: str) -> str:
    text = _CONTROL_RE.sub("", text.replace("\r\n", "\n").replace("\r", "\n"))
    text = text.replace("\\", "/")
    text = text.replace("{", "(").replace("}", ")")
    text = "\\N".join(part.strip() for part in text.split("\n"))
    return text.strip()


def chunk_words(
    words: list[Word],
    clip_start: float,
    clip_end: float,
    *,
    max_words: int = MAX_WORDS,
    max_seconds: float = MAX_SECONDS,
    max_gap: float = MAX_GAP,
) -> list[Caption]:
    """Group the words inside the clip into captions (times relative to the clip start)."""
    clip_duration = max(0.0, clip_end - clip_start)
    selected = [
        w
        for w in sorted(words, key=lambda w: w.start)
        if w.end > clip_start + 0.05 and w.start < clip_end - 0.05 and w.word.strip()
    ]
    groups: list[list[Word]] = []
    current: list[Word] = []
    for w in selected:
        if current and (
            len(current) >= max_words
            or (w.end - current[0].start) > max_seconds
            or (w.start - current[-1].end) > max_gap
        ):
            groups.append(current)
            current = []
        current.append(w)
    if current:
        groups.append(current)

    captions: list[Caption] = []
    for group in groups:
        start = max(0.0, group[0].start - clip_start)
        end = min(clip_duration, group[-1].end - clip_start)
        if end - start < MIN_CAPTION_SECONDS:
            end = min(clip_duration, start + MIN_CAPTION_SECONDS)
        if end <= start:
            continue
        text = " ".join(w.word.strip() for w in group)
        captions.append(Caption(round(start, 2), round(end, 2), text))
    for i in range(len(captions) - 1):
        if captions[i].end > captions[i + 1].start:
            captions[i].end = captions[i + 1].start
    return [c for c in captions if c.end > c.start]


def _font_size(preset: StylePreset, play_res_y: int) -> int:
    return max(10, int(round(play_res_y * preset.size_ratio)))


def _margin_h(play_res_x: int) -> int:
    return max(10, int(round(play_res_x * 0.05)))


def safe_area_for(aspect: str | None, play_res_x: int, play_res_y: int) -> SafeArea:
    """Safe area of the output format. Without ``aspect`` it is inferred from the play
    resolution (a 9:16 frame is at least 1.5x taller than wide)."""
    if aspect is None:
        aspect = "9:16" if play_res_y >= play_res_x * 1.5 else "other"
    return SAFE_AREAS.get(aspect, DEFAULT_SAFE_AREA)


def estimated_title_lines(title: str, font_size: int, usable_width: int) -> int:
    """Lines the title wraps to (greedy word wrap with an average character width)."""
    per_line = max(1, int(usable_width / max(1.0, font_size * TITLE_CHAR_WIDTH_EM)))
    lines = 0
    for paragraph in title.replace("\r", "\n").split("\n"):
        lines += 1
        current = 0
        for word in paragraph.split():
            if current == 0:
                current = len(word)
            elif current + 1 + len(word) <= per_line:
                current += 1 + len(word)
            else:
                lines += 1
                current = len(word)
    return max(1, lines)


def caption_margins(
    position: str,
    *,
    title: str | None,
    play_res_x: int,
    play_res_y: int,
    aspect: str | None = None,
) -> tuple[int, int]:
    """``(caption MarginV, title MarginV)`` in pixels of the play resolution."""
    area = safe_area_for(aspect, play_res_x, play_res_y)
    title_margin = int(round(play_res_y * area.title))
    if position == "top":
        if title and title.strip():
            size = _font_size(TITLE_PRESET, play_res_y)
            usable = play_res_x - 2 * _margin_h(play_res_x)
            lines = estimated_title_lines(title, size, usable)
            outline = play_res_y * TITLE_PRESET.outline_ratio
            block = lines * size * TITLE_LINE_HEIGHT_EM + 2 * outline
            below_title = title_margin + block + play_res_y * TITLE_GAP_RATIO
            caption = max(play_res_y * area.top_with_title, below_title)
        else:
            caption = play_res_y * area.top
    else:  # bottom; ``center`` uses the middle alignment, which ignores MarginV
        caption = play_res_y * area.bottom
    return int(round(caption)), title_margin


def _style_line(
    name: str, preset: StylePreset, play_res_x: int, play_res_y: int, alignment: int, margin_v: int
) -> str:
    size = _font_size(preset, play_res_y)
    outline = round(play_res_y * preset.outline_ratio, 1)
    shadow = round(play_res_y * preset.shadow_ratio, 1)
    margin_h = _margin_h(play_res_x)
    return (
        f"Style: {name},{preset.fontname},{size},{preset.primary},&H000000FF,"
        f"{preset.outline_colour},{preset.back_colour},{preset.bold},0,0,0,100,100,0,0,"
        f"{preset.border_style},{outline},{shadow},"
        f"{alignment},{margin_h},{margin_h},{margin_v},1"
    )


def build_ass(
    captions: list[Caption],
    *,
    title: str | None = None,
    style: str = "bold",
    position: str = "bottom",
    play_res_x: int = 1080,
    play_res_y: int = 1920,
    clip_duration: float | None = None,
    aspect: str | None = None,
) -> str:
    preset = STYLE_PRESETS.get(style, STYLE_PRESETS["bold"])
    alignment = ALIGNMENT.get(position, 2)
    caption_margin, title_margin = caption_margins(
        position, title=title, play_res_x=play_res_x, play_res_y=play_res_y, aspect=aspect
    )

    lines = [
        "[Script Info]",
        "; generated by ClipForge processing",
        "ScriptType: v4.00+",
        f"PlayResX: {int(play_res_x)}",
        f"PlayResY: {int(play_res_y)}",
        "WrapStyle: 0",
        "ScaledBorderAndShadow: yes",
        "YCbCr Matrix: TV.709",
        "",
        "[V4+ Styles]",
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, "
        "BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, "
        "BorderStyle, Outline, "
        "Shadow, Alignment, MarginL, MarginR, MarginV, Encoding",
        _style_line("Caption", preset, play_res_x, play_res_y, alignment, caption_margin),
        _style_line("Title", TITLE_PRESET, play_res_x, play_res_y, 8, title_margin),
        "",
        "[Events]",
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
    ]
    if title and title.strip():
        end = (
            clip_duration
            if clip_duration is not None
            else (max((c.end for c in captions), default=0.0) or 3600.0)
        )
        lines.append(
            f"Dialogue: 1,{format_ass_time(0.0)},{format_ass_time(end)},Title,,0,0,0,,"
            f"{escape_ass_text(title)}"
        )
    for cap in captions:
        text = escape_ass_text(cap.text)
        if not text:
            continue
        lines.append(
            f"Dialogue: 0,{format_ass_time(cap.start)},{format_ass_time(cap.end)},Caption,,0,0,0,,"
            f"{text}"
        )
    return "\n".join(lines) + "\n"


def write_ass(path: str | os.PathLike[str], content: str) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")
    return path


def filter_path(path: str | os.PathLike[str]) -> str:
    """Escape a path for use inside a single-quoted filter option (``filename='...'``).

    ffmpeg parses ``-filter_complex`` twice with the same tokenizer (``av_get_token``):
    the graph parser strips the quotes and backslash escapes of the option string, then
    the filter's option parser applies the same rules again to each ``key=value``. Inside
    the first level's quotes everything is literal except ``'``, so every character that
    is special at the *second* level gets one backslash, which the first level keeps:

    * ``\\`` -> ``\\\\``: a backslash escapes at the second level; Windows separators and
      backslashes inside POSIX names both survive unchanged;
    * ``:`` -> ``\\:``: the second level's option separator (drive letters, or a colon
      inside a POSIX directory name);
    * ``'`` -> ``\\'\\''``: literal backslash, close the quote, escaped quote, reopen, so the
      second level still sees an escaped quote. A bare quote there would be dropped and
      swallow the rest of the string (``:fontsdir=...`` ending up in the file name).

    Spaces, ``[ ] , ; = %`` and non-ASCII characters need nothing: they are literal inside
    the first level's quotes and not special at the second.
    """
    text = os.fspath(path).replace("\\", "\\\\")
    text = text.replace("'", "\\'\\''")
    return text.replace(":", "\\:")


def subtitles_filter(ass_path: str | os.PathLike[str], fonts_dir: str | os.PathLike[str]) -> str:
    return f"subtitles=filename='{filter_path(ass_path)}':fontsdir='{filter_path(fonts_dir)}'"
