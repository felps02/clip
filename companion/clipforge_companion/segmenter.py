"""Follows a growing recording with FFmpeg and turns it into upload-ready segments.

FFmpeg reads the recording while OBS writes it (``-follow 1`` on the ``file:`` protocol) and the
segment muxer cuts it on keyframes without re-encoding. Every completed segment appends one line
to a CSV list (``seg_00003.mkv,180.000000,240.033000``); :class:`Segmenter` tails that list and
yields :class:`Segment` objects.

Things learned by measuring FFmpeg 9 on Windows (see the README for details):

* ``-rw_timeout`` must be comfortably longer than the writer's flush interval (Matroska clusters
  of up to ~5 s). A shorter timeout makes the Matroska demuxer hit read errors mid-stream and
  resynchronise, silently dropping the tail of clusters.
* After the recording stops growing FFmpeg needs roughly ``3 x rw_timeout`` to give up and close
  the last segment; sending ``q`` does not shorten that wait (the read is blocked in the file
  protocol), it only helps while data is still flowing.
* Sending ``q`` on stdin (no ``-nostdin``) stops FFmpeg gracefully: the current segment is
  closed and listed. A hard kill loses it.
"""

from __future__ import annotations

import asyncio
import contextlib
import csv
import logging
import math
import re
import struct
import subprocess
import sys
import time
from collections import deque
from collections.abc import AsyncIterator, Callable
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import IO

log = logging.getLogger(__name__)

SEGMENT_LIST_NAME = "segments.csv"
SEGMENT_FILE_PATTERN = "seg_%05d.mkv"

_FILE_NUMBER = re.compile(r"(\d+)\.[A-Za-z0-9]+$")


# -- segment list (CSV) ----------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class SegmentListEntry:
    filename: str
    start: float
    end: float


def file_number(filename: str) -> int:
    match = _FILE_NUMBER.search(filename)
    if match is None:
        raise ValueError(f"segment file name without a number: {filename!r}")
    return int(match.group(1))


def parse_segment_list_line(line: str) -> SegmentListEntry | None:
    """Parses ``seg_00003.mkv,180.000000,240.033000`` (CSV, the name may be quoted)."""
    line = line.strip()
    if not line:
        return None
    try:
        row = next(csv.reader([line]))
    except (csv.Error, StopIteration):
        return None
    if len(row) < 3:
        return None
    name = row[0].strip()
    try:
        start = float(row[1])
        end = float(row[2])
    except ValueError:
        return None
    if not (math.isfinite(start) and math.isfinite(end)) or start < 0 or end < start:
        return None
    if not _FILE_NUMBER.search(name):
        return None
    return SegmentListEntry(name, start, end)


class SegmentListReader:
    """Reads the CSV list incrementally. A line is only parsed once its ``\\n`` was written, so
    a line caught half-written by FFmpeg is kept for the next read."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._offset = 0
        self._partial = b""
        self.invalid_lines: list[str] = []

    def read_new(self) -> list[SegmentListEntry]:
        try:
            with self.path.open("rb") as handle:
                handle.seek(0, 2)
                size = handle.tell()
                if size < self._offset:  # truncated / recreated: start over
                    self._offset = 0
                    self._partial = b""
                handle.seek(self._offset)
                data = handle.read()
        except FileNotFoundError:
            return []
        if not data:
            return []
        self._offset += len(data)
        *lines, self._partial = (self._partial + data).split(b"\n")
        return self._parse(lines)

    def flush_partial(self) -> list[SegmentListEntry]:
        """At the end of the run: parses a trailing line that has no newline (if valid)."""
        rest, self._partial = self._partial, b""
        return self._parse([rest]) if rest.strip() else []

    def _parse(self, lines: list[bytes]) -> list[SegmentListEntry]:
        entries: list[SegmentListEntry] = []
        for raw in lines:
            text = raw.decode("utf-8", "replace").strip()
            if not text:
                continue
            entry = parse_segment_list_line(text)
            if entry is None:
                self.invalid_lines.append(text)
                log.warning("linha inválida na lista de segmentos ignorada: %r", text)
            else:
                entries.append(entry)
        return entries


@dataclass(frozen=True, slots=True)
class Segment:
    """A completed segment. ``start``/``end`` are offsets in seconds from the session start;
    ``last`` marks the final segment produced by one FFmpeg run."""

    sequence: int
    path: Path
    start: float
    end: float
    last: bool = False

    @property
    def duration(self) -> float:
        return max(0.0, self.end - self.start)


# -- recording format ------------------------------------------------------------------------


class RecordingFormat(Enum):
    PENDING = "pending"  # missing, empty or header not written yet
    MATROSKA = "matroska"
    MPEGTS = "mpegts"
    FLV = "flv"
    MP4_FRAGMENTED = "mp4-fragmented"  # fragmented / hybrid MP4: readable while written
    MP4_COMPLETE = "mp4-complete"  # finished MP4/MOV (moov present)
    MP4_INCOMPLETE = "mp4-incomplete"  # plain MP4/MOV still being written: unreadable
    UNKNOWN = "unknown"


PLAIN_MP4_MESSAGE = (
    "A gravação {name} está em MP4 comum, que só pode ser lido depois que a gravação termina. "
    "No OBS, abra Configurações > Saída > Gravação, escolha o formato MKV (recomendado) ou MP4 "
    "híbrido/fragmentado e inicie uma nova gravação. Um MKV pode ser convertido para MP4 depois "
    "em Arquivo > Remux de gravações."
)


class RecordingFormatError(Exception):
    """The recording cannot be read while it is being written (message in pt-BR)."""


_MP4_TOP_LEVEL = {
    b"ftyp", b"styp", b"moov", b"moof", b"mdat", b"free", b"skip", b"wide", b"uuid", b"sidx",
    b"mfra", b"pdin", b"meta", b"pnot", b"emsg", b"prft",
}  # fmt: skip


def _is_box_type(value: bytes) -> bool:
    return len(value) == 4 and all(0x20 <= b <= 0x7E for b in value)


def _read_box_header(handle: IO[bytes], pos: int, size: int) -> tuple[int, bytes, int] | None:
    """Returns (box_size, box_type, header_len) or None when the header is incomplete/invalid.
    A size of 0 ("until end of file") is reported as the remaining length."""
    if pos + 8 > size:
        return None
    handle.seek(pos)
    header = handle.read(16)
    if len(header) < 8:
        return None
    box_size, box_type = struct.unpack(">I4s", header[:8])
    header_len = 8
    if box_size == 1:
        if len(header) < 16:
            return None
        box_size = struct.unpack(">Q", header[8:16])[0]
        header_len = 16
    elif box_size == 0:
        box_size = size - pos
    if not _is_box_type(box_type) or box_size < header_len:
        return None
    return box_size, box_type, header_len


def _has_child(handle: IO[bytes], start: int, end: int, size: int, child: bytes) -> bool:
    pos = start
    limit = min(end, size)
    while pos + 8 <= limit:
        header = _read_box_header(handle, pos, limit)
        if header is None:
            return False
        box_size, box_type, _ = header
        if box_type == child:
            return True
        pos += box_size
    return False


def _scan_iso_bmff(handle: IO[bytes], size: int) -> RecordingFormat:
    pos = 0
    while pos < size:
        header = _read_box_header(handle, pos, size)
        if header is None:
            break
        box_size, box_type, header_len = header
        if box_type in (b"moof", b"styp", b"sidx"):
            return RecordingFormat.MP4_FRAGMENTED
        if box_type == b"moov":
            if pos + box_size > size:
                return RecordingFormat.PENDING  # header still being written
            if _has_child(handle, pos + header_len, pos + box_size, size, b"mvex"):
                return RecordingFormat.MP4_FRAGMENTED
            return RecordingFormat.MP4_COMPLETE
        if box_type == b"mdat":
            # Some muxers nest the fragments of a hybrid file inside one big mdat.
            inner = _read_box_header(handle, pos + header_len, size)
            if inner is not None and inner[1] == b"moof":
                return RecordingFormat.MP4_FRAGMENTED
            if pos + box_size >= size:
                return RecordingFormat.MP4_INCOMPLETE  # open-ended mdat, no moov yet
        pos += box_size
    return RecordingFormat.PENDING


def detect_recording_format(path: Path) -> RecordingFormat:
    """Classifies a (possibly growing) recording by its first bytes / top-level boxes."""
    try:
        size = path.stat().st_size
        if size == 0:
            return RecordingFormat.PENDING
        with path.open("rb") as handle:
            head = handle.read(189)
            if head.startswith(b"\x1a\x45\xdf\xa3"):
                return RecordingFormat.MATROSKA
            if head.startswith(b"FLV"):
                return RecordingFormat.FLV
            if head[:1] == b"\x47":
                if len(head) < 189:
                    return RecordingFormat.PENDING
                if head[188:189] == b"\x47":
                    return RecordingFormat.MPEGTS
            if len(head) < 8:
                return RecordingFormat.PENDING
            if head[4:8] in _MP4_TOP_LEVEL:
                return _scan_iso_bmff(handle, size)
            return RecordingFormat.UNKNOWN
    except FileNotFoundError:
        return RecordingFormat.PENDING
    except OSError:
        return RecordingFormat.UNKNOWN


def check_recording_format(path: Path) -> RecordingFormat:
    """Like :func:`detect_recording_format` but raises for a plain MP4 still being written."""
    fmt = detect_recording_format(path)
    if fmt is RecordingFormat.MP4_INCOMPLETE:
        raise RecordingFormatError(PLAIN_MP4_MESSAGE.format(name=path.name))
    return fmt


async def wait_for_recording(
    path: Path,
    *,
    timeout: float = 30.0,
    poll_interval: float = 0.5,
    should_stop: Callable[[], bool] | None = None,
) -> RecordingFormat:
    """Waits until the recording exists and its container can be identified.

    Returns :attr:`RecordingFormat.PENDING` on timeout; raises :class:`RecordingFormatError`
    for a plain MP4 that is still being written.
    """
    deadline = time.monotonic() + timeout
    while True:
        fmt = check_recording_format(path)
        if fmt is not RecordingFormat.PENDING:
            return fmt
        if time.monotonic() >= deadline or (should_stop is not None and should_stop()):
            return RecordingFormat.PENDING
        await asyncio.sleep(poll_interval)


# -- FFmpeg process --------------------------------------------------------------------------


class SegmenterError(Exception):
    """FFmpeg could not be started or failed before producing anything (pt-BR message)."""


def build_command(
    ffmpeg: str, recording: Path, out_dir: Path, segment_seconds: float, rw_timeout: float
) -> list[str]:
    """The follow + segment command (stream copy, cuts on keyframes).

    ``-nostdin`` is deliberately absent: stdin is a private pipe used to send ``q`` for a
    graceful stop. ``%`` in the output directory is doubled because the segment muxer expands
    the whole output path as a pattern.
    """
    pattern = str(out_dir / SEGMENT_FILE_PATTERN)
    pattern = pattern[: -len(SEGMENT_FILE_PATTERN)].replace("%", "%%") + SEGMENT_FILE_PATTERN
    return [
        ffmpeg,
        "-hide_banner",
        "-loglevel", "warning",
        "-follow", "1",
        "-rw_timeout", str(round(rw_timeout * 1_000_000)),
        "-i", "file:" + str(recording),
        "-map", "0:v:0",
        "-map", "0:a:0?",
        "-c", "copy",
        "-f", "segment",
        "-segment_time", f"{segment_seconds:g}",
        "-reset_timestamps", "1",
        "-segment_list", str(out_dir / SEGMENT_LIST_NAME),
        "-segment_list_type", "csv",
        "-segment_list_flags", "+live",
        pattern,
    ]  # fmt: skip


class Segmenter:
    """Runs one FFmpeg follow+segment process over ``recording`` and yields its segments.

    * ``sequence_offset`` / ``time_offset`` map FFmpeg's numbering and times to the session
      (used when OBS switches to a new file during the same recording);
    * segments whose session sequence is ``<= skip_through`` were already handled by an earlier
      run over the same file: their files are deleted and they are not yielded. Segmentation of
      the same file is deterministic, so the sequences line up. :attr:`listed_through` tells
      how far FFmpeg got (a graceful stop while it is still inside the skipped range would
      close a segment that is thrown away).

    A segment is yielded once FFmpeg is known to keep running after writing it (``settle``
    seconds) or once FFmpeg has exited; the very last one carries ``last=True``.
    """

    def __init__(
        self,
        *,
        ffmpeg: str,
        recording: Path,
        out_dir: Path,
        segment_seconds: float,
        rw_timeout: float = 15.0,
        sequence_offset: int = 0,
        time_offset: float = 0.0,
        skip_through: int = -1,
        poll_interval: float = 0.5,
        settle_seconds: float = 2.0,
        stop_timeout: float | None = None,
    ) -> None:
        self.ffmpeg = ffmpeg
        self.recording = recording
        self.out_dir = out_dir
        self.segment_seconds = segment_seconds
        self.rw_timeout = rw_timeout
        self.sequence_offset = sequence_offset
        self.time_offset = time_offset
        self.skip_through = skip_through
        self.poll_interval = poll_interval
        self.settle_seconds = settle_seconds
        # FFmpeg needs about 3 x rw_timeout to notice that the input stopped growing.
        self.stop_timeout = stop_timeout if stop_timeout is not None else 3 * rw_timeout + 15
        self.list_path = out_dir / SEGMENT_LIST_NAME
        self.error: str | None = None
        self.stderr_tail: deque[str] = deque(maxlen=40)
        self.produced = 0
        self.skipped = 0
        # End (session time) of the furthest segment this run produced, skipped ones excluded:
        # tells a run that reached new data from one that only re-created what was known.
        self.produced_end: float | None = None
        # Highest session sequence FFmpeg has completed (listed) in this run.
        self.listed_through = sequence_offset - 1
        self._proc: asyncio.subprocess.Process | None = None
        self._wait_task: asyncio.Task[int] | None = None
        self._stderr_task: asyncio.Task[None] | None = None
        self._watchdog: asyncio.Task[None] | None = None
        self._stop_requested = False
        self._terminated = False

    @property
    def command(self) -> list[str]:
        return build_command(
            self.ffmpeg, self.recording, self.out_dir, self.segment_seconds, self.rw_timeout
        )

    @property
    def running(self) -> bool:
        return self._proc is not None and self._proc.returncode is None

    @property
    def returncode(self) -> int | None:
        return None if self._proc is None else self._proc.returncode

    @property
    def stop_requested(self) -> bool:
        return self._stop_requested

    async def start(self) -> None:
        self.out_dir.mkdir(parents=True, exist_ok=True)
        with contextlib.suppress(FileNotFoundError):
            self.list_path.unlink()
        kwargs: dict[str, object] = {}
        if sys.platform == "win32":
            # Own process group: a Ctrl+C in the console reaches only the companion, which then
            # stops FFmpeg in a controlled way.
            kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
        else:
            kwargs["start_new_session"] = True
        log.debug("starting ffmpeg: %s", self.command)
        try:
            self._proc = await asyncio.create_subprocess_exec(
                *self.command,
                stdin=asyncio.subprocess.PIPE,
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.PIPE,
                **kwargs,  # type: ignore[arg-type]
            )
        except FileNotFoundError as exc:
            raise SegmenterError(
                f"FFmpeg não encontrado ({self.ffmpeg}). Use --ffmpeg ou a variável FFMPEG_PATH."
            ) from exc
        except OSError as exc:
            raise SegmenterError(f"Não foi possível iniciar o FFmpeg: {exc}") from exc
        self._wait_task = asyncio.create_task(self._proc.wait(), name="ffmpeg-wait")
        self._stderr_task = asyncio.create_task(self._read_stderr(), name="ffmpeg-stderr")

    async def segments(self) -> AsyncIterator[Segment]:
        """Yields segments until FFmpeg exits. Check :attr:`error` afterwards."""
        if self._proc is None or self._wait_task is None:
            raise RuntimeError("Segmenter.start() was not called")
        reader = SegmentListReader(self.list_path)
        held: list[SegmentListEntry] = []
        held_since = 0.0
        while True:
            new = reader.read_new()
            if new:
                held.extend(new)
                held_since = time.monotonic()
                self._note_listed(new)
            if self._wait_task.done():
                rest = reader.read_new() + reader.flush_partial()
                self._note_listed(rest)
                held.extend(rest)
                for index, entry in enumerate(held):
                    segment = self._to_segment(entry, last=index == len(held) - 1)
                    if segment is not None:
                        yield segment
                break
            # An entry followed by another one cannot be the last: emit it right away (this
            # keeps disk usage low while FFmpeg catches up with a long recording). Only the
            # newest entry waits until FFmpeg is seen running ``settle_seconds`` after it.
            if len(held) > 1:
                for entry in held[:-1]:
                    segment = self._to_segment(entry, last=False)
                    if segment is not None:
                        yield segment
                del held[:-1]
            settled = time.monotonic() - held_since >= self.settle_seconds
            if held and settled and not self._stop_requested:
                segment = self._to_segment(held.pop(), last=False)
                if segment is not None:
                    yield segment
            await asyncio.wait({self._wait_task}, timeout=self.poll_interval)
        await self._finish()

    def request_stop(self) -> None:
        """Graceful stop: ``q`` on stdin. FFmpeg closes the current segment and exits (after up
        to ``3 x rw_timeout`` when the input already stopped growing). A watchdog kills it if it
        is still running after :attr:`stop_timeout`."""
        if not self.running or self._stop_requested:
            return
        self._stop_requested = True
        proc = self._proc
        assert proc is not None
        if proc.stdin is not None:
            with contextlib.suppress(Exception):
                proc.stdin.write(b"q")
        self._watchdog = asyncio.create_task(self._kill_after(self.stop_timeout))

    async def terminate(self, grace: float = 5.0) -> None:
        """Hard stop (the segment being written is lost)."""
        if not self.running:
            return
        self._terminated = True
        proc = self._proc
        assert proc is not None and self._wait_task is not None
        with contextlib.suppress(ProcessLookupError):
            proc.terminate()
        try:
            await asyncio.wait_for(asyncio.shield(self._wait_task), grace)
        except TimeoutError:
            with contextlib.suppress(ProcessLookupError):
                proc.kill()
            await self._wait_task

    async def close(self) -> None:
        """Makes sure FFmpeg is gone and background tasks are finished."""
        if self.running:
            await self.terminate()
        await self._finish()

    async def _kill_after(self, delay: float) -> None:
        assert self._wait_task is not None
        try:
            await asyncio.wait_for(asyncio.shield(self._wait_task), delay)
        except TimeoutError:
            log.warning("FFmpeg did not stop %.0f s after 'q'; terminating it", delay)
            await self.terminate()

    async def _finish(self) -> None:
        if self._wait_task is not None:
            await asyncio.shield(self._wait_task)
        if self._stderr_task is not None:
            with contextlib.suppress(Exception):
                await asyncio.wait_for(asyncio.shield(self._stderr_task), 5)
        if self._watchdog is not None and not self._watchdog.done():
            self._watchdog.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._watchdog
        proc = self._proc
        if proc is not None and proc.stdin is not None:
            with contextlib.suppress(Exception):
                proc.stdin.close()
        rc = self.returncode
        if rc not in (0, None) and not self._terminated and not self._stop_requested:
            tail = " | ".join(list(self.stderr_tail)[-5:]) or "sem detalhes"
            self.error = f"O FFmpeg terminou com código {rc}: {tail}"

    async def _read_stderr(self) -> None:
        proc = self._proc
        if proc is None or proc.stderr is None:
            return
        while True:
            line = await proc.stderr.readline()
            if not line:
                return
            text = line.decode("utf-8", "replace").rstrip()
            if text:
                self.stderr_tail.append(text)
                log.debug("ffmpeg: %s", text)

    def _note_listed(self, entries: list[SegmentListEntry]) -> None:
        for entry in entries:
            self.listed_through = max(
                self.listed_through, file_number(entry.filename) + self.sequence_offset
            )

    def _to_segment(self, entry: SegmentListEntry, *, last: bool) -> Segment | None:
        sequence = file_number(entry.filename) + self.sequence_offset
        path = self.out_dir / entry.filename
        if sequence <= self.skip_through:
            self.skipped += 1
            with contextlib.suppress(OSError):
                path.unlink()
            return None
        self.produced += 1
        segment = Segment(
            sequence=sequence,
            path=path,
            start=round(entry.start + self.time_offset, 6),
            end=round(entry.end + self.time_offset, 6),
            last=last,
        )
        if self.produced_end is None or segment.end > self.produced_end:
            self.produced_end = segment.end
        return segment
