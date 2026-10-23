"""Shared test helpers.

* ``async def`` tests are run with :func:`asyncio.run` (no pytest-asyncio dependency), with a
  per-test timeout (``@pytest.mark.async_timeout(seconds)``, default 60 s).
* FFmpeg / ffprobe and the fixture video are located like the companion does; tests that need
  them are skipped when they are missing.
"""

from __future__ import annotations

import asyncio
import inspect
import os
import shutil
import subprocess
import sys
import time
from collections.abc import Iterator
from pathlib import Path

import pytest

from clipforge_companion.config import default_ffmpeg_path
from tests.media import run_ffmpeg

DEFAULT_ASYNC_TIMEOUT = 60.0


def pytest_configure(config: pytest.Config) -> None:
    config.addinivalue_line("markers", "async_timeout(seconds): timeout of an async test")


@pytest.hookimpl(tryfirst=True)
def pytest_pyfunc_call(pyfuncitem: pytest.Function) -> bool | None:
    if not inspect.iscoroutinefunction(pyfuncitem.obj):
        return None
    marker = pyfuncitem.get_closest_marker("async_timeout")
    timeout = float(marker.args[0]) if marker else DEFAULT_ASYNC_TIMEOUT
    kwargs = {name: pyfuncitem.funcargs[name] for name in pyfuncitem._fixtureinfo.argnames}

    async def runner() -> None:
        await asyncio.wait_for(pyfuncitem.obj(**kwargs), timeout)

    asyncio.run(runner())
    return True


def _runs(executable: str) -> bool:
    try:
        result = subprocess.run(
            [executable, "-hide_banner", "-version"],
            capture_output=True,
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return False
    return result.returncode == 0


def _ffprobe_for(ffmpeg: str) -> str:
    path = Path(ffmpeg)
    if path.parent != Path("."):
        candidate = path.with_name("ffprobe" + path.suffix)
        if candidate.exists():
            return str(candidate)
    return shutil.which("ffprobe") or "ffprobe"


def _fixture_video() -> Path:
    configured = os.environ.get("COMPANION_TEST_FIXTURE")
    if configured:
        return Path(configured)
    base = os.environ.get("LOCALAPPDATA") if sys.platform == "win32" else None
    if base:
        return Path(base) / "ClipForge" / "fixtures" / "e2e-fixture.mp4"
    return Path.home() / ".local" / "share" / "clipforge" / "fixtures" / "e2e-fixture.mp4"


@pytest.fixture(scope="session", autouse=True)
def _remove_temporary_media(
    tmp_path_factory: pytest.TempPathFactory, request: pytest.FixtureRequest
) -> Iterator[None]:
    """Deletes this run's temporary directory (recordings, segments) when every test passed.
    Retries because Windows may keep freshly written media files locked for a moment."""
    yield
    if request.session.testsfailed:
        return
    base = tmp_path_factory.getbasetemp()
    for _ in range(20):
        shutil.rmtree(base, ignore_errors=True)
        if not base.exists():
            return
        time.sleep(0.25)


@pytest.fixture(scope="session")
def ffmpeg() -> str:
    executable = default_ffmpeg_path()
    if not _runs(executable):
        pytest.skip(f"FFmpeg not available ({executable})")
    return executable


@pytest.fixture(scope="session")
def ffprobe(ffmpeg: str) -> str:
    executable = _ffprobe_for(ffmpeg)
    if not _runs(executable):
        pytest.skip(f"ffprobe not available ({executable})")
    return executable


@pytest.fixture(scope="session")
def fixture_video() -> Path:
    path = _fixture_video()
    if not path.is_file():
        pytest.skip(f"fixture video not found: {path} (set COMPANION_TEST_FIXTURE)")
    return path


@pytest.fixture(scope="session")
def media_dir(tmp_path_factory: pytest.TempPathFactory) -> Path:
    return tmp_path_factory.mktemp("media")


@pytest.fixture(scope="session")
def finished_mkv(ffmpeg: str, fixture_video: Path, media_dir: Path) -> Path:
    """20 s Matroska copy of the fixture (keyframes at 0, 8.33 and 16.67 s)."""
    path = media_dir / "finished.mkv"
    run_ffmpeg(
        ffmpeg, "-i", str(fixture_video), "-t", "20", "-c", "copy", "-f", "matroska", str(path)
    )
    return path


@pytest.fixture(scope="session")
def live_mkv(ffmpeg: str, fixture_video: Path, media_dir: Path) -> Path:
    """20 s Matroska written in live mode (no cues/seek head), like a file still being
    recorded; its byte prefixes at cluster boundaries are valid growing recordings."""
    path = media_dir / "live.mkv"
    run_ffmpeg(
        ffmpeg,
        "-i", str(fixture_video), "-t", "20", "-c", "copy", "-f", "matroska", "-live", "1",
        str(path),
    )  # fmt: skip
    return path
