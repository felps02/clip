"""Builds a small end-to-end test video with real speech (Windows SAPI TTS) and audio bursts.

Usage:
    python scripts/e2e/make_fixture.py --out <path.mp4> [--ffmpeg <ffmpeg.exe>]

The clip is 70 s, 640x360, H.264 + AAC. Events at known times:
    5 s   calm sentence ("Bora, chat!" in pt-BR, default; --lang en for English)
    20 s  hype sentence + loud tone burst at 21 s
    35 s  visual scene change (pattern switch)
    40 s  tense sentence
    55 s  hype sentence + loud tone burst at 56 s
On machines without SAPI voices the speech parts are skipped (bursts remain).
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_FFMPEG = os.path.expandvars(r"%LOCALAPPDATA%\ClipForge\tools\ffmpeg\bin\ffmpeg.exe")

PHRASES = {
    # Brazilian streamer lines spoken by a pt-BR voice (the product's main audience).
    "pt": [
        (5.0, "Bora, chat! Vamos começar a ranqueada agora, vamos ver no que dá."),
        (20.0, "Meu Deus, não acredito! Que jogada insana! Bora, bora!"),
        (40.0, "Cuidado, cuidado. Um contra um. Segura, segura."),
        (55.0, "Clutch! Ganhamos o round! Que loucura, gente!"),
    ],
    # The same layout in English, spoken by an en-US voice.
    "en": [
        (5.0, "Okay chat, we are starting the ranked match now. Let's see how it goes."),
        (20.0, "Oh my god, no way! That was insane! Let's go! Let's go!"),
        (40.0, "Careful, careful. Last one alive. One versus one. Hold on."),
        (55.0, "Clutch! We won the round! GG, that was crazy!"),
    ],
}
CULTURES = {"pt": "pt-BR", "en": "en-US"}
BURSTS = [(21.0, 1.6), (56.0, 1.4)]


def tts(text: str, out: Path, culture: str) -> bool:
    """Speaks `text` with an installed Windows voice of `culture` (never another language's voice:
    an English text read by a pt-BR voice is unintelligible to speech recognition)."""
    if sys.platform != "win32":
        return False
    script = (
        "Add-Type -AssemblyName System.Speech; "
        "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
        f"$v = $s.GetInstalledVoices() | Where-Object {{ $_.VoiceInfo.Culture.Name -eq '{culture}' }} | Select-Object -First 1; "
        "if (-not $v) { exit 3 }; $s.SelectVoice($v.VoiceInfo.Name); "
        f"$s.SetOutputToWaveFile('{out}'); $s.Rate = 1; $s.Speak('{text.replace(chr(39), chr(39) * 2)}'); $s.Dispose(); exit 0"
    )
    r = subprocess.run(["powershell.exe", "-NoProfile", "-Command", script], capture_output=True, text=True)
    return r.returncode == 0 and out.exists() and out.stat().st_size > 1000


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--ffmpeg", default=DEFAULT_FFMPEG)
    ap.add_argument("--duration", type=float, default=70.0)
    ap.add_argument("--lang", choices=sorted(PHRASES), default="pt", help="language of the spoken lines (pt = pt-BR voice)")
    args = ap.parse_args()
    ffmpeg = args.ffmpeg if Path(args.ffmpeg).exists() else shutil.which("ffmpeg") or "ffmpeg"
    out = Path(args.out).resolve()
    out.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as td:
        tdir = Path(td)
        inputs: list[str] = []
        filters: list[str] = []
        labels: list[str] = []
        idx = 0

        # video: two visual patterns concatenated → scene change at 35 s
        half = args.duration / 2
        inputs += ["-f", "lavfi", "-i", f"testsrc2=size=640x360:rate=30:duration={half}"]
        inputs += ["-f", "lavfi", "-i", f"testsrc=size=640x360:rate=30:duration={half}"]
        filters.append("[0:v][1:v]concat=n=2:v=1:a=0,format=yuv420p[vout]")
        idx = 2

        # audio base: quiet noise so the track is never digital silence
        inputs += ["-f", "lavfi", "-i", f"anoisesrc=color=pink:amplitude=0.01:duration={args.duration}:sample_rate=48000"]
        filters.append(f"[{idx}:a]aformat=channel_layouts=mono[a_base]")
        labels.append("[a_base]")
        idx += 1

        spoken = 0
        phrases = PHRASES[args.lang]
        for t, text in phrases:
            wav = tdir / f"tts_{int(t)}.wav"
            if tts(text, wav, CULTURES[args.lang]):
                inputs += ["-i", str(wav)]
                ms = int(t * 1000)
                filters.append(f"[{idx}:a]aformat=sample_rates=48000:channel_layouts=mono,volume=1.0,adelay={ms}|{ms}[a_tts{idx}]")
                labels.append(f"[a_tts{idx}]")
                idx += 1
                spoken += 1

        for t, d in BURSTS:
            inputs += ["-f", "lavfi", "-i", f"sine=frequency=520:duration={d}:sample_rate=48000"]
            ms = int(t * 1000)
            filters.append(f"[{idx}:a]aformat=channel_layouts=mono,volume=0.9,adelay={ms}|{ms}[a_burst{idx}]")
            labels.append(f"[a_burst{idx}]")
            idx += 1

        filters.append("".join(labels) + f"amix=inputs={len(labels)}:duration=first:normalize=0[aout]")
        cmd = [
            ffmpeg, "-nostdin", "-hide_banner", "-y", *inputs,
            "-filter_complex", ";".join(filters),
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
            "-movflags", "+faststart", "-t", str(args.duration), str(out),
        ]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stderr[-3000:], file=sys.stderr)
            return 1
    print(f"fixture written: {out} ({out.stat().st_size // 1024} KiB), language {args.lang}, spoken phrases: {spoken}/{len(phrases)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
