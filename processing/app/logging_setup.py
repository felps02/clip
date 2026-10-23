"""structlog configuration (console for development, JSON for containers)."""

from __future__ import annotations

import contextlib
import logging
import sys
from typing import Any

import structlog

_configured = False


def ensure_utf8_stdio(*streams: Any) -> None:
    """Switch the standard streams to UTF-8 (``errors="replace"``) when possible.

    On Windows a process whose output is redirected (a log file, the pipe of the dev
    scripts) writes with the ANSI code page (cp1252), so "…" and accented pt-BR messages
    reach UTF-8 readers as mojibake. ``reconfigure`` changes the existing stream objects in
    place, so the handlers that already hold them (uvicorn's, structlog's) follow. Streams
    that cannot be reconfigured (not a text wrapper, already closed...) are left alone.
    """
    for stream in streams or (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        with contextlib.suppress(Exception):
            reconfigure(encoding="utf-8", errors="replace")


def configure_logging(level: str = "INFO", fmt: str = "console") -> None:
    global _configured
    ensure_utf8_stdio()
    numeric = getattr(logging, level.upper(), logging.INFO)
    logging.basicConfig(level=numeric, stream=sys.stdout, format="%(message)s")
    for noisy in ("httpx", "httpcore", "faster_whisper", "urllib3"):
        logging.getLogger(noisy).setLevel(max(numeric, logging.WARNING))

    processors: list[structlog.types.Processor] = [
        structlog.contextvars.merge_contextvars,
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso", utc=True),
        structlog.processors.StackInfoRenderer(),
        structlog.processors.format_exc_info,
    ]
    if fmt == "json":
        processors.append(structlog.processors.JSONRenderer())
    else:
        processors.append(structlog.dev.ConsoleRenderer(colors=False))

    structlog.configure(
        processors=processors,
        wrapper_class=structlog.make_filtering_bound_logger(numeric),
        logger_factory=structlog.PrintLoggerFactory(sys.stdout),
        cache_logger_on_first_use=False,
    )
    _configured = True


def get_logger(name: str) -> structlog.stdlib.BoundLogger:
    if not _configured:
        configure_logging()
    return structlog.get_logger(name)  # type: ignore[no-any-return]
