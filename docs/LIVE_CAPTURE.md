# Live capture (phase 5)

Goal: detect highlights **while a stream is running**, from the creator's own OBS recording,
without waiting for the end of a multi-hour live. Only authorised sources: the companion runs
on the creator's machine, reads the creator's own recording file and uploads it in segments.
No platform stream is scraped, no chat is read without OAuth consent.

```
OBS (records to MKV) ──obs-websocket v5──▶ companion ──segments (device token)──▶ API ──live_segment jobs──▶ worker
        │                                      │                                  │                          │
        └── recording file grows on disk ──────┘ ffmpeg -follow 1 + segment muxer  │        incremental detection, clips
                                                                                   └──▶ Lives page (polling) + notifications
```

States are reported separately and never invented: **highlight detected** (candidate stored),
**clip rendering** (render job running), **clip ready** (file available for download).

## 1. Companion (`companion/`)

Python ≥ 3.11 package `clipforge_companion` (deps: `httpx`, `websockets`; uses the same FFmpeg
build as the worker). CLI:

```
python -m clipforge_companion run --api https://api.example.com --token cfd_xxx \
    [--obs ws://127.0.0.1:4455 --obs-password ****] [--auto-record]      # OBS mode
    [--file D:/OBS/2026-10-23 20-00-00.mkv]                               # file mode (no OBS)
    [--segment-seconds 60] [--title "Live de sexta"] [--work-dir DIR] [--ffmpeg PATH]
    [--max-upload-mbps 20] [--keep-segments]
python -m clipforge_companion check --api ... --token ...   # validates token + API reachability
```

Environment variables mirror the flags (`CLIPFORGE_API_URL`, `CLIPFORGE_DEVICE_TOKEN`,
`OBS_WEBSOCKET_URL`, `OBS_WEBSOCKET_PASSWORD`, `FFMPEG_PATH`). The token is never logged.
`--segment-seconds` applies to new sessions only (see *Resume rules*). One companion runs per
device and work directory (`<work-dir>/companion-<deviceId>.lock`); a second one exits with a
message instead of taking the first one's live session for a leftover. Every session directory
is also locked (`<work-dir>/<sessionId>/session.lock`) by the process working on it.

### OBS mode (obs-websocket v5, OBS ≥ 28)
- Connect, receive `Hello` (op 0). When `authentication` is present compute
  `secret = base64(sha256(password + salt))`, `auth = base64(sha256(secret + challenge))`.
- Send `Identify` (op 1) `{rpcVersion: 1, authentication, eventSubscriptions: 64}` (Outputs),
  wait for `Identified` (op 2).
- Requests (op 6/7): `GetVersion`, `GetRecordStatus`, `GetStreamStatus`, `GetRecordDirectory`,
  `StartRecord` (only with `--auto-record`). Events (op 5): `RecordStateChanged`
  (`outputActive`, `outputState`, `outputPath`), `RecordFileChanged` (`newOutputPath`,
  obs-websocket ≥ 5.5), `StreamStateChanged`.
- **Sessions are created on demand**, not at startup: when OBS starts recording (or streams
  without recording, so that the warning reaches the Lives page). One recording = one session:
  a new recording always gets a new session, even right after the previous one stopped. A
  recording started while FFmpeg still drains the previous file belongs to the new session
  (each capture is tied to a recording "generation", bumped on every start/stop).
- The device's previous session may still be `ending` (its last segments being analysed):
  for starting a new session it counts as finished, but the API answers `409 plan_limit` until
  it is terminal. The companion then asks that session's status (heartbeat) and retries the
  creation with backoff (5 s → 30 s) while it is `ending`, printing "Aguardando a análise da
  sessão anterior". This also works across restarts: sessions the device ended are kept in
  `<work-dir>/ending-<deviceId>.json` (per API URL, forgotten once terminal, after a successful
  creation, or after 24 h). A `409 plan_limit` with no such session is reported as a conflict.
- Recording started → start capture on `outputPath` (fallback: newest file in
  `GetRecordDirectory`, or one level below, modified in the last 2 minutes). Recording stopped
  → flush the last segment and end the session. Streaming without recording → warn
  (`recording: false` and the reason in `lastError` of the heartbeat) and, with
  `--auto-record`, call `StartRecord` once.
- **Split files** (OBS automatic file splitting): `RecordFileChanged` → the capture goes on in
  the new file, in the same session (sequences and times continue). With obs-websocket < 5.5,
  or after a reconnect (the event may have been missed), the record folder is scanned for the
  next file: same extension, created within 15 s of the current file's last write, not a
  Replay Buffer save (`Replay*`), and growing.
- **Pause**: the file stops growing, FFmpeg ends its run; the capture waits until the file
  grows again and reads it again from the start (cuts are deterministic, so sequences line up).
  The last segment of the paused run is held back and replaced by the longer copy the next
  run produces.
- **Reconnects** with exponential backoff (1 s → 30 s) when OBS closes the socket; capture
  keeps running while the file keeps growing. If the file stops growing while OBS is
  unreachable, the capture waits up to 60 s for OBS before ending the session. On every
  reconnect `GetRecordStatus` and `GetRecordDirectory` are read again (events were missed, and
  OBS may have been restarted and be recording again although it reports "recording" before
  and after). The file OBS writes (the newest growing one) is captured, whether it is the one
  seen before or not:
  - the file being captured still grows → nothing changes;
  - the next file of a split → the capture goes on in it (same session);
  - another recording (created later, not a split) → the current session ends with its file,
    the new recording gets its own session (read from its start);
  - no capture in progress → that file is captured; if its capture had already ended (OBS was
    unreachable for too long) a new session goes on from the last segment the ended session
    sent (same segment length and the file only grew; otherwise from the start).
  - a file resolved before the reconnect whose capture had not started yet (its session still
    being created, e.g. waiting for the previous one to be analysed) is still captured; the
    reconnect counts for it as for a capture in progress: once it stops growing, the record
    folder is searched (next file of a split, or a new recording that ends its session).
- The recording must be readable while it is written: MKV (OBS default) or fragmented/hybrid
  MP4. Plain MP4 is rejected with a clear message (no session is created for it).

### File mode
`--file` captures a growing (or finished) file without OBS. Used for tests and for setups that
record with other software. The capture ends when the file stops growing for ~3 × `rw_timeout`.

### Segmenting (no re-encode)
```
ffmpeg -hide_banner -loglevel warning -follow 1 -rw_timeout 15000000 -i file:<recording>
       -map 0:v:0 -map 0:a:0? -c copy -f segment -segment_time <segmentSeconds> -reset_timestamps 1
       -segment_list <work-dir>/<sessionId>/run-NN/segments.csv -segment_list_type csv
       -segment_list_flags +live <work-dir>/<sessionId>/run-NN/seg_%05d.mkv
```
- **No `-nostdin`**: stdin is a private pipe; a graceful stop writes `q` to it, so FFmpeg closes
  and lists the segment being written (a hard kill loses it). On Windows FFmpeg runs in its
  own process group, so a Ctrl+C in the console reaches only the companion.
- **Per-run subfolders** `<work-dir>/<sessionId>/run-NN/`: every FFmpeg run over a file (first
  run, re-run after a pause, after a resume, next file of a split) gets its own folder, list and
  numbering from 0. A run's file number is mapped to the session sequence with
  `sequence = number + sequenceOffset` and `start = csvStart + timeOffset` (stored per source
  file in `queue.json`); sequences already handled (`≤ skipThrough`) are deleted, not uploaded.
- Each CSV line `seg_00003.mkv,180.000,240.033` is a completed segment → upload with
  `sequence = 3 + sequenceOffset`, `startOffsetSeconds = 180.0 + timeOffset`,
  `durationSeconds = 60.033`. A line is uploaded once another one follows it, or once FFmpeg
  is seen running 2 s after it; the last line of a run waits for the end of the run. Cuts
  happen on keyframes (OBS default keyframe interval 2 s for streaming), so durations vary.
- **`rw_timeout` is 15 s and must stay ≥ 8 s**: it has to be well above the writer's flush
  interval (Matroska clusters of up to ~5 s); below ~5 s the demuxer hits the end of the
  growing file mid-cluster, resynchronises and silently drops frames.
- **Final segment drain**: after the recording stops growing FFmpeg needs ~3 × `rw_timeout`
  (≈ 45 s) to give up and close the last segment; `q` does not shorten that wait (the read is
  blocked in the file protocol). A watchdog kills FFmpeg 3 × `rw_timeout` + 15 s after `q`.
  A graceful stop is held back while a run still re-reads the part already sent (after a
  pause or a resume), so the tail of the live is never closed into a discarded segment.
- **FFmpeg failures** are counted per recording file and reset only when a run reaches new
  data (> 0.5 s past the furthest segment of that file; re-creating the held segment is not
  progress), whether that run failed further on or ended cleanly (FFmpeg exits 0 when a
  followed file stops growing, so most runs that end on a pause are clean); the error reported
  in `lastError` is cleared then. A failure with nothing captured yet refuses the file; 3
  failures without progress stop its capture with a pt-BR error ("O FFmpeg falhou 3 vezes ao
  ler …"): what was captured is sent, the last segment flagged final, and the session ended.
  FFmpeg missing → exit 1.

### Upload queue
- Persistent queue in `<work-dir>/<sessionId>/queue.json` (session id, API URL, device id,
  mode, `segmentSeconds`, source file with its offsets, run counter, last sequence/end, acked
  sequence, items, and, once the final segment is queued, the mark of where the capture of its
  file stopped: FFmpeg number and file time of the last segment, segment length, file size):
  survives restarts; resumes from the first sequence not acknowledged. The API is idempotent per
  `(session, sequence)`. Never contains the token.
- Retries with exponential backoff (2 s → 60 s, or `Retry-After`) on network/TLS errors / 5xx /
  408 / 429; stops and reports `lastError` on 401/403 (token revoked) or 409 (session ended /
  quota exceeded); other 4xx (413, 422) skip that segment. A segment file is deleted only after
  the API accepted or refused it for good, unless `--keep-segments`.
- Optional bandwidth cap (`--max-upload-mbps`).
- Heartbeat every 10 s (and right after a state change) with `{obsConnected, streaming,
  recording, uploading, queueLength, lastError}`; the response tells the companion to stop
  (`stopRequested`).
- Console shows the connection state (`OBS conectado`, `gravando`, `enviando 3/5`, `offline`).

### Resume rules (unfinished sessions of earlier runs)
- Same recording file (and mode) → **the same session is resumed**: a heartbeat returns
  `nextSequence`; FFmpeg reads the file again from the start, cut with the session's own
  `segmentSeconds` from `queue.json`, whatever `--segment-seconds` says now (the console says
  so: cutting with another length would skip or repeat parts); sequences
  `≤ max(lastSequence, nextSequence − 1)` are skipped.
- Final segment already queued (Ctrl+C once or a stop from the Lives page, then the run
  stopped before `/end`: Ctrl+C again while the API is offline, a crash) → **never resumed**,
  whatever the recording: nothing may follow a final segment (the worker commits peaks up to
  its end), and the stop cut it short, so re-cutting the file would skip the part the short
  copy lacks. Its queue is sent and it is ended (`/end`); when the same file is captured again
  (file mode, or OBS still recording it), the new session goes on from that segment, re-cut
  whole, using the mark in `queue.json` (same segment length and the file only grew; otherwise
  from the start).
- Server says the session is `ending` (it was ended, the run stopped before writing that down)
  → treated as finished: not resumed; its queue (if any) is sent, `/end` repeated, and a new
  session is created (waiting while it is `ending`, see above).
- Heartbeat `409`/`404` → already terminal: dropped locally. Other 4xx → closed as a leftover.
- Another recording → the leftover's queue is sent (heartbeats keep it online meanwhile), its
  last item flagged final, and it is ended (`/end`) before a new session is created.
- OBS mode handles leftovers at startup, without waiting for a new recording: final segment
  already queued → the queue is sent and the session ended right away (whatever OBS records);
  the file OBS still records → resumed by the main loop; a finished recording still on disk →
  the rest of it is captured and the session ended; otherwise → the queue is sent and the
  session ended.
- A session directory locked by another companion is left alone.

## 2. API

Device tokens: `cfd_` + 40 random base62 characters. Stored as SHA-256 hex; only the first 8
characters after the prefix (`tokenPrefix`) are kept in clear for display. Shown once.
Authentication scheme `Device` reads `Authorization: Bearer cfd_…`; revoked tokens → 401.

### User endpoints (cookie/JWT)

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/api/live/companion/tokens` | `{ name }` (1–60 chars) | `201 { device: Device, token }` |
| GET | `/api/live/companion/devices` | – | `{ items: Device[] }` (revoked last) |
| DELETE | `/api/live/companion/devices/{id}` | – | `204`; active sessions of the device → `error` ("dispositivo revogado") |
| GET | `/api/live/sessions` | `?page&pageSize&status` | paged `{ items: LiveSession[] }` |
| GET | `/api/live/sessions/{id}` | – | `LiveSessionDetail` |
| POST | `/api/live/sessions/{id}/stop` | – | `202 { session }` → `stopRequested = true` (the companion ends the session) |

`Device`: `{ id, name, tokenPrefix, createdAt, lastSeenAt, revokedAt, online }`
(`online` = last request within 60 s and not revoked).

`LiveSession`: `{ id, projectId, title, status, deviceId, deviceName, segmentSeconds,
startedAt, endedAt, lastSegmentAt, lastHeartbeatAt, online, stopRequested, endReason,
companionState: { obsConnected, streaming, recording, uploading, queueLength, lastError },
stats: { segmentsReceived, segmentsAnalyzed, segmentsFailed, highlights, recordedSeconds } }`.
`online` = heartbeat within 30 s. `status`: `waiting` (created, nothing received) → `live`
(first segment or heartbeat with `recording: true`) → `ending` (end called; segments still
being analysed) → `ended`; `error` from any non-terminal state (device revoked, quota
exhausted, companion offline for `Live:SessionTimeoutMinutes`, default 10).

`LiveSessionDetail`: `{ session: LiveSession, settings: AnalysisSettings, segments:
LiveSegment[] (latest 50, newest first), highlights: ClipSummary[] (clips of the session's
project, newest first, max 50), candidateCount }`.

`LiveSegment`: `{ id, sequence, startOffsetSeconds, durationSeconds, status:
"received"|"queued"|"analyzing"|"analyzed"|"failed", jobId, error, createdAt }`.

### Companion endpoints (`Authorization: Bearer cfd_…`)

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/api/live/companion/me` | – | `200 { device: { id, name }, user: { displayName }, plan: { code, name } }` |
| POST | `/api/live/sessions` | `{ title?, segmentSeconds? (20–120, default 60), settings? (partial AnalysisSettings), obs?: { version?, recordingPath? } }` | `201 { session: LiveSession, settings }`; `409 plan_limit` when the device already has a non-terminal session or the plan's active project / concurrency limits are reached |
| POST | `/api/live/sessions/{id}/heartbeat` | `{ state: companionState }` | `200 { status, stopRequested, nextSequence }`; `409 conflict` when the session is terminal |
| POST | `/api/live/sessions/{id}/segments` | `multipart/form-data`: `sequence` (int ≥ 0), `startOffsetSeconds` (≥ 0), `durationSeconds` (> 0, ≤ 300), `final` (bool, optional), `file` (≤ `Live:MaxSegmentMb`, default 512; Matroska/WebM or MP4 by magic bytes) | `201 { segment, duplicate: false }`; `200 { segment, duplicate: true }` when `(session, sequence)` already exists; `409` session terminal; `413` too big; `422 invalid_media`; `409 quota_exceeded` (monthly minutes exhausted: the session goes to `error`) |
| POST | `/api/live/sessions/{id}/end` | `{ lastSequence? }` | `200 { session }` → `ending`, then `ended` once every segment job is terminal |

A session is always scoped to the device's user; another user's session id → `404`.
Each session owns one project (`sourceKind: "live"`, `sourcePlatform: "obs"`, title from
the companion or "Live de dd/MM HH:mm"); live projects count as active projects, and an open
session (or one whose segments are still being analysed) takes one concurrent-job slot of the
plan, however many of its segments are queued.

Each segment creates a `media_assets` row (kind `live_segment`, key
`users/{userId}/live/{sessionId}/seg_{sequence:00000}.{ext}`) and a
`processing_jobs` row (kind `live_segment`). **Segments of a session are processed in order:**
a `live_segment` job is only claimable when every lower-sequence job of the same session is
terminal, so the context of segment N is available to segment N+1 (after a failed segment, the
newest earlier `liveContext` is passed). Quota: the worker's
`/probe` reserves the segment duration exactly like an uploaded source.

## 3. Worker: `live_segment` jobs

Payload (in addition to the common fields `jobId, kind, attempt, userId, projectId, leaseSeconds, settings, limits`):

```json
{
  "live": { "sessionId": "…", "segmentId": "…", "sequence": 3, "startOffsetSeconds": 180.0,
            "durationSeconds": 60.0, "final": false, "segmentSeconds": 60 },
  "source": { "assetId": "…", "downloadUrl": "…", "fileName": "seg_00003.mkv",
              "contentType": "video/x-matroska", "sizeBytes": 123, "durationSeconds": 60.0 },
  "previous": { "segmentId": "…", "sequence": 2, "downloadUrl": "…", "startOffsetSeconds": 120.0,
                "durationSeconds": 60.0 },
  "context": { "compositeSample": [1.2, 0.8], "language": "pt" }
}
```

`previous` is `null` for sequence 0 (or when the previous segment failed); `context` is the
`liveContext` object the previous segment's job reported in its `/analysis` summary (`null`
for the first segment).

Flow: download current (+ previous) → `/probe` (current segment only) → features over
`previousTail + current`, where `previousTail` = last `min(45 s, previous duration)` of the
previous segment → fusion with absolute session times → **commit rule**:
`postRoll = clamp(maxClipSeconds / 2, 8, 30)`; segment N commits candidates whose peak lies in
`[start_N − postRoll, end_N − postRoll)` (segment 0: from 0; when `final` is true: up to
`end_N`). Peaks after `end_N − postRoll` are left for segment N+1, which re-detects them with
this segment's tail as context, so no shared state is needed and no peak is reported twice.
Score = `round(100 · (0.5·pctSession + 0.5·sigmoid(peak − threshold)))` where `pctSession` is
the percentile of the peak among `context.compositeSample` + this window (reservoir of at most
200 values, returned as `liveContext.compositeSample`). The detected language is fixed for the
session after the first segment with speech (`liveContext.language`).
Committed candidates → `/candidates` (job-scoped, absolute times) → auto-render up to
`autoRenderTop` of them from the local `previousTail + current` media → `/clips` →
`/analysis` (`summary.liveContext`) → `/complete` (`processedSeconds` = segment duration).

`render_clip` for clips of live projects: the payload carries `segments: [{ downloadUrl,
startOffsetSeconds, durationSeconds, fileName }]` (every segment overlapping the clip range)
instead of a single `source`; the worker downloads and concatenates them (concat demuxer,
stream copy) before rendering. `source` stays the single-file path for uploads.

## 4. Web (Lives page)

- **Dispositivos:** create a token (name → token shown once with a copy button and a warning;
  hiding it asks for confirmation), list devices with `online` state and last activity,
  revoke with confirmation.
- **Sessões:** real states only (`waiting`, `live`, `ending`, `ended`, `error`) with the
  companion state chips (OBS conectado, gravando, enviando, fila N), counters (segmentos
  recebidos/analisados, destaques, tempo gravado), polling every 5 s while any session is
  `waiting`/`live`/`ending`; session detail with segments timeline, highlights (ClipCards)
  and a link to the session's project; "Encerrar sessão" (POST stop) with confirmation.
- **Setup instructions:** how to run the companion from this repository (no installer yet —
  say so), enable obs-websocket, record in MKV.
- Notifications: a `clip_ready` notification per live highlight ("Novo destaque na live").

## 5. Honest limits

- The companion has no installer yet; it runs with Python from this repository.
- Twitch/YouTube "connect my live" buttons are not offered (no authorised ingest path).
- Detection latency ≈ one segment + post-roll + processing time (≈ 1.5–3 min with 60 s
  segments on a 4-core CPU); this is stated on the Lives page.

## 6. Distribution note

The companion is not distributed as an installer yet. The web only shows a download link when
`NEXT_PUBLIC_COMPANION_DOWNLOAD_URL` is configured; until then the Lives page and
Settings → Integrações explain how to run it from this repository (`companion/README.md`).
