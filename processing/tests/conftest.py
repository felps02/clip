from __future__ import annotations

import os
import subprocess
from pathlib import Path

import pytest

from app.config import Settings, settings_for_tests
from app.media.ffmpeg import FfmpegTools
from tests.fixtures.make_fixture import SHORT_SPEC, FixtureSpec, make_fixture
from tests.fixtures.make_tts_fixture import EN_TEXT, make_tts_wav

PROCESSING_DIR = Path(__file__).resolve().parents[1]
GENERATED_DIR = PROCESSING_DIR / "tests" / "fixtures" / "generated"
FIXTURE_SCRIPT = PROCESSING_DIR / "tests" / "fixtures" / "make_fixture.py"


def _stale(path: Path) -> bool:
    return (
        not path.exists()
        or path.stat().st_size == 0
        or path.stat().st_mtime < FIXTURE_SCRIPT.stat().st_mtime
    )


@pytest.fixture(scope="session")
def base_settings() -> Settings:
    return settings_for_tests(
        worker_enabled=False,
        dev_endpoints=True,
        whisper_enabled=True,
        whisper_local_files_only=True,
        work_dir=str(GENERATED_DIR / "work"),
        webcam_samples=12,
        log_level="WARNING",
        # the free-space check is tested on its own: a nearly full development disk
        # must not skip the renders the other tests expect
        render_min_free_mb=0,
    )


@pytest.fixture(scope="session")
def tools(base_settings: Settings) -> FfmpegTools:
    tools = FfmpegTools.from_settings(base_settings)
    if not tools.available():
        pytest.skip(f"ffmpeg/ffprobe not available at {base_settings.ffmpeg_path}")
    return tools


@pytest.fixture(scope="session")
def fixture_spec() -> FixtureSpec:
    return FixtureSpec()


@pytest.fixture(scope="session")
def fixture_video(tools: FfmpegTools, fixture_spec: FixtureSpec) -> Path:
    out = GENERATED_DIR / "fixture-90s.mp4"
    return make_fixture(tools.ffmpeg_path, out, fixture_spec, force=_stale(out))


@pytest.fixture(scope="session")
def short_video(tools: FfmpegTools) -> Path:
    out = GENERATED_DIR / "fixture-20s.mp4"
    return make_fixture(tools.ffmpeg_path, out, SHORT_SPEC, force=_stale(out))


@pytest.fixture(scope="session")
def rotated_video(tools: FfmpegTools, short_video: Path) -> Path:
    """``short_video`` with a 90° display matrix, like a portrait phone recording: stored
    as 640x360, displayed (and decoded by ffmpeg's autorotate) as 360x640."""
    out = GENERATED_DIR / "fixture-20s-rotated.mp4"
    if _stale(out) or out.stat().st_mtime < short_video.stat().st_mtime:
        proc = subprocess.run(
            [
                tools.ffmpeg_path,
                "-nostdin",
                "-hide_banner",
                "-y",
                "-loglevel",
                "error",
                "-display_rotation",
                "90",
                "-i",
                str(short_video),
                "-c",
                "copy",
                str(out),
            ],
            capture_output=True,
            text=True,
            timeout=120,
            check=False,
        )
        if proc.returncode != 0 or not out.exists():
            pytest.skip(f"ffmpeg cannot write rotation metadata: {proc.stderr[-300:]}")
    return out


@pytest.fixture(scope="session")
def tts_wav(tools: FfmpegTools) -> tuple[Path, str] | None:
    out = GENERATED_DIR / "tts-ptbr.wav"
    if out.exists() and out.stat().st_size > 1000 and os.environ.get("CLIPFORGE_TTS_FORCE") != "1":
        return out, "cached"
    return make_tts_wav(out)


@pytest.fixture
def work_settings(base_settings: Settings, tmp_path: Path) -> Settings:
    return base_settings.model_copy(update={"work_dir": str(tmp_path / "work")})


@pytest.fixture(scope="session")
def tts_en_wav(tools: FfmpegTools) -> tuple[Path, str] | None:
    """English phrases spoken by an en-US SAPI voice (``None`` when none is installed)."""
    out = GENERATED_DIR / "tts-en.wav"
    if out.exists() and out.stat().st_size > 1000 and os.environ.get("CLIPFORGE_TTS_FORCE") != "1":
        return out, "cached"
    return make_tts_wav(out, EN_TEXT, culture="en-US", require_culture=True)
