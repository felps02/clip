"""Integration with a real API (skipped unless CLIPFORGE_API_URL and CLIPFORGE_DEVICE_TOKEN
are set). It creates a real live session with a 45 s copy of the fixture video and ends it.

    CLIPFORGE_API_URL=http://localhost:5080 CLIPFORGE_DEVICE_TOKEN=cfd_... \
        .venv/Scripts/python.exe -m pytest -m integration
"""

from __future__ import annotations

import io
import os
from pathlib import Path

import pytest

from clipforge_companion.app import EXIT_OK, CompanionApp
from clipforge_companion.cli import run_check
from clipforge_companion.config import Config, normalize_api_url
from clipforge_companion.logsetup import Redactor
from clipforge_companion.status import StatusReporter
from tests.media import run_ffmpeg

pytestmark = pytest.mark.integration


@pytest.fixture
def real_api() -> tuple[str, str]:
    url = os.environ.get("CLIPFORGE_API_URL", "").strip()
    token = os.environ.get("CLIPFORGE_DEVICE_TOKEN", "").strip()
    if not url or not token:
        pytest.skip("set CLIPFORGE_API_URL and CLIPFORGE_DEVICE_TOKEN to run against a real API")
    return normalize_api_url(url), token


async def test_check_against_real_api(real_api: tuple[str, str]) -> None:
    url, token = real_api
    console = io.StringIO()
    code = await run_check(
        Config(api_url=url, token=token), StatusReporter(console, redactor=Redactor([token]))
    )
    assert code == EXIT_OK, console.getvalue()
    assert "Token válido" in console.getvalue()
    assert token not in console.getvalue()


@pytest.mark.async_timeout(600)
async def test_file_mode_against_real_api(
    real_api: tuple[str, str], ffmpeg: str, fixture_video: Path, tmp_path: Path
) -> None:
    url, token = real_api
    recording = tmp_path / "integration.mkv"
    run_ffmpeg(
        ffmpeg, "-i", str(fixture_video), "-t", "45", "-c", "copy", "-f", "matroska", str(recording)
    )
    console = io.StringIO()
    config = Config(
        api_url=url,
        token=token,
        file=recording,
        segment_seconds=20,
        title="Teste de integração do companion",
        work_dir=tmp_path / "work",
        ffmpeg=ffmpeg,
        rw_timeout=2.0,
    )
    app = CompanionApp(
        config,
        status=StatusReporter(console, redactor=Redactor([token])),
        install_signal_handlers=False,
    )
    code = await app.run()
    output = console.getvalue()
    assert code == EXIT_OK, output
    [ended] = app.ended_sessions
    assert ended["uploaded"] >= 2 and ended["skipped"] == 0, output
    assert ended["lastSequence"] == ended["uploaded"] - 1
    assert token not in output
