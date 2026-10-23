from __future__ import annotations

import pytest
from pydantic import ValidationError

from app.config import settings_for_tests
from app.models import AnalysisSettings, CandidateInput, JobPayload

ANALYZE_PAYLOAD = {
    "jobId": "j1",
    "kind": "analyze_source",
    "attempt": 1,
    "userId": "u",
    "projectId": "p",
    "leaseSeconds": 180,
    "source": {
        "assetId": "a",
        "downloadUrl": "https://x/signed",
        "fileName": "live.mp4",
        "contentType": "video/mp4",
        "sizeBytes": 1234,
        "durationSeconds": None,
    },
    "settings": {
        "minClipSeconds": 15,
        "maxClipSeconds": 60,
        "targetClipSeconds": 30,
        "sensitivity": 50,
        "maxCandidates": 20,
        "autoRenderTop": 5,
        "aspectRatio": "9:16",
        "subtitles": {"enabled": True, "style": "bold", "position": "bottom"},
        "language": "auto",
        "framing": {"mode": "auto"},
        "title": None,
    },
    "limits": {
        "maxRenderHeight": 1080,
        "maxSourceDurationSeconds": 21600,
        "remainingProcessingSeconds": 3600,
        "transcriptionModel": "base",
        "allowWebcamLayout": True,
    },
    "unknownFutureField": {"ignored": True},
}

RENDER_PAYLOAD = {
    "jobId": "j2",
    "kind": "render_clip",
    "attempt": 1,
    "userId": "u",
    "projectId": "p",
    "leaseSeconds": 180,
    "source": {
        "assetId": "a",
        "downloadUrl": "https://x",
        "fileName": "f",
        "contentType": "c",
        "sizeBytes": 1,
        "durationSeconds": 3600.0,
    },
    "clip": {
        "clipId": "c",
        "renderId": "r",
        "version": 2,
        "title": "t",
        "startSeconds": 120.5,
        "endSeconds": 150.0,
        "aspectRatio": "9:16",
        "subtitles": {"enabled": True, "style": "bold", "position": "bottom"},
        "framing": {"mode": "auto", "focusX": 0.5, "focusY": 0.5, "webcamRegion": None},
    },
    "analysis": {
        "transcriptUrl": None,
        "webcamRegion": {"x": 0.75, "y": 0.70, "w": 0.25, "h": 0.30},
    },
    "limits": {"maxRenderHeight": 1080, "allowWebcamLayout": True},
}


def test_documented_payloads_parse() -> None:
    job = JobPayload.model_validate(ANALYZE_PAYLOAD)
    assert job.kind == "analyze_source" and job.lease_seconds == 180
    assert job.settings.subtitles.style == "bold" and job.settings.whisper_language is None
    assert job.limits.transcription_model == "base"
    render = JobPayload.model_validate(RENDER_PAYLOAD)
    assert render.clip is not None and render.clip.version == 2
    assert render.analysis is not None and render.analysis.webcam_region is not None
    assert render.analysis.webcam_region.w == 0.25
    assert render.settings.aspect_ratio == "9:16"  # defaults when absent


def test_settings_are_normalised() -> None:
    s = AnalysisSettings(
        min_clip_seconds=50,
        max_clip_seconds=20,
        target_clip_seconds=5,
        sensitivity=250,
        max_candidates=0,
        language="PT-br",
        title="  x  ",
    )
    assert s.min_clip_seconds == 50 and s.max_clip_seconds == 52
    assert s.target_clip_seconds == 50
    assert s.sensitivity == 100 and s.max_candidates == 1
    assert s.language == "pt-br" and s.whisper_language == "pt"
    assert s.title == "x"


def test_candidate_input_bounds_and_camel_case() -> None:
    cand = CandidateInput(
        rank=1,
        start_seconds=1,
        end_seconds=5,
        score=88,
        category="hype",
        reasoning="r",
        transcript="",
        signals={},
    )
    assert cand.to_api()["startSeconds"] == 1
    with pytest.raises(ValidationError):
        CandidateInput(
            rank=1, start_seconds=1, end_seconds=5, score=101, category="hype", reasoning="r"
        )
    with pytest.raises(ValidationError):
        CandidateInput(
            rank=1, start_seconds=1, end_seconds=5, score=10, category="viral", reasoning="r"
        )


def test_settings_expand_environment_variables(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("CLIPFORGE_TEST_BASE", "C:/base")
    s = settings_for_tests(
        ffmpeg_path="%CLIPFORGE_TEST_BASE%/ffmpeg.exe",
        work_dir="$CLIPFORGE_TEST_BASE/work",
        clipforge_api_base_url="http://api/",
    )
    assert s.ffmpeg_path == "C:/base/ffmpeg.exe"
    assert s.work_dir == "C:/base/work"
    assert s.clipforge_api_base_url == "http://api"
    assert s.kinds == ["analyze_source", "render_clip", "live_segment"]
    assert settings_for_tests(job_kinds=" render_clip , ").kinds == ["render_clip"]
    assert s.llm_enabled is False
    anth = settings_for_tests(llm_provider="anthropic", anthropic_api_key="k")
    assert anth.llm_enabled is True


def test_round3_settings_default_and_parse_tolerantly() -> None:
    defaults = AnalysisSettings()
    assert defaults.focus_categories == [] and defaults.custom_keywords == []
    assert defaults.instructions is None
    # autoRenderTop absent: every candidate ("todos os momentos encontrados")
    assert defaults.auto_render_top == defaults.max_candidates == 20
    assert AnalysisSettings(max_candidates=15).render_top == 15
    assert AnalysisSettings(max_candidates=15, auto_render_top=3).render_top == 3
    assert AnalysisSettings(auto_render_top=0).render_top == 0

    parsed = AnalysisSettings.model_validate(
        {
            "focusCategories": ["Funny", "viral", "funny", " gameplay ", 7, None, ["x"]],
            "customKeywords": [
                "  clutch ",
                "CLUTCH",
                "Clútch",
                "",
                "!!!",
                "que   isso",
                "x" * 60,
                *[f"palavra{i}" for i in range(30)],
            ],
            "instructions": "  quero os momentos engraçados  " + "a" * 600,
        }
    )
    assert parsed.focus_categories == ["funny", "gameplay"]  # unknown ones dropped
    assert parsed.custom_keywords[:3] == ["clutch", "que isso", "x" * 40]
    assert len(parsed.custom_keywords) == 20  # at most 20, accent/case duplicates dropped
    assert parsed.instructions is not None and len(parsed.instructions) == 500
    assert parsed.instructions.startswith("quero os momentos engraçados")

    loose = AnalysisSettings.model_validate(
        {"focusCategories": "funny, hype", "customKeywords": "gg, ez;ace", "instructions": " "}
    )
    assert loose.focus_categories == ["funny", "hype"]
    assert loose.custom_keywords == ["gg", "ez", "ace"]
    assert loose.instructions is None
    nulls = AnalysisSettings.model_validate(
        {"focusCategories": None, "customKeywords": None, "instructions": None}
    )
    assert nulls.focus_categories == [] and nulls.custom_keywords == []
    job = JobPayload.model_validate(
        {
            **ANALYZE_PAYLOAD,
            "settings": {**ANALYZE_PAYLOAD["settings"], "focusCategories": ["quote"]},
        }
    )
    assert job.settings.focus_categories == ["quote"] and job.settings.auto_render_top == 5


def test_summary_omits_unknown_speech_flag() -> None:
    from app.models import AnalysisSummary

    assert "speechDetected" not in AnalysisSummary().to_api()
    assert AnalysisSummary(speech_detected=False).to_api()["speechDetected"] is False


def test_llm_and_whisper_settings() -> None:
    s = settings_for_tests()
    assert s.whisper_model == "small"  # the cap; the plan picks the model per job
    assert s.llm_provider == "none" and s.llm_enabled is False and s.llm_model_label is None
    local = settings_for_tests(llm_provider=" Local ", llm_base_url="http://127.0.0.1:8090/")
    assert local.llm_provider == "local" and local.llm_enabled is True
    assert local.llm_base_url == "http://127.0.0.1:8090"
    assert local.llm_timeout_seconds == 60 and local.llm_budget_seconds == 100
    assert settings_for_tests(llm_provider="gemini").llm_enabled is False  # no key
    gemini = settings_for_tests(llm_provider="gemini", gemini_api_key="k")
    assert gemini.llm_enabled is True and gemini.llm_model_label == "gemini-2.5-flash"
    with pytest.raises(ValidationError):
        settings_for_tests(llm_provider="openai")
    assert s.whisper_ignored_languages == ("la", "sa")
    assert s.work_dir_orphan_minutes == 15


# --------------------------------------------------------------------------------------
# live capture payloads (LIVE_CAPTURE.md section 3)
# --------------------------------------------------------------------------------------

LIVE_PAYLOAD = {
    "jobId": "j3",
    "kind": "live_segment",
    "attempt": 1,
    "userId": "u",
    "projectId": "p",
    "leaseSeconds": 180,
    "settings": ANALYZE_PAYLOAD["settings"],
    "limits": ANALYZE_PAYLOAD["limits"],
    "live": {
        "sessionId": "s",
        "segmentId": "g3",
        "sequence": 3,
        "startOffsetSeconds": 180.0,
        "durationSeconds": 60.0,
        "final": False,
        "segmentSeconds": 60,
    },
    "source": {
        "assetId": "a",
        "downloadUrl": "https://x/seg_00003.mkv",
        "fileName": "seg_00003.mkv",
        "contentType": "video/x-matroska",
        "sizeBytes": 123,
        "durationSeconds": 60.0,
    },
    "previous": {
        "segmentId": "g2",
        "sequence": 2,
        "downloadUrl": "https://x/seg_00002.mkv",
        "startOffsetSeconds": 120.0,
        "durationSeconds": 60.0,
    },
    "context": {"compositeSample": [1.2, 0.8], "language": "pt"},
}


def test_live_segment_payload_parses() -> None:
    job = JobPayload.model_validate(LIVE_PAYLOAD)
    assert job.kind == "live_segment"
    assert job.live is not None and job.live.sequence == 3 and job.live.final is False
    assert job.live.start_offset_seconds == 180.0 and job.live.duration_seconds == 60.0
    assert job.live.session_id == "s" and job.live.segment_seconds == 60
    assert job.media.file_name == "seg_00003.mkv"
    assert job.previous is not None and job.previous.sequence == 2
    assert job.previous.start_offset_seconds == 120.0 and job.previous.duration_seconds == 60
    assert job.context is not None and job.context.composite_sample == [1.2, 0.8]
    assert job.context.language == "pt" and job.context.committed_until is None
    assert job.segments == []

    first = JobPayload.model_validate(
        {
            **LIVE_PAYLOAD,
            "live": {**LIVE_PAYLOAD["live"], "sequence": 0},
            "previous": None,
            "context": None,
        }
    )
    assert first.previous is None and first.context is None


def test_live_context_is_parsed_tolerantly() -> None:
    from app.models import LiveContext

    ctx = LiveContext.model_validate(
        {
            "compositeSample": [1, 2.5, "x", None, True, float("inf")],
            "language": "PT-br",
            "sequence": 2,
            "committedUntil": 172.25,
            "somethingNew": {"ignored": True},
        }
    )
    assert ctx.composite_sample == [1.0, 2.5]
    assert ctx.language == "pt" and ctx.sequence == 2 and ctx.committed_until == 172.25
    assert LiveContext.model_validate({"language": "auto"}).language is None
    assert LiveContext.model_validate({"language": ""}).language is None
    assert LiveContext.model_validate({"compositeSample": None}).composite_sample == []
    assert LiveContext.model_validate({"committedUntil": -1}).committed_until is None
    # an unknown or bad duration is "unknown", never a validation error
    from app.models import LiveSegmentInfo

    info = LiveSegmentInfo.model_validate(
        {"sequence": 1, "startOffsetSeconds": 60, "durationSeconds": 0, "final": None}
    )
    assert info.duration_seconds is None and info.final is False


@pytest.mark.parametrize(
    ("change", "message"),
    [
        ({"live": None}, "'live' is required"),
        ({"source": None}, "'source' is required"),
        ({"live": {"startOffsetSeconds": 0.0}}, "live.sequence"),
        ({"live": {"sequence": -1, "startOffsetSeconds": 0.0}}, "live.sequence"),
    ],
)
def test_live_segment_payload_without_its_fields_is_invalid(
    change: dict[str, object], message: str
) -> None:
    with pytest.raises(ValidationError) as info:
        JobPayload.model_validate({**LIVE_PAYLOAD, **change})
    assert message in str(info.value) or message.replace(".", "\n  ") in str(info.value)


def test_render_clip_of_a_live_project_carries_segments() -> None:
    payload = {key: value for key, value in RENDER_PAYLOAD.items() if key != "source"}
    payload["segments"] = [
        {
            "downloadUrl": "https://x/seg_00001.mkv",
            "startOffsetSeconds": 60.0,
            "durationSeconds": 60.0,
            "fileName": "seg_00001.mkv",
        },
        {
            "downloadUrl": "https://x/seg_00002.mkv",
            "startOffsetSeconds": 120.0,
            "durationSeconds": 60.0,
            "fileName": "seg_00002.mkv",
        },
    ]
    job = JobPayload.model_validate(payload)
    assert job.source is None and len(job.segments) == 2
    assert job.segments[1].start_offset_seconds == 120.0
    assert job.segments[0].file_name == "seg_00001.mkv"
    assert JobPayload.model_validate({**payload, "source": None}).segments
    # an uploaded video's render keeps the single source
    assert JobPayload.model_validate(RENDER_PAYLOAD).segments == []
    with pytest.raises(ValidationError) as info:
        JobPayload.model_validate({**payload, "segments": []})
    assert "'source' or 'segments'" in str(info.value)
    with pytest.raises(ValidationError):
        JobPayload.model_validate({**payload, "segments": None})
    with pytest.raises(ValidationError):  # a segment without its place in the session
        JobPayload.model_validate({**payload, "segments": [{"downloadUrl": "https://x"}]})


def test_summary_carries_the_live_context_only_for_live_segments() -> None:
    from app.models import AnalysisSummary

    assert "liveContext" not in AnalysisSummary().to_api()
    context = {"compositeSample": [0.1], "language": "pt", "sequence": 0, "committedUntil": 52.0}
    assert AnalysisSummary(live_context=context).to_api()["liveContext"] == context


def test_live_segment_is_claimed_by_default() -> None:
    assert settings_for_tests().kinds == ["analyze_source", "render_clip", "live_segment"]
    live = settings_for_tests()
    assert live.live_llm_budget_seconds == 20 and live.live_webcam_samples == 8
