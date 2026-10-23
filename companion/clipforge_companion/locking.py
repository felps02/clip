"""Exclusive, non-blocking lock files.

Used so that two companions never work on the same session (or run for the same device with
the same work directory): the second one would otherwise take the first one's live session for
a leftover of a crashed run, upload its queue concurrently and end it.

The lock is held through an open file descriptor (``msvcrt.locking`` on Windows, ``flock``
elsewhere), so it disappears with the process: a crash never leaves a stale lock behind. Two
:class:`FileLock` objects on the same path conflict even inside one process.
"""

from __future__ import annotations

import contextlib
import os
import sys
from pathlib import Path


class FileLock:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._fd: int | None = None

    @property
    def held(self) -> bool:
        return self._fd is not None

    def acquire(self) -> bool:
        """Takes the lock without waiting. False when someone else holds it; raises
        :class:`OSError` when the lock file cannot be created at all."""
        if self._fd is not None:
            return True
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(self.path, os.O_RDWR | os.O_CREAT, 0o644)
        try:
            _lock(fd)
        except OSError:
            os.close(fd)
            return False
        self._fd = fd
        return True

    def release(self) -> None:
        fd, self._fd = self._fd, None
        if fd is None:
            return
        with contextlib.suppress(OSError):
            _unlock(fd)
        with contextlib.suppress(OSError):
            os.close(fd)


if sys.platform == "win32":
    import msvcrt

    def _lock(fd: int) -> None:
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)

    def _unlock(fd: int) -> None:
        os.lseek(fd, 0, os.SEEK_SET)
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)

else:
    import fcntl

    def _lock(fd: int) -> None:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def _unlock(fd: int) -> None:
        fcntl.flock(fd, fcntl.LOCK_UN)
