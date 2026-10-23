"""Generate a real spoken WAV with Windows SAPI (System.Speech) through PowerShell.

Returns ``None`` when no voice / PowerShell is available (the transcription test is then
skipped with a clear reason). Output goes to ``tests/fixtures/generated/`` (git-ignored).

    python tests/fixtures/make_tts_fixture.py [output.wav]
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

GENERATED_DIR = Path(__file__).resolve().parent / "generated"

# pt-BR sentence packed with lexicon keywords (hype / reaction / gameplay / funny)
DEFAULT_TEXT = (
    "Que jogada incrível, não acredito, foi um clutch absurdo. "
    "Vamos, vamos, vamos! Isso foi muito engraçado, meu Deus."
)
KEYWORDS = ["incrivel", "nao acredito", "clutch", "absurdo", "vamos", "engracado", "meu deus"]

# English phrases of the e2e fixture (scripts/e2e/make_fixture.py), for an en-US voice
EN_TEXT = (
    "Okay chat, we are starting the ranked match now. Let's see how it goes. "
    "Oh my god, no way! That was insane! Let's go! "
    "Careful, careful. Last one alive. Clutch! We won the round! GG, that was crazy!"
)
EN_KEYWORDS = ["ranked match", "oh my god", "no way", "insane", "careful", "clutch", "we won"]

_PS_TEMPLATE = r"""
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voices = @($s.GetInstalledVoices() | Where-Object { $_.Enabled })
if ($voices.Count -eq 0) { Write-Output 'NOVOICE'; exit 3 }
$preferred = $voices | Where-Object { $_.VoiceInfo.Culture.Name -eq '{CULTURE}' } |
    Select-Object -First 1
if ($preferred) { $s.SelectVoice($preferred.VoiceInfo.Name) }
elseif ('{REQUIRE}' -eq '1') { Write-Output 'NOVOICE'; exit 3 }
$s.Rate = -1
$s.SetOutputToWaveFile('{OUT}')
$s.Speak('{TEXT}')
$voiceName = $s.Voice.Name
$s.Dispose()
Write-Output ('VOICE=' + $voiceName)
"""


def _powershell() -> str | None:
    for name in ("powershell", "pwsh"):
        found = shutil.which(name)
        if found:
            return found
    return None


def make_tts_wav(
    out: Path, text: str = DEFAULT_TEXT, culture: str = "pt-BR", *, require_culture: bool = False
) -> tuple[Path, str] | None:
    """Return ``(path, voice name)`` or ``None`` when TTS is not available (with
    ``require_culture``: also when no voice of ``culture`` is installed, instead of falling
    back to the default voice — on a pt-BR Windows that is a Portuguese voice)."""
    if not sys.platform.startswith("win"):
        return None
    ps = _powershell()
    if ps is None:
        return None
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    script = (
        _PS_TEMPLATE.replace("{CULTURE}", culture)
        .replace("{REQUIRE}", "1" if require_culture else "0")
        .replace("{OUT}", str(out).replace("'", "''"))
        .replace("{TEXT}", text.replace("'", "''"))
    )
    with tempfile.NamedTemporaryFile("w", suffix=".ps1", delete=False, encoding="utf-8-sig") as fh:
        fh.write(script)
        script_path = fh.name
    try:
        proc = subprocess.run(
            [
                ps,
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                script_path,
            ],
            capture_output=True,
            text=True,
            timeout=120,
            check=False,
        )
    finally:
        Path(script_path).unlink(missing_ok=True)
    if (
        proc.returncode != 0
        or "NOVOICE" in proc.stdout
        or not out.exists()
        or out.stat().st_size < 1000
    ):
        return None
    voice = "unknown"
    for line in proc.stdout.splitlines():
        if line.startswith("VOICE="):
            voice = line[len("VOICE=") :].strip()
    return out, voice


def main(argv: list[str] | None = None) -> int:
    out = Path(argv[0]) if argv else GENERATED_DIR / "tts-ptbr.wav"
    result = make_tts_wav(out)
    if result is None:
        print("TTS not available (no SAPI voice / PowerShell)")
        return 3
    print(f"{result[0]} ({result[1]})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
