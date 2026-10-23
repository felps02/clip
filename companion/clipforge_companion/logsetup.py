"""Logging configuration with secret redaction.

The device token (and the OBS password) must never reach a log line or the console, even when
an exception message or a URL happens to contain it. Every handler installed here passes its
output through :class:`Redactor`.
"""

from __future__ import annotations

import logging
import re
import sys
from collections.abc import Iterable
from typing import TextIO

# Anything shaped like a device token, even one we were not told about.
_TOKEN_LIKE = re.compile(r"cfd_[A-Za-z0-9]{6,}")
_BEARER = re.compile(r"(?i)(bearer\s+)[^\s\"',;]+")
REDACTED = "***"


class Redactor:
    """Replaces known secrets and token-shaped strings with ``***``."""

    def __init__(self, secrets: Iterable[str | None] = ()) -> None:
        self._secrets: list[str] = []
        for secret in secrets:
            self.add(secret)

    def add(self, secret: str | None) -> None:
        if secret and len(secret) >= 4 and secret not in self._secrets:
            self._secrets.append(secret)
            # Longest first so that a secret containing another one is fully replaced.
            self._secrets.sort(key=len, reverse=True)

    def redact(self, text: str) -> str:
        if not text:
            return text
        for secret in self._secrets:
            if secret in text:
                text = text.replace(secret, REDACTED)
        text = _TOKEN_LIKE.sub("cfd_" + REDACTED, text)
        return _BEARER.sub(lambda m: m.group(1) + REDACTED, text)


class RedactingFilter(logging.Filter):
    """Redacts the rendered message of every record that passes through a handler."""

    def __init__(self, redactor: Redactor) -> None:
        super().__init__()
        self.redactor = redactor

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            message = record.getMessage()
        except Exception:  # malformed %-args: keep the record, logging reports it
            return True
        redacted = self.redactor.redact(message)
        if redacted != message or record.args:
            record.msg = redacted
            record.args = None
        return True


class RedactingFormatter(logging.Formatter):
    """Also redacts tracebacks and anything added by the formatter."""

    def __init__(self, redactor: Redactor, fmt: str, datefmt: str | None = None) -> None:
        super().__init__(fmt, datefmt)
        self.redactor = redactor

    def format(self, record: logging.LogRecord) -> str:
        return self.redactor.redact(super().format(record))


_HANDLER_MARK = "_clipforge_companion_handler"


def setup_logging(
    *, verbose: bool = False, redactor: Redactor | None = None, stream: TextIO | None = None
) -> Redactor:
    """Installs one redacting stderr handler on the root logger (idempotent)."""
    redactor = redactor or Redactor()
    root = logging.getLogger()
    for handler in list(root.handlers):
        if getattr(handler, _HANDLER_MARK, False):
            root.removeHandler(handler)
    handler = logging.StreamHandler(stream or sys.stderr)
    setattr(handler, _HANDLER_MARK, True)
    handler.addFilter(RedactingFilter(redactor))
    handler.setFormatter(
        RedactingFormatter(redactor, "%(asctime)s %(levelname)s %(name)s: %(message)s", "%H:%M:%S")
    )
    root.addHandler(handler)
    root.setLevel(logging.DEBUG if verbose else logging.WARNING)
    # Third-party loggers stay quiet: httpx logs every request line, websockets logs frames.
    for noisy in ("httpx", "httpcore", "websockets", "asyncio"):
        logging.getLogger(noisy).setLevel(logging.WARNING)
    return redactor
