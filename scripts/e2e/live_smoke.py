"""Live-capture end-to-end test against a running stack (API + worker) and the real companion.

Nothing is mocked: a user registers, creates a device token, a recording grows on disk in real
time exactly like an OBS MKV recording (ffmpeg -re), the companion (file mode) slices it and
uploads the segments, the worker analyses them incrementally, and the test follows the session
through the user API until it ends. Then it checks segments, highlights and downloads.

Usage:
    python scripts/e2e/live_smoke.py --fixture <video.mp4> [--api http://localhost:5080]
           [--segment-seconds 20] [--timeout 900]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parents[2]
LOCAL = Path(os.environ.get("LOCALAPPDATA", str(Path.home())))
DEFAULT_FFMPEG = LOCAL / "ClipForge" / "tools" / "ffmpeg" / "bin" / "ffmpeg.exe"
DEFAULT_FFPROBE = LOCAL / "ClipForge" / "tools" / "ffmpeg" / "bin" / "ffprobe.exe"
COMPANION_PY = ROOT / "companion" / ".venv" / "Scripts" / "python.exe"
TERMINAL = {"ended", "error"}


def tool(path: Path, name: str) -> str:
    return str(path) if path.exists() else (shutil.which(name) or name)


class LiveSmoke:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.api = args.api.rstrip("/")
        self.client = httpx.Client(base_url=self.api, timeout=60.0)
        self.steps: list[dict] = []
        self.work = Path(tempfile.mkdtemp(prefix="clipforge-live-e2e-"))
        self.procs: list[subprocess.Popen] = []

    def step(self, name: str, ok: bool, **info) -> None:
        self.steps.append({"step": name, "ok": ok, **info})
        print(f"[{'OK ' if ok else 'ERR'}] {name} {json.dumps(info, default=str, ensure_ascii=False)[:400]}", flush=True)
        if not ok:
            raise SystemExit(self.finish(1))

    def finish(self, code: int) -> int:
        for p in self.procs:
            if p.poll() is None:
                p.kill()
        print(json.dumps({"ok": code == 0, "steps": self.steps}, indent=2, default=str, ensure_ascii=False))
        shutil.rmtree(self.work, ignore_errors=True)
        return code

    def run(self) -> int:
        c = self.client
        self.step("health", c.get("/health").status_code == 200)
        email = f"live-e2e-{uuid.uuid4().hex[:10]}@example.com"
        r = c.post("/api/auth/register", json={"email": email, "password": "Senha12345", "displayName": "Live E2E", "acceptedTerms": True})
        self.step("register", r.status_code == 201, status=r.status_code)

        r = c.post("/api/live/companion/tokens", json={"name": "PC de teste"})
        self.step("device.token", r.status_code == 201, status=r.status_code)
        token = r.json()["token"]

        # A recording that grows in real time, like OBS writing an MKV while streaming.
        growing = self.work / "recording.mkv"
        writer = subprocess.Popen(
            [tool(DEFAULT_FFMPEG, "ffmpeg"), "-nostdin", "-hide_banner", "-loglevel", "error", "-re",
             "-i", str(self.args.fixture), "-c", "copy", "-f", "matroska", str(growing)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.procs.append(writer)
        deadline = time.time() + 15
        while time.time() < deadline and (not growing.exists() or growing.stat().st_size < 4096):
            time.sleep(0.2)
        self.step("recording.growing", growing.exists(), bytes=growing.stat().st_size if growing.exists() else 0)

        env = dict(os.environ, CLIPFORGE_DEVICE_TOKEN=token, FFMPEG_PATH=tool(DEFAULT_FFMPEG, "ffmpeg"), PYTHONIOENCODING="utf-8")
        companion_log = (self.work / "companion.log").open("w", encoding="utf-8")
        companion = subprocess.Popen(
            [str(COMPANION_PY), "-m", "clipforge_companion", "run", "--api", self.api, "--file", str(growing),
             "--segment-seconds", str(self.args.segment_seconds), "--title", "Live E2E",
             "--work-dir", str(self.work / "companion")],
            stdout=companion_log, stderr=subprocess.STDOUT, env=env, cwd=str(ROOT / "companion"))
        self.procs.append(companion)
        started = time.time()

        session: dict = {}
        last = None
        while time.time() - started < self.args.timeout:
            r = c.get("/api/live/sessions", params={"pageSize": 5})
            items = r.json().get("items", []) if r.status_code == 200 else []
            if items:
                session = items[0]
                stats = session.get("stats") or {}
                sig = (session.get("status"), stats.get("segmentsReceived"), stats.get("segmentsAnalyzed"), stats.get("highlights"))
                if sig != last:
                    state = session.get("companionState") or {}
                    print(f"    t+{time.time() - started:5.0f}s status={sig[0]} recebidos={sig[1]} analisados={sig[2]} "
                          f"destaques={sig[3]} gravando={state.get('recording')} fila={state.get('queueLength')}", flush=True)
                    last = sig
                if session.get("status") in TERMINAL and companion.poll() is not None:
                    break
            time.sleep(3)
        companion_log.close()
        tail = (self.work / "companion.log").read_text(encoding="utf-8", errors="replace").splitlines()[-12:]

        self.step("session.ended", session.get("status") == "ended", status=session.get("status"),
                  endReason=session.get("endReason"), seconds=round(time.time() - started))
        self.step("companion.exit", companion.poll() == 0, code=companion.poll(), tail=tail[-4:])
        stats = session.get("stats") or {}
        self.step("segments.all.analysed",
                  stats.get("segmentsReceived", 0) >= 3 and stats.get("segmentsAnalyzed") == stats.get("segmentsReceived")
                  and stats.get("segmentsFailed", 0) == 0, stats=stats)

        detail = c.get(f"/api/live/sessions/{session['id']}").json()
        seqs = sorted(s["sequence"] for s in detail.get("segments", []))
        self.step("segments.contiguous", seqs == list(range(len(seqs))), sequences=seqs)

        cands = c.get(f"/api/projects/{session['projectId']}/candidates").json().get("items", [])
        windows = sorted((float(x["startSeconds"]), float(x["endSeconds"])) for x in cands)
        overlap_dupes = [
            (a, b) for i, a in enumerate(windows) for b in windows[i + 1:]
            if min(a[1], b[1]) - max(a[0], b[0]) > 0.5 * min(a[1] - a[0], b[1] - b[0])
        ]
        within = all(0 <= s and e <= 75 for s, e in windows)
        self.step("candidates.absolute.no-duplicates", len(cands) > 0 and within and not overlap_dupes,
                  count=len(cands), windows=windows, duplicates=overlap_dupes)

        highlights = detail.get("highlights", [])
        self.step("highlights.present", len(highlights) > 0, count=len(highlights),
                  titles=[h.get("title") for h in highlights])
        ready = [h for h in highlights if h.get("status") == "ready"]
        self.step("highlights.ready", len(ready) == len(highlights), ready=len(ready))
        h = ready[0]
        dl = c.get(f"/api/clips/{h['id']}/download").json()
        r = httpx.get(dl["url"], timeout=120.0)
        out = self.work / "highlight.mp4"
        out.write_bytes(r.content)
        probe = subprocess.run([tool(DEFAULT_FFPROBE, "ffprobe"), "-v", "error", "-print_format", "json", "-show_streams", "-show_format", str(out)],
                               capture_output=True, text=True)
        info = json.loads(probe.stdout or "{}") if probe.returncode == 0 else {}
        v = next((s for s in info.get("streams", []) if s.get("codec_type") == "video"), {})
        self.step("highlight.download", r.status_code == 200 and len(r.content) > 10_000 and v.get("codec_name") == "h264",
                  bytes=len(r.content), width=v.get("width"), height=v.get("height"),
                  duration=info.get("format", {}).get("duration"))

        notes = c.get("/api/notifications", params={"pageSize": 20}).json().get("items", [])
        self.step("notification.highlight", any(n.get("type") == "clip_ready" for n in notes),
                  types=sorted({n.get("type") for n in notes}))

        # Re-render a live highlight with a new range: the worker must fetch every segment that
        # overlaps the range, join them and cut the right part of the session.
        clip = c.get(f"/api/clips/{h['id']}").json()
        start = max(0.0, float(clip["startSeconds"]) - 2.0)
        end = start + 12.0
        r = c.patch(f"/api/clips/{h['id']}", json={"startSeconds": start, "endSeconds": end, "aspectRatio": "16:9"})
        self.step("live.clip.patch", r.status_code == 200 and r.json().get("needsRender") is True, status=r.status_code)
        r = c.post(f"/api/clips/{h['id']}/render")
        self.step("live.clip.render", r.status_code == 202, status=r.status_code)
        version = (r.json().get("render") or {}).get("version")
        deadline = time.time() + 300
        detail_clip: dict = {}
        while time.time() < deadline:
            detail_clip = c.get(f"/api/clips/{h['id']}").json()
            cur = detail_clip.get("currentRender") or {}
            if detail_clip.get("status") in ("ready", "failed") and cur.get("version") == version:
                break
            time.sleep(2)
        self.step("live.clip.rerendered", detail_clip.get("status") == "ready", status=detail_clip.get("status"), version=version)
        dl = c.get(f"/api/clips/{h['id']}/download").json()
        r = httpx.get(dl["url"], timeout=120.0)
        out.write_bytes(r.content)
        probe = subprocess.run([tool(DEFAULT_FFPROBE, "ffprobe"), "-v", "error", "-print_format", "json", "-show_streams", "-show_format", str(out)],
                               capture_output=True, text=True)
        info = json.loads(probe.stdout or "{}") if probe.returncode == 0 else {}
        v = next((s for s in info.get("streams", []) if s.get("codec_type") == "video"), {})
        duration = float(info.get("format", {}).get("duration") or 0)
        self.step("live.clip.rerender.file", r.status_code == 200 and abs(duration - 12.0) < 0.6 and v.get("width") == 1280,
                  width=v.get("width"), height=v.get("height"), duration=duration)
        return self.finish(0)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default=os.environ.get("CLIPFORGE_API_BASE_URL", "http://localhost:5080"))
    ap.add_argument("--fixture", required=True, type=Path)
    ap.add_argument("--segment-seconds", type=int, default=20)
    ap.add_argument("--timeout", type=float, default=900)
    args = ap.parse_args()
    if not args.fixture.exists():
        print(f"fixture not found: {args.fixture}", file=sys.stderr)
        return 2
    if not COMPANION_PY.exists():
        print(f"companion venv not found: {COMPANION_PY}", file=sys.stderr)
        return 2
    return LiveSmoke(args).run()


if __name__ == "__main__":
    raise SystemExit(main())
