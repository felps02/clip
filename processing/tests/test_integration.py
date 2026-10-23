"""Integration tests need the real API running (``pytest -m integration``)."""

from __future__ import annotations

import httpx
import pytest

from app.api_client import ApiError, InternalApiClient
from app.config import get_settings

pytestmark = pytest.mark.integration


def test_claim_against_real_api() -> None:
    settings = get_settings()
    try:
        httpx.get(settings.clipforge_api_base_url + "/health", timeout=3.0)
    except httpx.HTTPError:
        pytest.skip(f"API not reachable at {settings.clipforge_api_base_url}")
    with InternalApiClient(
        settings.clipforge_api_base_url,
        settings.clipforge_internal_api_key,
        "integration-test",
        max_retries=0,
    ) as client:
        try:
            payload = client.claim(
                ["render_clip"]
            )  # render_clip only: cheap if something is queued
        except ApiError as exc:
            pytest.fail(f"claim failed: {exc}")
        assert payload is None or payload.kind == "render_clip"
