"""Service configuration (pydantic-settings).

Every variable of the *Processing* section of ``.env.example`` is mapped here. Values can
come from the process environment or from ``.env`` files at the repository root and in
``processing/``. ``%LOCALAPPDATA%``-style references are expanded with
``os.path.expandvars`` so the Windows defaults of ``.env.example`` work unchanged.
"""

from __future__ import annotations

import os
import sys
from functools import lru_cache
from pathlib import Path
from typing import Any, Literal

from pydantic import Field, field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

PROCESSING_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = PROCESSING_DIR.parent
ASSETS_DIR = PROCESSING_DIR / "assets"
FONTS_DIR = ASSETS_DIR / "fonts"
IS_WINDOWS = sys.platform.startswith("win")


def _local_app_data() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "ClipForge"
    return Path.home() / "AppData" / "Local" / "ClipForge"


def _default_tool(name: str) -> str:
    if IS_WINDOWS:
        return str(_local_app_data() / "tools" / "ffmpeg" / "bin" / f"{name}.exe")
    return name


def _default_dir(name: str) -> str:
    if IS_WINDOWS:
        return str(_local_app_data() / name)
    return f"/var/lib/clipforge/{name}"


def expand_path(value: str | os.PathLike[str]) -> str:
    """Expand ``%VAR%`` / ``$VAR`` references and ``~``."""
    text = os.fspath(value)
    return os.path.expanduser(os.path.expandvars(text))


class Settings(BaseSettings):
    """Processing service settings (see ``.env.example``, section *Processing*)."""

    model_config = SettingsConfigDict(
        env_file=(str(REPO_ROOT / ".env"), str(PROCESSING_DIR / ".env")),
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
    )

    # --- worker ---------------------------------------------------------------------
    worker_enabled: bool = True
    worker_id: str = "worker-local"
    worker_poll_interval_seconds: float = Field(default=2.0, ge=0.1)
    max_parallel_jobs: int = Field(default=1, ge=1)
    job_kinds: str = "analyze_source,render_clip,live_segment"

    # --- tools / directories ------------------------------------------------------------
    ffmpeg_path: str = Field(default_factory=lambda: _default_tool("ffmpeg"))
    ffprobe_path: str = Field(default_factory=lambda: _default_tool("ffprobe"))
    ffmpeg_threads: int = Field(default=0, ge=0)
    work_dir: str = Field(default_factory=lambda: _default_dir("work"))
    # job directories in WORK_DIR untouched for this long are left over by a crashed worker
    # and removed when the worker starts (0 disables the sweep)
    work_dir_stale_hours: float = Field(default=6.0, ge=0)
    # after each job, directories this worker created (marker with its WORKER_ID) that are
    # not the current job's and unchanged for this long are removed too (0 disables)
    work_dir_orphan_minutes: float = Field(default=15.0, ge=0)
    # checked before each clip an analysis renders: below this much free space on the
    # WORK_DIR volume the remaining clips are skipped with a warning event (0 disables)
    render_min_free_mb: int = Field(default=500, ge=0)
    models_dir: str = Field(default_factory=lambda: _default_dir("models"))

    # --- transcription ----------------------------------------------------------------
    whisper_enabled: bool = True
    # the largest model a job may use (the plan picks one through limits.transcriptionModel)
    whisper_model: str = "small"
    whisper_device: str = "cpu"
    whisper_compute_type: str = "int8"
    whisper_local_files_only: bool = False
    whisper_cpu_threads: int = Field(default=0, ge=0)
    transcription_chunk_seconds: float = Field(default=300.0, ge=30.0)
    # languages never taken from the automatic detection (comma-separated codes)
    whisper_detect_ignore: str = "la,sa"

    # --- optional LLM --------------------------------------------------------------------
    # none (default) | local (OpenAI-compatible llama-server at LLM_BASE_URL, the product's
    # own model) | gemini (Google AI Studio key) | anthropic (paid API, opt-in only)
    llm_provider: Literal["none", "anthropic", "local", "gemini"] = "none"
    llm_base_url: str = "http://127.0.0.1:8090"
    # label reported for the local model; empty = the model id the server reports
    llm_local_model: str = ""
    # one request; the whole re-rank + titles stage is bounded by LLM_BUDGET_SECONDS
    llm_timeout_seconds: float = Field(default=60.0, ge=1.0)
    llm_budget_seconds: float = Field(default=100.0, ge=0.0)
    gemini_api_key: str = ""
    gemini_model: str = "gemini-2.5-flash"
    anthropic_api_key: str = ""
    llm_model: str = "claude-haiku-5-5"
    llm_max_windows: int = Field(default=60, ge=1)

    # --- service / API ----------------------------------------------------------------------
    service_host: str = "127.0.0.1"
    service_port: int = 8008
    clipforge_api_base_url: str = "http://localhost:5080"
    clipforge_internal_api_key: str = "change-me-internal-key"
    api_timeout_seconds: float = Field(default=30.0, ge=1.0)
    api_max_retries: int = Field(default=4, ge=0)
    dev_endpoints: bool = False
    log_level: str = "INFO"
    log_format: Literal["console", "json"] = "console"

    # --- analysis tuning ------------------------------------------------------------------
    visual_fps: float = Field(default=2.0, gt=0)
    visual_width: int = Field(default=320, ge=64)
    webcam_detection: bool = True
    webcam_samples: int = Field(default=40, ge=4)

    # --- live capture (live_segment jobs) ------------------------------------------------
    # the optional LLM assist stage of a live segment runs only within this budget (also
    # capped by LLM_BUDGET_SECONDS; 0 skips it): live detection latency comes first
    live_llm_budget_seconds: float = Field(default=20.0, ge=0.0)
    # webcam detection samples per live window (a window is ~1-2 min, and each sample is an
    # ffmpeg seek of its own: fewer than WEBCAM_SAMPLES keeps the latency low)
    live_webcam_samples: int = Field(default=8, ge=4)

    @field_validator("ffmpeg_path", "ffprobe_path", "work_dir", "models_dir", mode="before")
    @classmethod
    def _expand(cls, value: Any) -> Any:
        if isinstance(value, str | os.PathLike):
            return expand_path(value)
        return value

    @field_validator("whisper_model")
    @classmethod
    def _whisper_model(cls, value: str) -> str:
        value = value.strip().lower()
        return value or "small"

    @field_validator("llm_provider", mode="before")
    @classmethod
    def _llm_provider(cls, value: Any) -> Any:
        if isinstance(value, str):
            return value.strip().lower() or "none"
        return value

    @model_validator(mode="after")
    def _normalise(self) -> Settings:
        self.clipforge_api_base_url = self.clipforge_api_base_url.rstrip("/")
        self.llm_base_url = self.llm_base_url.strip().rstrip("/")
        self.log_level = self.log_level.upper()
        return self

    # --- helpers ------------------------------------------------------------------------
    @property
    def work_path(self) -> Path:
        return Path(self.work_dir)

    @property
    def models_path(self) -> Path:
        return Path(self.models_dir)

    @property
    def fonts_dir(self) -> Path:
        return FONTS_DIR

    @property
    def kinds(self) -> list[str]:
        return [k.strip() for k in self.job_kinds.split(",") if k.strip()]

    @property
    def llm_enabled(self) -> bool:
        """A provider is configured with what it needs (never contacted here)."""
        if self.llm_provider == "local":
            return bool(self.llm_base_url)
        if self.llm_provider == "gemini":
            return bool(self.gemini_api_key.strip())
        if self.llm_provider == "anthropic":
            return bool(self.anthropic_api_key.strip())
        return False

    @property
    def llm_model_label(self) -> str | None:
        """Model reported for the configured provider (``None`` when disabled)."""
        if not self.llm_enabled:
            return None
        if self.llm_provider == "local":
            return self.llm_local_model.strip() or None
        if self.llm_provider == "gemini":
            return self.gemini_model
        return self.llm_model

    @property
    def whisper_ignored_languages(self) -> tuple[str, ...]:
        return tuple(
            code.strip().lower() for code in self.whisper_detect_ignore.split(",") if code.strip()
        )

    @property
    def transcription_enabled(self) -> bool:
        return self.whisper_enabled and self.whisper_model != "false"


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """Process-wide settings singleton (environment + ``.env`` files)."""
    return Settings()


def settings_for_tests(**overrides: Any) -> Settings:
    """Settings that ignore ``.env`` files (explicit overrides only)."""
    return Settings(_env_file=None, **overrides)  # type: ignore[call-arg]
