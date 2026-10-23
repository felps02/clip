"""Parsing of FFmpeg's CSV segment list, including lines caught half-written."""

from __future__ import annotations

from pathlib import Path

import pytest

from clipforge_companion.segmenter import (
    SegmentListEntry,
    SegmentListReader,
    file_number,
    parse_segment_list_line,
)


def test_parses_ffmpeg_line() -> None:
    entry = parse_segment_list_line("seg_00001.mkv,16.734000,33.400000")
    assert entry == SegmentListEntry("seg_00001.mkv", 16.734, 33.4)
    assert file_number(entry.filename) == 1


def test_parses_quoted_name_and_crlf() -> None:
    entry = parse_segment_list_line('"seg,odd_00007.mkv",420.000000,480.033000\r')
    assert entry == SegmentListEntry("seg,odd_00007.mkv", 420.0, 480.033)
    assert file_number(entry.filename) == 7


@pytest.mark.parametrize(
    "line",
    [
        "",
        "seg_00001.mkv",
        "seg_00001.mkv,abc,1.0",
        "seg_00001.mkv,2.0,1.0",  # end before start
        "seg_00001.mkv,-1.0,1.0",
        "seg_00001.mkv,nan,1.0",
        "segment.mkv,0.0,1.0",  # no number in the name
    ],
)
def test_rejects_invalid_lines(line: str) -> None:
    assert parse_segment_list_line(line) is None


def test_reader_waits_for_the_newline_of_a_partial_line(tmp_path: Path) -> None:
    csv_path = tmp_path / "segments.csv"
    reader = SegmentListReader(csv_path)
    assert reader.read_new() == []  # file not created yet

    csv_path.write_bytes(b"seg_00000.mkv,0.000000,8.4")  # FFmpeg caught mid-write
    assert reader.read_new() == []
    with csv_path.open("ab") as handle:
        handle.write(b"00000\nseg_00001.mkv,8.400000,16.")
    assert reader.read_new() == [SegmentListEntry("seg_00000.mkv", 0.0, 8.4)]
    with csv_path.open("ab") as handle:
        handle.write(b"733000\nseg_00002.mkv,16.734000,25.067000\n")
    assert reader.read_new() == [
        SegmentListEntry("seg_00001.mkv", 8.4, 16.733),
        SegmentListEntry("seg_00002.mkv", 16.734, 25.067),
    ]
    assert reader.read_new() == []


def test_reader_flushes_a_final_line_without_newline(tmp_path: Path) -> None:
    csv_path = tmp_path / "segments.csv"
    csv_path.write_bytes(b"seg_00000.mkv,0.000000,8.400000\nseg_00001.mkv,8.400000,9.0")
    reader = SegmentListReader(csv_path)
    assert [e.filename for e in reader.read_new()] == ["seg_00000.mkv"]
    assert reader.flush_partial() == [SegmentListEntry("seg_00001.mkv", 8.4, 9.0)]
    assert reader.flush_partial() == []


def test_reader_skips_garbage_and_restarts_after_truncation(tmp_path: Path) -> None:
    csv_path = tmp_path / "segments.csv"
    csv_path.write_bytes(b"garbage line\nseg_00000.mkv,0.000000,5.000000\n")
    reader = SegmentListReader(csv_path)
    assert [e.filename for e in reader.read_new()] == ["seg_00000.mkv"]
    assert reader.invalid_lines == ["garbage line"]
    csv_path.write_bytes(b"seg_00000.mkv,0.000000,6.000000\n")  # list recreated (shorter)
    assert reader.read_new() == [SegmentListEntry("seg_00000.mkv", 0.0, 6.0)]
