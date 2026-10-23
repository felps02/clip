"""Runtime configuration of the companion (built by the CLI from flags and environment)."""

from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from urllib.parse import urlsplit

ENV_API_URL = "CLIPFORGE_API_URL"
ENV_TOKEN = "CLIPFORGE_DEVICE_TOKEN"
ENV_OBS_URL = "OBS_WEBSOCKET_URL"
ENV_OBS_PASSWORD = "OBS_WEBSOCKET_PASSWORD"
ENV_FFMPEG = "FFMPEG_PATH"

DEFAULT_OBS_URL = "ws://127.0.0.1:4455"
DEFAULT_SEGMENT_SECONDS = 60
MIN_SEGMENT_SECONDS = 20
MAX_SEGMENT_SECONDS = 120
# FFmpeg gives up on a recording that did not grow for this long. It must stay well above the
# writer's flush interval (Matroska clusters of up to ~5 s), at least 8 s: with a shorter timeout
# FFmpeg hits read errors mid-stream, resynchronises and silently drops the tail of clusters.
# (Tests use 1 s with finished files or files written cluster by cluster.)
DEFAULT_RW_TIMEOUT_SECONDS = 15.0
MIN_RW_TIMEOUT_SECONDS = 1.0
DEFAULT_HEARTBEAT_SECONDS = 10.0

TOKEN_PATTERN = re.compile(r"^cfd_[A-Za-z0-9]{40}$")


class ConfigError(ValueError):
    """Invalid configuration; the message is user-facing (pt-BR)."""


def env(name: str) -> str | None:
    """Returns a non-empty environment variable (stripped) or None."""
    value = os.environ.get(name)
    if value is None:
        return None
    value = value.strip()
    return value or None


def default_ffmpeg_path() -> str:
    """FFMPEG_PATH, else the development build installed by scripts/dev-setup.ps1 on Windows,
    else ``ffmpeg`` from PATH."""
    configured = env(ENV_FFMPEG)
    if configured:
        return configured
    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            candidate = Path(local) / "ClipForge" / "tools" / "ffmpeg" / "bin" / "ffmpeg.exe"
            if candidate.is_file():
                return str(candidate)
    return "ffmpeg"


def default_work_dir() -> Path:
    """Where segments and upload queues are kept while a session runs."""
    if sys.platform == "win32":
        local = os.environ.get("LOCALAPPDATA")
        if local:
            return Path(local) / "ClipForge" / "companion"
    xdg = os.environ.get("XDG_STATE_HOME")
    base = Path(xdg) if xdg else Path.home() / ".local" / "state"
    return base / "clipforge-companion"


def normalize_api_url(value: str) -> str:
    """Validates the API base URL and removes trailing slashes (and a trailing ``/api``)."""
    url = (value or "").strip()
    parts = urlsplit(url)
    if parts.scheme not in ("http", "https") or not parts.netloc:
        raise ConfigError(
            f"URL da API inválida: {url!r}. Use o endereço completo, por exemplo "
            "https://api.exemplo.com"
        )
    if parts.query or parts.fragment:
        raise ConfigError("A URL da API não deve ter parâmetros (?...) nem fragmentos (#...).")
    url = url.rstrip("/")
    if url.lower().endswith("/api"):
        url = url[: -len("/api")]
    return url


def is_insecure_remote(url: str) -> bool:
    """True for plain-HTTP URLs that do not point at this machine (token sent in clear)."""
    parts = urlsplit(url)
    host = (parts.hostname or "").lower()
    return parts.scheme == "http" and host not in ("localhost", "127.0.0.1", "::1")


def normalize_token(value: str) -> str:
    token = (value or "").strip()
    if not token:
        raise ConfigError("Token do dispositivo vazio.")
    if any(ch.isspace() or ord(ch) < 32 for ch in token):
        raise ConfigError("O token do dispositivo contém espaços ou caracteres inválidos.")
    return token


def looks_like_device_token(token: str) -> bool:
    return bool(TOKEN_PATTERN.match(token))


def normalize_obs_url(value: str) -> str:
    url = (value or "").strip()
    parts = urlsplit(url)
    if parts.scheme not in ("ws", "wss") or not parts.netloc:
        raise ConfigError(
            f"Endereço do obs-websocket inválido: {url!r}. Exemplo: ws://127.0.0.1:4455"
        )
    return url


@dataclass(frozen=True, slots=True)
class Config:
    """Everything the companion needs to run. Secrets are excluded from ``repr``."""

    api_url: str
    token: str = field(repr=False)
    obs_url: str = DEFAULT_OBS_URL
    obs_password: str | None = field(default=None, repr=False)
    auto_record: bool = False
    file: Path | None = None
    segment_seconds: float = DEFAULT_SEGMENT_SECONDS
    title: str | None = None
    work_dir: Path = field(default_factory=default_work_dir)
    ffmpeg: str = field(default_factory=default_ffmpeg_path)
    max_upload_mbps: float | None = None
    keep_segments: bool = False
    rw_timeout: float = DEFAULT_RW_TIMEOUT_SECONDS
    heartbeat_interval: float = DEFAULT_HEARTBEAT_SECONDS
    verbose: bool = False

    @property
    def mode(self) -> str:
        return "file" if self.file is not None else "obs"

    def secrets(self) -> list[str]:
        """Values that must never appear in logs or console output."""
        return [s for s in (self.token, self.obs_password) if s]
