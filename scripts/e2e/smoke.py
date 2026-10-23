"""End-to-end smoke test against a running stack (API + worker).

Usage:
    python scripts/e2e/smoke.py --fixture <video.mp4> [--api http://localhost:5080] [--timeout 1800]

Flow: health → register → upload (signed URL) → complete → create project (idempotent) →
poll job → candidates → clips → download + ffprobe → cross-user isolation check.
Exit code 0 only when every step succeeds. Prints a JSON summary at the end.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

import httpx

DEFAULT_FFPROBE = os.path.expandvars(r"%LOCALAPPDATA%\ClipForge\tools\ffmpeg\bin\ffprobe.exe")
TERMINAL = {"completed", "failed", "cancelled"}


class Smoke:
    def __init__(self, api: str, fixture: Path, timeout: float, ffprobe: str, aspect: str, extended: bool = True) -> None:
        self.api = api.rstrip("/")
        self.fixture = fixture
        self.timeout = timeout
        self.ffprobe = ffprobe
        self.aspect = aspect
        self.extended = extended
        self.client = httpx.Client(base_url=self.api, timeout=60.0)
        self.summary: dict = {"api": api, "steps": []}

    def step(self, name: str, ok: bool, **info) -> None:
        self.summary["steps"].append({"step": name, "ok": ok, **info})
        print(f"[{'OK ' if ok else 'ERR'}] {name} {json.dumps(info, default=str)[:400]}")
        if not ok:
            raise SystemExit(self.finish(1))

    def finish(self, code: int) -> int:
        self.summary["ok"] = code == 0
        print(json.dumps(self.summary, indent=2, default=str))
        return code

    def expect(self, r: httpx.Response, status: int, name: str) -> dict:
        body: dict = {}
        try:
            body = r.json() if r.content else {}
        except ValueError:
            body = {"raw": r.text[:300]}
        self.step(name, r.status_code == status, status=r.status_code, expected=status, body=str(body)[:300])
        return body

    def run(self) -> int:
        c = self.client
        r = c.get("/health")
        self.expect(r, 200, "health")

        email = f"e2e-{uuid.uuid4().hex[:10]}@example.com"
        password = "Senha12345"
        user = self.expect(c.post("/api/auth/register", json={
            "email": email, "password": password, "displayName": "E2E Tester", "acceptedTerms": True,
        }), 201, "register")
        me = self.expect(c.get("/api/auth/me"), 200, "me")
        self.summary["user"] = {"email": email, "id": (me.get("user") or {}).get("id")}

        size = self.fixture.stat().st_size
        up = self.expect(c.post("/api/media/uploads", json={
            "fileName": self.fixture.name, "sizeBytes": size, "contentType": "video/mp4",
            "declaredDurationSeconds": 70,
        }), 201, "upload.create")
        asset_id = up["asset"]["id"]
        target = up["upload"]
        with self.fixture.open("rb") as fh:
            headers = dict(target.get("headers") or {})
            headers.setdefault("Content-Type", "video/mp4")
            r = httpx.request(target["method"], target["url"], content=fh, headers=headers, timeout=600.0)
        self.step("upload.put", r.status_code in (200, 201, 204), status=r.status_code, body=r.text[:200])
        asset = self.expect(c.post(f"/api/media/uploads/{asset_id}/complete"), 200, "upload.complete")
        self.step("upload.status", asset["asset"]["status"] == "uploaded", status=asset["asset"]["status"])

        key = str(uuid.uuid4())
        payload = {
            "title": "E2E fixture",
            "source": {"kind": "upload", "mediaAssetId": asset_id},
            "settings": {
                "minClipSeconds": 8, "maxClipSeconds": 25, "targetClipSeconds": 15,
                "sensitivity": 65, "maxCandidates": 10, "autoRenderTop": 2,
                "aspectRatio": self.aspect,
                "subtitles": {"enabled": True, "style": "bold", "position": "bottom"},
                "language": "auto", "framing": {"mode": "auto"},
            },
        }
        created = self.expect(c.post("/api/projects", json=payload, headers={"Idempotency-Key": key}), 201, "project.create")
        project_id = created["project"]["id"]
        job_id = created["job"]["id"]
        again = self.expect(c.post("/api/projects", json=payload, headers={"Idempotency-Key": key}), 201, "project.idempotent")
        self.step("project.idempotent.sameId", again["project"]["id"] == project_id, first=project_id, second=again["project"]["id"])

        started = time.time()
        last = None
        job: dict = {}
        while time.time() - started < self.timeout:
            job = self.expect(c.get(f"/api/projects/{project_id}/jobs/{job_id}"), 200, "job.poll") if last is None else c.get(f"/api/projects/{project_id}/jobs/{job_id}").json()
            sig = (job.get("status"), job.get("progress"), job.get("statusMessage"))
            if sig != last:
                print(f"    job {job.get('status')} {job.get('progress')}% {job.get('statusMessage') or ''}")
                last = sig
            if job.get("status") in TERMINAL:
                break
            time.sleep(2)
        self.summary["job"] = {k: job.get(k) for k in ("status", "progress", "errorCode", "errorMessage", "result", "attempt")}
        self.step("job.completed", job.get("status") == "completed", status=job.get("status"), error=job.get("errorMessage"), seconds=round(time.time() - started))

        cands = self.expect(c.get(f"/api/projects/{project_id}/candidates"), 200, "candidates.list")
        items = cands.get("items", [])
        self.summary["candidates"] = [
            {k: it.get(k) for k in ("rank", "startSeconds", "endSeconds", "score", "category", "reasoning")} for it in items
        ]
        self.step("candidates.nonEmpty", len(items) > 0, count=len(items), signals=cands.get("signals"))
        scores = [it["score"] for it in items]
        self.step("candidates.scoreRange", all(0 <= s <= 100 for s in scores) and scores == sorted(scores, reverse=True), scores=scores)

        clips = self.expect(c.get("/api/clips", params={"projectId": project_id, "pageSize": 50}), 200, "clips.list")
        ready = [cl for cl in clips.get("items", []) if cl.get("status") == "ready"]
        self.step("clips.ready", len(ready) > 0, total=len(clips.get("items", [])), ready=len(ready))
        clip = ready[0]
        detail = self.expect(c.get(f"/api/clips/{clip['id']}"), 200, "clip.detail")
        self.step("clip.hasRender", bool((detail.get("currentRender") or {}).get("downloadUrl")), render=detail.get("currentRender"))
        dl = self.expect(c.get(f"/api/clips/{clip['id']}/download"), 200, "clip.downloadUrl")
        r = httpx.get(dl["url"], timeout=120.0, follow_redirects=True)
        self.step("clip.downloadBytes", r.status_code == 200 and len(r.content) > 10_000, status=r.status_code, bytes=len(r.content))
        out = Path(os.environ.get("TEMP", ".")) / f"e2e-clip-{clip['id']}.mp4"
        out.write_bytes(r.content)
        probe = subprocess.run([self.ffprobe, "-v", "error", "-print_format", "json", "-show_streams", "-show_format", str(out)], capture_output=True, text=True)
        info = json.loads(probe.stdout or "{}") if probe.returncode == 0 else {}
        v = next((s for s in info.get("streams", []) if s.get("codec_type") == "video"), {})
        a = next((s for s in info.get("streams", []) if s.get("codec_type") == "audio"), {})
        w, h = int(v.get("width", 0)), int(v.get("height", 0))
        want = {"9:16": 9 / 16, "16:9": 16 / 9, "1:1": 1.0}[self.aspect]
        ratio_ok = h > 0 and abs(w / h - want) < 0.02
        self.step("clip.ffprobe", probe.returncode == 0 and v.get("codec_name") == "h264" and a.get("codec_name") == "aac" and ratio_ok,
                  width=w, height=h, vcodec=v.get("codec_name"), acodec=a.get("codec_name"), duration=info.get("format", {}).get("duration"))

        thumb = clip.get("thumbnailUrl")
        if thumb:
            tr = httpx.get(thumb, timeout=60.0)
            self.step("clip.thumbnail", tr.status_code == 200 and len(tr.content) > 500, status=tr.status_code, bytes=len(tr.content))

        dash = self.expect(c.get("/api/dashboard/summary"), 200, "dashboard.summary")
        self.step("dashboard.counts", dash.get("clipsTotal", 0) >= 1, clipsTotal=dash.get("clipsTotal"), minutes=dash.get("minutesProcessedThisPeriod"))
        usage = self.expect(c.get("/api/account/usage"), 200, "account.usage")
        self.step("usage.recorded", (usage.get("used") or {}).get("processingSeconds", 0) > 0, used=usage.get("used"))

        if self.extended:
            self.extended_steps(c, project_id, items, clip)

        other = httpx.Client(base_url=self.api, timeout=30.0)
        other.post("/api/auth/register", json={"email": f"e2e-b-{uuid.uuid4().hex[:8]}@example.com", "password": password, "displayName": "Other", "acceptedTerms": True})
        r = other.get(f"/api/projects/{project_id}")
        self.step("isolation.project", r.status_code in (403, 404), status=r.status_code)
        r = other.get(f"/api/clips/{clip['id']}/download")
        self.step("isolation.download", r.status_code in (403, 404), status=r.status_code)
        r = other.get(f"/api/projects/{project_id}/candidates")
        self.step("isolation.candidates", r.status_code in (403, 404), status=r.status_code)

        self.expect(c.post("/api/auth/logout"), 204, "logout")
        r = c.get("/api/auth/me")
        self.step("logout.effective", r.status_code == 401, status=r.status_code)
        return self.finish(0)

    # ------------------------------------------------------------------ extended --
    def wait_clip(self, c: httpx.Client, clip_id: str, want_version: int | None, timeout: float = 600) -> dict:
        started = time.time()
        last = None
        d: dict = {}
        while time.time() - started < timeout:
            d = c.get(f"/api/clips/{clip_id}").json()
            cur = d.get("currentRender") or {}
            sig = (d.get("status"), cur.get("version"), cur.get("status"))
            if sig != last:
                print(f"    clip {clip_id[:8]} status={d.get('status')} render=v{cur.get('version')} {cur.get('status')}")
                last = sig
            if d.get("status") == "failed":
                return d
            if d.get("status") == "ready" and (want_version is None or cur.get("version") == want_version):
                return d
            time.sleep(2)
        return d

    def probe_url(self, url: str, name: str) -> tuple[int, int, int, float]:
        r = httpx.get(url, timeout=120.0, follow_redirects=True)
        out = Path(os.environ.get("TEMP", ".")) / name
        out.write_bytes(r.content)
        p = subprocess.run([self.ffprobe, "-v", "error", "-print_format", "json", "-show_streams", "-show_format", str(out)],
                           capture_output=True, text=True)
        info = json.loads(p.stdout or "{}") if p.returncode == 0 else {}
        v = next((s for s in info.get("streams", []) if s.get("codec_type") == "video"), {})
        return r.status_code, int(v.get("width", 0)), int(v.get("height", 0)), float(info.get("format", {}).get("duration", 0) or 0)

    def extended_steps(self, c: httpx.Client, project_id: str, candidates: list[dict], clip: dict) -> None:
        # 1) edit the clip (trim + subtitle style + title) and render a new version
        new_start = float(clip["startSeconds"]) + 1.0
        new_end = new_start + 6.0
        patched = self.expect(c.patch(f"/api/clips/{clip['id']}", json={
            "title": "Editado no teste", "startSeconds": new_start, "endSeconds": new_end,
            "subtitles": {"enabled": True, "style": "boxed", "position": "center"},
        }), 200, "clip.patch")
        self.step("clip.patch.needsRender", patched.get("needsRender") is True, needsRender=patched.get("needsRender"))
        rr = self.expect(c.post(f"/api/clips/{clip['id']}/render"), 202, "clip.render")
        version = (rr.get("render") or {}).get("version")
        d = self.wait_clip(c, clip["id"], version)
        cur = d.get("currentRender") or {}
        self.step("clip.rerender.ready", d.get("status") == "ready" and cur.get("version") == version,
                  status=d.get("status"), version=cur.get("version"), expected=version)
        self.step("clip.rerender.history", len(d.get("renders") or []) >= 2, renders=len(d.get("renders") or []))
        dl = self.expect(c.get(f"/api/clips/{clip['id']}/download"), 200, "clip.rerender.downloadUrl")
        status, w, h, dur = self.probe_url(dl["url"], f"e2e-rerender-{clip['id']}.mp4")
        self.step("clip.rerender.ffprobe", status == 200 and abs(dur - 6.0) < 0.5 and h > w, width=w, height=h, duration=dur)

        # 2) turn a candidate that was not auto-rendered into a 16:9 clip
        pending = [cd for cd in candidates if not cd.get("clipId")]
        self.step("candidates.someNotRendered", len(pending) > 0, pending=len(pending))
        cd = pending[0]
        made = self.expect(c.post(f"/api/projects/{project_id}/candidates/{cd['id']}/clips",
                                  json={"aspectRatio": "16:9", "title": "Candidato em 16:9"}), 202, "candidate.toClip")
        new_id = made["clip"]["id"]
        d = self.wait_clip(c, new_id, None)
        self.step("candidate.clip.ready", d.get("status") == "ready", status=d.get("status"))
        dl = self.expect(c.get(f"/api/clips/{new_id}/download"), 200, "candidate.clip.downloadUrl")
        status, w, h, dur = self.probe_url(dl["url"], f"e2e-16x9-{new_id}.mp4")
        self.step("candidate.clip.ffprobe16x9", status == 200 and h > 0 and abs(w / h - 16 / 9) < 0.02, width=w, height=h, duration=dur)

        # 3) start a new analysis of the same project and cancel it (queued or running)
        job = self.expect(c.post(f"/api/projects/{project_id}/jobs", json={}), 201, "reanalyze.create")
        job_id = (job.get("job") or job).get("id")
        time.sleep(3)  # give the worker a chance to claim it, so the running-cancel path is exercised
        self.expect(c.post(f"/api/projects/{project_id}/jobs/{job_id}/cancel"), 202, "reanalyze.cancel")
        started = time.time()
        status_ = None
        while time.time() - started < 180:
            status_ = c.get(f"/api/projects/{project_id}/jobs/{job_id}").json().get("status")
            if status_ in TERMINAL:
                break
            time.sleep(1)
        self.step("reanalyze.cancelled", status_ == "cancelled", status=status_, seconds=round(time.time() - started))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default=os.environ.get("CLIPFORGE_API_BASE_URL", "http://localhost:5080"))
    ap.add_argument("--fixture", required=True)
    ap.add_argument("--timeout", type=float, default=1800)
    ap.add_argument("--aspect", default="9:16", choices=["9:16", "16:9", "1:1"])
    ap.add_argument("--ffprobe", default=DEFAULT_FFPROBE if Path(DEFAULT_FFPROBE).exists() else (shutil.which("ffprobe") or "ffprobe"))
    ap.add_argument("--basic", action="store_true", help="skip the edit/re-render/candidate/cancel steps")
    args = ap.parse_args()
    fixture = Path(args.fixture)
    if not fixture.exists():
        print(f"fixture not found: {fixture}", file=sys.stderr)
        return 2
    return Smoke(args.api, fixture, args.timeout, args.ffprobe, args.aspect, extended=not args.basic).run()


if __name__ == "__main__":
    raise SystemExit(main())
