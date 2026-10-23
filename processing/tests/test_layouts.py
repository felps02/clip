from __future__ import annotations

import re

import pytest

from app.models import WebcamRegion
from app.render.layouts import build_layout, resolution_class, target_size, validate_region

SAFE_FILTER = re.compile(r"^[A-Za-z0-9\[\]:=,;()/\-+*._]+$")


def numbers_in(filter_text: str) -> list[str]:
    return re.findall(r"(?<![a-z_])-?\d+(?:\.\d+)?", filter_text)


def test_resolution_classes() -> None:
    assert resolution_class(1080) == 1080
    assert resolution_class(2160) == 1080
    assert resolution_class(720) == 720
    assert resolution_class(900) == 720
    assert resolution_class(480) == 480
    assert target_size("16:9", 1080) == (1920, 1080)
    assert target_size("9:16", 720) == (720, 1280)
    assert target_size("1:1", 1080) == (1080, 1080)


def test_letterbox_16_9() -> None:
    plan = build_layout("16:9", 1280, 720, 1080)
    assert (plan.out_w, plan.out_h, plan.layout) == (1920, 1080, "letterbox")
    assert "scale=1920:1080:force_original_aspect_ratio=decrease" in plan.filter_complex
    assert "pad=1920:1080:" in plan.filter_complex
    assert plan.filter_complex.endswith("[vout]")
    assert SAFE_FILTER.match(plan.filter_complex)
    plan = build_layout("16:9", 1920, 1080, 720)
    assert (plan.out_w, plan.out_h) == (1280, 720)


def test_center_crop_9_16_uses_focus_x() -> None:
    plan = build_layout("9:16", 1920, 1080, 1080, focus_x=0.5)
    assert (plan.out_w, plan.out_h, plan.layout) == (1080, 1920, "center")
    assert "crop=606:1080:656:0,scale=1080:1920" in plan.filter_complex
    assert "crop=606:1080:0:0" in build_layout("9:16", 1920, 1080, 1080, focus_x=0.0).filter_complex
    assert (
        "crop=606:1080:1314:0" in build_layout("9:16", 1920, 1080, 1080, focus_x=1.0).filter_complex
    )
    assert SAFE_FILTER.match(plan.filter_complex)
    assert all(float(n) == int(float(n)) for n in numbers_in(plan.filter_complex))


def test_square_crop() -> None:
    plan = build_layout("1:1", 1920, 1080, 1080, focus_x=0.5)
    assert (plan.out_w, plan.out_h) == (1080, 1080)
    assert "crop=1080:1080:420:0,scale=1080:1080" in plan.filter_complex


def test_portrait_source_keeps_full_width() -> None:
    plan = build_layout("9:16", 1080, 1920, 720)
    assert "crop=1080:1920:0:0" in plan.filter_complex
    plan = build_layout("9:16", 720, 1600, 720)
    assert "crop=720:1280:0:160" in plan.filter_complex


def test_webcam_split_builds_vstack() -> None:
    region = WebcamRegion(x=0.72, y=0.68, w=0.26, h=0.3)
    plan = build_layout("9:16", 1920, 1080, 1080, mode="auto", webcam_region=region)
    assert plan.layout == "webcam-split"
    assert "split=2[cam][game]" in plan.filter_complex
    assert "vstack=inputs=2[vout]" in plan.filter_complex
    assert "crop=498:324:1382:734" in plan.filter_complex
    assert (plan.out_w, plan.out_h) == (1080, 1920)
    assert plan.warnings == []
    assert SAFE_FILTER.match(plan.filter_complex)
    heights = re.findall(r"scale=1080:(\d+)", plan.filter_complex)
    assert sum(int(h) for h in heights) == 1920
    assert all(int(h) % 2 == 0 for h in heights)


@pytest.mark.parametrize(
    "region",
    [
        WebcamRegion(x=0.9, y=0.9, w=0.5, h=0.5),  # outside the frame
        WebcamRegion(x=0.1, y=0.1, w=0.01, h=0.2),  # too thin
        WebcamRegion(x=-0.1, y=0.1, w=0.3, h=0.3),  # negative origin
        WebcamRegion(x=float("nan"), y=0.1, w=0.3, h=0.3),  # not a number
    ],
)
def test_invalid_webcam_region_falls_back_to_center(region: WebcamRegion) -> None:
    assert validate_region(region, 1920, 1080) is None
    plan = build_layout("9:16", 1920, 1080, 1080, mode="webcam-split", webcam_region=region)
    assert plan.layout == "center"
    assert plan.warnings
    assert "vstack" not in plan.filter_complex
    plan_auto = build_layout("9:16", 1920, 1080, 1080, mode="auto", webcam_region=region)
    assert plan_auto.layout == "center"


def test_webcam_split_requires_plan_permission_and_region() -> None:
    region = WebcamRegion(x=0.7, y=0.65, w=0.3, h=0.35)
    plan = build_layout(
        "9:16", 1920, 1080, 1080, mode="auto", webcam_region=region, allow_webcam=False
    )
    assert plan.layout == "center" and plan.warnings
    plan = build_layout("9:16", 1920, 1080, 1080, mode="webcam-split", webcam_region=None)
    assert plan.layout == "center" and plan.warnings
    plan = build_layout("9:16", 1920, 1080, 1080, mode="center", webcam_region=region)
    assert plan.layout == "center" and not plan.warnings


def test_unsupported_values_raise() -> None:
    with pytest.raises(ValueError):
        build_layout("4:3", 1920, 1080, 1080)
    with pytest.raises(ValueError):
        build_layout("16:9", 0, 1080, 1080)
