"""``PipeProcess`` watchdog: a stalled child cannot block the job beyond its timeout.

The child is a Python one-liner standing in for ffmpeg (``PipeProcess`` takes the full
argument list), so these tests do not depend on the ffmpeg binary.
"""

from __future__ import annotations

import sys
import time

import pytest

from app.media.ffmpeg import FfmpegTimeout, PipeProcess


def child(code: str) -> list[str]:
    return [sys.executable, "-c", code]


def test_watchdog_kills_a_child_that_never_writes() -> None:
    pipe = PipeProcess(child("import time; time.sleep(30)"), timeout=1.0, cancel=None)
    started = time.monotonic()
    with pytest.raises(FfmpegTimeout), pipe:
        pipe.read_exact(10)  # blocking read: only the watchdog can end it
    assert time.monotonic() - started < 10
    assert pipe.timed_out


def test_watchdog_kills_a_child_that_stalls_mid_stream() -> None:
    code = "import sys, time; sys.stdout.buffer.write(b'abc'); sys.stdout.flush(); time.sleep(30)"
    pipe = PipeProcess(child(code), timeout=1.0, cancel=None)
    started = time.monotonic()
    with pytest.raises(FfmpegTimeout), pipe:
        for _chunk in pipe.iter_chunks(10):
            pass
    assert time.monotonic() - started < 10


def test_fast_child_is_not_affected_by_the_watchdog() -> None:
    pipe = PipeProcess(
        child("import sys; sys.stdout.buffer.write(b'x' * 100)"), timeout=30.0, cancel=None
    )
    with pipe:
        chunks = list(pipe.iter_chunks(40))
        pipe.finished_cleanly = True
    assert b"".join(chunks) == b"x" * 100
    assert [len(c) for c in chunks] == [40, 40, 20]
    assert not pipe.timed_out
