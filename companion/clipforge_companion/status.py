"""Concise pt-BR console status lines.

Two kinds of output go to stdout:

* event lines (``info`` / ``warn`` / ``error``), one per thing that happened;
* a state summary (``OBS conectado · gravando · enviando 3/5 · fila 2``) printed only when it
  changes.

Everything is passed through the :class:`~clipforge_companion.logsetup.Redactor` first, so a
secret can never be printed by accident.
"""

from __future__ import annotations

import sys
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime
from typing import TextIO

from clipforge_companion.logsetup import Redactor


def fmt_number(value: float, decimals: int = 1) -> str:
    """pt-BR decimal comma: ``12,5``."""
    return f"{value:.{decimals}f}".replace(".", ",")


def fmt_seconds(value: float) -> str:
    return f"{fmt_number(value, 1)} s"


def fmt_clock(seconds: float) -> str:
    """Session offset as ``m:ss`` or ``h:mm:ss``."""
    total = max(0, round(seconds))
    hours, rest = divmod(total, 3600)
    minutes, secs = divmod(rest, 60)
    return f"{hours}:{minutes:02d}:{secs:02d}" if hours else f"{minutes}:{secs:02d}"


def fmt_size(num_bytes: int) -> str:
    if num_bytes >= 1024**3:
        return f"{fmt_number(num_bytes / 1024**3, 2)} GB"
    if num_bytes >= 1024**2:
        return f"{fmt_number(num_bytes / 1024**2, 1)} MB"
    if num_bytes >= 1024:
        return f"{fmt_number(num_bytes / 1024, 0)} KB"
    return f"{num_bytes} B"


def short_id(value: str) -> str:
    return value[:8] if value else "?"


@dataclass(frozen=True, slots=True)
class StatusSnapshot:
    """What the state summary line shows."""

    mode: str  # "obs" or "file"
    obs_connected: bool = False
    streaming: bool = False
    recording: bool = False
    capturing: bool = False
    uploading: bool = False
    upload_index: int = 0  # 1-based position of the segment being sent in the session
    upload_total: int = 0  # segments produced in the session so far
    queue_length: int = 0
    online: bool = True


def format_state(state: StatusSnapshot) -> str:
    parts: list[str] = []
    if state.mode == "obs":
        parts.append("OBS conectado" if state.obs_connected else "OBS desconectado")
        if state.streaming:
            parts.append("transmitindo")
        if state.recording:
            parts.append("gravando")
    elif state.capturing:
        parts.append("capturando arquivo")
    if state.uploading and state.upload_total:
        parts.append(f"enviando {state.upload_index}/{state.upload_total}")
    if state.queue_length:
        parts.append(f"fila {state.queue_length}")
    if not state.online:
        parts.append("offline")
    return " · ".join(parts) if parts else "aguardando"


class StatusReporter:
    """Prints timestamped, redacted status lines."""

    def __init__(
        self,
        stream: TextIO | None = None,
        *,
        redactor: Redactor | None = None,
        now: Callable[[], datetime] = datetime.now,
    ) -> None:
        self._stream = stream
        self.redactor = redactor or Redactor()
        self._now = now
        self._last_state: str | None = None

    @property
    def stream(self) -> TextIO:
        # Resolved lazily so that a replaced sys.stdout (tests, redirection) is honoured.
        return self._stream if self._stream is not None else sys.stdout

    def _emit(self, prefix: str, message: str) -> None:
        line = self.redactor.redact(f"[{self._now():%H:%M:%S}] {prefix}{message}")
        stream = self.stream
        try:
            stream.write(line + "\n")
        except UnicodeEncodeError:
            encoding = getattr(stream, "encoding", None) or "ascii"
            stream.write(line.encode(encoding, "replace").decode(encoding) + "\n")
        stream.flush()

    def info(self, message: str) -> None:
        self._emit("", message)

    def warn(self, message: str) -> None:
        self._emit("aviso: ", message)

    def error(self, message: str) -> None:
        self._emit("erro: ", message)

    def state(self, snapshot: StatusSnapshot) -> None:
        text = format_state(snapshot)
        if text != self._last_state:
            self._last_state = text
            self._emit("estado: ", text)
