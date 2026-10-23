from __future__ import annotations

import io
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.logging_setup import ensure_utf8_stdio
from app.main import create_app


@pytest.fixture
def client(base_settings: Settings) -> TestClient:
    settings = base_settings.model_copy(
        update={"whisper_enabled": False, "webcam_detection": False}
    )
    with TestClient(create_app(settings)) as client:
        yield client


def test_health_reports_tools_and_worker(client: TestClient) -> None:
    body = client.get("/health").json()
    assert body["status"] == "ok"
    assert body["ffmpeg"]["available"] is True and body["ffmpeg"]["version"]
    assert body["ffprobe"]["available"] is True
    assert {"enabled", "model", "loaded"} <= set(body["whisper"])
    assert body["whisper"]["enabled"] is False
    assert body["worker"] == {
        "enabled": False,
        "running": False,
        "currentJobId": None,
        "workerId": "worker-local",
    }
    assert body["devEndpoints"] is True


def test_version(client: TestClient) -> None:
    body = client.get("/version").json()
    assert body["service"] == "clipforge-processing"
    assert body["version"]
    assert body["ffmpeg"]


def test_analyze_dev_endpoint(client: TestClient, short_video: Path) -> None:
    response = client.post(
        "/analyze",
        json={
            "path": str(short_video),
            "settings": {"minClipSeconds": 5, "maxClipSeconds": 12, "sensitivity": 50},
            "transcription": False,
            "webcam": False,
        },
    )
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["probe"]["hasVideo"] is True
    assert body["durationSeconds"] == pytest.approx(20.0, abs=0.2)
    assert body["candidates"], body
    first = body["candidates"][0]
    assert first["rank"] == 1 and first["startSeconds"] <= 8.5 <= first["endSeconds"]
    assert body["signals"]["transcript"]["available"] is False
    assert body["weights"]["audio"] > 0
    assert "total" in body["timings"]


def test_analyze_missing_file(client: TestClient) -> None:
    assert client.post("/analyze", json={"path": "C:/does/not/exist.mp4"}).status_code == 404


def test_analyze_is_absent_without_dev_endpoints(base_settings: Settings) -> None:
    settings = base_settings.model_copy(update={"dev_endpoints": False, "whisper_enabled": False})
    with TestClient(create_app(settings)) as client:
        assert client.post("/analyze", json={"path": "x"}).status_code in (404, 405)
        assert client.get("/health").json()["devEndpoints"] is False


def test_worker_thread_lifecycle_with_unreachable_api(base_settings: Settings) -> None:
    settings = base_settings.model_copy(
        update={
            "worker_enabled": True,
            "whisper_enabled": False,
            "clipforge_api_base_url": "http://127.0.0.1:1",
            "api_max_retries": 0,
            "worker_poll_interval_seconds": 0.05,
        }
    )
    app = create_app(settings)
    with TestClient(app) as client:
        body = client.get("/health").json()
        assert body["worker"]["enabled"] is True
        assert body["worker"]["running"] is True
        assert body["worker"]["currentJobId"] is None
    assert app.state.service.worker is not None
    assert app.state.service.worker.running is False


def test_stdio_is_switched_to_utf8_for_redirected_windows_output() -> None:
    """A redirected stream on Windows defaults to cp1252: "…" and accents of the pt-BR
    status messages reached the logs as mojibake. The streams are re-encoded as UTF-8."""
    raw = io.BytesIO()
    stream = io.TextIOWrapper(raw, encoding="cp1252", errors="strict")
    ensure_utf8_stdio(stream)
    stream.write("Preparando arquivo… ação ✓")
    stream.flush()
    assert raw.getvalue().decode("utf-8") == "Preparando arquivo… ação ✓"
    assert stream.encoding == "utf-8" and stream.errors == "replace"
    ensure_utf8_stdio(io.StringIO(), object())  # not reconfigurable: left alone, no error


def test_analyze_dev_endpoint_reports_a_short_source(client: TestClient, short_video: Path) -> None:
    body = client.post(
        "/analyze",
        json={
            "path": str(short_video),
            "settings": {"minClipSeconds": 30, "maxClipSeconds": 60, "focusCategories": ["hype"]},
            "transcription": False,
            "webcam": False,
        },
    ).json()
    assert [n["message"] for n in body["notices"]] == [
        "Vídeo mais curto que a duração mínima; usando o vídeo inteiro como limite"
    ]
    assert len(body["candidates"]) == 1 and body["candidates"][0]["startSeconds"] == 0.0
    assert body["candidates"][0]["signals"]["focus"]["categories"] == ["hype"]
    assert body["suggestedTitles"] == {}
    assert body["signals"]["llm"] == {"available": False, "provider": "none"}


def test_health_reports_the_llm_provider_without_secrets(base_settings: Settings) -> None:
    settings = base_settings.model_copy(
        update={
            "whisper_enabled": False,
            "llm_provider": "local",
            "llm_base_url": "http://127.0.0.1:8090",
            "llm_local_model": "Qwen3-1.7B-Q4_K_M",
        }
    )
    with TestClient(create_app(settings)) as client:
        llm = client.get("/health").json()["llm"]
    assert llm == {
        "provider": "local",
        "enabled": True,
        "model": "Qwen3-1.7B-Q4_K_M",
        "budgetSeconds": 100.0,
        "baseUrl": "http://127.0.0.1:8090",
    }
    gemini = base_settings.model_copy(
        update={"whisper_enabled": False, "llm_provider": "gemini", "gemini_api_key": "secret"}
    )
    with TestClient(create_app(gemini)) as client:
        body = client.get("/health").text
    assert "secret" not in body and '"provider":"gemini"' in body
