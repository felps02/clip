"""``live_segment`` and ``render_clip`` of live projects through the worker loop, against
the fake API, with real segments: the e2e fixture cut into Matroska pieces of ~25 s by the
FFmpeg segment muxer, exactly the way the companion cuts an OBS recording
(``-c copy -f segment -reset_timestamps 1``, cut list in CSV).

The e2e fixture (70 s) has speech at 5, 20, 40 and 55 s, loud 520 Hz tones at 21 s and
56 s and a scene change at 35 s. The segment muxer cuts it at 25.067 s and 51.734 s."""

from __future__ import annotations

import csv
import itertools
import json
import math
import os
import shutil
import subprocess
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import pytest

import app.analysis.pipeline as pipeline_module
from app.analysis.llm_assist import AssistOutcome
from app.analysis.pipeline import Analyzer
from app.analysis.transcription import TranscriptionService
from app.api_client import InternalApiClient
from app.config import Settings
from app.media.ffmpeg import FfmpegTools
from app.worker.loop import WorkerLoop
from app.worker.runner import JobRunner
from tests.fake_api import (
    API_BASE,
    STORAGE_BASE,
    FakeInternalApi,
    live_segment_payload,
    render_payload,
)
from tests.fake_transcription import FakeSpeechProvider, FakeTranscriptionService

E2E_FIXTURE = Path(
    os.environ.get("CLIPFORGE_E2E_FIXTURE")
    or Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    / "ClipForge"
    / "fixtures"
    / "e2e-fixture.mp4"
)
SEGMENT_SECONDS = 25
POST_ROLL = 8.0  # clamp(maxClipSeconds 12 / 2, 8, 30)
TONES = (21.0, 56.0)  # onsets in the fixture


@dataclass(frozen=True)
class Piece:
    name: str
    start: float  # session time (the cut list's start)
    duration: float  # the cut list's duration (what the companion sends)
    path: Path

    @property
    def end(self) -> float:
        return self.start + self.duration

    @property
    def size(self) -> int:
        return self.path.stat().st_size

    def previous_ref(self) -> dict[str, Any]:
        return {"fileName": self.name, "start": self.start, "duration": self.duration}


@pytest.fixture(scope="module")
def pieces(tools: FfmpegTools, tmp_path_factory: pytest.TempPathFactory) -> Iterator[list[Piece]]:
    if not E2E_FIXTURE.is_file():
        pytest.skip(f"e2e fixture not found at {E2E_FIXTURE} (CLIPFORGE_E2E_FIXTURE)")
    out = tmp_path_factory.mktemp("live-segments")
    proc = subprocess.run(
        [
            tools.ffmpeg_path,
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(E2E_FIXTURE),
            "-map",
            "0:v:0",
            "-map",
            "0:a:0?",
            "-c",
            "copy",
            "-f",
            "segment",
            "-segment_time",
            str(SEGMENT_SECONDS),
            "-reset_timestamps",
            "1",
            "-segment_list",
            str(out / "segments.csv"),
            "-segment_list_type",
            "csv",
            str(out / "seg_%05d.mkv"),
        ],
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    assert proc.returncode == 0, proc.stderr[-500:]
    with (out / "segments.csv").open(newline="", encoding="utf-8") as fh:
        rows = [row for row in csv.reader(fh) if row]
    result = [
        Piece(name, float(start), float(end) - float(start), out / name)
        for name, start, end in rows
    ]
    assert len(result) == 3 and all(20 <= p.duration <= 30 for p in result[:2]), result
    try:
        yield result
    finally:
        shutil.rmtree(out, ignore_errors=True)  # temporary media: never left behind


@pytest.fixture
def settings(work_settings: Settings) -> Settings:
    return work_settings.model_copy(
        update={"whisper_enabled": False, "webcam_samples": 6, "live_webcam_samples": 4}
    )


def make_loop(
    fake: FakeInternalApi, settings: Settings, tools: FfmpegTools, analyzer: Any = None
) -> tuple[WorkerLoop, InternalApiClient]:
    api = InternalApiClient(
        API_BASE,
        "test-key",
        "worker-test",
        transport=fake.transport(),
        sleep=lambda _s: None,
        backoff_base=0.01,
    )
    runner = JobRunner(settings, api, tools, analyzer=analyzer)
    return WorkerLoop(settings, api, runner, poll_interval=0.01), api


@dataclass
class JobRecord:
    job_id: str
    sequence: list[str]
    bodies: dict[str, list[Any]]

    def one(self, endpoint: str) -> Any:
        assert len(self.bodies.get(endpoint, [])) == 1, (endpoint, self.sequence)
        return self.bodies[endpoint][0]

    @property
    def candidates(self) -> list[dict[str, Any]]:
        return self.one("candidates")["candidates"]

    @property
    def summary(self) -> dict[str, Any]:
        return self.one("analysis")["summary"]


def run_job(loop: WorkerLoop, fake: FakeInternalApi, payload: dict[str, Any]) -> JobRecord:
    """Queue ``payload``, run one claim, and return what the worker reported for it."""
    mark = len(fake.calls)
    fake.payloads.append(payload)
    assert loop.run_once() is True
    job_id = payload["jobId"]
    calls = fake.calls[mark:]
    bodies: dict[str, list[Any]] = {}
    for _method, path, body in calls:
        prefix = f"/internal/jobs/{job_id}/"
        if path.startswith(prefix):
            bodies.setdefault(path[len(prefix) :], []).append(body)
    sequence = [name for name in fake.sequence()[mark:] if name != "heartbeat"]
    return JobRecord(job_id, sequence, bodies)


def without_nulls(data: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in data.items() if value is not None}


def session_payload(
    pieces: list[Piece], index: int, context: dict[str, Any] | None, **kwargs: Any
) -> dict[str, Any]:
    piece = pieces[index]
    return live_segment_payload(
        sequence=index,
        file_name=piece.name,
        start=piece.start,
        duration=piece.duration,
        size_bytes=piece.size,
        final=index == len(pieces) - 1,
        previous=pieces[index - 1].previous_ref() if index else None,
        context=context,
        **kwargs,
    )


def tone_onset(ffmpeg: str, media: Path, frequency: float = 520.0) -> float | None:
    """Time of the first 0.3 s during which the ``frequency`` tone dominates the audio."""
    raw = subprocess.run(
        [
            ffmpeg,
            *("-nostdin", "-v", "error", "-i", str(media), "-map", "0:a:0"),
            *("-ac", "1", "-ar", "16000", "-f", "s16le", "-"),
        ],
        capture_output=True,
        timeout=120,
        check=True,
    ).stdout
    x = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    frame = 1600
    n = len(x) // frame
    frames = x[: n * frame].reshape(n, frame) * np.hanning(frame)
    spectrum = np.abs(np.fft.rfft(frames, axis=1))
    freqs = np.fft.rfftfreq(frame, 1.0 / 16000)
    band = (freqs > frequency - 20) & (freqs < frequency + 20)
    ratio = spectrum[:, band].sum(axis=1) / (spectrum.sum(axis=1) + 1e-9)
    for index in range(max(0, n - 2)):
        if bool(np.all(ratio[index : index + 3] > 0.3)):
            return index / 10.0
    return None


def clip_tone_onset(tools: FfmpegTools, data: bytes, directory: Path) -> float | None:
    """``tone_onset`` of a rendered clip the fake storage received (written, measured and
    removed: no test media is left behind)."""
    media = directory / "measured.mp4"
    media.write_bytes(data)
    try:
        return tone_onset(tools.ffmpeg_path, media)
    finally:
        media.unlink(missing_ok=True)


def session_tone(tools: FfmpegTools, piece: Piece) -> float:
    """Session time of the tone a segment holds, measured in the segment file itself."""
    onset = tone_onset(tools.ffmpeg_path, piece.path)
    assert onset is not None, piece.name
    return piece.start + onset


# --------------------------------------------------------------------------------------
# a live session, segment after segment
# --------------------------------------------------------------------------------------


def test_a_live_session_end_to_end(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools, tmp_path: Path
) -> None:
    fake = FakeInternalApi(files={p.name: p.path for p in pieces}, keep_uploads=True)
    loop, api = make_loop(fake, settings, tools)
    jobs: list[JobRecord] = []
    rendered: list[dict[str, Any]] = []  # final segment: the clips and their media
    context: dict[str, Any] | None = None
    with api:
        for index in range(len(pieces)):
            final = index == len(pieces) - 1
            payload = session_payload(
                pieces, index, context, settings={"autoRenderTop": 3} if final else None
            )
            job = run_job(loop, fake, payload)
            jobs.append(job)
            context = job.summary["liveContext"]
            if final:
                for clip in job.bodies.get("clips", [{"clips": []}])[0]["clips"]:
                    name = clip["render"]["storageKey"].rsplit("/", 1)[-1]
                    rendered.append({**clip, "data": fake.upload_bodies[name]})

    s0, s1, s2 = pieces
    assert s1.start == pytest.approx(25.067, abs=0.01) and s2.start == pytest.approx(
        51.734, abs=0.01
    )
    handovers = [round(s0.start + s0.duration - POST_ROLL, 3), round(s1.end - POST_ROLL, 3)]
    ranges = [(-math.inf, handovers[0]), (handovers[0], handovers[1]), (handovers[1], math.inf)]

    for index, (job, piece) in enumerate(zip(jobs, pieces, strict=True)):
        seq = job.sequence
        assert fake.job_status(job.job_id) == "completed", seq
        assert "fail" not in seq
        # the current segment is downloaded and probed first, then its previous one
        order = [f"GET:{piece.name}", "probe"]
        if index:
            order.append(f"GET:{pieces[index - 1].name}")
        order += [
            "status:analyzing",
            "status:detecting_moments",
            "candidates",
            "status:generating_clips",
            "status:finalizing",
            "PUT:transcript.json",
            "PUT:signals.json",
            "analysis",
            "complete",
        ]
        positions = [seq.index(name) for name in order]
        assert positions == sorted(positions), seq
        statuses = [b["status"] for b in job.bodies["status"]]
        assert statuses == ["analyzing", "detecting_moments", "generating_clips", "finalizing"]
        # /probe: this segment only
        probe = job.one("probe")
        assert probe["hasVideo"] is True and probe["container"] == "mkv"
        assert probe["durationSeconds"] == pytest.approx(piece.duration, abs=0.1)
        complete = job.one("complete")
        assert complete["processedSeconds"] == pytest.approx(probe["durationSeconds"], abs=0.01)
        # the same summary (the two bodies differ only in how they spell an unknown field)
        assert without_nulls(complete["summary"]) == without_nulls(job.summary)

        # the commit rule, in session times
        low, high = ranges[index]
        for cand in job.candidates:
            peak = cand["signals"]["peakSeconds"]
            assert low <= peak < high, (index, peak)
            live = cand["signals"]["live"]
            assert live["sequence"] == index and live["sessionId"] == "session-1"
            assert live["commitSeconds"] == [
                None if math.isinf(low) else low,
                None if math.isinf(high) else high,
            ]
            window_start, window_end = live["windowSeconds"]
            assert window_start <= cand["startSeconds"] < cand["endSeconds"] <= window_end + 0.01
            assert cand["startSeconds"] <= peak <= cand["endSeconds"]
            assert 0 <= cand["score"] <= 100

        # the context the next segment receives
        live_context = job.summary["liveContext"]
        assert live_context["sequence"] == index
        assert live_context["committedUntil"] == (None if math.isinf(high) else high)
        assert live_context["language"] is None  # no transcription in this run

        clips = job.bodies["clips"][0]["clips"] if "clips" in job.bodies else []
        expected_clips = min(3 if index == 2 else 1, len(job.candidates))
        assert len(clips) == expected_clips
        # the top candidates, in rank order, with their session times
        for clip, cand in zip(clips, job.candidates, strict=False):
            assert (clip["startSeconds"], clip["endSeconds"]) == (
                cand["startSeconds"],
                cand["endSeconds"],
            )
            assert (clip["render"]["width"], clip["render"]["height"]) == (480, 852)
        assert job.summary["candidatesFound"] == len(job.candidates)
        assert job.summary["clipsRendered"] == expected_clips

    # every moment reported once: the tones at 21 s and 56 s, each by the segment whose
    # commit range holds its peak (the first one by segment 1: segment 0 leaves it, at 4 s
    # from its end, to the next window)
    peaks = sorted(c["signals"]["peakSeconds"] for job in jobs for c in job.candidates)
    assert all(b - a >= 1.0 for a, b in itertools.pairwise(peaks)), peaks
    for tone, owner in zip(TONES, (1, 2), strict=True):
        covering = [
            (i, c)
            for i, job in enumerate(jobs)
            for c in job.candidates
            if c["startSeconds"] <= tone + 0.5 <= c["endSeconds"]
            and abs(c["signals"]["peakSeconds"] - tone) < 3.0
        ]
        assert [i for i, _ in covering] == [owner], (tone, covering)
    assert not [c for c in jobs[0].candidates if c["signals"]["peakSeconds"] >= 17.0]

    # one value per second of the session, each second counted once
    samples = [job.summary["liveContext"]["compositeSample"] for job in jobs]
    assert [len(s) for s in samples] == [25, 52, 71]
    assert samples[1][:25] == samples[0] and samples[2][:52] == samples[1]

    # the artifacts of the last window: transcript in session times, signals with the plan
    signals = json.loads(fake.upload_bodies["signals.json"])
    assert signals["live"]["originSeconds"] == 26.0  # ceil(51.734 - 26.666)
    assert signals["live"]["tailSeconds"] == pytest.approx(25.734, abs=0.001)
    assert signals["live"]["commitSeconds"] == [handovers[1], None]
    transcript = json.loads(fake.upload_bodies["transcript.json"])
    assert transcript["timeBase"] == "session" and transcript["originSeconds"] == 26.0

    # the clip of the 56 s tone was cut from the window file at the right place: the
    # window starts 0.932 s into the previous segment, the tone is where the session says
    tone = session_tone(tools, s2)
    tone_clip = next(c for c in rendered if c["startSeconds"] <= tone <= c["endSeconds"])
    onset = clip_tone_onset(tools, tone_clip["data"], tmp_path)
    assert onset == pytest.approx(tone - tone_clip["startSeconds"], abs=0.35)
    assert not any(Path(settings.work_dir).glob("job-live-*"))  # work dirs removed


def test_the_session_language_is_fixed_by_the_first_segment_with_speech(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools
) -> None:
    provider = FakeSpeechProvider(speech=False, language="pt")
    analyzer = Analyzer(settings, tools, transcription=FakeTranscriptionService(provider))  # type: ignore[arg-type]
    fake = FakeInternalApi(files={p.name: p.path for p in pieces})
    loop, api = make_loop(fake, settings, tools, analyzer)
    no_renders = {"autoRenderTop": 0}
    with api:
        first = run_job(loop, fake, session_payload(pieces, 0, None, settings=no_renders))
        # no speech: no language yet, the next segment still detects
        assert first.summary["speechDetected"] is False
        assert first.summary["liveContext"]["language"] is None
        assert provider.detect_calls == 0

        provider.speech = True
        second = run_job(
            loop,
            fake,
            session_payload(pieces, 1, first.summary["liveContext"], settings=no_renders),
        )
        assert provider.detect_calls == 1
        assert second.summary["signals"]["transcript"]["languageSource"] == "detected"
        assert second.summary["liveContext"]["language"] == "pt"
        fixed = [e for e in second.bodies["events"] if e["message"].startswith("Idioma da live")]
        assert fixed and fixed[0]["data"] == {"language": "pt"}

        provider.language = "es"  # a detection now would say otherwise
        third = run_job(
            loop,
            fake,
            session_payload(pieces, 2, second.summary["liveContext"], settings=no_renders),
        )
    assert provider.detect_calls == 1  # known: no detection
    assert provider.transcribed[-1] == "pt"
    transcript = third.summary["signals"]["transcript"]
    assert transcript["languageSource"] == "session" and transcript["language"] == "pt"
    assert third.summary["language"] == "pt"
    assert third.summary["liveContext"]["language"] == "pt"
    assert not [e for e in third.bodies.get("events", []) if "Idioma da live" in e["message"]]
    for job in (first, second, third):
        assert "clips" not in job.bodies  # autoRenderTop 0
        assert fake.job_status(job.job_id) == "completed"


def test_a_previous_segment_gone_from_the_storage_only_costs_the_context(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools
) -> None:
    s0 = pieces[0]
    fake = FakeInternalApi(files={p.name: p.path for p in pieces}, storage_errors={s0.name: 404})
    loop, api = make_loop(fake, settings, tools)
    context = {"compositeSample": [0.1] * 25, "sequence": 0, "committedUntil": 17.067}
    with api:
        job = run_job(loop, fake, session_payload(pieces, 1, context))
    assert fake.job_status(job.job_id) == "completed" and "fail" not in job.sequence
    assert job.sequence.count(f"GET:{s0.name}") == 1  # a 404 is not retried
    warnings = [e for e in job.bodies["events"] if e["level"] == "warning"]
    assert any(
        e["message"].startswith("Segmento anterior não usado como contexto (não encontrado")
        for e in warnings
    ), warnings
    # analysed alone, from the first whole second of the segment; the commit rule holds
    for cand in job.candidates:
        assert cand["signals"]["live"]["windowSeconds"][0] == 26.0
        assert cand["signals"]["peakSeconds"] >= 17.067
    assert len(job.summary["liveContext"]["compositeSample"]) == 25 + 26  # seconds 26..51


def test_a_previous_segment_the_storage_cannot_serve_fails_the_job_for_a_retry(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools
) -> None:
    s0, _, _ = pieces
    fake = FakeInternalApi(files={p.name: p.path for p in pieces}, storage_errors={s0.name: 503})
    loop, api = make_loop(fake, settings, tools)
    with api:
        job = run_job(loop, fake, session_payload(pieces, 1, None))
    fail = job.one("fail")
    assert fail["code"] == "transfer_failed" and fail["retryable"] is True
    assert "segmento anterior" in fail["message"] and "processedSeconds" not in fail
    assert "candidates" not in job.bodies and "complete" not in job.bodies


class RecordingLlmClient:
    name = "local"
    model = "fake-qwen"


def test_the_llm_stage_of_a_live_segment_runs_only_within_the_live_budget(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The window rating is skipped and the assist stage (re-rank, titles) gets
    ``min(LLM_BUDGET_SECONDS, LIVE_LLM_BUDGET_SECONDS)``; 0 skips it."""
    budgets: list[float] = []

    def recording_assist(client: Any, candidates: list[Any], *_: Any, **kwargs: Any) -> Any:
        budgets.append(kwargs["budget_seconds"])
        return AssistOutcome(client.name, client.model, budget_seconds=kwargs["budget_seconds"])

    monkeypatch.setattr(pipeline_module, "run_assist", recording_assist)
    piece = pieces[0]
    for live_budget, expected in ((0.0, []), (15.0, [15.0])):
        current = settings.model_copy(update={"live_llm_budget_seconds": live_budget})
        analyzer = Analyzer(current, tools, llm_client=RecordingLlmClient())  # type: ignore[arg-type]
        fake = FakeInternalApi(files={piece.name: piece.path})
        loop, api = make_loop(fake, current, tools, analyzer)
        with api:
            job = run_job(
                loop,
                fake,
                live_segment_payload(
                    sequence=0,
                    file_name=piece.name,
                    start=piece.start,
                    duration=piece.duration,
                    final=True,
                    settings={"autoRenderTop": 0, "instructions": "momentos engraçados"},
                ),
            )
        assert fake.job_status(job.job_id) == "completed" and job.candidates
        assert budgets == expected
        budgets.clear()


# --------------------------------------------------------------------------------------
# render_clip of a live project
# --------------------------------------------------------------------------------------


def test_render_clip_of_a_live_project_joins_its_segments(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools, tmp_path: Path
) -> None:
    """A clip from 50 s to 60 s of the session spans segments 1 and 2: both are
    downloaded, joined without re-encoding and the session range is cut from the result
    (shifted by segment 1's start); the transcript's session times give the captions."""
    _, s1, s2 = pieces
    payload = render_payload(
        download_url="unused",
        transcript_url=f"{STORAGE_BASE}/storage/download/transcript.json",
        clip={"startSeconds": 50.0, "endSeconds": 60.0, "title": None},
    )
    del payload["source"]
    payload["segments"] = [
        {
            "downloadUrl": f"{STORAGE_BASE}/storage/download/{p.name}",
            "startOffsetSeconds": p.start,
            "durationSeconds": p.duration,
            "fileName": p.name,
        }
        for p in (s2, s1)  # any order: placed by their session times
    ]
    transcript = {
        "available": True,
        "language": "pt",
        "timeBase": "session",
        "segments": [
            {
                "start": 55.0,
                "end": 55.6,
                "text": "clutch",
                "words": [{"start": 55.0, "end": 55.6, "word": " clutch", "probability": 0.9}],
            }
        ],
    }
    fake = FakeInternalApi(
        payloads=[payload],
        files={p.name: p.path for p in pieces},
        json_files={"transcript.json": transcript},
        keep_uploads=True,
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    seq = [name for name in fake.sequence() if name != "heartbeat"]
    assert fake.job_status("job-render-1") == "completed", seq
    order = [
        f"GET:{s1.name}",
        f"GET:{s2.name}",
        "GET:transcript.json",
        "status:generating_clips",
        "status:finalizing",
        "PUT:render-v2.mp4",
        "render-result",
        "complete",
    ]
    positions = [seq.index(name) for name in order]
    assert positions == sorted(positions), seq
    result = fake.bodies("render-result")[0]
    assert result["status"] == "ready"
    assert result["durationSeconds"] == pytest.approx(10.0, abs=0.3)
    assert result["hasSubtitles"] is True  # the word at 55 s of the session is in the clip
    onset = clip_tone_onset(tools, fake.upload_bodies["render-v2.mp4"], tmp_path)
    assert onset == pytest.approx(session_tone(tools, s2) - 50.0, abs=0.35)
    assert fake.bodies("complete")[0] == {"processedSeconds": 0.0}


def test_render_clip_of_one_live_segment_needs_no_join(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools, tmp_path: Path
) -> None:
    s0 = pieces[0]
    payload = render_payload(download_url="unused", clip={"startSeconds": 18.0, "endSeconds": 24.0})
    del payload["source"]
    payload["segments"] = [
        {
            "downloadUrl": f"{STORAGE_BASE}/storage/download/{s0.name}",
            "startOffsetSeconds": s0.start,
            "durationSeconds": s0.duration,
            "fileName": s0.name,
        }
    ]
    fake = FakeInternalApi(payloads=[payload], files={s0.name: s0.path}, keep_uploads=True)
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    assert fake.job_status("job-render-1") == "completed"
    result = fake.bodies("render-result")[0]
    assert result["durationSeconds"] == pytest.approx(6.0, abs=0.3)
    onset = clip_tone_onset(tools, fake.upload_bodies["render-v2.mp4"], tmp_path)
    assert onset == pytest.approx(session_tone(tools, s0) - 18.0, abs=0.35)


# --------------------------------------------------------------------------------------
# with the real speech model
# --------------------------------------------------------------------------------------


@pytest.mark.slow
def test_the_real_model_detects_once_then_keeps_the_session_language(
    pieces: list[Piece], settings: Settings, tools: FfmpegTools
) -> None:
    with_whisper = settings.model_copy(update={"whisper_enabled": True})
    service = TranscriptionService(with_whisper)
    if not service.provider_for("base").available():
        pytest.skip("faster-whisper base not available offline")
    analyzer = Analyzer(with_whisper, tools, transcription=service)
    fake = FakeInternalApi(files={p.name: p.path for p in pieces})
    loop, api = make_loop(fake, with_whisper, tools, analyzer)
    no_renders = {"autoRenderTop": 0}
    with api:
        first = run_job(loop, fake, session_payload(pieces[:2], 0, None, settings=no_renders))
        second = run_job(
            loop,
            fake,
            session_payload(pieces[:2], 1, first.summary["liveContext"], settings=no_renders),
        )
    spoken = first.summary["signals"]["transcript"]
    assert spoken["speechDetected"] is True and spoken["languageSource"] == "detected"
    language = first.summary["liveContext"]["language"]
    assert language and language == spoken["language"]
    kept = second.summary["signals"]["transcript"]
    assert kept["languageSource"] == "session" and kept["language"] == language
    assert "languageConfidence" not in kept  # not detected again
    assert second.summary["liveContext"]["language"] == language
