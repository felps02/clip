# Live capture (phase 5 design)

Goal: detect highlights while a stream is running, without waiting for the end of a
multi-hour live, using only authorised sources. No scraping of platform streams.

## Source of media: OBS companion

A small local application (`companion/`, Python CLI; packaged later) connects to the
user's OBS Studio through **obs-websocket v5** (bundled with OBS ≥ 28, user enables it and
sets a password). It is authorised by the user on their own machine; nothing is captured
from third parties.

Capabilities used:
- `GetStreamStatus` / `StreamStateChanged` and `RecordStateChanged` events → detect start/end.
- Recording: the companion asks the user to enable "record while streaming" (or starts the
  recording via `StartRecord` with consent). It reads `GetRecordStatus.outputPath` and
  slices the growing recording file with FFmpeg into fixed segments (default 60 s, configurable
  20–120 s) using `-ss/-t` on the file (MKV/fMP4 recommended in OBS for growing-file reads).
- Alternative: OBS **Replay Buffer** (`SaveReplayBuffer`) triggered by the companion when a
  local lightweight detector fires; the saved file is uploaded as a segment with context.

## Flow

```
OBS ──obs-websocket──▶ companion ──PUT segment (multipart, device token)──▶ API /api/live/sessions/{id}/segments
                                                                               │ creates media_asset + processing_job(kind=live_segment)
                                                              worker ◀─ claim ─┘
                                                              worker: analyse segment with carried context (last 30 s of signals)
                                                                      → candidates → auto-render → clips flagged "live"
                                                              API ─▶ notification + dashboard "Lives" page (polling, later SSE)
```

Guarantees and limits:
- Segments carry a `sequence` number; the API rejects duplicates (unique session+sequence)
  so a reconnecting companion never uploads the same content twice.
- The companion keeps a bounded local queue (disk) and retries uploads with backoff after
  network failures; it reports connection state (`connected`, `streaming`, `uploading`,
  `offline`) to the API via heartbeats and shows it in its own console/tray UI.
- CPU/bandwidth: segments are sliced with stream copy (`-c copy`), no re-encode; optional
  cap on upload rate (`--max-upload-mbps`).
- Detection on segments is incremental: the worker receives the previous segment's tail
  signals (`context` in the job input) so windows can span boundaries; a candidate is
  finalised when its post-roll is available (next segment), which bounds latency to about
  one segment plus processing time.
- Event detection, clip generation and final file availability are reported separately
  (`highlight_detected` → `clip_rendering` → `clip_ready`) and shown as distinct states.

## API surface (see `docs/API.md`, "Live")

Device tokens (`POST /api/live/companion/tokens`) authenticate the companion with
`Authorization: Bearer <device token>`; tokens are hashed at rest and revocable.

## Out of scope / honest limits

- Twitch/YouTube "connect my live" buttons are not offered until an authorised ingest path
  exists for that platform. Chat events are not read without the user's OAuth consent.
- The Lives page shows only real states coming from companion heartbeats; when nothing is
  connected it shows how to set up the companion.
- The companion is not distributed yet. The web only shows a download link when
  `NEXT_PUBLIC_COMPANION_DOWNLOAD_URL` is configured; until then the Lives page and
  Settings → Integrações state that it is still in development. Device tokens can already be
  issued and revoked, so the page says so instead of promising an installer.
