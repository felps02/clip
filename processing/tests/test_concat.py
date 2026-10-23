"""Joining live segments (``app/media/concat.py``): the concat list and the mapping of
session times onto the joined file."""

from __future__ import annotations

from pathlib import Path

import pytest

from app.media.concat import SegmentTimeline, concat_copy, concat_list
from app.media.ffmpeg import FfmpegTools
from app.media.probe import probe_file


def test_concat_list_names_the_parts_and_their_durations() -> None:
    text = concat_list([Path("w/previous.mkv"), Path("w/source.mkv")], [26.666, None])
    assert text == "ffconcat version 1.0\nfile previous.mkv\nduration 26.666\nfile source.mkv\n"
    assert concat_list([Path("part-000.mkv")]) == "ffconcat version 1.0\nfile part-000.mkv\n"
    # only bare names of the portable character set: the demuxer's safe mode stays on
    # (where the parts are is checked by concat_copy: next to the output)
    for bad in ("a b.mkv", ".hidden.mkv", "seg'1.mkv", "x;y.mkv", "ação.mkv"):
        with pytest.raises(ValueError):
            concat_list([Path("w") / bad])


def test_parts_must_sit_next_to_the_output(tmp_path: Path, tools: FfmpegTools) -> None:
    (tmp_path / "a").mkdir()
    with pytest.raises(ValueError):
        concat_copy(tools, [tmp_path / "a" / "x.mkv"], tmp_path / "out.mkv")
    with pytest.raises(ValueError):
        concat_copy(tools, [], tmp_path / "out.mkv")


def test_session_times_map_onto_the_joined_file() -> None:
    # three segments, a 0.5 s hole in the recording between the second and the third
    timeline = SegmentTimeline.from_segments([60.0, 120.0, 181.0], [60.0, 60.5, 59.0])
    assert timeline.duration == pytest.approx(179.5)
    assert timeline.to_local(60.0) == 0.0
    assert timeline.to_local(119.0) == 59.0
    assert timeline.to_local(120.0) == 60.0
    assert timeline.to_local(150.25) == 90.25
    assert timeline.to_local(180.75) == 120.5  # in the hole: the end of the second segment
    assert timeline.to_local(181.0) == 120.5
    assert timeline.to_local(200.0) == 139.5
    assert timeline.to_local(50.0) == -10.0  # before the first: the caller clamps


def test_two_pieces_join_without_re_encoding(
    tools: FfmpegTools, short_video: Path, tmp_path: Path
) -> None:
    """Two stream-copied halves of the 20 s fixture (keyframes every 2 s) joined back, in a
    directory whose name the list could not hold (the parts are named relative to it)."""
    directory = tmp_path / "O'Neil José [dir] 1,2;3"
    directory.mkdir()
    first, second = directory / "a.mkv", directory / "b.mkv"
    copy = ["-c", "copy", "-f", "matroska"]
    tools.run(["-i", str(short_video), "-t", "10", *copy, str(first)], timeout=60)
    tools.run(["-ss", "10", "-i", str(short_video), *copy, str(second)], timeout=60)
    durations = [probe_file(tools, first).duration, probe_file(tools, second).duration]
    assert all(d and d > 5 for d in durations), durations
    joined = concat_copy(
        tools, [first, second], directory / "joined.mkv", durations=[durations[0], None]
    )
    probe = probe_file(tools, joined)
    assert probe.has_video and probe.has_audio and probe.container == "mkv"
    assert probe.duration == pytest.approx(sum(d or 0.0 for d in durations), abs=0.2)
    assert not (directory / "joined.ffconcat").exists()  # the list is removed
