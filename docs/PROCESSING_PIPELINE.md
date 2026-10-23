# Processing pipeline

Everything that touches media runs in `processing/` (Python). The API owns the job rows;
the worker executes them and reports through the internal API (`docs/API.md`).

## Job kinds and state machine

Kinds: `analyze_source` (full analysis + auto-render of the top candidates), `render_clip`
(render one `ClipRender` version; for a clip of a live project from the session's segments),
`live_segment` (one segment of a live session: incremental detection + auto-render, see
[live_segment](#live_segment-live-capture-appanalysislivepy); same statuses as
`analyze_source`). The worker claims the kinds of `JOB_KINDS` (default all three).

```
pending ─▶ queued ─▶ preparing_media ─▶ analyzing ─▶ detecting_moments ─▶ generating_clips ─▶ finalizing ─▶ completed
                 │                                                                                   │
                 └──────────────────────────────▶ failed ◀───────────────────────────────────────────┘
                 └──────────────────────────────▶ cancelled (cancel requested; worker aborts at next heartbeat)
```

Allowed transitions are validated by the API (`JobStateMachine`). `render_clip` jobs go
`queued → preparing_media → generating_clips → finalizing → completed`. A `failed` job with
`retryable=true` and `attempt < maxAttempts` is put back to `queued`; the next claim bumps
`attempt` (the counter counts claims, docs/API.md). The reaper moves jobs whose
`lease_expires_at` passed back to `queued` (or `failed` when attempts are exhausted) and logs an
event. A re-queued job keeps its minute reservation until the next probe replaces it. When the
new attempt posts `/candidates`, the API removes the clips the previous attempt had already
auto-rendered for the job (rows, renders and their storage objects), so a retried analysis
never duplicates clips.

`queued → preparing_media` is performed by the **claim itself**: `POST /claim` returns the
payload with the job already in `preparing_media` (progress 0, lease and `workerId`
recorded). The worker therefore never posts `/status preparing_media`; its first `/status`
is `analyzing` (`render_clip`: `generating_clips`). `/status` is for transitions; re-posting
the running status the job already has is accepted as an idempotent progress update (`200`),
and progress / `statusMessage` updates inside a status go through `/heartbeat`. Every internal
call carries `X-Worker-Id` (the claim's `workerId`; mandatory, `400` without it), so a worker
whose lease expired and was re-claimed elsewhere gets `409` on every report instead of
clobbering the new run.

Progress ranges (analyze_source): preparing 0–10 · analyzing 10–60 (audio 10–20,
transcription 20–45: speech scan 20–24 "Procurando fala no áudio…", language 24
"Identificando o idioma da fala…", transcription 25–45; visual 45–60) · detecting 60–65
(fusion, then the optional LLM stage "Avaliando os momentos com o modelo de linguagem (1 de
2)…") · generating clips 65–95 · finalizing 95–100. `statusMessage` is a short pt-BR
sentence for the UI ("Transcrevendo áudio (12 de 60 min)…").

## Worker loop (`app/worker/loop.py`)

```
on start: remove the job directories a crashed worker left in WORK_DIR (see below)
loop:
  payload = POST /internal/workers/{workerId}/claim {kinds}   (job is now preparing_media)
  if 204: sleep(poll_interval); continue
  if the payload does not match the contract (unknown enum value, missing field...):
    POST /fail {code: "invalid_payload", retryable: false} for that jobId — the job is already
    leased to this worker — and claim again right away (no backoff: the next job may be fine)
  run job in a thread; a heartbeat thread posts /heartbeat every leaseSeconds/3 with progress
    → if cancelRequested: raise JobCancelled (statusMessage "Cancelando…"); on 409 the lease
      is lost: abort silently. The heartbeats go on while the cancelled job unwinds (an upload
      or LLM request in flight, a model being loaded...) until the final status is posted, so
      the lease never expires on a cancelled job (the reaper would re-queue it)
  progress/message updates → POST /heartbeat (throttled to one per 2 s = the UI polling)
  transitions → POST /status {analyzing | detecting_moments | generating_clips | finalizing}
  on success: POST /complete {processedSeconds, summary}   (render_clip: no summary)
  on JobCancelled (cancel_requested): POST /status {status: cancelled} — also when the cancel
    arrives during the source download or an upload; nothing is posted for a lost lease or a
    shutdown (the API consumes the reserved minutes when the analysis had already started)
  on MediaError/QuotaError (user errors): POST /fail {retryable: false, processedSeconds}
    (a terminal failure charges the processed seconds, capped by the reservation)
  on TransferError (signed download/upload with the storage, HTTP error of any status — 401,
    403, 404, 410, 413, 5xx... — or network error after the retries): POST /fail {code:
    "transfer_failed", retryable: true} without processedSeconds — a storage problem is never
    invalid_media/render_failed and never charged; the API re-queues the job and a fresh claim
    issues new signed URLs (the API's maxAttempts bounds a permanent problem). A source object
    that is gone (404/410) says so in the message ("o arquivo não foi encontrado no
    armazenamento"). Exception: the upload of one auto-rendered clip of an analysis, once the
    storage accepted an earlier clip of the job, only drops that clip (error event, see
    analyze_source step 8); the first clip's upload or two failures in a row fail the job
  on a missing ffmpeg/ffprobe binary (FfmpegNotFound, also while rendering): POST /fail
    {code: "worker_error", retryable: true} — a deployment problem, never render_failed and
    never a failed render-result for the user's clip
  on unexpected exception: POST /fail {retryable: true, code: "worker_error"} (API decides re-queue)
  always: delete the job's work directory, then sweep this worker's own leftovers (below)
```

A cancel wins over any error it provokes (killed ffmpeg, aborted transfer): the outcome is
`cancelled`, never `invalid_media`. The work directory is `WORK_DIR/{jobId}`: the id must
be a single safe path segment (`[A-Za-z0-9][A-Za-z0-9_-]{0,79}`, GUIDs qualify) and the
directory must resolve to a direct child of `WORK_DIR`, otherwise the job is refused
without touching the file system or the API.

**Leftover work directories.** A worker that dies mid-job (crash, OOM, host restart) leaves
`WORK_DIR/{jobId}` behind — several GB for a long stream. Right after creating that directory
the worker writes a marker into it, `.clipforge-job` (JSON: `jobId`, `workerId`,
`createdAt`); when the marker cannot be written the directory is removed and the job fails
with `worker_error`, retryable (a full disk is a worker problem). When the worker starts it
removes the directories of `WORK_DIR` that carry that marker — a regular file naming the
directory's own job id — and in which nothing changed for `WORK_DIR_STALE_HOURS` (default 6,
`0` disables the sweep). A name proves nothing (`storage`, `tools`, `models` or a GUID-named
folder all look like job ids), so nothing unmarked is ever touched: other files, links, the
data of other software when `WORK_DIR` points at a shared folder by mistake, directories left
by workers older than the marker (delete those by hand). A tree that cannot be walked
completely (more than 10 000 entries, an unreadable subdirectory) counts as changed just now.
The heartbeat refreshes the mtime of the running job's directory every `leaseSeconds/3`,
so a worker starting on a `WORK_DIR` shared with another worker never removes a live job.
`WORK_DIR` should still be a directory of its own.

**After every job** the same sweep runs again with a shorter age, restricted to what this
worker created: directories whose marker names this worker's id (`workerId` = the claim's
`X-Worker-Id`), never the current job's, unchanged for `WORK_DIR_ORPHAN_MINUTES` (default
15, `0` disables). It catches the leftovers that would otherwise wait for the next restart
(a directory `rmtree` could not delete because a file was still locked, a crash of this
worker just before it was restarted). Another worker's directories are only ever removed by
the start-up rule (6 h), and a live job's directory is never 15 minutes old (heartbeat).

**Retries of internal calls.** Connection errors, timeouts and 5xx answers are retried with
exponential backoff, but after a read timeout, a dropped connection or a 500/502/504 the API
may already have applied the request, so each endpoint has a policy:

| policy | endpoints | after a failure that may have been applied |
|---|---|---|
| idempotent | `/heartbeat`, `/probe`, `/upload-urls`, `/candidates` (replaces the job's candidates), `/analysis`, `/render-result` | resent |
| reconcile | `/status`, `/complete`, `/fail` | resent; a `409` answering that resend means the first attempt was applied (the transition happened, the job is already finished) and is not an error. Re-posting the current running status is a `200` progress update anyway |
| once | `/claim`, `/clips`, `/events` (each call creates rows) | **not** resent (only a request that never reached the API — connection refused, connect timeout, `503` — is). A claim that may have leased a job is left to the API's lease reaper (a resend would lease a second job). Unconfirmed `/clips`: the job goes on (warning event "Não foi possível confirmar o registro dos clipes…") and `clipsRendered` is left out of the summary so the API counts the renders it stored. A lost event is not resent |

Messages are cut to the API's validation limits before they are sent (UTF-16 code units, as
the API counts them): job events 1000, `/heartbeat` and `/status` messages 500, `/fail`
messages 1000; error text quoted inside an event is limited to 400 characters.

One job at a time per worker by default (`MAX_PARALLEL_JOBS=1`, CPU-bound). Every FFmpeg
subprocess runs with a timeout derived from the source duration and is killed on cancel;
PCM/rawvideo pipes are guarded by a watchdog that kills a stalled process when the timeout
expires, so a blocked pipe read cannot keep the job (and its lease) alive forever.

## analyze_source steps

1. **Download** the source to `WORK_DIR/{jobId}/source.ext` (streaming, resume not needed;
   a cancel during the download aborts it and ends the job as `cancelled`).
2. **Probe** with ffprobe (`-print_format json -show_format -show_streams`) → report
   `/probe`. `width`/`height` are the *display* dimensions: rotation metadata (`Display
   Matrix` side data or the legacy `rotate` tag of phone recordings) is applied, matching
   ffmpeg's autorotate on decode, so framing, frame sampling and the webcam region all use
   the same orientation. Stop if the API answers 409 (quota/plan/invalid media): the job is
   already failed server-side and the worker posts nothing else.
   Sources without a video stream are rejected in the MVP (clips cannot be rendered): the
   API answers `/probe` with `hasVideo: false` by failing the job (`invalid_media`, "arquivo
   sem vídeo") and a 409, so the worker just stops; the worker's own check (`/fail
   invalid_media` "arquivo sem vídeo") is a fallback should the API accept the probe. A probe
   with `durationSeconds ≤ 0` is failed the same way (`invalid_media`, 409); the worker
   already rejects durations of 0.5 s or less itself. (The
   analysis functions themselves degrade honestly — visual `available: false` — which the
   `POST /analyze` development endpoint exposes for audio-only files.)
3. **Audio features** (`analysis/audio_features.py`): decode to 16 kHz mono PCM through
   ffmpeg (`-f s16le`), frames of 100 ms (hop 100 ms): RMS energy (dBFS), spectral centroid,
   zero-crossing rate, spectral flux, onset strength. Derived per-second series:
   - `energy_z`: z-score of smoothed energy vs. a rolling 90 s baseline (robust: median/MAD);
   - `burst`: short bursts (≥ 6 dB above baseline for 0.3–2 s);
   - `sustained`: 3 s windows above baseline;
   - `scream_like`: energy_z > 2 ∧ centroid > 2.5 kHz ∧ zcr high for ≥ 0.4 s (heuristic);
   - `laughter_like`: amplitude modulation of the envelope in the 3–8 Hz band with moderate
     energy, sustained ≥ 1 s (heuristic);
   - `silence`: energy below −45 dBFS for ≥ 0.5 s (used to snap cut points).
   - typical level of the recording (`typicalLevelDbfs` in `signals.json`): median of the
     per-second level over the non-silent seconds, the reference of the reasoning text.
   All heuristics are labelled as such in `signals.audio.method`. The audio is decoded in
   full or the job fails (an undecodable file is `invalid_media`), so audio has no partial
   coverage.
4. **Transcription** (`analysis/transcription.py`, `TranscriptionProvider`): faster-whisper
   with word timestamps and VAD filter, model from `limits.transcriptionModel` (the plan:
   free `base`, creator/pro `small`) bounded by `WHISPER_MODEL` (the worker's cap, default
   `small`; the model is downloaded once into `MODELS_DIR`, `small` ≈ 480 MB), audio
   decoded in chunks of `TRANSCRIPTION_CHUNK_SECONDS` (300 s, cut at silences).
   - **Speech scan.** Every chunk first goes through the Silero voice activity detector
     bundled with faster-whisper (the options `transcribe(vad_filter=True)` uses; ≈ 400×
     real time on 4 cores, ~1 min of CPU for a 6 h stream). **No speech anywhere** → no
     language detection and no transcription at all: the transcript is
     `available: true, speechDetected: false`, `language: null` (never a language made up
     from noise, such as "ko" 0.58), the clips render without captions, the job gets the
     info event "Nenhuma fala detectada no áudio: sem idioma e sem legendas nos clipes" and
     `speechDetected: false` goes to the summary (`summary.speechDetected` and
     `summary.signals.transcript.speechDetected`) and to every candidate's
     `signals.transcript`. Chunks without speech are skipped (they are not gaps). Speech
     regions that yield no word (music, noise) also end as `speechDetected: false` with no
     language. A transcript without speech is left out of the composite (the other signals
     are renormalised, as for a missing signal).
   - **Language** (`language: "auto"`): detected **once per job**, with
     `WhisperModel.detect_language(audio, vad_filter=True, language_detection_segments=N,
     language_detection_threshold=0.5)` over up to N = 6 windows of 30 s of speech spread
     evenly over the speech of the whole recording (not only its beginning; a window is at
     most 90 s of audio, the densest window first because faster-whisper stops at the first
     30 s segment whose language is clear). Every chunk is then transcribed with that fixed
     language. Recorded: `language`, `languageProbability`, `languageSource`
     (`detected` | `settings`), `languageConfidence` (`low` below 0.5 — the best language is
     still used, and the job gets the warning "Idioma da fala identificado com baixa
     confiança: …"), and in `transcript.json` the `languageDetection` details (spans,
     top-5 candidates). Languages of `WHISPER_DETECT_IGNORE` (default `la,sa`: Latin and
     Sanskrit, where Whisper's language identification lands for accented or synthetic
     speech) are never taken: the best other candidate is used with its own probability
     (`languageDetection.ignored` keeps Whisper's pick). A language set in the settings is
     used as is (no detection, `languageProbability: null`).
   - **CPU cost** (measured on the 4-core development machine, int8): a 70 s clip with
     28 s of speech took 7 s with `base` and 23 s with `small` (model load included) — `small`
     costs about 3× the CPU time of `base` (roughly 0.25× vs 0.8× the speech duration on
     4 idle cores; more when the machine is busy). `tiny` is not used by any plan: it
     mis-detects the language.

   Output: segments + words `{start, end, word, probability}` saved to
   `analysis/transcript.json` (with `speechDetected`, `speechSeconds`, the language fields
   above, `coverage`, `gaps`). If the model cannot
   be loaded (offline, missing), transcription is marked unavailable and the pipeline
   continues; the failure is remembered for a backoff only (1 min, doubling per consecutive
   failure up to 30 min — `/health` shows `whisper.errors` and `whisper.retryInSeconds`), so
   a transient network/disk problem during the first model download heals on a later job
   without restarting the service. A chunk that fails after the first one (decoder error or
   timeout, model error) becomes a **gap**: the transcript stays available but reports
   `coverage` (share of the recording transcribed), `gaps` (`[[start, end], …]`) and an
   `error` naming the missing part; a failing first chunk makes the transcript unavailable.
   Derived series: `speech_rate_z` (words/s), `keyword_score` (lexicon hits with
   weights by category, pt-BR + en, see `analysis/lexicon.py`; the user's
   `settings.customKeywords` are matched the same way — accent/case-insensitive, longest
   phrase first — with weight 3 and flagged `custom: true`; a custom keyword that is already
   a lexicon phrase keeps its category, any other gets the category `custom`, which no
   candidate category rule uses), `exclamation` (`!`, `?!`),
   `pause_before` (silence then burst = reaction pattern; never right after a gap).
   `HF_HUB_DISABLE_SYMLINKS_WARNING=1` is set (unless already defined) before faster-whisper
   imports huggingface_hub: on Windows without Developer Mode the model cache stores copies
   instead of symlinks, which works, and the warning was only log noise.
5. **Visual features** (`analysis/visual_features.py`): frames sampled at 2 fps, 320 px wide,
   via ffmpeg rawvideo pipe → OpenCV: `motion` (mean abs diff), `scene_cut` (histogram
   distance above threshold), `flash` (global brightness jump). Z-scored per second. When the
   frame pipe stops before the end of the file (ffmpeg timeout, decoder error, a video
   stream shorter than the file) the features stay available for the decoded part only:
   `coverage` < 1, `gaps` with the rest and an `error` ("frames decoded up to 600s only:
   …"); fewer than two frames make the signal unavailable.
   Optional **webcam detection** (`analysis/webcam.py`): faces on ~40 sampled frames with
   OpenCV's YuNet detector (`cv2.FaceDetectorYN`, model bundled in `processing/assets/models`,
   MIT) — OpenCV 5 wheels no longer ship the Haar cascades, which remain a fallback on older
   builds; if faces cluster in a stable rectangle (typically a corner) → `webcamRegion`
   normalised `{x, y, w, h}` with confidence; otherwise `null`.
6. **Optional LLM** (`analysis/llm.py`, `LLM_PROVIDER`, default `none`; never required,
   never a reason to fail a job). Providers: `local` — the product's own model, an
   OpenAI-compatible llama.cpp `llama-server` at `LLM_BASE_URL` (default
   `http://127.0.0.1:8090`, Qwen3-1.7B Q4_K_M; requests use `response_format: json_schema`
   (strict), `chat_template_kwargs: {enable_thinking: false}` and `cache_prompt`);
   `gemini` — Google's REST `models/{GEMINI_MODEL}:generateContent` with a
   `responseSchema` (`GEMINI_API_KEY` from Google AI Studio, default model
   `gemini-2.5-flash`, thinking budget 0; the key goes in the `x-goog-api-key` header; on
   the free tier Google may use the prompts — transcript excerpts — to improve its
   products, so the administrator chooses it knowingly); `anthropic` — paid API, opt-in
   only (`ANTHROPIC_API_KEY`). Only the opt-in `anthropic` provider also rates transcript
   windows before fusion (0–1, adds the `llm_score` series, as before). The **assist
   stage** (`analysis/llm_assist.py`) runs after fusion with any provider:
   - **re-rank** — when `settings.instructions` or `settings.focusCategories` are present,
     the transcript excerpts (≤ 300 characters) of the top 20 candidates are rated 0–10
     against them; a rated candidate's rank priority moves by `2 × (rating − 5)`
     (−10…+10) and the ranks are renumbered. The stored score never changes. Without a
     provider the instructions are stored and ignored (`signals.llm.available: false`);
   - **titles** — a short pt-BR title for every candidate that will be rendered (the top
     `autoRenderTop`, within those 20). A title is used only when it passes the validation:
     ≤ 60 characters and ≤ 10 words, no `#`/`@`, link, emoji, laughter (`kkk`, `haha`) or
     non-Latin letters, and **grounded** — most of its content words come from its excerpt:
     at least 60 % of them rounded up, and at least 2 when the title has 3 or more (accents
     and case ignored, a word and its longer form count: `test`/`teste`,
     `jogada`/`jogadas`). Content words have 4+ characters or a digit and are not
     stopwords or spoken fillers (`vamos`, `cara`, `mano`, `olha`, `tipo`, `assim`,
     `coisa`, `falar`…), so one shared filler or term never grounds an invented title
     ("Vamos vencer o campeonato mundial" for "agora vamos para a próxima partida" and
     "Clutch no último round" for "what a CLUTCH" are rejected). Otherwise the clip keeps
     the title it would have had (the window rating's, then `settings.title`). The
     accepted title is the clip's title (`/clips`) and, like `settings.title`, it is drawn
     at the top of the clip when subtitles are enabled;
   - **requests** — one block of numbered excerpts (byte-stable prefix with the system
     prompt, so the server's prompt cache serves every request of a job) asked in batches
     of 10; the answer is a JSON object keyed by the excerpt numbers (schema-constrained:
     no excerpt skipped or invented);
   - **budget** — the stage is bounded by `LLM_BUDGET_SECONDS` (default 100): a request
     gets `min(LLM_TIMEOUT_SECONDS (60), what is left)`, none starts with less than 5 s
     left, and the stage stops at the first failure (timeout, connection, invalid answer),
     keeping what it obtained. A stop is a warning job event ("Modelo de linguagem (local)
     interrompido: …. A análise seguiu sem ele"); a cancelled job aborts the request in
     flight at once. Measured on the 4-core development machine with the running server:
     20 candidates (re-rank + 20 titles) in 40 s (2 requests), a single request ≈ 8 s cold;
   - `signals.llm` (summary) reports `available`, `provider`, `model` (the server's model
     id for `local`), `seconds`, `budgetSeconds`, `rerank {status, requested, rated}` and
     `titles {status, requested, accepted, rejected{reason: n}}`; each rated or titled
     candidate gets `signals.llm {available: true, provider, model, relevance, rankBonus,
     title}`. Measured quality with Qwen3-1.7B: titles are mostly usable and the
     validation rejects the copies and inventions; the relevance ratings are noisy (a
     small model), hence the bounded ±10 priority. With the stricter grounding rule
     (round 3 review), 20 pt-BR live excerpts on the busy development machine: 19 titles
     accepted, 1 rejected (`not_grounded`: only fillers), 68 s for 2 requests; the model
     mostly reuses the excerpt's words, so the rule costs few titles.
7. **Fusion & candidates** (`analysis/fusion.py`):
   - composite(t) = Σ wᵢ · clamp(zᵢ(t), −1, 4) over available signals, weights
     audio 0.45 · transcript 0.30 · visual 0.25 (renormalised when a signal is missing;
     `llm_score` adds 0.2 when present). A signal with gaps is left out of the seconds it
     did not analyse and the weights are renormalised **per second** over the signals that
     did, so an untranscribed chunk or a part without decoded frames neither counts as a
     calm moment nor lowers the composite there;
   - `threshold = 2.4 − 1.8 · sensitivity/100` (sensitivity 0 = very strict, 100 = loose);
   - peak picking with minimum distance `minClipSeconds`;
   - window = expand from the peak while composite > threshold/2, pre-roll ≥ 3 s, post-roll
     ≥ 4 s, bounded by `[minClipSeconds, maxClipSeconds]`, then snap start/end to the
     nearest silence or transcript segment boundary (±2 s);
   - merge overlapping/adjacent windows (gap < 2 s) keeping the max score; non-max
     suppression against near-duplicates (IoU > 0.5);
   - score 0–100: percentile of the peak within the recording blended with absolute
     composite (`score = round(100 · (0.5·pct + 0.5·sigmoid(composite − threshold)))`),
     documented as an **internal relevance estimate**;
   - category by dominant contributors: scream/hype keywords → `reaction`/`hype`,
     laughter → `funny`, tension lexicon/quiet-then-burst → `tension`, high motion +
     gameplay lexicon → `gameplay`, transcript-only strong + low audio → `quote`, otherwise
     `highlight`;
   - reasoning: pt-BR sentence built from the top 2–3 contributing signals, e.g.
     "Volume 18 dB acima do nível típico da gravação, fala rápida e menção a 'clutch'."
     Every statement is computed from the signals, never a z-score dressed up as a ratio:
     the audio fragment gives the level of the window's loudest second (smoothed RMS) in dB
     above the recording's typical level when that difference is ≥ 3 dB; otherwise (a moment
     loud only for its own part of the recording) it describes `energyZ` — a robust z-score
     against the rolling ~90 s baseline — in bands: "energia de áudio acima / bem acima
     (z ≥ 2) / muito acima (z ≥ 4) do normal naquele trecho". `energyZ` stays in the signals;
   - **focus** (`settings.focusCategories`, round 3) adjusts the weights before peak
     picking and ranking — funny: `laughter_like` ×1.8 and funny lexicon ×1.5;
     hype/reaction: `scream_like` and `burst` ×1.5, hype and reaction lexicon ×1.5; quote:
     transcript base weight ×1.5 (renormalised) and quote lexicon ×1.5; gameplay: visual
     motion ×1.5 and gameplay lexicon ×1.5; tension: tension lexicon ×1.5 and
     `pause_before` ×1.5; highlight: nothing. Several focused categories never stack on one
     term (the largest multiplier). Candidates whose category is focused get **+10 rank
     priority**; the stored `score` stays the unmodified internal estimate — it is computed
     from the composite *without* the focus (`signals.focus.neutralComposite`), so the same
     moment gets the same score whatever the focus. With a focus the top
     `3 × maxCandidates` windows are ranked before the list is cut, so the priority can bring
     a focused moment in. `signals.focus` reports `{categories, adjustments, inFocus,
     rankBonus, customKeywords (the user's keywords found in the window), neutralComposite}`;
   - custom keywords (`settings.customKeywords`) count like lexicon hits of weight 3 and are
     always named in the reasoning ("menção a 'patrocínio'", they come first);
   - **short sources** (round-3 P2): when the probed duration is below `minClipSeconds`, the
     effective min/target/max are clamped to the duration (the whole video is the limit),
     the job gets the info event "Vídeo mais curto que a duração mínima; usando o vídeo
     inteiro como limite" (`data: {durationSeconds, minClipSeconds, effectiveMinClipSeconds,
     effectiveMaxClipSeconds}`), and when no moment passes the threshold but anything
     happens in the video (non-silent audio, a transcribed word, moving frames or a scene
     cut) the whole video becomes the single candidate, with its honest (low) score and the
     reasoning "Vídeo inteiro usado como clipe por ser mais curto que a duração mínima: …";
     such candidates carry `signals.shortSource {durationSeconds, wholeVideo,
     belowThreshold}`. `signals.json` records the `effectiveSettings`;
   - order by rank priority (score + focus bonus, + the LLM bonus of step 6), then score,
     keep the top `maxCandidates`, `rank` 1..N, report `/candidates`.
8. **Render** the top `autoRenderTop` candidates (`render/renderer.py`; an absent
   `autoRenderTop` means every candidate, `maxCandidates` — "todos os momentos
   encontrados"; the API caps it by the plan's `maxAutoRenderPerJob`), one `ClipRender`
   each in `settings.aspectRatio` with subtitles if enabled, plus a JPEG thumbnail at the
   midpoint. The clip title is the LLM's validated suggestion, else the window rating's,
   else `settings.title`. A failure in one clip is logged as a job event and does not fail
   the job nor stop the next renders (tested with 15 renders, the 7th failing; a partial
   output is deleted) (a missing ffmpeg binary does: retryable `worker_error`). Each
   rendered clip gets an info event with the resolved layout ("Clipe 1 renderizado:
   1080x1920, webcam + jogo, com legendas"; `data: {candidateRank, layout, width, height,
   hasSubtitles}`).
   - **Each clip is uploaded right after its render** (`/upload-urls` for
     `clip-NN.mp4` + `clip-NN.jpg`, one call per clip, then the PUTs) and its local files
     (mp4, jpg, ass) are deleted before the next render, so `WORK_DIR` holds the source
     plus one clip at a time, even with 50 clips on the pro plan (1080p clips are tens of
     MB each). The storage keys are kept for `/clips`.
   - A clip whose upload fails (HTTP error or network error after the client's retries)
     **once the storage has accepted an earlier clip of the job** is dropped with an error
     event ("Falha ao enviar o clipe 2 (…): …", `data: {candidateRank, error}`), like a
     failed render: the clips already uploaded and the next ones are kept (the user can
     generate the missing one from the project page). The first clip's upload failing, or
     two clip uploads failing in a row, means the storage is unavailable: the job fails
     `transfer_failed` (retryable, nothing charged) as before.
   - **Free disk space** on the `WORK_DIR` volume is checked before each render: below
     `RENDER_MIN_FREE_MB` (default 500, `0` disables) the remaining clips are not
     rendered — warning event "Espaço em disco insuficiente no servidor de processamento
     (120 MB livres, mínimo 500 MB): 2 de 3 clipes não foram gerados. Gere-os pela página
     do projeto" (`data: {freeMb, minFreeMb, skipped, candidateRanks}`) — and the job goes
     on with the analysis and the clips already uploaded.

   Report `/clips` (after step 9's upload, from the stored keys):
   each clip's `framing` carries the **requested** `settings.framing.mode` (normally
   `auto`, so a later re-render decides the layout again), the focus and the webcam region
   that produced a `webcam-split` (otherwise `settings.framing.webcamRegion`).
9. **Upload the analysis artifacts** (status `finalizing`; `transcript.json` with
   `coverage`/`gaps`, `signals.json` with per-second series, availability flags and
   coverage — the clips are already in the storage), register the clips (`/clips`) and
   report `/analysis`. Signals that are unavailable or only partially covered also get a
   warning job event ("Sinal 'visual' analisado em apenas 17% da gravação: …"). The
   optional `llm` signal gets one only when a
   configured provider failed (no event when `LLM_PROVIDER=none`; the assist stage reports
   its own stop). A recording without speech gets the info event "Nenhuma fala detectada
   no áudio: sem idioma e sem legendas nos clipes", a low-confidence language the warning
   "Idioma da fala identificado com baixa confiança: en (42%). …".
10. **Complete** with `processedSeconds = duration` and the summary `{signals, webcamRegion,
    language, processedSeconds, candidatesFound, clipsRendered, speechDetected}` (the same
    object as `/analysis`; `signals` = the availability flags, with `coverage` for
    transcript and visual, and for the transcript `speechDetected`, `speechSeconds`,
    `languageSource`, `languageProbability`, `languageConfidence`; `llm` as in step 6;
    `clipsRendered` is omitted when the `/clips` registration could not be confirmed,
    `speechDetected` when transcription did not run — the API keeps `signals` as sent, so
    the web reads `result.signals.transcript.speechDetected`). `render_clip` completes with `{processedSeconds: 0}` and no summary: its
    result went through `/render-result`, its layout through an info event.

### `signals` object on a candidate

```json
{
  "audio": { "available": true, "method": "heuristic-v1", "energyZ": 24.2,
             "levelAboveTypicalDb": 18.4, "peakDbfs": -6.2,
             "burst": true, "screamLike": 0.8, "laughterLike": 0.1 },
  "transcript": { "available": true, "speechDetected": true, "model": "faster-whisper/base",
                  "language": "pt", "languageProbability": 0.97, "languageConfidence": "high",
                  "coverage": 1.0, "speechRateZ": 1.4,
                  "keywords": [ { "word": "patrocínio", "category": "custom", "weight": 3, "custom": true },
                                { "word": "clutch", "category": "gameplay", "weight": 2 } ],
                  "exclamations": 2, "pauseBefore": false },
  "visual": { "available": true, "coverage": 1.0, "motionZ": 2.2, "sceneCuts": 1, "flash": false },
  "llm": { "available": true, "provider": "local", "model": "Qwen3-1.7B-Q4_K_M",
           "relevance": 8, "rankBonus": 6, "title": "Clutch no último round" },
  "focus": { "categories": ["gameplay"], "adjustments": { "lexicon.gameplay": 1.5, "visual.motion": 1.5 },
             "inFocus": true, "rankBonus": 10, "customKeywords": ["patrocínio"],
             "neutralComposite": 2.6 },
  "composite": 2.9, "threshold": 1.5, "weights": { "audio": 0.45, "transcript": 0.30, "visual": 0.25 },
  "contributors": [ "audio.energy", "transcript.keyword", "visual.motion" ],
  "peakSeconds": 1834.5,
  "scoreNote": "internal relevance estimate (0-100), not a probability of going viral"
}
```

| key | meaning |
|---|---|
| `audio.energyZ` | max robust z-score of the level in the window (vs. the rolling ~90 s baseline); not a ratio |
| `audio.levelAboveTypicalDb` | level of the window's loudest second minus the recording's typical level, dB (`null` when the recording has no non-silent second) |
| `audio.peakDbfs` | sample peak in the window, dBFS |
| `transcript.coverage`, `visual.coverage` | share of the **window** the signal analysed (0–1; < 1 next to a gap — untranscribed chunk, frames missing after a decoder failure) |
| `transcript.pauseBefore` | silence followed by speech/burst inside the window |
| `transcript.speechDetected` | `false` when the recording has no speech (then `language` is `null` and the clips have no captions) |
| `transcript.languageConfidence` | `high` / `low` (probability below 0.5) for a detected language; `null` when it came from the settings |
| `transcript.keywords[].custom` | `true` for the user's own keywords (`settings.customKeywords`) |
| `llm` | window rating (anthropic): `{available: true, score, provider, model}`; assist stage: `relevance` (0–10), `rankBonus` (−10…+10) and/or the accepted `title`; otherwise `{available: false, provider?, error?}` |
| `focus` | the focus adjustments applied (see step 7); `inFocus`/`rankBonus` (+10) for a focused category; `neutralComposite`: the composite without the focus, which the score comes from |
| `shortSource` | only for a source shorter than `minClipSeconds`: `{durationSeconds, wholeVideo, belowThreshold}` |
| `weights` | weights in effect at the peak: the renormalised nominal weights, unless a signal has a gap there (then 0 for it and the others renormalised) |
| `contributors` | top 3 contributing signals (`audio.energy`, `audio.scream`, `audio.laughter`, `audio.burst`, `transcript.speech_rate`, `transcript.keyword`, `transcript.exclamation`, `transcript.pause`, `visual.motion`, `visual.scene_cut`, `visual.flash`, `llm.score`) |
| `peakSeconds` | time of the composite peak the window was built around |
| `scoreNote` | reminder that the score is an internal estimate |

## live_segment (live capture, `app/analysis/live.py`)

A live session is the creator's own OBS recording, cut by the companion into Matroska
segments of `segmentSeconds` (stream copy, keyframe cuts, so durations vary) and uploaded
while the recording grows (`docs/LIVE_CAPTURE.md`). Each segment is one `live_segment` job.
The API hands the jobs of a session out **in order** (a segment is claimable once every
lower-sequence job of its session is terminal), so a segment's job receives what the
previous one reported. Statuses and progress ranges are those of `analyze_source`
(`preparing_media` by the claim → `analyzing` → `detecting_moments` → `generating_clips` →
`finalizing` → `/complete`); the worker claims the kind by default
(`JOB_KINDS=analyze_source,render_clip,live_segment`).

Payload (besides the common `jobId, kind, attempt, userId, projectId, leaseSeconds,
settings, limits`):

```json
{
  "live": { "sessionId": "…", "segmentId": "…", "sequence": 3, "startOffsetSeconds": 180.0,
            "durationSeconds": 60.0, "final": false, "segmentSeconds": 60 },
  "source": { "assetId": "…", "downloadUrl": "…", "fileName": "seg_00003.mkv",
              "contentType": "video/x-matroska", "sizeBytes": 123, "durationSeconds": 60.0 },
  "previous": { "segmentId": "…", "sequence": 2, "downloadUrl": "…",
                "startOffsetSeconds": 120.0, "durationSeconds": 60.0 },
  "context": { "compositeSample": [1.2, 0.8], "language": "pt",
               "sequence": 2, "committedUntil": 172.0 }
}
```

`live.sequence`, `live.startOffsetSeconds` and `source` are required (otherwise the claim
fails the job with `invalid_payload`); a duration that is missing, zero or not a number is
"unknown" (the probed one is used). `previous` is `null` for sequence 0 or when the
previous segment failed; `context` is the `liveContext` the previous segment's job reported
(`null` for the first segment). Every time the worker reports is a **session time**
(seconds since the start of the recording: `startOffsetSeconds` + time in the segment).

### Flow

1. **Download** the segment (`source`), **probe** it and report `/probe` for this segment
   only (a `409` stops the job, a segment without video is `invalid_media`, as for an
   upload; the API reserves the segment's duration). Any positive duration is accepted
   (an upload needs more than 0.5 s): the last segment of a session can be a sliver, which
   the previous segment's tail gives enough to analyse.
2. **Previous segment** (when `previous` is the segment `sequence − 1` and ends where this
   one starts, ± 2 s): downloaded after the probe, probed locally (never reported). It is the
   context of this segment's start. When it cannot serve — gone from the storage
   (404/410), empty, unreadable, another audio/video format (codec, size, sample rate,
   channels: a recording restarted with other settings), not contiguous — the job goes on
   without it and posts the warning event "Segmento anterior não usado como contexto (…): o
   início deste segmento foi analisado sem ele". Any other storage error (5xx, network) is
   `transfer_failed` (retryable), like the segment's own download.
3. **Window file.** The previous segment and this one are joined without re-encoding:
   FFmpeg's concat demuxer, `-map 0:v:0 -map 0:a:0? -c copy` into Matroska
   (`app/media/concat.py`), in `WORK_DIR/{jobId}`; nothing is decoded into memory, the cost
   is a file copy. The list names the parts by bare names (the demuxer's `safe` mode stays
   on) and states the previous segment's duration **as the API knows it** (the companion's
   cut list; the file's own duration when they differ by more than 1 s): the segment
   muxer's files start with the first packet's offset (`seg_00001.mkv`: cut list 26.666 s,
   file 26.706 s), so only the cut list's durations put each segment where the recording
   has it (checked by cross-correlating the joined audio with the original). A join that
   fails or yields an unexpected duration (± 2 s) falls back to the segment alone with the
   warning "Não foi possível juntar o segmento anterior a este (…)".
4. **Analysis window.** `previousTail` = the last `min(45 s, previous duration)` of the
   previous segment, rounded so that the analysis starts on a **whole session second**:
   `origin = ceil(start − min(45, previous duration))`, the analysis skipping the
   `origin − fileOrigin` first seconds of the window file (every decoder seeks there: PCM,
   frames, speech scan, language spans, webcam samples). Every window therefore computes
   its per-second series on the same session-wide grid, and the same moment lands on the
   same second in the two windows that see it. Without a previous segment the analysis
   starts on the first whole second of the segment. Then the **same feature extraction and
   fusion as `analyze_source`** (steps 3–7, with `settings.focusCategories`,
   `customKeywords` and `instructions`), with these differences:
   - **commit rule** (below): only the peaks of the segment's commit range become
     candidates, chosen after the merge and the near-duplicate suppression (which see every
     peak of the window), before the ranking and the `maxCandidates` cut;
   - **edges**: a second on an edge of the window is never a peak, except at the start of
     the session (segment 0) and at its end (final segment). The composite of a moment just
     before the window decays into its first second: as a peak, that artefact would
     suppress the real peak a few seconds later, which the previous window leaves to this
     one — the moment would be lost (tested);
   - **score** = `round(100 · (0.5·pctSession + 0.5·sigmoid(peak − threshold)))`, where
     `pctSession` is the percentile of the peak (composite without focus) among
     `context.compositeSample` plus the window's own composite;
   - the short-source rules (whole video as the candidate) apply only to a window that is
     the whole session (sequence 0 and final);
   - **language**: the session language is fixed after the first segment with speech. A
     language set in the settings is used as is (`languageSource: "settings"`); else
     `context.language`, when known, is used without detection (`languageSource:
     "session"`); else the segment detects it as usual (step 4) and, when it had speech,
     the job posts the info event "Idioma da live identificado: pt. Os próximos segmentos
     usam o mesmo idioma" and returns it in `liveContext.language`. A segment without
     speech leaves it unknown, so the next one detects. A low-confidence detection is fixed
     too (warning event "… Ele vale para o resto da live; …");
   - **LLM**: the window rating (anthropic) is skipped; the assist stage (re-rank + titles)
     runs on the committed candidates only, within `min(LLM_BUDGET_SECONDS,
     LIVE_LLM_BUDGET_SECONDS)` (default 20 s; `0` skips it): live latency comes first;
   - webcam detection samples `LIVE_WEBCAM_SAMPLES` frames (default 8) of the window: each
     sample is an ffmpeg seek of its own (≈ 0.3–0.6 s on the development machine), the largest
     fixed cost of a job without transcription.
5. `/candidates` with the committed candidates in session times; each candidate's
   `signals.live` = `{sessionId, segmentId, sequence, windowSeconds: [origin, end],
   commitSeconds: [low|null, high|null], postRollSeconds}` and `signals.peakSeconds` is a
   session time.
6. **Auto-render** the top `autoRenderTop` committed candidates from the window file (file
   time = session time − the session time of the file's first sample; the caption words
   shifted the same way), each uploaded right after its render, as in step 8.
7. **Artifacts** (`finalizing`): `transcript.json` in session times (segments, words, gaps,
   detection spans; plus `originSeconds` and `timeBase: "session"`), which a later render of
   a clip of the session reads; `signals.json` as for an upload (per-second series from the
   analysis origin) plus `live: {originSeconds, tailSeconds, segmentStartSeconds,
   segmentDurationSeconds, final, windowSeconds, commitSeconds, postRollSeconds, …}`, its
   `peaks` and `candidates` in session times. Then `/clips` (session times), `/analysis`
   with `summary.liveContext`, and `/complete` with `processedSeconds` = this segment's
   probed duration (the tail is not charged twice) and the same summary.

### Commit rule

`postRoll = clamp(maxClipSeconds / 2, 8, 30)`. Segment N reports the candidates whose peak
lies in `[start_N − postRoll, end_N − postRoll)`; segment 0 from the start of the session;
the final segment up to its end (`end_N` included, whatever the file's exact length). The
moments after `end_N − postRoll` need media segment N does not have yet: segment N+1 detects
them again with segment N's tail as context. The ranges of consecutive segments meet at the
**hand-over point** `end_N − min(postRoll, duration_N)`, so every peak belongs to exactly
one segment:

- a segment shorter than `postRoll` (the last piece of an OBS file split, a short final
  run) hands over at its own start: the next window's tail is that whole segment and
  cannot see further back, so `[end_N − postRoll, start_N)` would otherwise be seen by
  nobody (tested: a 6 s segment with `postRoll` 10 s reports the moment at 57.5 s that the
  literal rule would lose);
- segment N writes its hand-over point into `liveContext.committedUntil` (`null` for the
  final segment) and segment N+1 starts its range there when the context is the one of
  `sequence − 1` (`context.sequence`), so both sides of a boundary use the very same number
  even if the durations the API holds are not exactly contiguous or the settings changed
  between the two jobs; without it (an API that does not pass the extras back), or when it
  cannot be the predecessor's hand-over point (more than 30 s + 2 s before the segment's
  start, or after it), N+1 computes `start_{N+1} − min(postRoll,
  previous.durationSeconds)` from the API's durations;
- both windows see a moment near a hand-over point and both apply the same rule on the
  same session grid: the one whose range holds the peak reports it, the other leaves it.

Tests (`tests/test_live.py`) run synthetic session composites through the real fusion,
segment by segment (windows planned as the worker plans them): every planted moment is
reported exactly once and by the expected segment, including peaks just before and on a
hand-over point, in a segment's last second, in the first second of the session, in the
last second of the final segment, and after a segment shorter than the post-roll. Known
limits: two *different* moments closer than `minClipSeconds` on the two sides of a
hand-over point may both be reported (each window sees only one of them in full, where a
batch analysis keeps the stronger); a peak on the very first second of the window that
follows a segment shorter than `postRoll` is not reported; a previous segment that failed
takes its own range with it (`previous: null`; the next segment starts on its own first
whole second).

### `liveContext` (summary of `/analysis` and `/complete`)

| field | meaning |
|---|---|
| `compositeSample` | ≤ 200 values of the session's per-second composite (without focus, 3 decimals): a reservoir sample (Algorithm R) of one value per second of the session, each second counted once (by the segment holding its midpoint; the tail's seconds were counted by the previous segment). The RNG is seeded with `sessionId:sequence`, so a retried job computes the same sample |
| `language` | the session language (`null` while no segment had speech) |
| `sequence` | the segment that wrote the context (extra: read back to trust `committedUntil`) |
| `committedUntil` | session time where this segment handed the peaks over (extra; `null` for the final segment) |

### Latency

A segment reaches the worker once the companion has closed and uploaded it, so a moment is
detected about one segment length + `postRoll` + the job's time after it happened. The job
analyses only `min(45 s, previous)` + the segment, joins the files without re-encoding,
skips the window rating and bounds the assist stage. Measured on the 4-core development
machine (other services and builds running, so the times varied between runs) with the e2e
fixture cut in 25 s segments, `maxClipSeconds` 12, one clip rendered per segment: 3–11 s per
job without transcription, mostly the webcam samples; with whisper `base`, 8–21 s for a
52 s window with 21 s of speech, transcription being most of it, and 37–40 s for the first
job of the worker (model load included). The tail is transcribed again on purpose: both
windows must see the same signals around a hand-over point to decide it the same way.

### `render_clip` of a live project

The payload carries `segments: [{downloadUrl, startOffsetSeconds, durationSeconds,
fileName}]` (every segment overlapping the clip range) instead of `source`; the clip's
`startSeconds`/`endSeconds` are session times. The worker downloads them (any order: they
are sorted by `startOffsetSeconds`), probes them, joins them with the concat demuxer
(stream copy, each segment occupying its API duration as above; one segment is used as
is) and renders the session range from the joined file: a session time maps to the
segment that starts at or before it (`file time = that segment's place in the file +
(t − its startOffsetSeconds)`; a time in a gap between two segments is held at the end of
the earlier one). The transcript of a live clip (`analysis.transcriptUrl`, a
`live_segment`'s `transcript.json`) is in session times and its words are mapped the same
way for the captions. A join that fails posts a failed `/render-result` and fails the job
`render_failed`; a segment that cannot be downloaded is `transfer_failed` (retryable). A
`render_clip` with `source` (an uploaded video) is unchanged.

## Rendering (`render/`)

FFmpeg is called with argument lists (`subprocess.run([...])`, never `shell=True`), paths
generated by the worker, `-nostdin -hide_banner -y`, timeouts, and the process is killed on
cancel. Accurate trimming: `-ss {start} -i source -t {duration}` with re-encode
(`-c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p -profile:v high -level 4.1
-c:a aac -b:a 160k -ar 48000 -movflags +faststart`). Output size is capped by
`limits.maxRenderHeight`, interpreted as the resolution class of the shorter side
(`>= 1080` → 1920×1080 / 1080×1920 / 1080×1080, `>= 720` → 1280×720 / 720×1280 / 720×720,
below 720 → 852×480 / 480×852 / 480×480; the shipped plans use 720 and 1080).

Layouts (`render/layouts.py` builds the `-filter_complex` string from validated numbers only):

| aspect | filter |
|---|---|
| `16:9` | `scale=w:h:force_original_aspect_ratio=decrease,pad=w:h:(ow-iw)/2:(oh-ih)/2` (1920×1080 or 1280×720) |
| `9:16` center | `crop=ih*9/16:ih:(iw-ih*9/16)*focusX:0,scale=1080:1920` (focusX 0..1) |
| `9:16` webcam-split | top: webcam region crop scaled to fit 1080×(h) (h between 25 % and 50 % of the height, from the region's aspect) and padded with black where it does not fill that area; bottom: gameplay center crop ; `vstack` → 1080×1920 |
| `1:1` | `crop=min(iw,ih):min(iw,ih):(iw-ow)*focusX:(ih-oh)/2,scale=1080:1080` |

`framing.mode=auto`: use `webcam-split` when a webcam region is known and the plan allows it,
otherwise `center`. If the webcam crop is invalid for any reason, fall back to `center` and
record a job event (never fail the render for framing).

Subtitles and title are rendered through an **ASS** file (`render/subtitles.py`) burned with
`subtitles=filename='…':fontsdir='…'`. Both paths are escaped for the two levels at which
ffmpeg parses a filter graph (the graph, then the filter's `key=value` options, both with
`av_get_token`): inside the single quotes `\` → `\\`, `:` → `\:` and `'` → `\'\''`, so the
path reaches ffmpeg unchanged — drive letters and Windows separators, a POSIX name with a
colon or a backslash, a Windows user name such as O'Neil, D'Souza or José in `WORK_DIR`,
and spaces, `[ ] , ; = % &` or accents in the fonts dir all open (tests render from such
directories and check that libass loads the bundled fonts from the exact `fontsdir`).
Captions are built from word timestamps in chunks of ≤ 4 words / ≤ 2 s, styles `bold`
(large white, black outline), `clean` (medium, light outline), `boxed` (semi-transparent
box), positioned `bottom`, `center` or `top`. Title = one ASS event for the whole clip in a
second style at the top. Fonts: `processing/assets/fonts/DejaVuSans*.ttf` (bundled, free
license) via `fontsdir=`. Text is escaped for ASS (`{`, `}`, `\`, newlines).

Vertical margins (`MarginV`, % of the output height) keep 9:16 captions inside the safe
area of TikTok/Reels/Shorts, whose interface covers roughly the bottom fifth (description,
buttons) and the top tenth (tabs):

| aspect | bottom captions | top captions | title (top) |
|---|---|---|---|
| `9:16` | 20 % | 12 %, and below the title when there is one | 8 % |
| `16:9`, `1:1` | 8 % | 8 %; 15 % with a title | 5 % |

`center` captions use the middle alignment, which ignores `MarginV` (unchanged). Captions at
the top never overlap the title: their margin is at least the title's margin plus its
estimated height (lines from a greedy word wrap at the title font size) plus a 2 % gap.

Audio: `loudnorm=I=-16:TP=-1.5:LRA=11` is always applied when the source has audio (the
contract has no switch for it); a source without audio renders a silent clip (`-an`).

Thumbnail: `-ss mid -frames:v 1 -vf scale=640:-2 -q:v 3 thumb.jpg`, taken from the rendered
clip (so it shows the final framing and subtitles).

## Development endpoint

`POST /analyze` on the FastAPI service (enabled only when `DEV_ENDPOINTS=true`, a
processing-only variable, default `false`) runs the analysis on a local file path and returns
candidates without the API: used by tests and for tuning. Body:
`{ path, settings?, limits?, transcription?: true, webcam?: true, llm?: false }`. The
answer also carries `notices` (the job events the worker would post: short source, LLM
stop) and `suggestedTitles` (`{"1": "…"}` by rank, with `llm: true` and a provider).

`GET /health` (unauthenticated, meant for the internal network: the service binds to
`SERVICE_HOST=127.0.0.1` by default and exposes local paths for diagnostics) returns:

```json
{
  "status": "ok | degraded (ffmpeg or ffprobe missing)",
  "version": "0.1.0", "uptimeSeconds": 12.3,
  "ffmpeg":  { "available": true, "version": "9.0.2", "path": "…/ffmpeg.exe" },
  "ffprobe": { "available": true, "version": "9.0.2", "path": "…/ffprobe.exe" },
  "whisper": { "enabled": true, "model": "base", "loaded": false, "device": "cpu",
               "computeType": "int8", "modelsDir": "…", "errors": { "base": "OSError: …" } | null,
               "retryInSeconds": { "base": 42.0 } | null },
  "llm": { "provider": "local", "enabled": true, "model": "Qwen3-1.7B-Q4_K_M",
           "budgetSeconds": 100.0, "baseUrl": "http://127.0.0.1:8090" },
  "worker": { "enabled": true, "running": true, "currentJobId": null, "workerId": "worker-local",
              "kinds": ["analyze_source", "render_clip", "live_segment"], "jobsRun": 3,
              "jobsRejected": 0, "lastError": null },
  "devEndpoints": false,
  "workDir": "…"
}
```

`whisper.errors` lists models that failed to load and `whisper.retryInSeconds` when the next
load attempt happens (0 = the next job retries). With `WORKER_ENABLED=false`, `worker` is
`{enabled: false, running: false, currentJobId: null, workerId}`. `llm` shows the configured
provider without contacting it (`model`: `LLM_LOCAL_MODEL`, or the id the server reported
to the last job; `baseUrl` only for `local`; keys are never shown).

## Logs

structlog to stdout (`LOG_FORMAT=console|json`). At startup stdout/stderr are switched to
UTF-8 (`errors="replace"`): on Windows a redirected stream defaults to the ANSI code page
(cp1252) and the pt-BR messages ("Preparando arquivo…", accents) reached UTF-8 readers as
mojibake.

## Testing the pipeline without real streams

`tests/fixtures/make_fixture.py` synthesises a 90 s test video with ffmpeg (`testsrc2` +
sine tone) containing three loud bursts and two visual cuts at known times. Tests assert that
candidate windows cover those times, that scores are ordered, that rendered MP4s are valid
(ffprobe) with the requested aspect ratio, and that cancel/timeout paths work. Real-world
tuning uses actual recordings supplied by the user (not committed).

Spoken fixtures come from Windows SAPI voices (`tests/fixtures/make_tts_fixture.py`): a
pt-BR sentence (pt-BR voice) and the English phrases of the e2e fixture read by an **en-US**
voice (the test is skipped without one). Note that `scripts/e2e/make_fixture.py` uses the
*default* voice: on a pt-BR Windows that is "Microsoft Maria" (pt-BR) reading English text,
which no Whisper model hears as plain English — `base` scores it Latin 0.67 / Portuguese
0.13 / English 0.10 and transcribes it as gibberish in any of those languages, `small`
detects English with 0.49 (low confidence) and gets 7 of the 10 key phrases. The same
phrases read by the en-US voice (Zira) are detected as English (0.99) with `base` and fully
transcribed.
