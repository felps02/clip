"""Worker loop against the fake API: protocol order, cancel path and failure paths."""

from __future__ import annotations

import copy
import json
import math
import os
import threading
import time
from datetime import datetime
from pathlib import Path
from typing import Any

import httpx
import pytest

import app.analysis.pipeline as pipeline_module
import app.worker.runner as runner_module
from app.analysis.pipeline import Analyzer
from app.analysis.transcription import TranscriptionService
from app.api_client import InternalApiClient
from app.config import Settings
from app.media.ffmpeg import FfmpegError, FfmpegTools
from app.models import JobPayload
from app.render.renderer import Renderer, RenderOutput
from app.worker.errors import CancelToken, JobCancelled
from app.worker.heartbeat import CANCELLING_MESSAGE, HeartbeatThread, ProgressState
from app.worker.loop import WorkerLoop
from app.worker.runner import WORK_DIR_MARKER, JobContext, JobRunner
from tests.fake_api import API_BASE, STORAGE_BASE, FakeInternalApi, analyze_payload, render_payload

TRANSCRIPT_JSON = {
    "available": True,
    "model": "faster-whisper/base",
    "language": "pt",
    "durationSeconds": 90.0,
    "segments": [
        {
            "start": 40.0,
            "end": 46.0,
            "text": "isso foi um clutch absurdo não acredito",
            "words": [
                {"start": 40.2 + i * 0.6, "end": 40.6 + i * 0.6, "word": w, "probability": 0.9}
                for i, w in enumerate(["isso", "foi", "um", "clutch", "absurdo", "não", "acredito"])
            ],
        }
    ],
}


def make_loop(
    fake: FakeInternalApi, settings: Settings, tools: FfmpegTools, *, analyzer: Any = None
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
    loop = WorkerLoop(settings, api, runner, poll_interval=0.01)
    return loop, api


@pytest.fixture
def settings(work_settings: Settings) -> Settings:
    return work_settings.model_copy(update={"whisper_enabled": False, "webcam_samples": 6})


def test_analyze_source_happy_path(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
            )
        ],
        files={"short.mp4": short_video},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
        assert loop.run_once() is False  # queue empty (204)
    assert loop.jobs_run == 1

    seq = fake.sequence()
    assert seq[0] == "claim"
    assert "fail" not in seq
    assert "status:cancelled" not in seq
    # queued -> preparing_media is performed by the claim itself: posting it again is a 409
    assert "status:preparing_media" not in seq
    order = [
        "GET:short.mp4",
        "probe",
        "status:analyzing",
        "status:detecting_moments",
        "candidates",
        "status:generating_clips",
        "upload-urls",  # each clip is uploaded as soon as it is rendered
        "PUT:clip-01.mp4",
        "PUT:clip-01.jpg",
        "status:finalizing",
        "PUT:transcript.json",
        "PUT:signals.json",
        "clips",
        "analysis",
        "complete",
    ]
    positions = [seq.index(name) for name in order]
    assert positions == sorted(positions), seq
    # progress inside preparing_media is published through /heartbeat before the download
    assert seq.index("heartbeat") < seq.index("GET:short.mp4")

    # every transition exactly once: the fake (like the API) answers 409 to a repeated status
    assert fake.statuses() == ["analyzing", "detecting_moments", "generating_clips", "finalizing"]
    assert fake.job_status("job-analyze-1") == "completed"
    progress = [b.get("progress") for b in fake.bodies("status") if b.get("progress") is not None]
    assert progress == sorted(progress)
    assert all(isinstance(b.get("message"), str) and b["message"] for b in fake.bodies("status"))
    beats = [b for b in fake.bodies("heartbeat") if isinstance(b, dict)]
    assert {"Preparando arquivo…", "Verificando o arquivo…"} <= {b.get("message") for b in beats}
    assert all(0 <= b.get("progress", 0) <= 100 for b in beats)
    assert set(fake.worker_ids) == {"worker-test"}  # X-Worker-Id on every internal call

    probe = fake.bodies("probe")[0]
    assert (
        probe["hasVideo"] is True
        and probe["width"] == 640
        and probe["durationSeconds"] == pytest.approx(20.0, abs=0.2)
    )

    cands = fake.bodies("candidates")[0]["candidates"]
    assert cands and cands[0]["rank"] == 1
    assert cands[0]["startSeconds"] <= 8.5 <= cands[0]["endSeconds"]
    assert 0 <= cands[0]["score"] <= 100
    assert cands[0]["signals"]["audio"]["available"] is True
    assert cands[0]["signals"]["transcript"]["available"] is False

    assert {"clip-01.mp4", "clip-01.jpg", "transcript.json", "signals.json"} <= set(fake.uploads)
    assert all(size > 0 for size in fake.uploads.values())
    upload_requests = [b["files"] for b in fake.bodies("upload-urls")]
    assert [[f["fileName"] for f in files] for files in upload_requests] == [
        ["clip-01.mp4", "clip-01.jpg"],  # the clip, right after its render
        ["transcript.json", "signals.json"],  # the analysis artifacts, while finalizing
    ]
    upload_kinds = {f["fileName"]: f["kind"] for files in upload_requests for f in files}
    assert (
        upload_kinds["clip-01.mp4"] == "clip_render" and upload_kinds["clip-01.jpg"] == "thumbnail"
    )
    assert upload_kinds["signals.json"] == "analysis"

    clips = fake.bodies("clips")[0]["clips"]
    assert len(clips) == 1
    assert clips[0]["candidateId"] == fake.candidate_ids[0]
    assert clips[0]["render"]["storageKey"].endswith("clip-01.mp4")
    assert clips[0]["render"]["width"] == 480 and clips[0]["render"]["height"] == 852
    assert clips[0]["thumbnailKey"].endswith("clip-01.jpg")
    assert clips[0]["aspectRatio"] == "9:16"
    # the clip keeps the requested framing mode (a re-render decides the layout again); the
    # layout actually used is reported through an info event
    assert clips[0]["framing"]["mode"] == "auto"
    infos = [e for e in fake.bodies("events") if e["level"] == "info"]
    assert [e["data"]["layout"] for e in infos] == ["center"]
    assert infos[0]["message"].startswith("Clipe 1 renderizado: 480x852, recorte central")
    assert fake.events_rejected == 0

    analysis = fake.bodies("analysis")[0]
    assert analysis["transcriptKey"].endswith("transcript.json")
    assert analysis["signalsKey"].endswith("signals.json")
    assert analysis["summary"]["signals"]["audio"]["available"] is True
    assert analysis["summary"]["signals"]["visual"]["coverage"] == 1.0
    assert analysis["summary"]["processedSeconds"] == pytest.approx(20.0, abs=0.2)
    assert "durationSeconds" not in analysis["summary"]  # not part of the contract

    complete = fake.bodies("complete")[0]
    assert complete["processedSeconds"] == pytest.approx(20.0, abs=0.2)
    assert complete["summary"]["candidatesFound"] == len(cands)
    assert complete["summary"]["clipsRendered"] == 1
    assert fake.heartbeats >= 0
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


class SlowAnalyzer:
    """Stands in for the analyzer and waits until the job is cancelled."""

    def __init__(self, max_seconds: float = 15.0) -> None:
        self.max_seconds = max_seconds
        self.started = threading.Event()

    def analyze(
        self,
        path: Any,
        probe: Any,
        job_settings: Any,
        limits: Any = None,
        *,
        options: Any = None,
        progress: Any = None,
        cancel: CancelToken | None = None,
    ) -> Any:
        self.started.set()
        deadline = time.monotonic() + self.max_seconds
        while time.monotonic() < deadline:
            if cancel is not None:
                cancel.raise_if_cancelled()
            if progress is not None:
                progress("analyzing", 15, "Analisando áudio…")
            time.sleep(0.05)
        raise AssertionError("cancellation never arrived")


def test_cancel_requested_by_heartbeat(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4", lease_seconds=3
            )
        ],
        files={"short.mp4": short_video},
    )
    slow = SlowAnalyzer()

    def cancel_once_analyzing() -> None:
        slow.started.wait(10)
        fake.cancel_at_heartbeat = 1  # every heartbeat from now on answers cancelRequested

    threading.Thread(target=cancel_once_analyzing, daemon=True).start()
    loop, api = make_loop(fake, settings, tools, analyzer=slow)
    started = time.monotonic()
    with api:
        assert loop.run_once() is True
    assert slow.started.is_set()
    assert time.monotonic() - started < 10
    seq = fake.sequence()
    assert "heartbeat" in seq
    assert "status:analyzing" in seq and "status:cancelled" in seq
    assert "complete" not in seq and "fail" not in seq and "candidates" not in seq
    assert fake.bodies("status")[-1]["status"] == "cancelled"
    assert fake.job_status("job-analyze-1") == "cancelled"
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_cancel_during_download_is_reported_as_cancelled(
    settings: Settings, tools: FfmpegTools
) -> None:
    """The download is the longest early step: a cancel there must end as ``cancelled``,
    never as ``/fail invalid_media`` ("download aborted")."""
    size = 2 * 1024 * 1024
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/slow.mp4",
                size_bytes=size,
                lease_seconds=3,
            )
        ],
        files={"slow.mp4": b"\0" * size},
        stream_delay=0.1,
        stream_chunk=32 * 1024,  # ~6 s download
        # beats 1-2 = forced progress ("Preparando…", "Baixando…") before the GET starts;
        # beat 3 (heartbeat thread at ~1 s, or throttled progress at 2 s) lands mid-download
        cancel_at_heartbeat=3,
    )
    loop, api = make_loop(fake, settings, tools)
    started = time.monotonic()
    with api:
        assert loop.run_once() is True
    assert time.monotonic() - started < 6  # aborted, not downloaded to the end
    seq = fake.sequence()
    assert seq.index("GET:slow.mp4") < seq.index("status:cancelled")
    assert "fail" not in seq and "probe" not in seq and "complete" not in seq
    assert fake.statuses() == ["cancelled"]
    assert fake.job_status("job-analyze-1") == "cancelled"
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_lost_lease_stops_the_job_silently(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """409 on a heartbeat (lease owned by another worker / job re-queued): the worker stops
    without posting /fail or /status, since the job is no longer its own."""
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4", lease_seconds=3
            )
        ],
        files={"short.mp4": short_video},
        lease_lost_at_heartbeat=2,
    )
    loop, api = make_loop(fake, settings, tools, analyzer=SlowAnalyzer())
    with api:
        payload = api.claim(["analyze_source"])
        assert payload is not None
        assert loop.runner.run(payload) == "cancelled"
    seq = fake.sequence()
    assert fake.heartbeats >= 2
    assert "fail" not in seq and "complete" not in seq and "status:cancelled" not in seq
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


@pytest.mark.parametrize(
    "job_id", ["..", ".", "D:evil", "a:b", "job/1", "job\\1", ".hidden", "-x", "", "x" * 81]
)
def test_unsafe_job_ids_never_touch_the_file_system_or_the_api(
    job_id: str, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi()
    loop, api = make_loop(fake, settings, tools)
    work = Path(settings.work_dir)
    work.mkdir(parents=True, exist_ok=True)
    sibling = work.parent / "keep.txt"  # what a ".." work dir used to wipe
    sibling.write_text("keep", encoding="utf-8")
    payload = JobPayload.model_validate(
        analyze_payload(job_id=job_id, download_url=f"{STORAGE_BASE}/storage/download/x.mp4")
    )
    with api:
        assert loop.runner.run(payload) == "failed"
    assert fake.calls == []  # an unsafe id never reaches a URL either
    assert loop.runner.job_work_dir(job_id) is None
    assert work.exists() and list(work.iterdir()) == []
    assert sibling.read_text(encoding="utf-8") == "keep"


def test_safe_job_ids_map_to_a_child_of_the_work_dir(
    settings: Settings, tools: FfmpegTools
) -> None:
    loop, api = make_loop(FakeInternalApi(), settings, tools)
    api.close()
    work = Path(settings.work_dir)
    for job_id in ("6f1c2c3e-1111-2222-3333-444444444444", "job-analyze-1", "A_b-9"):
        assert loop.runner.job_work_dir(job_id) == work / job_id


def test_cancel_kills_running_ffmpeg(tools: FfmpegTools) -> None:
    cancel = CancelToken()
    args = [
        "-f",
        "lavfi",
        "-i",
        "testsrc2=size=320x180:rate=25",
        "-t",
        "120",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "gray",
        "-",
    ]
    threading.Timer(0.5, cancel.cancel).start()
    started = time.monotonic()
    with pytest.raises(JobCancelled), tools.stream(args, timeout=60, cancel=cancel) as pipe:
        for _chunk in pipe.iter_chunks(320 * 180):
            time.sleep(0.01)
    assert time.monotonic() - started < 10
    assert cancel.cancelled


def test_missing_source_object_is_a_retryable_transfer_failure(
    settings: Settings, tools: FfmpegTools
) -> None:
    """A 404 on the signed download URL is a storage problem, never ``invalid_media``: the
    job is re-queued (a fresh claim signs a new URL; the API bounds the attempts) and the
    message says the object was not found."""
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/missing.mp4")]
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed"
    assert fails[0]["retryable"] is True
    assert "processedSeconds" not in fails[0]
    assert "não foi encontrado" in fails[0]["message"] and "HTTP 404" in fails[0]["message"]
    assert "complete" not in fake.sequence() and "probe" not in fake.sequence()


@pytest.mark.parametrize("status", [401, 403, 410, 413, 500])
def test_source_download_failures_are_retryable_transfer_failures(
    settings: Settings, tools: FfmpegTools, status: int
) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/src.mp4")],
        storage_errors={"src.mp4": status},
    )
    loop, api = make_loop(fake, settings, tools)
    api.max_retries = 1
    with api:
        assert loop.run_once() is True
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed" and fails[0]["retryable"] is True
    assert "processedSeconds" not in fails[0]
    assert f"HTTP {status}" in fails[0]["message"]
    assert "probe" not in fake.sequence()


def test_source_download_network_failure_is_a_retryable_transfer_failure(
    settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/src.mp4")]
    )

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.host == "fake-storage":
            raise httpx.ConnectError("connection refused", request=request)
        return fake.handler(request)

    api = InternalApiClient(
        API_BASE,
        "test-key",
        "worker-test",
        transport=httpx.MockTransport(handler),
        sleep=lambda _s: None,
        max_retries=2,
    )
    loop = WorkerLoop(settings, api, JobRunner(settings, api, tools), poll_interval=0.01)
    with api:
        assert loop.run_once() is True
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed" and fails[0]["retryable"] is True
    assert "connection refused" in fails[0]["message"]


def test_upload_failure_is_a_retryable_transfer_error_not_invalid_media(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """A rejected storage PUT (expired signature, bucket policy, clock skew) is an
    infrastructure problem after a successful analysis: never ``invalid_media``, never
    charged, and retryable so a fresh claim can issue new signed URLs."""
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
            )
        ],
        files={"short.mp4": short_video},
        storage_errors={"clip-01.mp4": 403},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    seq = fake.sequence()
    assert "PUT:clip-01.mp4" in seq and "clips" not in seq and "complete" not in seq
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed" and fails[0]["retryable"] is True
    assert "processedSeconds" not in fails[0]  # a storage problem is not charged
    assert "clip-01.mp4" in fails[0]["message"] and "HTTP 403" in fails[0]["message"]
    assert fake.job_status("job-analyze-1") == "failed"
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_rejected_upload_of_a_render_is_a_retryable_transfer_error(
    fixture_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """Even a 413 from the storage PUT is not the user's clip being invalid: retryable
    ``transfer_failed``, no failed render-result, nothing charged."""
    fake = FakeInternalApi(
        payloads=[
            render_payload(
                download_url=f"{STORAGE_BASE}/storage/download/source.mp4",
                size_bytes=fixture_video.stat().st_size,
            )
        ],
        files={"source.mp4": fixture_video},
        storage_errors={"render-v2.mp4": 413},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    seq = fake.sequence()
    assert "PUT:render-v2.mp4" in seq and "render-result" not in seq and "complete" not in seq
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed" and fails[0]["retryable"] is True
    assert "HTTP 413" in fails[0]["message"] and "render-v2.mp4" in fails[0]["message"]
    assert "processedSeconds" not in fails[0]


def test_invalid_claim_payload_fails_the_job_without_backing_off(
    settings: Settings, tools: FfmpegTools
) -> None:
    """Contract drift (an aspect ratio this worker does not know) fails that one job with
    ``invalid_payload`` and the loop claims again right away: no backoff, no reaper."""
    bad = analyze_payload(
        download_url=f"{STORAGE_BASE}/storage/download/x.mp4", settings={"aspectRatio": "4:5"}
    )
    fake = FakeInternalApi(payloads=[bad])
    loop, api = make_loop(fake, settings, tools)
    waits: list[float] = []
    loop._sleep = waits.append
    with api:
        loop._stop.clear()
        threading.Timer(0.2, loop._stop.set).start()
        loop.run_forever()
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "invalid_payload" and fails[0]["retryable"] is False
    assert fake.job_status("job-analyze-1") == "failed"
    assert loop.jobs_rejected == 1 and loop.jobs_run == 0 and loop.last_error is None
    assert loop.status()["jobsRejected"] == 1
    assert waits and max(waits) == loop.poll_interval  # idle polling only, never a backoff
    assert fake.sequence().count("claim") >= 2  # the loop went straight back to claiming


def test_audio_only_source_is_rejected(
    tts_wav: tuple[Path, str] | None, short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    if tts_wav is None:
        pytest.skip("no wav fixture available (Windows SAPI voice missing)")
    wav, _ = tts_wav
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/a.wav", file_name="a.wav"
            )
        ],
        files={"a.wav": wav},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        loop.run_once()
    fails = fake.bodies("fail")
    assert fails and fails[0]["code"] == "invalid_media" and fails[0]["retryable"] is False
    assert "sem vídeo" in fails[0]["message"]
    assert "probe" in fake.sequence()  # the probe was reported before the rejection


def test_unexpected_exception_is_retryable_worker_error(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    class Broken:
        def analyze(self, *args: Any, **kwargs: Any) -> Any:
            raise RuntimeError("boom")

    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/short.mp4")],
        files={"short.mp4": short_video},
    )
    loop, api = make_loop(fake, settings, tools, analyzer=Broken())
    with api:
        loop.run_once()
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "worker_error"
    assert fails[0]["retryable"] is True
    assert "RuntimeError" in fails[0]["message"]
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_probe_409_stops_the_job_without_fail(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/short.mp4")],
        files={"short.mp4": short_video},
        probe_conflict=("quota_exceeded", "minutos esgotados"),
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        loop.run_once()
    seq = fake.sequence()
    assert seq[-1] == "probe"
    assert "fail" not in seq and "status:analyzing" not in seq


def test_render_clip_happy_path(
    fixture_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[
            render_payload(
                download_url=f"{STORAGE_BASE}/storage/download/source.mp4",
                size_bytes=fixture_video.stat().st_size,
                transcript_url=f"{STORAGE_BASE}/storage/download/transcript.json",
                webcam_region={"x": 0.7, "y": 0.65, "w": 0.3, "h": 0.35},
            )
        ],
        files={"source.mp4": fixture_video},
        json_files={"transcript.json": TRANSCRIPT_JSON},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    seq = fake.sequence()
    assert "fail" not in seq
    assert "status:preparing_media" not in seq  # already set by the claim
    assert fake.statuses() == ["generating_clips", "finalizing"]
    order = [
        "GET:source.mp4",
        "GET:transcript.json",
        "status:generating_clips",
        "status:finalizing",
        "upload-urls",
        "PUT:render-v2.mp4",
        "render-result",
        "complete",
    ]
    positions = [seq.index(name) for name in order]
    assert positions == sorted(positions), seq
    assert fake.job_status("job-render-1") == "completed"
    result = fake.bodies("render-result")[0]
    assert result["renderId"] == "r2" and result["status"] == "ready"
    assert result["storageKey"].endswith("render-v2.mp4")
    assert result["width"] == 480 and result["height"] == 852
    assert result["hasSubtitles"] is True
    assert result["durationSeconds"] == pytest.approx(6.0, abs=0.3)
    assert result["thumbnailKey"].endswith("render-v2.jpg")
    complete = fake.bodies("complete")[0]
    # no summary: the documented summary is the analysis one; the render went through
    # /render-result and the resolved layout through an info event
    assert complete == {"processedSeconds": 0.0}
    infos = [e for e in fake.bodies("events") if e["level"] == "info"]
    assert len(infos) == 1 and infos[0]["data"]["layout"] == "webcam-split"
    assert infos[0]["message"] == (
        "Versão 2 do clipe renderizada: 480x852, webcam + jogo, com legendas"
    )
    assert not (Path(settings.work_dir) / "job-render-1").exists()


def test_render_clip_with_a_missing_ffmpeg_binary_is_a_retryable_worker_error(
    fixture_video: Path, settings: Settings, tools: FfmpegTools, tmp_path: Path
) -> None:
    """ffprobe works but ffmpeg is missing (partial tools install, wrong FFMPEG_PATH): a
    deployment problem, so the job is re-queued for a healthy worker; the user's render
    is not marked failed."""
    broken = FfmpegTools(str(tmp_path / "missing" / "ffmpeg.exe"), tools.ffprobe_path)
    fake = FakeInternalApi(
        payloads=[
            render_payload(
                download_url=f"{STORAGE_BASE}/storage/download/source.mp4",
                size_bytes=fixture_video.stat().st_size,
            )
        ],
        files={"source.mp4": fixture_video},
    )
    loop, api = make_loop(fake, settings, broken)
    with api:
        assert loop.run_once() is True
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "worker_error" and fails[0]["retryable"] is True
    assert "render-result" not in fake.sequence()  # the render is not reported as failed
    assert "processedSeconds" not in fails[0]


def test_analyze_source_with_a_missing_ffmpeg_for_the_renders_is_a_retryable_worker_error(
    short_video: Path, settings: Settings, tools: FfmpegTools, tmp_path: Path
) -> None:
    """The per-clip render loop logs real render failures and goes on, but a missing
    ffmpeg binary must not be swallowed as N clip failures: the job fails retryable."""
    broken = FfmpegTools(str(tmp_path / "missing" / "ffmpeg.exe"), tools.ffprobe_path)
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
            )
        ],
        files={"short.mp4": short_video},
    )
    api = InternalApiClient(
        API_BASE, "test-key", "worker-test", transport=fake.transport(), sleep=lambda _s: None
    )
    runner = JobRunner(settings, api, tools, renderer=Renderer(broken, settings.fonts_dir))
    with api:
        payload = api.claim(["analyze_source"])
        assert payload is not None
        assert runner.run(payload) == "failed"
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "worker_error" and fails[0]["retryable"] is True
    assert "FFmpeg indisponível" in fails[0]["message"]
    assert "clips" not in fake.sequence() and "complete" not in fake.sequence()
    assert not any(e["level"] == "error" for e in fake.bodies("events"))


def test_render_clip_failure_reports_render_failed(
    fixture_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[
            render_payload(
                download_url=f"{STORAGE_BASE}/storage/download/source.mp4",
                clip={"startSeconds": 10.0, "endSeconds": 10.1},
            )
        ],
        files={"source.mp4": fixture_video},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        loop.run_once()
    fails = fake.bodies("fail")
    assert fails and fails[0]["code"] == "render_failed" and fails[0]["retryable"] is False


def test_loop_thread_start_and_stop(settings: Settings, tools: FfmpegTools) -> None:
    fake = FakeInternalApi()
    loop, api = make_loop(fake, settings, tools)
    with api:
        loop.start()
        assert loop.running
        time.sleep(0.1)
        loop.stop(timeout=5)
    assert not loop.running
    assert fake.sequence().count("claim") >= 1
    assert loop.status()["currentJobId"] is None


def test_loop_survives_api_outage(settings: Settings, tools: FfmpegTools) -> None:
    fake = FakeInternalApi(flaky={"claim": [500] * 100000})
    loop, api = make_loop(fake, settings, tools)
    waits: list[float] = []
    loop._sleep = waits.append
    api.max_retries = 0
    with api:
        loop._stop.clear()
        threading.Timer(0.3, loop._stop.set).start()
        loop.run_forever()
    assert loop.last_error
    assert waits and waits[-1] >= waits[0]


def test_analyzer_default_is_real(settings: Settings, tools: FfmpegTools) -> None:
    fake = FakeInternalApi()
    api = InternalApiClient(API_BASE, "test-key", "w", transport=fake.transport())
    runner = JobRunner(settings, api, tools)
    assert isinstance(runner.analyzer, Analyzer)
    api.close()


class StubbornAnalyzer:
    """Waits for the cancel like ``SlowAnalyzer``, then keeps working for ``unwind_seconds``
    without consulting the token (an upload or an LLM request in flight) before unwinding."""

    def __init__(self, unwind_seconds: float, max_seconds: float = 15.0) -> None:
        self.unwind_seconds = unwind_seconds
        self.max_seconds = max_seconds
        self.started = threading.Event()

    def analyze(
        self,
        path: Any,
        probe: Any,
        job_settings: Any,
        limits: Any = None,
        *,
        options: Any = None,
        progress: Any = None,
        cancel: CancelToken | None = None,
    ) -> Any:
        self.started.set()
        deadline = time.monotonic() + self.max_seconds
        while time.monotonic() < deadline:
            if cancel is not None and cancel.cancelled:
                time.sleep(self.unwind_seconds)
                cancel.raise_if_cancelled()
            time.sleep(0.05)
        raise AssertionError("cancellation never arrived")


def test_heartbeats_continue_while_a_cancelled_job_unwinds(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """After the cancel the job still has to unwind and post ``/status cancelled``, which
    can outlast the lease: the heartbeat must keep extending it, otherwise the reaper
    re-queues the cancelled job and the worker's final status is a 409."""
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4", lease_seconds=3
            )
        ],
        files={"short.mp4": short_video},
    )
    stubborn = StubbornAnalyzer(unwind_seconds=2.8)

    def cancel_once_analyzing() -> None:
        stubborn.started.wait(10)
        fake.cancel_at_heartbeat = 1

    threading.Thread(target=cancel_once_analyzing, daemon=True).start()
    loop, api = make_loop(fake, settings, tools, analyzer=stubborn)
    with api:
        assert loop.run_once() is True
    seq = fake.sequence()
    assert "fail" not in seq and "complete" not in seq
    assert fake.statuses()[-1] == "cancelled"
    assert fake.job_status("job-analyze-1") == "cancelled"
    # the lease was extended during the unwind (heartbeat every 1 s, unwind 2.8 s)
    assert fake.cancel_answered_at is not None
    assert fake.heartbeats >= fake.cancel_answered_at + 2
    beats = [b for b in fake.bodies("heartbeat") if isinstance(b, dict)]
    assert any(b.get("message") == CANCELLING_MESSAGE for b in beats)
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_heartbeat_thread_keeps_beating_after_a_cancel_until_stopped(
    settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")],
        cancel_at_heartbeat=2,
    )
    _loop, api = make_loop(fake, settings, tools)
    with api:
        payload = api.claim(["analyze_source"])
        assert payload is not None
        cancel = CancelToken()
        state = ProgressState()
        heartbeat = HeartbeatThread(api, payload.job_id, payload.lease_seconds, state, cancel)
        heartbeat.interval = 0.05
        heartbeat.start()
        deadline = time.monotonic() + 5
        while not cancel.cancelled and time.monotonic() < deadline:
            time.sleep(0.01)
        assert cancel.reason == "cancel_requested"
        at_cancel = fake.heartbeats
        time.sleep(0.4)
        assert heartbeat.is_alive()
        assert fake.heartbeats >= at_cancel + 3  # still extending the lease
        assert state.get()[1] == CANCELLING_MESSAGE
        # the job thread posts the final status, then stops the heartbeat
        api.status(payload.job_id, "cancelled", None, "Cancelado pelo usuário")
        heartbeat.stop(timeout=5)
        assert not heartbeat.is_alive()
    assert fake.job_status("job-analyze-1") == "cancelled"
    assert cancel.reason == "cancel_requested"  # a 409 after the final status is no lost lease


def short_analyze_fake(short_video: Path, **kwargs: Any) -> FakeInternalApi:
    return FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
            )
        ],
        files={"short.mp4": short_video},
        **kwargs,
    )


def test_unconfirmed_clips_registration_is_not_resent_and_the_job_completes(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """/clips applied by the API but its answer lost: resending would duplicate the clips,
    so the worker goes on, warns, and lets the API count the renders it stored."""
    fake = short_analyze_fake(short_video, answer_lost={"clips": 1})
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    assert fake.clips_created == 1 and len(fake.bodies("clips")) == 1
    assert fake.job_status("job-analyze-1") == "completed"
    assert "fail" not in fake.sequence()
    summary = fake.bodies("complete")[0]["summary"]
    assert "clipsRendered" not in summary and summary["candidatesFound"] >= 1
    warnings = [e["message"] for e in fake.bodies("events") if e["level"] == "warning"]
    assert any("confirmar o registro dos clipes" in m for m in warnings)


def test_job_events_are_bounded_to_the_api_limit(settings: Settings, tools: FfmpegTools) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")]
    )
    _loop, api = make_loop(fake, settings, tools)
    with api:
        payload = api.claim(["analyze_source"])
        assert payload is not None
        ctx = JobContext(payload, api, Path(settings.work_dir), CancelToken(), ProgressState())
        ctx.event("warning", "Sinal 'visual' indisponível: " + "x" * 5000, {"signal": "visual"})
    body = fake.bodies("events")[0]
    assert len(body["message"]) == 1000 and body["message"].endswith("…")
    assert fake.events_rejected == 0  # the API would answer 422 above 1000 characters


def test_partial_visual_coverage_is_flagged_not_presented_as_complete(
    short_video: Path, settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    real = pipeline_module.compute_visual_features

    def stopped_at_10s(*args: Any, **kwargs: Any) -> Any:
        features = real(*args, **kwargs)
        features.gaps = [(10.0, features.duration)]
        features.error = "frames decoded up to 10s only: FfmpegTimeout: simulated"
        return features

    monkeypatch.setattr(pipeline_module, "compute_visual_features", stopped_at_10s)
    fake = short_analyze_fake(short_video)
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    assert fake.job_status("job-analyze-1") == "completed"
    visual = fake.bodies("analysis")[0]["summary"]["signals"]["visual"]
    assert visual["available"] is True and visual["coverage"] == pytest.approx(0.5, abs=0.02)
    assert "FfmpegTimeout" in visual["error"]
    partial = [e for e in fake.bodies("events") if "analisado em apenas" in e["message"]]
    assert len(partial) == 1 and partial[0]["data"]["signal"] == "visual"
    assert partial[0]["message"].startswith("Sinal 'visual' analisado em apenas 50% da gravação")
    for cand in fake.bodies("candidates")[0]["candidates"]:
        assert 0.0 <= cand["signals"]["visual"]["coverage"] <= 1.0


class WorkDirWatcher:
    """Analyzer stand-in: ages the job's work directory, then waits for the heartbeat to
    refresh its mtime (the liveness marker of the stale-directory sweep)."""

    def __init__(self) -> None:
        self.refreshed = threading.Event()

    def analyze(self, path: Any, *args: Any, **kwargs: Any) -> Any:
        work_dir = Path(path).parent
        old = time.time() - 10 * 3600
        os.utime(work_dir, (old, old))
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if work_dir.stat().st_mtime > old + 3600:
                self.refreshed.set()
                break
            time.sleep(0.05)
        raise RuntimeError("done watching")


def test_heartbeat_keeps_the_work_dir_fresh(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4", lease_seconds=3
            )
        ],
        files={"short.mp4": short_video},
    )
    watcher = WorkDirWatcher()
    loop, api = make_loop(fake, settings, tools, analyzer=watcher)
    with api:
        assert loop.run_once() is True
    assert watcher.refreshed.is_set()


def _age(*paths: Path, hours: float) -> None:
    old = time.time() - hours * 3600
    for path in paths:
        os.utime(path, (old, old))


def _tree(root: Path) -> list[Path]:
    return [root, *root.rglob("*")]


def _job_dir(work: Path, name: str, *, marker_job_id: str | None = None) -> Path:
    """A job directory as :meth:`JobRunner.run` creates it: marked for its own job id,
    unless ``marker_job_id`` names another job."""
    path = work / name
    path.mkdir(parents=True)
    runner_module._write_work_dir_marker(path, marker_job_id or name, "worker-test")
    return path


class MarkerProbe:
    """Analyzer stand-in: reads the marker of the job's work directory, then crashes."""

    def __init__(self) -> None:
        self.marker: dict[str, Any] | None = None

    def analyze(self, path: Any, *args: Any, **kwargs: Any) -> Any:
        marker = Path(path).parent / WORK_DIR_MARKER
        self.marker = json.loads(marker.read_text(encoding="utf-8"))
        raise RuntimeError("worker crashed")


def test_the_runner_marks_its_work_dir_so_a_crash_leftover_is_swept(
    short_video: Path, settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/short.mp4")],
        files={"short.mp4": short_video},
    )
    probe = MarkerProbe()
    loop, api = make_loop(fake, settings, tools, analyzer=probe)
    # a worker that dies mid-job never reaches the final cleanup of its work directory
    monkeypatch.setattr(JobRunner, "_remove_work_dir", lambda _self, _work_dir: None)
    with api:
        assert loop.run_once() is True
    assert probe.marker is not None
    assert probe.marker["jobId"] == "job-analyze-1"
    assert probe.marker["workerId"] == "worker-test"  # the X-Worker-Id of the claim
    assert datetime.fromisoformat(probe.marker["createdAt"]).tzinfo is not None

    leftover = Path(settings.work_dir) / "job-analyze-1"
    assert (leftover / WORK_DIR_MARKER).is_file()
    assert (leftover / "source.mp4").is_file()
    assert loop.runner.sweep_stale_work_dirs() == []  # still fresh
    _age(*_tree(leftover), hours=7)
    assert loop.runner.sweep_stale_work_dirs() == ["job-analyze-1"]
    assert not leftover.exists()


def test_a_work_dir_that_cannot_be_marked_fails_the_job_retryable(
    settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Unmarked, a directory left by a crash would never be swept: the job does not run in
    it (a worker problem such as a full disk -> worker_error, retryable) and it is removed."""

    def disk_full(work_dir: Path, job_id: str, worker_id: str) -> None:
        raise OSError(28, "No space left on device")

    monkeypatch.setattr(runner_module, "_write_work_dir_marker", disk_full)
    fake = FakeInternalApi(
        payloads=[analyze_payload(download_url=f"{STORAGE_BASE}/storage/download/x.mp4")]
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "worker_error" and fails[0]["retryable"] is True
    assert "processedSeconds" not in fails[0]
    assert "No space left on device" in fails[0]["message"]
    assert "GET:x.mp4" not in fake.sequence()  # nothing is downloaded into it
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_stale_work_dirs_are_swept_when_the_worker_starts(
    settings: Settings, tools: FfmpegTools
) -> None:
    work = Path(settings.work_dir)
    stale = _job_dir(work, "6f1c2c3e-1111-2222-3333-000000000001")
    (stale / "clips").mkdir()
    (stale / "source.mp4").write_bytes(b"x" * 10)
    (stale / "clips" / "clip-01.mp4").write_bytes(b"x")
    fresh = _job_dir(work, "6f1c2c3e-1111-2222-3333-000000000002")
    (fresh / "source.mp4").write_bytes(b"x")
    busy = _job_dir(work, "job-busy")  # old directory, but a file in it was just written
    (busy / "source.mp4.part").write_bytes(b"x")
    foreign = work / "not a job dir"
    foreign.mkdir()
    stray = work / "notes.txt"
    stray.write_text("keep", encoding="utf-8")
    _age(*_tree(stale), hours=7)
    _age(busy, busy / WORK_DIR_MARKER, foreign, stray, hours=7)

    loop, api = make_loop(FakeInternalApi(), settings, tools)
    waits: list[float] = []
    loop._sleep = waits.append
    with api:
        loop._stop.clear()
        threading.Timer(0.2, loop._stop.set).start()
        loop.run_forever()
    assert not stale.exists()
    assert fresh.exists() and busy.exists() and foreign.exists() and stray.exists()


def test_the_sweep_never_removes_what_the_runner_did_not_create(
    settings: Settings, tools: FfmpegTools
) -> None:
    """WORK_DIR pointed by mistake at a shared folder (the ClipForge folder of
    LOCALAPPDATA, /var/lib/clipforge): its plain-named siblings match the job id pattern
    and are old, yet only the directory the runner marked for its own job id goes."""
    work = Path(settings.work_dir)
    siblings = {
        "tools": "ffmpeg/bin/ffmpeg.exe",
        "models": "faster-whisper-base/model.bin",
        "storage": "users/u1/projects/p1/source.mp4",
        "pgdata": "base/5/16384",
        "logs": "api.log",
        # GUID-named but unmarked: a storage folder, or the leftover of an older worker
        "6f1c2c3e-1111-2222-3333-000000000003": "source.mp4",
    }
    for name, relative in siblings.items():
        file = work / name / relative
        file.parent.mkdir(parents=True)
        file.write_bytes(b"data")
    copied = _job_dir(work, "job-copy", marker_job_id="job-original")  # names another job
    dir_marker = work / "job-dir-marker"
    (dir_marker / WORK_DIR_MARKER).mkdir(parents=True)  # a directory, not a file
    garbage = work / "job-garbage"
    garbage.mkdir()
    (garbage / WORK_DIR_MARKER).write_text("not json", encoding="utf-8")
    oversized = work / "job-oversized"
    oversized.mkdir()
    (oversized / WORK_DIR_MARKER).write_text(
        json.dumps({"jobId": "job-oversized", "padding": "x" * 5000}), encoding="utf-8"
    )
    leftover = _job_dir(work, "6f1c2c3e-1111-2222-3333-000000000004")
    (leftover / "source.mp4").write_bytes(b"x")
    _age(*_tree(work), hours=48)

    loop, api = make_loop(FakeInternalApi(), settings, tools)
    api.close()
    assert loop.runner.sweep_stale_work_dirs() == [leftover.name]
    assert not leftover.exists()
    for name, relative in siblings.items():
        assert (work / name / relative).read_bytes() == b"data", name
    assert all(path.exists() for path in (copied, dir_marker, garbage, oversized))


def test_a_tree_too_big_to_walk_is_never_judged_stale(
    settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    work = Path(settings.work_dir)
    big = _job_dir(work, "job-big")
    for index in range(5):
        (big / f"part-{index}.bin").write_bytes(b"x")
    _age(*_tree(big), hours=48)
    loop, api = make_loop(FakeInternalApi(), settings, tools)
    api.close()
    monkeypatch.setattr(runner_module, "WORK_DIR_WALK_LIMIT", 3)
    assert loop.runner.sweep_stale_work_dirs() == []  # a partial walk proves nothing
    assert big.exists()
    monkeypatch.setattr(runner_module, "WORK_DIR_WALK_LIMIT", 100)
    assert loop.runner.sweep_stale_work_dirs() == ["job-big"]
    assert not big.exists()


def test_newest_mtime_of_a_partial_walk_is_infinite(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tree = tmp_path / "tree"
    (tree / "sub").mkdir(parents=True)
    (tree / "sub" / "a.bin").write_bytes(b"x")
    (tree / "b.bin").write_bytes(b"x")
    _age(*_tree(tree), hours=10)
    age = time.time() - runner_module._newest_mtime(tree, limit=100)
    assert 9.9 * 3600 < age < 10.1 * 3600
    assert runner_module._newest_mtime(tree, limit=2) == math.inf  # 3 entries

    real_walk = os.walk

    def unreadable_subdir(top: Any, *args: Any, onerror: Any = None, **kwargs: Any) -> Any:
        yield from real_walk(top, *args, **kwargs)
        if onerror is not None:
            onerror(PermissionError(13, "Access is denied", str(tree / "sub")))

    monkeypatch.setattr(runner_module.os, "walk", unreadable_subdir)
    assert runner_module._newest_mtime(tree, limit=100) == math.inf

    def vanishing_entry(top: Any, *args: Any, **kwargs: Any) -> Any:
        yield str(top), [], ["source.mp4.part"]  # listed, then gone before its stat

    monkeypatch.setattr(runner_module.os, "walk", vanishing_entry)
    assert runner_module._newest_mtime(tree, limit=100) == math.inf


def test_stale_work_dir_sweep_can_be_disabled(settings: Settings, tools: FfmpegTools) -> None:
    work = Path(settings.work_dir)
    stale = _job_dir(work, "job-old")
    _age(*_tree(stale), hours=48)
    disabled = settings.model_copy(update={"work_dir_stale_hours": 0})
    _loop, api = make_loop(FakeInternalApi(), disabled, tools)
    api.close()
    assert JobRunner(disabled, api, tools).sweep_stale_work_dirs() == []
    assert stale.exists()
    assert JobRunner(settings, api, tools).sweep_stale_work_dirs() == ["job-old"]
    assert not stale.exists()


# --------------------------------------------------------------------------------------
# round 3: every candidate rendered, short sources, no speech, LLM titles, orphan sweep
# --------------------------------------------------------------------------------------


class ManyCandidatesAnalyzer:
    """The real analysis, then ``count`` short windows spread over the source in place of
    the detected candidates (to exercise the render loop with many clips)."""

    def __init__(self, inner: Analyzer, count: int) -> None:
        self.inner = inner
        self.count = count

    def analyze(self, *args: Any, **kwargs: Any) -> Any:
        from app.analysis.fusion import rank_candidates

        result = self.inner.analyze(*args, **kwargs)
        template = result.candidates[0]
        many = []
        for i in range(self.count):
            start = round(i * 1.25, 2)
            cand = copy.deepcopy(template)
            cand.start, cand.end, cand.peak = start, start + 1.0, start + 0.5
            cand.score = cand.priority = 90 - i
            many.append(cand)
        rank_candidates(many)
        result.fusion.candidates[:] = many
        return result


class FlakyRenderer(Renderer):
    """Fails the render of one clip (a real ffmpeg error), renders the others."""

    def __init__(self, *args: Any, fail_name: str, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self.fail_name = fail_name
        self.attempts: list[str] = []

    def render(self, request: Any, out_dir: Path, *, cancel: Any = None) -> Any:
        self.attempts.append(request.output_name)
        if request.output_name == self.fail_name:
            raise FfmpegError("ffmpeg exited with 1: simulated encoder failure", stderr="x")
        return super().render(request, out_dir, cancel=cancel)


@pytest.mark.slow
def test_every_candidate_is_rendered_and_one_failure_does_not_stop_the_others(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """U1: autoRenderTop absent -> every candidate (maxCandidates); 15 renders, the 7th
    fails: an error event, the 14 others are uploaded and registered, the job completes."""
    payload = analyze_payload(
        download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
        size_bytes=short_video.stat().st_size,
        settings={"maxCandidates": 15},
    )
    del payload["settings"]["autoRenderTop"]
    fake = FakeInternalApi(payloads=[payload], files={"short.mp4": short_video})
    api = InternalApiClient(
        API_BASE, "test-key", "worker-test", transport=fake.transport(), sleep=lambda _s: None
    )
    renderer = FlakyRenderer(tools, settings.fonts_dir, fail_name="clip-07")
    analyzer = ManyCandidatesAnalyzer(Analyzer(settings, tools), 15)
    runner = JobRunner(settings, api, tools, analyzer=analyzer, renderer=renderer)  # type: ignore[arg-type]
    with api:
        claimed = api.claim(["analyze_source"])
        assert claimed is not None and claimed.settings.render_top == 15
        assert runner.run(claimed) == "completed"
    assert len(renderer.attempts) == 15
    assert len(fake.bodies("candidates")[0]["candidates"]) == 15
    clips = fake.bodies("clips")[0]["clips"]
    assert len(clips) == 14
    assert fake.candidate_ids[6] not in {c["candidateId"] for c in clips}
    errors = [e for e in fake.bodies("events") if e["level"] == "error"]
    assert len(errors) == 1 and errors[0]["message"].startswith("Falha ao renderizar o clipe 7")
    assert errors[0]["data"]["candidateRank"] == 7
    uploads = {name for name in fake.uploads if name.startswith("clip-")}
    assert len(uploads) == 28 and "clip-07.mp4" not in uploads  # 14 renders + thumbnails
    assert fake.bodies("complete")[0]["summary"]["clipsRendered"] == 14
    assert fake.job_status("job-analyze-1") == "completed"


def test_short_source_uses_the_whole_video_and_says_so(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """P2: a 20 s source with minClipSeconds 30 -> limits clamped, whole-video candidate."""
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
                settings={"minClipSeconds": 30, "maxClipSeconds": 60, "targetClipSeconds": 30},
            )
        ],
        files={"short.mp4": short_video},
    )
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    assert fake.job_status("job-analyze-1") == "completed"
    notes = [
        e
        for e in fake.bodies("events")
        if e["message"]
        == "Vídeo mais curto que a duração mínima; usando o vídeo inteiro como limite"
    ]
    assert len(notes) == 1 and notes[0]["level"] == "info"
    assert notes[0]["data"]["minClipSeconds"] == 30
    assert notes[0]["data"]["durationSeconds"] == pytest.approx(20.0, abs=0.2)
    cands = fake.bodies("candidates")[0]["candidates"]
    assert len(cands) == 1
    assert cands[0]["startSeconds"] == 0.0
    assert cands[0]["endSeconds"] == pytest.approx(20.0, abs=0.2)
    assert cands[0]["signals"]["shortSource"]["wholeVideo"] is True
    assert len(fake.bodies("clips")[0]["clips"]) == 1


@pytest.mark.slow
def test_a_source_without_speech_has_no_language_and_no_subtitles(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """P3 through the worker with the real model: the VAD finds no speech in the tones."""
    with_whisper = settings.model_copy(update={"whisper_enabled": True})
    probe_service = TranscriptionService(with_whisper)
    if not probe_service.provider_for("base").available():
        pytest.skip("faster-whisper base not available offline")
    fake = short_analyze_fake(short_video)
    loop, api = make_loop(fake, with_whisper, tools)
    with api:
        assert loop.run_once() is True
    assert fake.job_status("job-analyze-1") == "completed"
    summary = fake.bodies("complete")[0]["summary"]
    assert summary["speechDetected"] is False and summary.get("language") is None
    transcript = summary["signals"]["transcript"]
    assert transcript["available"] is True and transcript["speechDetected"] is False
    assert "language" not in transcript
    cand = fake.bodies("candidates")[0]["candidates"][0]
    assert cand["signals"]["transcript"]["speechDetected"] is False
    assert cand["signals"]["transcript"]["language"] is None
    clip = fake.bodies("clips")[0]["clips"][0]
    assert clip["subtitles"]["enabled"] is True and clip["render"]["hasSubtitles"] is False
    infos = [e["message"] for e in fake.bodies("events") if e["level"] == "info"]
    assert "Nenhuma fala detectada no áudio: sem idioma e sem legendas nos clipes" in infos
    assert not [e for e in fake.bodies("events") if "Sinal 'llm'" in e["message"]]


class TitleClient:
    """A JSON LLM client that titles every excerpt it is asked about."""

    name = "local"
    model = "fake-qwen"

    def __init__(self) -> None:
        self.calls = 0

    def complete_json(self, system: str, user: str, schema: dict[str, Any], **_: Any) -> Any:
        self.calls += 1
        return {key: {"titulo": "Clutch absurdo no fim"} for key in schema["properties"]}


def test_llm_titles_name_the_rendered_clips(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    client = TitleClient()
    analyzer = Analyzer(settings, tools, llm_client=client)  # type: ignore[arg-type]
    original = analyzer.analyze

    def analyze_with_speech(*args: Any, **kwargs: Any) -> Any:
        result = original(*args, **kwargs)
        for cand in result.candidates:
            cand.transcript = "que clutch absurdo no fim do round"
        from app.analysis.llm_assist import run_assist

        result.assist = run_assist(
            client,
            result.candidates,
            args[2],
            budget_seconds=30,
            timeout_seconds=10,
        )
        return result

    analyzer.analyze = analyze_with_speech  # type: ignore[method-assign]
    fake = short_analyze_fake(short_video)
    loop, api = make_loop(fake, settings, tools, analyzer=analyzer)
    with api:
        assert loop.run_once() is True
    clip = fake.bodies("clips")[0]["clips"][0]
    assert clip["title"] == "Clutch absurdo no fim"
    cand = fake.bodies("candidates")[0]["candidates"][0]
    assert cand["signals"]["llm"]["title"] == "Clutch absurdo no fim"
    llm = fake.bodies("complete")[0]["summary"]["signals"]["llm"]
    assert llm["available"] is True and llm["provider"] == "local" and llm["model"] == "fake-qwen"
    assert llm["titles"]["accepted"] >= 1 and llm["rerank"]["status"] == "skipped"


def test_orphans_of_this_worker_are_swept_after_each_job(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """Review finding: besides the start-up sweep (6 h, any worker), after every job the
    directories this worker created are swept with a shorter age — never another worker's,
    never a fresh one (a live job's heartbeat keeps its directory fresh)."""
    work = Path(settings.work_dir)
    mine_old = _job_dir(work, "job-orphan-mine")
    (mine_old / "source.mp4").write_bytes(b"x" * 10)
    other_old = work / "job-orphan-other"
    other_old.mkdir(parents=True)
    runner_module._write_work_dir_marker(other_old, "job-orphan-other", "worker-elsewhere")
    mine_fresh = _job_dir(work, "job-fresh-mine")
    _age(*_tree(mine_old), *_tree(other_old), hours=1)
    fake = short_analyze_fake(short_video)
    loop, api = make_loop(fake, settings, tools)
    with api:
        assert loop.run_once() is True
    assert fake.job_status("job-analyze-1") == "completed"
    assert not mine_old.exists()
    assert other_old.exists() and mine_fresh.exists()
    disabled = settings.model_copy(update={"work_dir_orphan_minutes": 0})
    _age(*_tree(mine_fresh), hours=1)
    _loop, api2 = make_loop(FakeInternalApi(), disabled, tools)
    with api2:
        JobRunner(disabled, api2, tools)._sweep_after_job()
    assert mine_fresh.exists()
    assert JobRunner(settings, api2, tools).sweep_stale_work_dirs(
        60.0, worker_id="worker-test"
    ) == ["job-fresh-mine"]


# --------------------------------------------------------------------------------------
# review of round 3: each clip uploaded and deleted right after its render, disk check
# --------------------------------------------------------------------------------------

MB = 1024 * 1024


class StubRenderer:
    """Writes small stand-in files instead of encoding (the runner's upload/delete logic is
    under test here, not ffmpeg) and records, when each render starts, what the clips
    directory holds and what the storage already received."""

    def __init__(self, fake: FakeInternalApi, *, fail_name: str | None = None) -> None:
        self.fake = fake
        self.fail_name = fail_name
        self.names: list[str] = []
        self.seen: list[list[str]] = []
        self.uploaded: list[set[str]] = []

    def render(self, request: Any, out_dir: Path, *, cancel: Any = None) -> RenderOutput:
        out_dir.mkdir(parents=True, exist_ok=True)
        self.names.append(request.output_name)
        self.seen.append(sorted(p.name for p in out_dir.iterdir()))
        self.uploaded.append(set(self.fake.uploads))
        video = out_dir / f"{request.output_name}.mp4"
        video.write_bytes(b"v" * 4096)
        if request.output_name == self.fail_name:  # a partial output, then the failure
            raise FfmpegError("ffmpeg exited with 1: simulated encoder failure", stderr="x")
        thumbnail = out_dir / f"{request.output_name}.jpg"
        thumbnail.write_bytes(b"j" * 512)
        ass = out_dir / f"{request.output_name}.ass"
        ass.write_text("[Script Info]", encoding="utf-8")
        return RenderOutput(
            path=video,
            thumbnail_path=thumbnail,
            width=480,
            height=852,
            duration=round(request.end - request.start, 3),
            size_bytes=4096,
            has_subtitles=False,
            layout="center",
            ass_path=ass,
        )


def run_many_clips(
    short_video: Path,
    settings: Settings,
    tools: FfmpegTools,
    count: int,
    *,
    storage_errors: dict[str, int] | None = None,
    fail_name: str | None = None,
) -> tuple[str, FakeInternalApi, StubRenderer]:
    """One analyze_source job whose analysis yields ``count`` candidates, all rendered by
    a :class:`StubRenderer`; returns the outcome, the fake API and the renderer."""
    fake = FakeInternalApi(
        payloads=[
            analyze_payload(
                download_url=f"{STORAGE_BASE}/storage/download/short.mp4",
                size_bytes=short_video.stat().st_size,
                settings={"maxCandidates": count, "autoRenderTop": count},
            )
        ],
        files={"short.mp4": short_video},
        storage_errors=storage_errors,
    )
    api = InternalApiClient(
        API_BASE,
        "test-key",
        "worker-test",
        transport=fake.transport(),
        sleep=lambda _s: None,
        max_retries=1,
    )
    renderer = StubRenderer(fake, fail_name=fail_name)
    analyzer = ManyCandidatesAnalyzer(Analyzer(settings, tools), count)
    runner = JobRunner(settings, api, tools, analyzer=analyzer, renderer=renderer)  # type: ignore[arg-type]
    with api:
        claimed = api.claim(["analyze_source"])
        assert claimed is not None
        outcome = runner.run(claimed)
    return outcome, fake, renderer


def _names(clips: list[dict[str, Any]], field: str) -> list[str]:
    def get(clip: dict[str, Any]) -> str:
        value = clip["render"]["storageKey"] if field == "storageKey" else clip[field]
        return str(value).rsplit("/", 1)[-1]

    return [get(c) for c in clips]


def test_each_clip_is_uploaded_and_deleted_before_the_next_render(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """Review finding: rendering every clip (up to 50) kept all renders on disk until one
    final upload. Each clip and its thumbnail now go to the storage right after the render
    and are deleted, so the clips directory is empty whenever a render starts."""
    outcome, fake, renderer = run_many_clips(short_video, settings, tools, 3)
    assert outcome == "completed"
    assert renderer.names == ["clip-01", "clip-02", "clip-03"]
    assert renderer.seen == [[], [], []]  # mp4, jpg and ass of the previous clip are gone
    assert renderer.uploaded[0] == set()
    assert {"clip-01.mp4", "clip-01.jpg"} <= renderer.uploaded[1]
    assert {"clip-02.mp4", "clip-02.jpg"} <= renderer.uploaded[2]
    assert "clip-02.mp4" not in renderer.uploaded[1]
    assert [[f["fileName"] for f in b["files"]] for b in fake.bodies("upload-urls")] == [
        ["clip-01.mp4", "clip-01.jpg"],
        ["clip-02.mp4", "clip-02.jpg"],
        ["clip-03.mp4", "clip-03.jpg"],
        ["transcript.json", "signals.json"],
    ]
    seq = fake.sequence()
    assert (
        seq.index("PUT:clip-03.jpg")
        < seq.index("status:finalizing")
        < seq.index("PUT:signals.json")
        < seq.index("clips")
    ), seq
    clips = fake.bodies("clips")[0]["clips"]
    assert [c["candidateId"] for c in clips] == fake.candidate_ids[:3]
    assert _names(clips, "storageKey") == ["clip-01.mp4", "clip-02.mp4", "clip-03.mp4"]
    assert _names(clips, "thumbnailKey") == ["clip-01.jpg", "clip-02.jpg", "clip-03.jpg"]
    assert all(c["render"]["sizeBytes"] == 4096 for c in clips)
    assert fake.bodies("complete")[0]["summary"]["clipsRendered"] == 3
    assert not (Path(settings.work_dir) / "job-analyze-1").exists()


def test_a_failed_render_leaves_no_partial_file_behind(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    outcome, fake, renderer = run_many_clips(short_video, settings, tools, 2, fail_name="clip-01")
    assert outcome == "completed"
    assert renderer.seen == [[], []]  # the partial clip-01.mp4 was deleted
    errors = [e for e in fake.bodies("events") if e["level"] == "error"]
    assert len(errors) == 1 and errors[0]["message"].startswith("Falha ao renderizar o clipe 1")
    assert _names(fake.bodies("clips")[0]["clips"], "storageKey") == ["clip-02.mp4"]


def test_one_failed_clip_upload_drops_only_that_clip(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """Once the storage accepted a clip of the job, a clip whose upload fails (after the
    client's retries) is dropped with an error event like a failed render: the clips
    already uploaded and the next ones are kept, the job completes."""
    outcome, fake, renderer = run_many_clips(
        short_video, settings, tools, 3, storage_errors={"clip-02.mp4": 500}
    )
    assert outcome == "completed"
    assert "fail" not in fake.sequence()
    assert renderer.seen == [[], [], []]  # the files of the clip that failed are gone too
    errors = [e for e in fake.bodies("events") if e["level"] == "error"]
    assert len(errors) == 1
    assert errors[0]["message"].startswith("Falha ao enviar o clipe 2")
    assert "HTTP 500" in errors[0]["message"] and errors[0]["data"]["candidateRank"] == 2
    clips = fake.bodies("clips")[0]["clips"]
    assert [c["candidateId"] for c in clips] == [fake.candidate_ids[0], fake.candidate_ids[2]]
    assert _names(clips, "storageKey") == ["clip-01.mp4", "clip-03.mp4"]
    assert fake.bodies("complete")[0]["summary"]["clipsRendered"] == 2


def test_two_failed_clip_uploads_in_a_row_fail_the_job_as_transfer_failed(
    short_video: Path, settings: Settings, tools: FfmpegTools
) -> None:
    """Two clip uploads failing in a row mean the storage is unavailable: the job fails
    ``transfer_failed`` (retryable, nothing charged) without rendering the rest."""
    outcome, fake, renderer = run_many_clips(
        short_video,
        settings,
        tools,
        4,
        storage_errors={"clip-02.mp4": 500, "clip-03.mp4": 500},
    )
    assert outcome == "failed"
    assert renderer.names == ["clip-01", "clip-02", "clip-03"]  # clip-04 never rendered
    fails = fake.bodies("fail")
    assert len(fails) == 1
    assert fails[0]["code"] == "transfer_failed" and fails[0]["retryable"] is True
    assert "processedSeconds" not in fails[0]
    assert "clip-03.mp4" in fails[0]["message"] and "HTTP 500" in fails[0]["message"]
    seq = fake.sequence()
    assert "clips" not in seq and "complete" not in seq and "status:finalizing" not in seq


def test_low_disk_space_stops_the_renders_with_a_warning(
    short_video: Path, settings: Settings, tools: FfmpegTools, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Below ``RENDER_MIN_FREE_MB`` on the WORK_DIR volume no further clip is rendered:
    a warning event says how many were skipped; the analysis and the clips already
    uploaded are delivered and the job completes."""
    free = iter([10_000 * MB, 120 * MB])
    checked: list[Path] = []

    def disk_free(path: Path) -> int:
        checked.append(path)
        return next(free)

    monkeypatch.setattr(runner_module, "_disk_free_bytes", disk_free)
    low = settings.model_copy(update={"render_min_free_mb": 500})
    outcome, fake, renderer = run_many_clips(short_video, low, tools, 3)
    assert outcome == "completed"
    assert renderer.names == ["clip-01"]
    assert checked == [Path(low.work_dir) / "job-analyze-1"] * 2
    warnings = [e for e in fake.bodies("events") if e["level"] == "warning"]
    disk = [e for e in warnings if "Espaço em disco insuficiente" in e["message"]]
    assert len(disk) == 1
    assert "120 MB livres, mínimo 500 MB" in disk[0]["message"]
    assert "2 de 3 clipes não foram gerados" in disk[0]["message"]
    assert disk[0]["data"] == {
        "freeMb": 120,
        "minFreeMb": 500,
        "skipped": 2,
        "candidateRanks": [2, 3],
    }
    assert _names(fake.bodies("clips")[0]["clips"], "storageKey") == ["clip-01.mp4"]
    assert fake.bodies("complete")[0]["summary"]["clipsRendered"] == 1


def test_disk_free_bytes_reads_the_volume_and_tolerates_a_missing_path(tmp_path: Path) -> None:
    free = runner_module._disk_free_bytes(tmp_path)
    assert isinstance(free, int) and free > 0
    assert runner_module._disk_free_bytes(tmp_path / "missing" / "dir") is None
