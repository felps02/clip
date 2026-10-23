"""Detection of recordings that cannot be read while they are written (plain MP4)."""

from __future__ import annotations

import struct
from pathlib import Path

import pytest

from clipforge_companion.segmenter import (
    RecordingFormat,
    RecordingFormatError,
    check_recording_format,
    detect_recording_format,
    wait_for_recording,
)
from tests.media import run_ffmpeg


def box(kind: bytes, payload: bytes = b"", size: int | None = None) -> bytes:
    return struct.pack(">I4s", len(payload) + 8 if size is None else size, kind) + payload


FTYP = box(b"ftyp", b"isom\x00\x00\x02\x00isomiso2avc1mp41")


def write(path: Path, data: bytes) -> Path:
    path.write_bytes(data)
    return path


def test_matroska_flv_and_mpegts(tmp_path: Path) -> None:
    assert detect_recording_format(write(tmp_path / "a.mkv", b"\x1a\x45\xdf\xa3" + b"\0" * 60)) is (
        RecordingFormat.MATROSKA
    )
    assert detect_recording_format(write(tmp_path / "a.flv", b"FLV\x01\x05" + b"\0" * 20)) is (
        RecordingFormat.FLV
    )
    ts = (b"\x47" + b"\0" * 187) * 3
    assert detect_recording_format(write(tmp_path / "a.ts", ts)) is RecordingFormat.MPEGTS


def test_missing_empty_and_header_only_files_are_pending(tmp_path: Path) -> None:
    assert detect_recording_format(tmp_path / "missing.mkv") is RecordingFormat.PENDING
    assert detect_recording_format(write(tmp_path / "empty.mp4", b"")) is RecordingFormat.PENDING
    assert detect_recording_format(write(tmp_path / "ftyp.mp4", FTYP)) is RecordingFormat.PENDING


def test_plain_mp4_being_written_is_refused_with_clear_message(tmp_path: Path) -> None:
    # libavformat / OBS layout while recording: ftyp, free, mdat with size 0 ("until EOF").
    path = write(
        tmp_path / "2026-10-23 20-00-00.mp4",
        FTYP + box(b"free") + box(b"mdat", b"\x00" * 4096, size=0),
    )
    assert detect_recording_format(path) is RecordingFormat.MP4_INCOMPLETE
    with pytest.raises(RecordingFormatError) as info:
        check_recording_format(path)
    message = str(info.value)
    assert "MP4 comum" in message and "MKV" in message and path.name in message


def test_plain_mp4_with_placeholder_size_past_eof_is_refused(tmp_path: Path) -> None:
    path = write(
        tmp_path / "rec.mov", FTYP + box(b"wide") + box(b"mdat", b"\x01" * 100, size=10_000)
    )
    assert detect_recording_format(path) is RecordingFormat.MP4_INCOMPLETE


def test_fragmented_and_hybrid_mp4_are_accepted(tmp_path: Path) -> None:
    moov_fragmented = box(
        b"moov", box(b"mvhd", b"\0" * 100) + box(b"mvex", box(b"trex", b"\0" * 24))
    )
    fragment = box(b"moof", box(b"mfhd", b"\0" * 8)) + box(b"mdat", b"\x02" * 500)
    path = write(tmp_path / "hybrid.mp4", FTYP + moov_fragmented + fragment)
    assert detect_recording_format(path) is RecordingFormat.MP4_FRAGMENTED
    # Fragments without an initial moov (CMAF-like) or nested in a big mdat are fine too.
    path2 = write(tmp_path / "frag.mp4", FTYP + box(b"free") + fragment)
    assert detect_recording_format(path2) is RecordingFormat.MP4_FRAGMENTED
    nested = box(b"mdat", fragment, size=0)
    path3 = write(tmp_path / "nested.mp4", FTYP + nested)
    assert detect_recording_format(path3) is RecordingFormat.MP4_FRAGMENTED


def test_finished_mp4_is_accepted(tmp_path: Path) -> None:
    moov = box(b"moov", box(b"mvhd", b"\0" * 100) + box(b"trak", b"\0" * 50))
    at_end = write(tmp_path / "end.mp4", FTYP + box(b"mdat", b"\x03" * 1000) + moov)
    assert detect_recording_format(at_end) is RecordingFormat.MP4_COMPLETE
    faststart = write(tmp_path / "fast.mp4", FTYP + moov + box(b"mdat", b"\x03" * 1000))
    assert detect_recording_format(faststart) is RecordingFormat.MP4_COMPLETE


def test_unknown_container(tmp_path: Path) -> None:
    assert detect_recording_format(write(tmp_path / "x.bin", b"hello world, not media")) is (
        RecordingFormat.UNKNOWN
    )


def test_real_mp4_truncated_before_moov_is_refused(
    ffmpeg: str, fixture_video: Path, tmp_path: Path
) -> None:
    """A real (non-faststart) MP4 cut before its moov looks exactly like a recording in
    progress; the finished file and the fixture itself are accepted."""
    finished = tmp_path / "plain.mp4"
    run_ffmpeg(
        ffmpeg, "-i", str(fixture_video), "-t", "3", "-c", "copy", "-f", "mp4", str(finished)
    )
    assert detect_recording_format(finished) is RecordingFormat.MP4_COMPLETE
    assert detect_recording_format(fixture_video) is RecordingFormat.MP4_COMPLETE
    data = finished.read_bytes()
    moov_at = data.rfind(b"moov") - 4
    assert moov_at > data.find(b"mdat")  # libavformat writes moov at the end by default
    in_progress = write(tmp_path / "in-progress.mp4", data[:moov_at])
    assert detect_recording_format(in_progress) is RecordingFormat.MP4_INCOMPLETE


async def test_wait_for_recording_waits_for_the_header(tmp_path: Path) -> None:
    import asyncio

    path = tmp_path / "late.mkv"

    async def writer() -> None:
        await asyncio.sleep(0.3)
        path.write_bytes(b"\x1a\x45\xdf\xa3" + b"\0" * 60)

    task = asyncio.create_task(writer())
    fmt = await wait_for_recording(path, timeout=5, poll_interval=0.05)
    await task
    assert fmt is RecordingFormat.MATROSKA
    assert await wait_for_recording(tmp_path / "never.mkv", timeout=0.2, poll_interval=0.05) is (
        RecordingFormat.PENDING
    )
    plain = write(tmp_path / "plain.mp4", FTYP + box(b"mdat", b"\0" * 64, size=0))
    with pytest.raises(RecordingFormatError):
        await wait_for_recording(plain, timeout=1, poll_interval=0.05)
