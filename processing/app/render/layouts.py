"""Layout filter graphs built from validated numbers only (never user strings).

| aspect | layout |
|---|---|
| 16:9 | letterbox/pad to 1920x1080 or 1280x720 |
| 9:16 center | crop ``ih*9/16 x ih`` at ``focusX`` then scale to 1080x1920 (or 720x1280) |
| 9:16 webcam-split | top: webcam region crop; bottom: gameplay center crop; ``vstack`` |
| 1:1 | square center crop at ``focusX`` then scale to 1080x1080 (or 720x720) |

``maxRenderHeight`` is interpreted as the resolution class (shorter side): >= 1080 ->
1080p outputs, otherwise 720p (480p below 720).
"""

from __future__ import annotations

from dataclasses import dataclass, field

from app.media.ffmpeg import fmt_num
from app.models import WebcamRegion

MIN_REGION_FRACTION = 0.05
MIN_REGION_PIXELS = 32


@dataclass(slots=True)
class LayoutPlan:
    aspect: str
    out_w: int
    out_h: int
    filter_complex: str
    output_label: str
    layout: str  # letterbox | center | webcam-split
    warnings: list[str] = field(default_factory=list)
    webcam_region: WebcamRegion | None = None


def resolution_class(max_render_height: int | None) -> int:
    h = int(max_render_height or 1080)
    if h >= 1080:
        return 1080
    if h >= 720:
        return 720
    return 480


def even(value: float) -> int:
    """Even size (at least 2)."""
    return max(2, int(value) // 2 * 2)


def even_offset(value: float) -> int:
    """Even, non-negative offset."""
    return max(0, int(value) // 2 * 2)


def target_size(aspect: str, cls: int) -> tuple[int, int]:
    if aspect == "16:9":
        return even(cls * 16 / 9), cls
    if aspect == "9:16":
        return cls, even(cls * 16 / 9)
    if aspect == "1:1":
        return cls, cls
    raise ValueError(f"unsupported aspect ratio {aspect!r}")


def validate_region(
    region: WebcamRegion | None, src_w: int, src_h: int
) -> tuple[int, int, int, int] | None:
    """Return the region in even source pixels, or ``None`` when it is unusable."""
    if region is None:
        return None
    try:
        x, y, w, h = float(region.x), float(region.y), float(region.w), float(region.h)
    except TypeError, ValueError:
        return None
    values = (x, y, w, h)
    if any(v != v or v in (float("inf"), float("-inf")) for v in values):
        return None
    if w < MIN_REGION_FRACTION or h < MIN_REGION_FRACTION or x < 0 or y < 0:
        return None
    if x + w > 1.0005 or y + h > 1.0005:
        return None
    px = min(even_offset(x * src_w), src_w - 2)
    py = min(even_offset(y * src_h), src_h - 2)
    pw = even(min(w * src_w, src_w - px))
    ph = even(min(h * src_h, src_h - py))
    if pw < MIN_REGION_PIXELS or ph < MIN_REGION_PIXELS:
        return None
    return px, py, pw, ph


def _center_crop(
    src_w: int, src_h: int, out_w: int, out_h: int, focus_x: float
) -> tuple[int, int, int, int]:
    """Largest crop of the source with the output aspect, horizontally placed by ``focus_x``."""
    target_ratio = out_w / out_h
    if src_w / src_h >= target_ratio:
        crop_h = even(src_h)
        crop_w = even(min(src_w, src_h * target_ratio))
    else:
        crop_w = even(src_w)
        crop_h = even(min(src_h, src_w / target_ratio))
    crop_x = even_offset((src_w - crop_w) * focus_x)
    crop_y = even_offset((src_h - crop_h) / 2.0)
    crop_x = max(0, min(crop_x, src_w - crop_w))
    crop_y = max(0, min(crop_y, src_h - crop_h))
    return crop_w, crop_h, crop_x, crop_y


def build_layout(
    aspect: str,
    src_w: int,
    src_h: int,
    max_render_height: int | None,
    *,
    focus_x: float = 0.5,
    mode: str = "auto",
    webcam_region: WebcamRegion | None = None,
    allow_webcam: bool = True,
) -> LayoutPlan:
    src_w, src_h = int(src_w), int(src_h)
    if src_w < 2 or src_h < 2:
        raise ValueError("invalid source dimensions")
    focus_x = min(1.0, max(0.0, float(focus_x)))
    cls = resolution_class(max_render_height)
    out_w, out_h = target_size(aspect, cls)
    warnings: list[str] = []

    if aspect == "16:9":
        chain = (
            f"[0:v]scale={out_w}:{out_h}:force_original_aspect_ratio=decrease:force_divisible_by=2,"
            f"pad={out_w}:{out_h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[vout]"
        )
        return LayoutPlan(aspect, out_w, out_h, chain, "[vout]", "letterbox", warnings)

    if aspect == "1:1":
        cw, ch, cx, cy = _center_crop(src_w, src_h, out_w, out_h, focus_x)
        chain = f"[0:v]crop={cw}:{ch}:{cx}:{cy},scale={out_w}:{out_h},setsar=1[vout]"
        return LayoutPlan(aspect, out_w, out_h, chain, "[vout]", "center", warnings)

    if aspect != "9:16":
        raise ValueError(f"unsupported aspect ratio {aspect!r}")

    wants_webcam = mode in ("auto", "webcam-split")
    region_px = validate_region(webcam_region, src_w, src_h) if wants_webcam else None
    if wants_webcam and region_px is None and (webcam_region is not None or mode == "webcam-split"):
        warnings.append("webcam region missing or invalid; falling back to center crop")
    if wants_webcam and region_px is not None and not allow_webcam:
        warnings.append("webcam layout not allowed by the plan; using center crop")
        region_px = None

    if region_px is not None:
        rx, ry, rw, rh = region_px
        top_h = even(min(out_h * 0.5, max(out_h * 0.25, out_w * rh / rw)))
        bottom_h = out_h - top_h
        cw, ch, cx, cy = _center_crop(src_w, src_h, out_w, bottom_h, focus_x)
        chain = (
            f"[0:v]split=2[cam][game];"
            f"[cam]crop={rw}:{rh}:{rx}:{ry},"
            f"scale={out_w}:{top_h}:force_original_aspect_ratio=decrease:force_divisible_by=2,"
            f"pad={out_w}:{top_h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[top];"
            f"[game]crop={cw}:{ch}:{cx}:{cy},scale={out_w}:{bottom_h},setsar=1[bot];"
            f"[top][bot]vstack=inputs=2[vout]"
        )
        return LayoutPlan(
            aspect, out_w, out_h, chain, "[vout]", "webcam-split", warnings, webcam_region
        )

    cw, ch, cx, cy = _center_crop(src_w, src_h, out_w, out_h, focus_x)
    chain = f"[0:v]crop={cw}:{ch}:{cx}:{cy},scale={out_w}:{out_h},setsar=1[vout]"
    return LayoutPlan(aspect, out_w, out_h, chain, "[vout]", "center", warnings)


def describe(plan: LayoutPlan) -> str:
    return f"{plan.layout} {plan.out_w}x{plan.out_h} ({fmt_num(plan.out_w / plan.out_h)})"
