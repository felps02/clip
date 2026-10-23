from __future__ import annotations

import shutil
from pathlib import Path

import numpy as np
import pytest

from app.analysis.transcription import Word
from app.config import Settings
from app.media.ffmpeg import FfmpegTools
from app.media.frames import extract_frame
from app.media.probe import probe_file
from app.models import WebcamRegion
from app.render.renderer import Renderer, RenderRequest

EXPECTED = {"16:9": (1280, 720), "9:16": (720, 1280), "1:1": (720, 720)}


@pytest.fixture(scope="module")
def renderer(base_settings: Settings, tools: FfmpegTools) -> Renderer:
    return Renderer(tools, base_settings.fonts_dir)


@pytest.fixture(scope="module")
def fake_words() -> list[Word]:
    text = ["isso", "foi", "um", "clutch", "absurdo", "não", "acredito", "vamos", "vamos", "vamos"]
    return [Word(40.3 + i * 0.5, 40.7 + i * 0.5, f" {w}", 0.9) for i, w in enumerate(text)]


def base_request(src: Path, tools: FfmpegTools, **overrides: object) -> RenderRequest:
    probe = probe_file(tools, src)
    fields: dict[str, object] = {
        "source": src,
        "src_w": probe.width,
        "src_h": probe.height,
        "start": 40.0,
        "end": 46.0,
        "max_render_height": 720,
        "has_audio": probe.has_audio,
    }
    fields.update(overrides)
    return RenderRequest(**fields)  # type: ignore[arg-type]


@pytest.mark.parametrize("aspect", list(EXPECTED))
def test_render_dimensions_subtitles_and_thumbnail(
    aspect: str,
    renderer: Renderer,
    tools: FfmpegTools,
    fixture_video: Path,
    fake_words: list[Word],
    tmp_path: Path,
) -> None:
    req = base_request(
        fixture_video,
        tools,
        aspect=aspect,
        words=fake_words,
        title="Clipe de teste",
        output_name=f"clip-{aspect.replace(':', 'x')}",
    )
    out = renderer.render(req, tmp_path)
    assert (out.width, out.height) == EXPECTED[aspect]
    info = probe_file(tools, out.path)
    assert (info.width, info.height) == EXPECTED[aspect]
    assert info.video_codec == "h264" and info.audio_codec == "aac" and info.sample_rate == 48000
    assert info.duration == pytest.approx(6.0, abs=0.3)
    assert out.duration == pytest.approx(6.0, abs=0.3)
    assert out.size_bytes == out.path.stat().st_size > 1000
    assert out.has_subtitles is True
    assert out.ass_path is not None and out.ass_path.exists()
    ass = out.ass_path.read_text(encoding="utf-8")
    assert "clutch" in ass and "Clipe de teste" in ass
    assert f"PlayResX: {EXPECTED[aspect][0]}" in ass
    assert out.thumbnail_path is not None and out.thumbnail_path.exists()
    assert out.thumbnail_path.read_bytes()[:3] == b"\xff\xd8\xff"
    thumb = probe_file(tools, out.thumbnail_path)
    assert thumb.width == 640
    assert out.layout == ("letterbox" if aspect == "16:9" else "center")


def test_render_without_subtitles(
    renderer: Renderer,
    tools: FfmpegTools,
    fixture_video: Path,
    fake_words: list[Word],
    tmp_path: Path,
) -> None:
    req = base_request(
        fixture_video,
        tools,
        aspect="9:16",
        words=fake_words,
        subtitles_enabled=False,
        max_render_height=480,
        end=43.0,
    )
    out = renderer.render(req, tmp_path)
    assert out.has_subtitles is False
    assert out.ass_path is None
    assert (out.width, out.height) == (480, 852)


def test_render_webcam_split_and_fallback(
    renderer: Renderer, tools: FfmpegTools, fixture_video: Path, tmp_path: Path
) -> None:
    region = WebcamRegion(x=0.7, y=0.65, w=0.3, h=0.35)
    req = base_request(
        fixture_video,
        tools,
        aspect="9:16",
        webcam_region=region,
        subtitles_enabled=False,
        max_render_height=480,
        end=43.0,
        output_name="split",
    )
    out = renderer.render(req, tmp_path)
    assert out.layout == "webcam-split" and (out.width, out.height) == (480, 852)
    bad = base_request(
        fixture_video,
        tools,
        aspect="9:16",
        webcam_region=WebcamRegion(x=0.9, y=0.9, w=0.5, h=0.5),
        framing_mode="webcam-split",
        subtitles_enabled=False,
        max_render_height=480,
        end=43.0,
        output_name="fallback",
    )
    out = renderer.render(bad, tmp_path)
    assert out.layout == "center" and out.warnings


def test_render_title_only_creates_ass_without_captions(
    renderer: Renderer, tools: FfmpegTools, fixture_video: Path, tmp_path: Path
) -> None:
    req = base_request(
        fixture_video,
        tools,
        aspect="1:1",
        words=None,
        title="Só o título",
        max_render_height=480,
        end=43.0,
        output_name="title",
    )
    out = renderer.render(req, tmp_path)
    assert out.has_subtitles is False
    assert out.ass_path is not None and "Só o título" in out.ass_path.read_text(encoding="utf-8")


def test_render_rejects_invalid_range(
    renderer: Renderer, tools: FfmpegTools, fixture_video: Path, tmp_path: Path
) -> None:
    req = base_request(
        fixture_video,
        tools,
        aspect="9:16",
        start=200.0,
        end=206.0,
        subtitles_enabled=False,
        max_render_height=480,
    )
    with pytest.raises(Exception):  # noqa: B017 - ffmpeg produces no output past the end of the file
        renderer.render(req, tmp_path)


def test_render_with_special_characters_in_the_work_and_fonts_dirs(
    base_settings: Settings,
    tools: FfmpegTools,
    fixture_video: Path,
    fake_words: list[Word],
    tmp_path: Path,
) -> None:
    """The default Windows WORK_DIR contains the user name (O'Neil, D'Souza, José...) and
    the fonts dir the checkout path: apostrophes, spaces, punctuation, accents and the
    drive colon / backslashes must survive ffmpeg's two parsing levels of the filter
    graph, in the ASS file name and in ``fontsdir`` alike, and the captions are burned."""
    fonts = tmp_path / "d'souza fonts [1],x;y"
    shutil.copytree(base_settings.fonts_dir, fonts)
    renderer = Renderer(tools, fonts)
    common: dict[str, object] = {
        "aspect": "9:16",
        "words": fake_words,
        "title": "Clipe do O'Neil",
        "max_render_height": 480,
        "end": 43.0,
    }
    work = tmp_path / "José d'Ávila" / "o'neil [x],y;z=w (a&b)" / "clips"
    out = renderer.render(base_request(fixture_video, tools, output_name="quote", **common), work)
    assert out.path.exists() and out.has_subtitles is True
    assert out.ass_path is not None and out.ass_path.parent == work
    assert "O'Neil" in out.ass_path.read_text(encoding="utf-8")
    assert (out.width, out.height) == (480, 852)

    # same clip without subtitles: the title band and the caption band must differ
    plain = renderer.render(
        base_request(fixture_video, tools, output_name="plain", subtitles_enabled=False, **common),
        tmp_path / "plain",
    )
    burned = frame_at(tools, out.path, 1.5)
    reference = frame_at(tools, plain.path, 1.5)
    diff = np.abs(burned.astype(np.int16) - reference.astype(np.int16))
    title_band = diff[int(852 * 0.08) : int(852 * 0.16)].mean()
    caption_band = diff[int(852 * 0.62) : int(852 * 0.80)].mean()
    untouched = diff[int(852 * 0.35) : int(852 * 0.50)].mean()
    assert title_band > 5 * (untouched + 0.5), (title_band, untouched)
    assert caption_band > 5 * (untouched + 0.5), (caption_band, untouched)


def frame_at(tools: FfmpegTools, path: Path, at: float) -> np.ndarray:
    info = probe_file(tools, path)
    frame = extract_frame(
        tools,
        path,
        at=at,
        src_w=int(info.width or 0),
        src_h=int(info.height or 0),
        width=int(info.width or 0),
    )
    assert frame is not None
    return frame
