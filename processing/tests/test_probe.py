from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

import pytest

from app.media.ffmpeg import FfmpegTools
from app.media.frames import iter_frames
from app.media.probe import (
    container_from_format,
    parse_fps,
    parse_probe,
    parse_rotation,
    probe_file,
)
from app.render.layouts import build_layout

VIDEO_JSON = {
    "streams": [
        {
            "index": 0,
            "codec_name": "h264",
            "codec_type": "video",
            "width": 1920,
            "height": 1080,
            "r_frame_rate": "60/1",
            "avg_frame_rate": "30000/1001",
            "duration": "120.5",
            "disposition": {"attached_pic": 0},
        },
        {
            "index": 1,
            "codec_name": "aac",
            "codec_type": "audio",
            "sample_rate": "48000",
            "channels": 2,
            "duration": "120.5",
        },
    ],
    "format": {
        "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
        "duration": "120.517000",
        "size": "123456789",
        "bit_rate": "8190000",
    },
}


def test_parse_video_with_audio() -> None:
    result = parse_probe(VIDEO_JSON, file_name="live.mp4")
    assert result.has_video and result.has_audio
    assert (result.width, result.height) == (1920, 1080)
    assert result.fps == pytest.approx(29.97, abs=0.01)
    assert result.duration == pytest.approx(120.517)
    assert result.container == "mp4"
    assert result.video_codec == "h264" and result.audio_codec == "aac"
    assert result.size_bytes == 123456789
    assert result.sample_rate == 48000 and result.channels == 2
    report = result.to_report().to_api()
    assert report == {
        "durationSeconds": 120.517,
        "width": 1920,
        "height": 1080,
        "fps": 29.97,
        "hasAudio": True,
        "hasVideo": True,
        "container": "mp4",
        "videoCodec": "h264",
        "audioCodec": "aac",
        "sizeBytes": 123456789,
    }


def test_audio_only_and_cover_art_are_not_video() -> None:
    data = {
        "streams": [
            {
                "codec_type": "video",
                "codec_name": "mjpeg",
                "width": 300,
                "height": 300,
                "disposition": {"attached_pic": 1},
            },
            {"codec_type": "audio", "codec_name": "mp3", "sample_rate": "44100", "channels": 2},
        ],
        "format": {"format_name": "mp3", "duration": "200.0", "size": "100"},
    }
    result = parse_probe(data)
    assert result.has_audio and not result.has_video
    assert result.width is None and result.height is None
    assert result.container == "mp3"


def test_duration_falls_back_to_streams_and_size_to_stat() -> None:
    data = {
        "streams": [
            {
                "codec_type": "video",
                "codec_name": "vp9",
                "width": 640,
                "height": 360,
                "duration": "33.3",
            }
        ],
        "format": {"format_name": "matroska,webm"},
    }
    result = parse_probe(data, size_bytes=42, file_name="x.webm")
    assert result.duration == pytest.approx(33.3)
    assert result.size_bytes == 42
    assert result.container == "webm"
    assert not result.has_audio
    assert parse_probe({"streams": [], "format": {}}).duration is None


def test_fps_and_container_parsing() -> None:
    assert parse_fps("30000/1001") == pytest.approx(29.97, abs=0.01)
    assert parse_fps("0/0") is None
    assert parse_fps("25/1") == 25.0
    assert parse_fps(None) is None
    assert parse_fps("garbage") is None
    assert container_from_format("matroska,webm", "a.mkv") == "mkv"
    assert container_from_format("matroska,webm", "a.webm") == "webm"
    assert container_from_format("mov,mp4,m4a,3gp,3g2,mj2", "a.mov") == "mov"
    assert container_from_format("wav") == "wav"
    assert container_from_format(None) is None


def test_probe_real_file(tools: FfmpegTools, short_video: Path) -> None:
    result = probe_file(tools, short_video)
    assert result.has_video and result.has_audio
    assert (result.width, result.height) == (640, 360)
    assert result.rotation == 0
    assert result.duration == pytest.approx(20.0, abs=0.2)
    assert result.container == "mp4"
    assert result.size_bytes == short_video.stat().st_size


def _with_video_stream(**extra: Any) -> dict[str, Any]:
    data = json.loads(json.dumps(VIDEO_JSON))
    data["streams"][0].update(extra)
    return data


def test_rotation_metadata_gives_display_dimensions() -> None:
    matrix = {"side_data_type": "Display Matrix", "displaymatrix": "…", "rotation": -90}
    result = parse_probe(_with_video_stream(side_data_list=[matrix]))
    assert (result.width, result.height, result.rotation) == (1080, 1920, 270)
    assert result.to_report().to_api()["width"] == 1080  # the API/UI get the display size
    quarter = parse_probe(_with_video_stream(side_data_list=[{**matrix, "rotation": 90}]))
    assert (quarter.width, quarter.height, quarter.rotation) == (1080, 1920, 90)
    upside_down = parse_probe(_with_video_stream(side_data_list=[{**matrix, "rotation": 180}]))
    assert (upside_down.width, upside_down.height, upside_down.rotation) == (1920, 1080, 180)
    legacy = parse_probe(_with_video_stream(tags={"rotate": "270"}))
    assert (legacy.width, legacy.height, legacy.rotation) == (1080, 1920, 270)
    other = parse_probe(
        _with_video_stream(
            side_data_list=[{"side_data_type": "Content Light Level metadata"}],
            tags={"rotate": "garbage"},
        )
    )
    assert (other.width, other.height, other.rotation) == (1920, 1080, 0)
    assert parse_probe(VIDEO_JSON).rotation == 0
    assert parse_rotation({"side_data_list": [{"rotation": 89.6}]}) == 90
    assert parse_rotation({"side_data_list": [{"rotation": -270}]}) == 90
    assert parse_rotation({"side_data_list": "not a list", "tags": None}) == 0


def test_probe_rotated_real_file(tools: FfmpegTools, rotated_video: Path) -> None:
    """A portrait phone recording (stored landscape + 90° display matrix) is reported and
    processed in its display orientation, because ffmpeg autorotates when decoding."""
    probe = probe_file(tools, rotated_video)
    assert probe.rotation == 90
    assert (probe.width, probe.height) == (360, 640)
    # the first rows of a decoded frame are probe.width pixels wide
    result = tools.run(
        [
            "-i",
            os.fspath(rotated_video),
            "-frames:v",
            "1",
            "-vf",
            "crop=iw:2:0:0",
            "-f",
            "rawvideo",
            "-pix_fmt",
            "gray",
            "-",
        ],
        timeout=60.0,
        capture_stdout=True,
    )
    assert len(result.stdout) == probe.width * 2
    # 9:16 keeps the whole portrait frame (not a 56 % zoom computed from the stored landscape)
    plan = build_layout("9:16", probe.width, probe.height, 480)
    assert plan.filter_complex.startswith("[0:v]crop=360:")
    # sampled analysis frames use the display orientation too
    frames = iter_frames(
        tools,
        rotated_video,
        src_w=probe.width,
        src_h=probe.height,
        fps=1.0,
        width=64,
        total_duration=probe.duration,
    )
    try:
        _t, frame = next(frames)
    finally:
        frames.close()
    assert frame.shape == (114, 64)
