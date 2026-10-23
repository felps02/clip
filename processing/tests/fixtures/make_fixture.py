"""Synthesise the test video: ``testsrc2`` + a quiet sine tone with loud bursts at known
times and visual cuts (different lavfi sources concatenated).

Default: 90 s, 640x360, bursts at 20 s (1.2 s, 900 Hz), 45 s (1 s, 3 kHz + noise,
scream-like) and 70 s (0.8 s, 1200 Hz), cuts at 30 s and 60 s, silence 60.5-62.5 s.
Generated files live under ``tests/fixtures/generated/`` (git-ignored).

    python tests/fixtures/make_fixture.py [output.mp4] [--duration 90]
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

GENERATED_DIR = Path(__file__).resolve().parent / "generated"
_CREATION_FLAGS = (
    getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform.startswith("win") else 0
)


@dataclass(frozen=True)
class Burst:
    at: float
    length: float
    frequency: float
    noise: float = 0.0  # 0..1 share of white noise mixed into the burst

    @property
    def end(self) -> float:
        return self.at + self.length

    @property
    def mid(self) -> float:
        return self.at + self.length / 2.0


@dataclass(frozen=True)
class FixtureSpec:
    duration: float = 90.0
    width: int = 640
    height: int = 360
    fps: int = 25
    bursts: tuple[Burst, ...] = (
        Burst(20.0, 1.2, 900.0),
        Burst(45.0, 1.0, 3000.0, noise=0.5),
        Burst(70.0, 0.8, 1200.0),
    )
    cuts: tuple[float, ...] = (30.0, 60.0)
    silence: tuple[float, float] | None = (60.5, 62.5)
    baseline_amplitude: float = 0.03  # about -30 dBFS
    burst_amplitude: float = 0.8  # about -2 dBFS
    extra: dict[str, str] = field(default_factory=dict)

    @property
    def burst_times(self) -> list[float]:
        return [b.mid for b in self.bursts]


SHORT_SPEC = FixtureSpec(
    duration=20.0,
    bursts=(Burst(8.0, 1.0, 900.0),),
    cuts=(12.0,),
    silence=(15.0, 16.0),
)


def audio_expression(spec: FixtureSpec) -> str:
    """ffmpeg ``aevalsrc`` expression: quiet tone + bursts + optional silence window."""
    base = f"{spec.baseline_amplitude}*sin(2*PI*220*t)"
    if spec.silence is not None:
        s, e = spec.silence
        base = f"({base})*(1-between(t,{s},{e}))"
    parts = [base, "0.002*(random(1)-0.5)"]
    for index, burst in enumerate(spec.bursts):
        tone = f"sin(2*PI*{burst.frequency}*t)"
        if burst.noise > 0:
            tone = f"({1 - burst.noise}*{tone}+{burst.noise}*(random({index + 2})-0.5)*2)"
        parts.append(f"{spec.burst_amplitude}*{tone}*between(t,{burst.at},{burst.end})")
    return "+".join(parts)


def video_sources(spec: FixtureSpec) -> list[str]:
    """One lavfi source per visual segment (cuts between them)."""
    size = f"size={spec.width}x{spec.height}:rate={spec.fps}"
    patterns = ["testsrc2", "smptehdbars", "testsrc", "rgbtestsrc", "pal75bars"]
    bounds = [0.0, *spec.cuts, spec.duration]
    sources: list[str] = []
    for index in range(len(bounds) - 1):
        length = bounds[index + 1] - bounds[index]
        pattern = patterns[index % len(patterns)]
        sources.append(f"{pattern}={size}:duration={length:.3f}")
    return sources


def build_args(ffmpeg: str, out: Path, spec: FixtureSpec) -> list[str]:
    sources = video_sources(spec)
    args = [ffmpeg, "-nostdin", "-hide_banner", "-y", "-loglevel", "error"]
    for source in sources:
        args += ["-f", "lavfi", "-i", source]
    args += [
        "-f",
        "lavfi",
        "-i",
        f"aevalsrc=exprs='{audio_expression(spec)}':s=48000:d={spec.duration}",
    ]
    inputs = "".join(f"[{i}:v]" for i in range(len(sources)))
    filter_complex = f"{inputs}concat=n={len(sources)}:v=1:a=0,format=yuv420p[v]"
    args += [
        "-filter_complex",
        filter_complex,
        "-map",
        "[v]",
        "-map",
        f"{len(sources)}:a",
        "-t",
        f"{spec.duration}",
        "-c:v",
        "libx264",
        "-preset",
        "ultrafast",
        "-crf",
        "28",
        "-g",
        str(spec.fps * 2),
        "-c:a",
        "aac",
        "-b:a",
        "128k",
        "-movflags",
        "+faststart",
        "-shortest",
        str(out),
    ]
    return args


def make_fixture(
    ffmpeg: str, out: Path, spec: FixtureSpec | None = None, *, force: bool = False
) -> Path:
    spec = spec or FixtureSpec()
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists() and not force and out.stat().st_size > 0:
        return out
    args = build_args(ffmpeg, out, spec)
    proc = subprocess.run(
        args,
        capture_output=True,
        text=True,
        timeout=600,
        check=False,
        creationflags=_CREATION_FLAGS,
    )
    if proc.returncode != 0 or not out.exists():
        raise RuntimeError(
            f"fixture generation failed (exit {proc.returncode}): {proc.stderr[-2000:]}"
        )
    return out


def default_ffmpeg() -> str:
    try:
        from app.config import get_settings

        return get_settings().ffmpeg_path
    except Exception:
        return "ffmpeg"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", nargs="?", default=str(GENERATED_DIR / "fixture-90s.mp4"))
    parser.add_argument("--duration", type=float, default=90.0)
    parser.add_argument("--ffmpeg", default=None)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args(argv)
    spec = FixtureSpec() if args.duration == 90.0 else FixtureSpec(duration=args.duration)
    out = make_fixture(args.ffmpeg or default_ffmpeg(), Path(args.output), spec, force=args.force)
    print(out)
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
    raise SystemExit(main())
