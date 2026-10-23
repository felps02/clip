"""Clip rendering with ffmpeg (accurate trim + layout + burned ASS subtitles + loudnorm).

Encoding: ``libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -profile:v high -level 4.1``,
``aac 160k 48 kHz``, ``+faststart``. The output height is capped by
``limits.maxRenderHeight`` (resolution class, see :mod:`app.render.layouts`). A JPEG
thumbnail is taken at the midpoint of the rendered clip.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

from app.analysis.transcription import Word
from app.logging_setup import get_logger
from app.media.ffmpeg import FfmpegError, FfmpegTools, fmt_num, timeout_for
from app.media.probe import probe_file
from app.models import WebcamRegion
from app.render.layouts import LayoutPlan, build_layout
from app.render.subtitles import build_ass, chunk_words, subtitles_filter, write_ass
from app.worker.errors import CancelToken

log = get_logger("renderer")

LOUDNORM = "loudnorm=I=-16:TP=-1.5:LRA=11"
RENDER_TIMEOUT_FACTOR = 30.0
RENDER_TIMEOUT_MIN = 240.0


@dataclass(slots=True)
class RenderRequest:
    source: Path
    src_w: int
    src_h: int
    start: float
    end: float
    aspect: str = "9:16"
    max_render_height: int = 1080
    framing_mode: str = "auto"
    focus_x: float = 0.5
    webcam_region: WebcamRegion | None = None
    allow_webcam: bool = True
    subtitles_enabled: bool = True
    subtitle_style: str = "bold"
    subtitle_position: str = "bottom"
    words: list[Word] | None = None
    title: str | None = None
    normalize_audio: bool = True
    has_audio: bool = True
    output_name: str = "clip"

    @property
    def duration(self) -> float:
        return max(0.1, float(self.end) - float(self.start))


@dataclass(slots=True)
class RenderOutput:
    path: Path
    thumbnail_path: Path | None
    width: int
    height: int
    duration: float
    size_bytes: int
    has_subtitles: bool
    layout: str
    warnings: list[str] = field(default_factory=list)
    ass_path: Path | None = None
    plan: LayoutPlan | None = None


class Renderer:
    def __init__(self, tools: FfmpegTools, fonts_dir: str | os.PathLike[str]) -> None:
        self.tools = tools
        self.fonts_dir = Path(fonts_dir)

    def render(
        self, req: RenderRequest, out_dir: Path, *, cancel: CancelToken | None = None
    ) -> RenderOutput:
        out_dir.mkdir(parents=True, exist_ok=True)
        plan = build_layout(
            req.aspect,
            req.src_w,
            req.src_h,
            req.max_render_height,
            focus_x=req.focus_x,
            mode=req.framing_mode,
            webcam_region=req.webcam_region,
            allow_webcam=req.allow_webcam,
        )
        warnings = list(plan.warnings)
        graph = plan.filter_complex
        label = plan.output_label

        ass_path: Path | None = None
        has_subtitles = False
        if req.subtitles_enabled:
            captions = chunk_words(req.words or [], req.start, req.end)
            if captions or (req.title and req.title.strip()):
                ass_path = write_ass(
                    out_dir / f"{req.output_name}.ass",
                    build_ass(
                        captions,
                        title=req.title,
                        style=req.subtitle_style,
                        position=req.subtitle_position,
                        play_res_x=plan.out_w,
                        play_res_y=plan.out_h,
                        clip_duration=req.duration,
                        aspect=req.aspect,
                    ),
                )
                graph = f"{graph};{label}{subtitles_filter(ass_path, self.fonts_dir)}[vsub]"
                label = "[vsub]"
                has_subtitles = bool(captions)

        out_path = out_dir / f"{req.output_name}.mp4"
        args: list[str] = [
            "-ss",
            fmt_num(max(0.0, req.start)),
            "-i",
            os.fspath(req.source),
            "-t",
            fmt_num(req.duration),
            "-filter_complex",
            graph,
            "-map",
            label,
        ]
        if req.has_audio:
            args += ["-map", "0:a:0"]
            if req.normalize_audio:
                args += ["-af", LOUDNORM]
            args += ["-c:a", "aac", "-b:a", "160k", "-ar", "48000"]
        else:
            args += ["-an"]
        args += [
            "-sn",
            "-dn",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "22",
            "-pix_fmt",
            "yuv420p",
            "-profile:v",
            "high",
            "-level",
            "4.1",
            "-movflags",
            "+faststart",
            *self.tools.encode_thread_args(),
            "-f",
            "mp4",
            os.fspath(out_path),
        ]
        timeout = timeout_for(
            req.duration, factor=RENDER_TIMEOUT_FACTOR, minimum=RENDER_TIMEOUT_MIN, extra=120.0
        )
        log.info(
            "render.start",
            output=out_path.name,
            layout=plan.layout,
            size=f"{plan.out_w}x{plan.out_h}",
            start=req.start,
            end=req.end,
            subtitles=has_subtitles,
        )
        result = self.tools.run(args, timeout=timeout, cancel=cancel)
        if not out_path.exists() or out_path.stat().st_size == 0:
            raise FfmpegError("ffmpeg produced no output", args_list=args, stderr=result.stderr)

        info = probe_file(self.tools, out_path)
        if not info.has_video:
            raise FfmpegError(
                "rendered file has no video stream", args_list=args, stderr=result.stderr
            )
        duration = float(info.duration or req.duration)

        thumbnail: Path | None = None
        try:
            thumbnail = self.thumbnail(
                out_path, duration / 2.0, out_dir / f"{req.output_name}.jpg", cancel=cancel
            )
        except FfmpegError as exc:
            warnings.append(f"thumbnail failed: {exc}")
            log.warning("thumbnail.failed", error=str(exc)[:200])

        log.info(
            "render.done",
            output=out_path.name,
            seconds=round(result.elapsed, 1),
            size=out_path.stat().st_size,
        )
        return RenderOutput(
            path=out_path,
            thumbnail_path=thumbnail,
            width=int(info.width or plan.out_w),
            height=int(info.height or plan.out_h),
            duration=round(duration, 3),
            size_bytes=out_path.stat().st_size,
            has_subtitles=has_subtitles,
            layout=plan.layout,
            warnings=warnings,
            ass_path=ass_path,
            plan=plan,
        )

    def thumbnail(
        self, video: Path, at: float, out: Path, *, cancel: CancelToken | None = None
    ) -> Path:
        args = [
            "-ss",
            fmt_num(max(0.0, at)),
            "-i",
            os.fspath(video),
            "-frames:v",
            "1",
            "-vf",
            "scale=640:-2",
            "-q:v",
            "3",
            "-update",
            "1",
            "-f",
            "image2",
            os.fspath(out),
        ]
        self.tools.run(args, timeout=120.0, cancel=cancel)
        if not out.exists() or out.stat().st_size == 0:
            raise FfmpegError("thumbnail not written", args_list=args)
        return out
