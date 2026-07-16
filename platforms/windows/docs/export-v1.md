# Export pipeline contract — v1 (E5)

Normative spec for Stage F's export-pipeline milestone — the plan's "Export pipeline" section
(GPU gamma-RGB→NV12 compute pass, staging-ring readback, NVENC/QSV-opportunistic encode,
`ExportQueue` reimplementation) plus the full Phase-1 export matrix (`ExportView`'s destinations).
This is the UI↔engine contract referenced there. **This document defines the contract only** —
same "declarations here, no `.cpp`" boundary docs/audio-playback-v1.md and docs/color-scopes-v1.md
already established: the `PE_ExportStart`/`PE_ExportCancel` declarations in
`native/include/palmier_engine.h` (doc-commented, no native `ExportSession.h/.cpp`) and the
`IExportService`/`IExportQueue` C# interfaces in `PalmierPro.Services.Export` are the three
normative surfaces this document specifies. No `NativeMethods.cs`/`TimelineSession.cs` P/Invoke
wiring, no concrete `VideoExportService`/`ExportQueue` implementation, no Export UI (M6) ships
with it — see §14 for exactly which future agent owns each of those.

## 1. Scope — the full Phase-1 export matrix

Verified against `Export/ExportOptions.swift` (`ExportFormat`/`ExportResolution`/`VideoCodec`) and
`Export/ExportQueue.swift`. Four destinations, all reachable from one `IExportService.ExportAsync`
call (§10):

| Destination | Mac source | Phase 1 disposition |
|---|---|---|
| Video: H.264, H.265 (SDR 8-bit BT.709) | `AVAssetExportSession` | **This document's core** — native `PE_ExportStart` (§4-§9) |
| Video: ProRes 422 (10-bit 4:2:2 SDR) | `AVAssetExportSession` + `AVAssetExportPresetAppleProRes422LPCM` | **Included** — same `PE_ExportStart` call, `yuv422p10le` path (§5.3) |
| Video: HEVC 10-bit HDR (BT.2020/HLG) | `HDRVideoExporter.swift` | **Deferred fast-follow** — explicitly absent, not a dead option (§13) |
| Timeline: FCPXML (1.10–1.14) + XMEML v4 | `FCPXMLExporter.swift`/`XMLExporter.swift` | **Already landed** (Stage C) — `FcpxmlExporter`/`XmemlExporter`, routed not reimplemented (§3) |
| Palmier Project package | `PalmierProjectExporter.swift` | **Already landed** — `PalmierProjectExporter.Export`, routed not reimplemented (§3) |

Everything below except §3's routing table and §11's queue contract is about the **Video** row —
that's the only destination with no existing Windows implementation to route to.

## 2. Reuse map — what Stage F builds on top of, not from scratch

Per the task brief's explicit reuse points, verified against the actual Windows source:

- **`AlphaVideoEncoder` (`native/AlphaVideoEncoder.h/.cpp`)** — already a working `prores_ks`
  FFmpeg wrapper (Open/PushFrame/Close/Abort), built for the Lottie-bake alpha-video path
  (`yuva444p10le`, 4:4:4:4 with alpha). §5.3/§6 explain precisely what's reused (the FFmpeg
  wiring pattern, the `mov`-muxer timebase workaround) vs. what a video-export ProRes encoder
  needs that this class doesn't have (audio, hardware codecs, non-alpha `yuv422p10le`) — it is
  **not** reused unmodified; see §6's rationale for why a literal shared instance would be wrong.
- **`AudioMixer::RenderRange` (`native/AudioMixer.h/.cpp`)**, already backing
  `PE_TimelineRenderAudioRange` — the offline mix loop E4.5 built, gain/fade/mute-correct and
  already routed through `RetimeStretcher` (vendored signalsmith-stretch) for `speed != 1.0`
  clips. This document's audio-policy decision (§7) is to reuse this path for export verbatim,
  not to build a second, FFmpeg-`atempo`-based mix.
- **`GpuCompositor::ComposeToAccumulator` (`native/GpuCompositor.h/.cpp`)** — the shared stage
  `Compose()` (preview) and `ComputeColorScopes()` (E6) already both terminate into before
  diverging (`ReadbackToBgra8` vs. the GPU histogram pass). Export is a **third** terminal stage
  off the same accumulator (§5.2) — no compositing code changes, only a new
  `ReadbackToNv12`/`ReadbackToYuv422p10le` sibling to `ReadbackToBgra8`.
- **`TimelineSnapshotBuilder`/simdjson `TimelineSnapshotParser`** — export renders from an
  ordinary `TimelineSnapshot`, the same JSON contract every other engine consumer uses. §4.1
  flags the one additive gap: a render-size override so export can target a resolution other
  than the timeline's authoring canvas.

## 3. Routing — one call, four internal paths

`IExportService.ExportAsync(request, progress, ct)` (`IExportService.cs`) branches on
`request.Destination` exactly as the Mac's `ExportService.export()` branches on `format` before
touching anything format-specific (`Export/ExportService.swift:130,179`):

```
ExportDestinationKind.Fcpxml          -> FcpxmlExporter.ExportAsync(...)          (already landed)
ExportDestinationKind.Xmeml           -> XmemlExporter.ExportAsync(...)           (already landed)
ExportDestinationKind.PalmierProject  -> PalmierProjectExporter.Export(...)       (already landed)
ExportDestinationKind.Video           -> native PE_ExportStart (§4-§9)            (THIS document)
```

The first three are pure C#, already shipped, and this document does not change their internals —
only §9 flags one real gap in two of them (no atomic-rename staging). `ExportRequest` (§10) is
the single request shape across all four; see its own remarks for why a `ProjectFile` + timeline id
(not a bare `Timeline`) is the common substrate every branch needs.

## 4. Native ABI — `PE_ExportStart` / `PE_ExportCancel`

Declared in `native/include/palmier_engine.h`'s new "Export (Stage F / E5)" section. Full
per-call contract lives in that header's comments (mirroring every other ABI entry point in this
codebase) — this section is the narrative version plus the design rationale the header comments
don't have room for.

### 4.1 Handle lifecycle — a DEDICATED timeline handle, never the live-preview one

`PE_ExportStart(session, timeline, optionsJson, callbacks, outResult)` takes an already-open
`PE_TimelineHandle`. **The caller must open one solely for the export** (`PE_OpenTimeline`, then
`PE_CloseTimeline` once export finishes, fails, or is cancelled) — never the handle also driving
live preview (swap-chain-attached, or receiving `PE_TimelineSeek`/`PE_TimelinePlay` calls).

Why: `PE_ExportStart` composes every output frame **sequentially and synchronously** inside the
call, exactly like `PE_TimelineRenderFrameToFile`/`PE_TimelineRenderAudioRange` bypass the render
thread's seek mailbox entirely (`palmier_engine.h`'s existing "Threading" comments on those two
calls). Running that against a handle that's also fielding concurrent `PE_TimelineSeek` calls from
a live scrub gesture is undefined — the two loops would fight over the same `TimelineSession`'s
render-thread state. A dedicated handle sidesteps the question entirely rather than requiring a new
locking protocol between "export's own frame loop" and "the render thread's mailbox."

**Render-size gap this contract does NOT close (mirrors docs/audio-playback-v1.md §1's identical
framing):** `GpuCompositor::Compose`/`ComposeToAccumulator` currently derive their output size
directly from `snapshot.outputWidth`/`outputHeight` (`GpuCompositor.cpp:1099`) — there is no
override parameter today. Export routinely needs a DIFFERENT size than the project's authoring
canvas (e.g. exporting a 1080p-canvas project at 4K, or `MatchTimeline` exporting it at its native
size regardless of what a *different* open preview session happens to be showing). Since every
`Transform`/`Crop` value in the snapshot schema is already canvas-size-normalized (0–1,
docs/timeline-snapshot-v1.md §5), rendering the SAME snapshot at a different canvas size is
render-graph-safe — nothing needs rescaling. The gap is purely mechanical: **`TimelineSnapshotBuilder.Build`
needs an additive optional render-size-override parameter** (e.g. `(int Width, int Height)?
renderSizeOverride = null`, defaulting to today's `timeline.Width`/`Height` behavior when absent) so
its `TimelineSnapshotBuildResult` can be opened via a fresh `PE_OpenTimeline` call at the export
target size — mirroring the Mac's `CompositionBuilder.build(renderSize:)`, which already takes
exactly this kind of explicit override independent of `Timeline.width`/`height`
(`Export/ExportService.swift:489-490`: `resolution.renderSize(for: timelineCanvas)` computed
BEFORE `CompositionBuilder.build` is called). **Whoever implements the native export loop (§14)
makes this one-parameter C# change** — it's a mechanical follow of an established pattern (the
Mac already has the exact override this needs), not a design decision, which is why it's flagged
here rather than left for that agent to rediscover.

Given that gap, `PE_ExportStart` validates rather than re-derives: it requires the ALREADY-OPEN
`timeline` handle's current snapshot `outputWidth`/`outputHeight` to already equal
`optionsJson.width`/`height` (§4.2), failing `PE_ERROR_INVALID_ARGUMENT` if they don't. This keeps
`GpuCompositor` itself completely unmodified (no override parameter added there) — the export
orchestrator just requires its caller to have already opened the RIGHT-sized snapshot, the same
"native asserts an invariant and fails loudly rather than silently doing the wrong thing"
convention `PE_TimelineRefreshParams`'s media-set check already established.

At most one `PE_ExportStart` call may be in flight per session — a second concurrent call on the
same session returns `PE_ERROR_INVALID_ARGUMENT`. This mirrors `ExportQueue`'s own
single-active-job invariant (§11) as a defensive native-side check, not a real usage pattern: the
queue's single-worker pump never calls in twice.

### 4.2 `optionsJson` schema

```jsonc
{
  "codec": "h264",              // "h264" | "h265" | "prores"
  "container": "mp4",           // "mp4" | "mov" — h264/h265 -> mp4, prores -> mov; any other
                                 // pairing is PE_ERROR_INVALID_ARGUMENT (ExportContainerExtensions
                                 // .ContainerFor is the C#-side source of truth that keeps a caller
                                 // from ever constructing the mismatched pairing in the first place)
  "width": 1920,                // ALREADY resolved — see below
  "height": 1080,
  "fps": 30,                    // ALWAYS the source timeline's own Fps verbatim (see below)
  "outputPath": "C:\\Users\\me\\Videos\\out.mp4"
}
```

- **`width`/`height`** — `ExportResolution.RenderSize(canvasWidth, canvasHeight)` (`ExportOptions.cs`),
  a direct port of `ExportResolution.renderSize(for:)` (`Export/ExportOptions.swift:47-59`):
  scales the timeline's canvas so its SHORT side equals the resolution's target (720/1080/1440/2160,
  or unscaled for `MatchTimeline`), then rounds each dimension down to even (chroma-subsampling
  requirement). **Native never sees the string `"matchTimeline"` or any other symbolic resolution
  name — only concrete, already-even pixel dimensions.** This mirrors §8 of
  docs/timeline-snapshot-v1.md's own "the engine never resolves an asset ref itself" principle,
  applied to resolution instead of media paths: exactly one place (`ExportResolutionExtensions`)
  owns the resolution→pixels mapping.
- **`fps`** — always the source timeline's `Fps` (`Timeline.cs:61`) verbatim. **v1 has no
  independent export-framerate picker** — neither does the Mac (`ExportOptions.swift` has no fps
  concept at all; `AVAssetExportSession` inherits the composition's own rate). The field exists in
  the wire schema anyway, carrying an explicit value rather than requiring native to infer it from
  the open snapshot, so a future frame-rate-conversion export feature is a value change, not a
  schema change — the identical "reserve the field now" rationale docs/timeline-snapshot-v1.md §1
  already used for its own `fps: {numerator, denominator}` shape.
- **`outputPath`** — native-separator absolute path. Never touched until `PE_OK` (§9).

### 4.3 Callback contract

```c
typedef void (*PE_ExportPhaseCallback)(void* userCtx, int32_t phase);       // PE_ExportPhase
typedef void (*PE_ExportProgressCallback)(void* userCtx, double fractionCompleted); // [0,1]

struct PE_ExportCallbacks { PE_ExportPhaseCallback onPhase; PE_ExportProgressCallback onProgress; void* userCtx; };
```

`onPhase` fires **at most once**, `PE_EXPORT_PHASE_PREPARING` -> `PE_EXPORT_PHASE_EXPORTING`, when
setup (encoder probe §6, container/file open) finishes and the per-frame loop starts — this is a
DIRECT mirror of the Mac's `ExportService.Phase` enum, which likewise only has two cases
(`Export/ExportService.swift:86-89`). Both callbacks may be null. `onProgress` fires repeatedly
during the exporting phase with `framesEncoded / totalFrames` (frame-count-based — not wall-clock
time-based, matching `AVAssetExportSession.states(updateInterval:)`'s own `fractionCompleted`
semantics the Mac already relies on). Both callbacks run **on the calling thread only**
(`PE_ExportStart` is fully synchronous, §4.4) — marshal to the UI thread in the C# subscriber, the
same contract every other engine callback in this header already carries.

Every finer-grained state `ExportJobStatus` (§12) exposes beyond `Preparing`/`Exporting`
(`Waiting`/`Canceling`/`Completed`/`Failed`/`Canceled`) is pure C#-side bookkeeping layered on top
of this ONE phase callback plus the call's own return value/exception — exactly how the Mac's
`ExportQueue.update(_ phase:)` (`Export/ExportQueue.swift:293-296`) already derives its own richer
`ExportJobStatus` purely from `ExportService.Phase`, never from a richer native signal.

### 4.4 Execution model and result

`PE_ExportStart` runs **synchronously on the calling thread** — the same convention
`PE_BakeLottieVideo` already established (`palmier_engine.h`'s comment on that call: "Runs
synchronously on the calling thread — callers invoke it from a background Task"). The caller is
`IExportQueue`'s single-worker pump (§11) — that IS the background Task. `PE_ExportCancel(session)`
is called from a DIFFERENT thread (the UI thread's Cancel button) and is safe to call concurrently
with the blocking `PE_ExportStart` call it targets (§8).

```c
struct PE_ExportResult { int64_t framesEncoded; int32_t usedHardwareEncoder; char encoderName[32]; };
```

`*outResult` is written only on `PE_OK`. `encoderName` (e.g. `"h264_nvenc"`, `"libx264"`,
`"prores_ks"`) and `usedHardwareEncoder` are diagnostics/telemetry only (§6) — surfaced through
`ExportResult.EncoderUsed`/`UsedHardwareEncoder` (`IExportService.cs`) for logging, never branched
on by the C# caller.

## 5. Render pipeline — GPU gamma RGB → NV12 / yuv422p10le

Per-frame, inside `PE_ExportStart`'s loop:

```
GpuCompositor::ComposeToAccumulator(snapshot, frame, ...)   // REUSED, unmodified (§2)
  -> R16G16B16A16_FLOAT accumulator, gamma-encoded, premultiplied alpha
  -> [NEW] RGB->NV12 (or ->yuv422p10le) GPU compute pass (§5.1-§5.3)
  -> [NEW] staging-texture ring readback, depth >= 3, Map frame N-2 (§5.4)
  -> avcodec_send_frame / avcodec_receive_packet (§6) -> av_interleaved_write_frame
```

This is the plan's literal "GPU compute pass converting gamma RGB → NV12 (explicit BT.709 matrix,
left-sited 4:2:0 chroma, explicit limited/full range...)" — **no linearization step**, consistent
with the Color pipeline section: the accumulator is already gamma-encoded (non-color-managed,
matching the Mac's `CIContext(workingColorSpace: NSNull())`), and this pass stays in that same
gamma space end to end. Straight (un-premultiplied) alpha is not needed here — the pass reads only
RGB and discards A (export has no alpha-video destination; that's `AlphaVideoEncoder`'s job).

### 5.1 BT.709 matrix (limited range, v1 default)

Reuses the SAME luma coefficients already hardcoded across this codebase's other BT.709-derived
math (`Kr=0.2126, Kg=0.7152, Kb=0.0722` — `ScopesHistogram.hlsl:61`, `Clarity.hlsl:30`,
`ChromaKey.hlsl:49`, `GradeCurves.hlsl:37`, `GlowBright.hlsl:24`, `Grain.hlsl:31`,
`HighlightsShadows.hlsl:22`) — do not introduce a second, differently-rounded constant set for
this pass. For gamma-encoded `R', G', B' ∈ [0,1]`:

```
Y_lin = Kr·R' + Kg·G' + Kb·B'                              // [0,1]
Y'  = 16/255 + (219/255)·Y_lin                              // R8_UNORM target, limited range
Cb' = 128/255 + (224/255)·0.5·(B' − Y_lin)/(1 − Kb)          // R8G8_UNORM target (with Cr')
Cr' = 128/255 + (224/255)·0.5·(R' − Y_lin)/(1 − Kr)
```

This is the standard ITU-R BT.709 studio-range (limited/"tv" range) RGB→Y'CbCr derivation — the
same one `libswscale` computes internally, expressed here directly from primaries rather than as
pre-rounded 0–255-domain magic constants, specifically so it's independently re-derivable/checkable
against the `Kr`/`Kg`/`Kb` values already in this codebase instead of needing to trust a second,
unrelated set of numbers. **Limited range is the only mode v1 implements** — matches
`AVAssetExportSession`/`AVAssetWriter`'s own default H.264/HEVC output range on the Mac, so this is
a genuine parity choice, not an oversight. A `fullRange: bool` parameter is a named, unimplemented
v1 extension point (deferred alongside HDR/P010, §13) — do not wire a schema field for it yet.

### 5.2 Chroma siting — left-sited 4:2:0 (NV12, H.264/H.265)

For chroma grid cell `(cx, cy)` covering luma pixels `(2cx, 2cy)`/`(2cx, 2cy+1)`/`(2cx+1, 2cy)`/
`(2cx+1, 2cy+1)`: **average only the LEFT COLUMN** — `(2cx, 2cy)` and `(2cx, 2cy+1)` — not all
four. This is "left" siting (horizontally collocated with the left luma sample, vertically centered
between the two rows) — the MPEG-2/H.264/`libswscale`-default convention, chosen specifically so a
player decoding this output needs no explicit chroma-siting side-channel to interpret it correctly
(an unlabeled NV12 stream is conventionally assumed left-sited by every common decoder).

Because the RGB→Y'CbCr transform above is linear, averaging the two source pixels' `R'G'B'` FIRST
and running the matrix ONCE is mathematically identical to running the matrix on each pixel and
averaging the resulting `Cb'`/`Cr'` — and half the arithmetic. Implement it the first way.

### 5.3 `yuv422p10le` — ProRes

ProRes uses a structurally different target: three SEPARATE planes (Y, U, V — not NV12's
luma-plus-interleaved-chroma), each **10-bit values stored in 16-bit little-endian words**
(`R16_UNORM` textures, value scaled into the low 10 bits), chroma subsampled 2:1 HORIZONTALLY ONLY
(4:2:2 — no vertical subsampling, unlike NV12's 4:2:0). The compute pass's OUTPUT stage is
therefore a second variant (3 UAV writes instead of NV12's 2, and **left-sited** chroma taken from
the LEFT sample of each horizontal pair — no averaging, since 4:2:2 has no second row for §5.2's
left-*column* average to combine; a horizontal box-average would instead be center-sited and no
longer match the `AVCHROMA_LOC_LEFT` tag the muxer writes) — the matrix math (§5.1) and the
left-sited chroma-siting logic (§5.2, minus its vertical-averaging half) are shared between both
variants, not duplicated.

`P010` (NV12's 10-bit analog, for the deferred HDR/BT.2020 fast-follow, §13) is a THIRD variant of
the same NV12-shaped output stage — 4:2:0 subsampling like NV12, but `R16_UNORM` planes like
`yuv422p10le` instead of `R8_UNORM`/`R8G8_UNORM`. Not implemented in v1; the render pipeline's
internal shape (parameterized bit-depth × subsampling variant) should make adding it later a new
enum case, not a redesign — this is what "parameterized to P010" in the plan means.

### 5.4 Staging ring — depth ≥ 3, `Map` frame N−2

A GPU→CPU readback that blocks the CPU on `Map` immediately after issuing the `CopyResource` that
same frame stalls the whole D3D11 pipeline (the CPU waits for a copy the GPU hasn't started yet).
The standard fix, and what the plan specifies: keep a ring of `stagingCount >= 3`
`D3D11_USAGE_STAGING` (`CPU_ACCESS_READ`) textures. Per output frame `N`:

```
GPU:  CopyResource(nv12Texture -> ring[N % stagingCount])          // issued, not waited on
CPU:  Map(ring[(N-2) % stagingCount]) -> build AVFrame -> Unmap    // ring[N-2]'s copy is ~2 frames old
```

By the time frame `N`'s `Map(ring[N-2])` runs, that texture's own `CopyResource` was issued two
loop iterations ago — comfortably enough GPU-queue slack that the driver has overwhelmingly likely
already finished it, so `Map` returns without stalling. `Unmap` immediately after copying the
mapped bytes into an FFmpeg-owned `AVFrame` buffer (`av_frame_get_buffer` + `memcpy`, not a raw
pointer alias into the mapped resource — the resource must be released promptly to keep the ring
cycling for the NEXT frame's `CopyResource`).

**Drain tail:** the loop's last two frames (`N = total-2`, `total-1`) have no `N+1`/`N+2` to hide
behind — after the compose loop ends, `Map` the two still-pending ring slots sequentially (a real,
if brief, stall is unavoidable and acceptable only here, at the very end). Skipping this drain
silently drops the export's final two frames — an easy-to-miss correctness bug, not a performance
nit, called out explicitly for that reason.

## 6. Encoder selection policy

Probed once, during `PE_EXPORT_PHASE_PREPARING`, before the first frame composes:

1. **Hardware, opportunistic, in this order:** `avcodec_find_encoder_by_name("h264_nvenc"/"hevc_nvenc")`,
   then (if NVENC's `avcodec_open2` fails — no NVIDIA GPU, driver too old, or the FFmpeg build
   lacks NVENC support) `"h264_qsv"/"hevc_qsv"` (Intel Quick Sync — this repo's FFmpeg build
   already carries `libavutil/hwcontext_qsv.h`/`libavcodec/qsv.h`, confirming QSV support was
   compiled in). A hardware attempt that fails `avcodec_open2` is a normal, expected outcome on a
   machine with neither vendor's hardware present (e.g. any CI runner) — log and fall through, not
   an error.
2. **Software fallback:** `libx264` / `libx265`. Always succeeds (bundled, no hardware dependency)
   — v1 does not need to handle "no encoder available at all" for h264/h265.
3. **ProRes: always `prores_ks`** (software). No hardware ProRes encoder exists in a standard
   FFmpeg build — there is no probe order to speak of, only the one codec.

**`PALMIERENGINE_FORCE_SW_ENCODE=1`** (read via `GetEnvironmentVariableA`, not `_dupenv_s` — this
codebase's established convention for exactly this kind of override, `EngineSession.cpp`'s
`PALMIERENGINE_FORCE_WARP`/`AudioEngine.cpp`'s `PALMIERENGINE_FORCE_NULL_AUDIO`/`TimelineSession.cpp`'s
`PALMIERENGINE_FORCE_CPU_COMPOSITOR`) skips straight to step 2 for h264/h265, regardless of what
hardware the runner has — so CI can produce a deterministic, hardware-independent encode (matching
the plan's own CI framing: "not just whichever branch hardware availability happens to take") and
so an export golden-fixture test's output bytes don't depend on which GPU vendor happened to build
the machine it ran on.

`AlphaVideoEncoder`'s `prores_ks` wiring (`AVFormatContext`/`AVCodecContext`/`AVStream` setup, its
`kTimeBase = {1, 90000}` `mov`-muxer workaround documented at `AlphaVideoEncoder.h:92-100`,
`EncodeAndMux`) is the pattern the export orchestrator's own ProRes path reuses — same lessons, same
muxer family (`mov`), same codec — but it is a SIBLING implementation, not the literal same class
instance: `AlphaVideoEncoder` is single-stream (video only, no audio), alpha-carrying
(`yuva444p10le`, 4:4:4:4), and shaped around Lottie's monotonic-`presentationSeconds`-push contract
(`PE_EncodeAlphaVideoPushFrame`). Export needs a NON-alpha `yuv422p10le` (4:2:2, no alpha channel)
profile, TWO muxed streams (video + AAC audio, §7), and a fixed frame-at-a-time cadence driven by
its own compose loop rather than an external caller's pushes. Reusing the wiring PATTERN while
building a distinct type for these real differences is the correct scope — literally sharing the
`AlphaVideoEncoder` class would either bolt audio/hwaccel onto a class that structurally has no
room for them, or silently narrow ProRes video export to something that happens to also carry an
unused alpha channel.

## 7. Audio policy — DECISION: reuse the offline `AudioMixer` path, not FFmpeg `atempo`

**Decision: export audio reuses `AudioMixer::RenderRange` (`native/AudioMixer.h/.cpp`) — the exact
mix loop already backing `PE_TimelineRenderAudioRange` and, by extension, live preview playback —
rather than building a second, FFmpeg-`atempo`-based mix path for export.**

Rationale, in order of weight:

1. **Preview/export parity is close to free this way, and expensive the other way.** `AudioMixer`
   already implements the complete gain model (`clip.volume × dB-keyframe × fade envelope`,
   `Track.muted`, no-pan, per-block-cadence sampling — docs/audio-playback-v1.md §1/§2) AND
   per-clip retiming for `speed != 1.0` clips via `RetimeStretcher` (vendored signalsmith-stretch,
   already integrated per `AudioMixer.h:21-29`). Reusing it for export means preview and export are
   LITERALLY the same code computing the same samples for the same input — the strongest possible
   form of the parity the plan asks for ("preview==export parity"). Building a second FFmpeg-filter-
   graph-based mix (demux each clip → `atempo` for retimed ones → per-clip volume/fade filters →
   `amix` for track summing → mute handling) would require reimplementing that entire gain model a
   SECOND time in FFmpeg's filter-graph DSL, with every chance for the two implementations to drift
   (a fade curve shape, a dB-clamp edge case, muted-clip handling) — and any such drift would be a
   preview/export MISMATCH bug, exactly the failure mode parity is meant to prevent.
2. **`atempo` cannot pitch-preserve arbitrary retime ratios without chaining, and this codebase
   already solved that problem differently.** FFmpeg's `atempo` filter only accepts factors in
   [0.5, 2.0] per instance — speeds outside that range need multiple chained instances (as the
   plan's own earlier-considered `atempo` framing acknowledged: "chain instances for speeds
   < 0.5×"). `RetimeStretcher`/signalsmith-stretch has no such range restriction and is already
   wired into the exact mix loop export would reuse — adopting `atempo` for export would ALSO mean
   two different pitch-preservation algorithms are used for the same clip depending on whether it's
   being scrubbed or exported, a second, subtler preview/export mismatch (audible artifact
   character, not just gain-formula correctness) beyond point 1's structural one.
3. **No process-boundary/pipe complexity.** An `atempo`-based design would need either (a) shelling
   out to `ffmpeg.exe` as a subprocess per export (this codebase already avoids that pattern
   elsewhere — `FfprobeSourceTimingReader` is the one place it shells out, specifically because
   `ffprobe` has no equivalent in the vendored `libav*` DLLs already linked into `PalmierEngine.dll`;
   audio mixing has no such gap) or (b) building a filter-graph (`avfilter`) pipeline in-process,
   which is a materially larger native surface than calling an already-existing, already-tested
   `RenderRange` function in a loop. Neither is justified when the reuse path is simpler AND more
   correct.

**What this decision does NOT change:** `libswresample` remains the only audio-specific FFmpeg
library export touches (matches the plan's own correction: "`libswresample` is audio-only; video
conversion happens on GPU, and `libswscale` is reserved for the software-decode fallback") — it
resamples `AudioMixer::RenderRange`'s Float32/48kHz/stereo/interleaved output into whatever sample
format the AAC encoder (`libavcodec`'s `aac`) actually wants, immediately before
`avcodec_send_frame`. `libavfilter`/`atempo` are not linked into the export path at all.

Per-block-vs-per-range: `RenderRange` already accepts an arbitrary `sampleCount` (not just
`AudioMixer::kBlockFrames` = 960 @ 48kHz) — export calls it with however many samples correspond to
one AAC frame's worth of audio (`aac`'s native frame size, typically 1024 samples), not the live
playback path's 20ms block size; §2 of docs/audio-playback-v1.md's "block cadence" gain-sampling
rule still applies (gain is sampled once per `AudioMixer` internal 960-sample block regardless of
how large a caller's outer `RenderRange` request is) since it's internal to `RenderRange` itself,
unaffected by the caller's chunk size.

Audio and video streams are muxed into the SAME `AVFormatContext` `PE_ExportStart` opens for
`optionsJson.outputPath` — `av_interleaved_write_frame` handles PTS-based interleaving automatically
given each stream's own correctly-set timebase (video: derived from `optionsJson.fps`; audio: the
AAC encoder's own timebase, sample-rate-based) — "PTS-interleaved" in the plan's phrasing describes
this muxer behavior, not a manual interleaving step either stream's producer needs to implement.

## 8. Cancellation

`PE_ExportCancel(session)` sets a session-scoped `std::atomic<bool>` — no separate export handle to
target, since §4.1 established at most one export is ever in flight per session. The export loop
polls it at three points per output frame, matching the plan's "decode/dispatch/encode boundaries"
literally:

1. **Decode boundary** — before starting that frame's `ComposeToAccumulator` call (which drives
   per-clip decode via the same `ClipFrameProvider` preview uses).
2. **Dispatch boundary** — after compose, before issuing the RGB→NV12/`yuv422p10le` compute
   dispatch and its `CopyResource` into the staging ring.
3. **Encode boundary** — before `avcodec_send_frame` for that frame's video packet (and,
   independently, before each `RenderRange` audio chunk's `avcodec_send_frame`).

On a positive poll, the loop stops promptly (within roughly one frame's worth of outstanding work —
an explicit, worth-stating latency bound, not "eventually"), performs §9's cleanup, and
`PE_ExportStart` returns `PE_ERROR_CANCELLED`. `IExportService.ExportAsync`'s C# wrapper (§10, §14)
is expected to bridge its own `CancellationToken` to `PE_ExportCancel` via `ct.Register(...)` — the
same bridging pattern the task brief names explicitly and every other native-cancellable call in
this codebase (`EngineSession.BakeLottieVideo`'s `cancelArray`/registration) already uses, adapted
here to a named ABI call instead of a caller-owned polled pointer specifically because §4.1's
"at most one export per session" invariant makes a named `PE_ExportCancel(session)` unambiguous and
simpler than pinning/passing a cancel flag pointer through the whole call chain for a
multi-minute-long operation.

## 9. Partial-file / atomic-output semantics

**Decision: `PE_ExportStart` owns its own temp-file + atomic-rename discipline internally** — the
same discipline `PE_BakeLottieVideo` already established (`palmier_engine.h`: "this call DOES own
its own temp-file + atomic-rename discipline... `utf8OutputPath` is only ever created, complete and
playable, on `PE_OK`; any failure or cancellation leaves no file at `utf8OutputPath` at all").
Concretely: `PE_ExportStart` writes to an internal temp path (same directory as
`optionsJson.outputPath`, so the final rename is same-volume/atomic — mirrors the Mac's own
`stagingURL` placement, `Export/ExportService.swift:457-464`: `.{stem}-{uuid}.partial.{ext}` next
to the real output) and only renames it to `outputPath` after the muxer's trailer write
(`av_write_trailer`) succeeds. Any error OR `PE_ERROR_CANCELLED` deletes the temp file before
returning — never leaves a `.partial`-suffixed orphan, and never leaves a truncated/unplayable file
AT `outputPath` itself.

**This is a stronger, simpler guarantee than the Mac's own `didCommitOutput` flag needs to provide**
(`Export/ExportService.swift:96,438-450`): the Mac tracks `didCommitOutput` as a boolean the CALLER
(`ExportQueue.run`) must check, because `AVAssetExportSession`'s own writer offers no equivalent
atomicity natively — the Mac's `ExportService` has to bolt staging/rename on top of it itself, and a
caller that forgets to check `didCommitOutput` could misreport a cancelled export as if it produced
a file. Building the SAME guarantee one layer lower (inside the native muxer wrapper, where the
temp-file lifecycle is naturally colocated with the muxer's own open/write/close calls) removes that
caller-must-check-a-flag failure mode entirely for the video destination: **`IExportService.ExportAsync`'s
own postcondition for `Destination == Video` is simply "success ⇒ file exists and is complete;
exception (including cancellation) ⇒ file does not exist"** — no separate flag to consult
(`IExportService.cs`'s own doc comment states this postcondition explicitly).

**A gap this document does NOT close: `FcpxmlExporter.ExportAsync`/`XmemlExporter.ExportAsync` have
no staging of their own today** — both call `File.WriteAllText(outputPath, ...)` directly
(`FcpxmlExporter.cs:142`; `XmemlExporter.cs` mirrors it), unlike `PalmierProjectExporter.Export`
(already stages via its own `.palmier-export-{guid}.partial` directory) and unlike the video path
above. This means a cancelled/failed FCPXML or XMEML export today can leave a truncated file at
`outputPath` — a real, if narrow (both are fast, in-memory-string-then-single-write operations, so
the failure window is small), divergence from the Mac's `withStagedOutput` wrapping EVERY export
type INCLUDING its XML/FCPXML branch (`Export/ExportService.swift:130,146`: the `.xml`/`.fcpxml`
case goes through `withStagedOutput` exactly like the video case does). **Whoever implements
`IExportService` (§14) must close this** — wrap those two calls' file write in the same
stage-then-atomically-move pattern (`File.Move(staging, outputPath, overwrite: true)` after a
successful write, delete the staging file on any exception) rather than modifying
`FcpxmlExporter`/`XmemlExporter` themselves, which are Stage C's already-landed, out-of-slice files.

## 10. C# surface

`PalmierPro.Services.Export`:

- **`ExportOptions.cs`** — `ExportVideoCodec` (H264/H265/ProRes — no HDR case, §13),
  `ExportContainer` (+`ContainerFor`/`FileExtension`), `ExportResolution` (+`RenderSize`, a direct
  port of `ExportResolution.renderSize(for:)`), `ExportDestinationKind`, `ExportJobSource`,
  `ExportOptions` (the exact `optionsJson` payload shape, §4.2).
- **`IExportService.cs`** — `ExportPhase` (C# mirror of `PE_ExportPhase`), `ExportProgress`
  (`IProgress<T>`'s `T`, bundling phase+fraction since `IProgress<T>` reports exactly one value),
  `ExportRequest` (the union request shape across all four destinations — see its own remarks for
  why it carries `ProjectFile` + a timeline id rather than a bare `Timeline`), `ExportResult`
  (union of `ExportRunReport` and `PalmierProjectExporter.Report`'s reportable fields),
  `IExportService` itself.
- **`IExportQueue.cs`** — `ExportJobStatus` (+`RawValue`/`IsRunning`/`IsFinished`/`IsPending`,
  §12), `ExportJob`, `ExportQueueSubmission`, `ExportDestinationInUseException`, `IExportQueue`
  (§11).

All three files are pure data contracts / interfaces — **zero dependency on `PalmierPro.Rendering`
or any P/Invoke surface**, deliberately, even though the `PalmierPro.Services` project already
references `PalmierPro.Rendering` (for `VideoEngine`) and could. This keeps them safely compilable
today regardless of the native header's implementation state, and keeps this document's actual
diff scoped to exactly "spec + declarations + interfaces" with no risk of colliding with whichever
agent writes the concrete `VideoExportService : IExportService` next (§14).

## 11. `ExportQueue` — single-worker pump + destination reservation

Ports `ExportQueue` (`Export/ExportQueue.swift`) — a cross-project-global singleton on the Mac
(`ExportQueue.shared`); `IExportQueue` keeps that scope (a single app-wide export queue, not
per-project). Reimplemented as a `System.Threading.Channels.Channel<T>`-backed single-worker pump
per the task brief: `Channel.CreateUnbounded<ExportJob>()` (or similar), one dedicated
`Task.Run` reader loop that is the ONLY thing ever calling `IExportService.ExportAsync` — the
channel's single-reader discipline gives the "at most one job is ever `Preparing`/`Exporting`/
`Canceling`" invariant for free, replacing the Mac's manual `activeID`/`activeTask`/`activeService`
field-juggling (`Export/ExportQueue.swift:73-75,226-233`) with a structural guarantee instead of
three cooperating mutable fields.

**Destination reservation** (`IsDestinationReserved`/`Enqueue`'s `ExportDestinationInUseException`)
mirrors `ExportQueue.isDestinationReserved(_:)`/`enqueue(...)`'s `ExportQueueError.destinationInUse`
throw (`Export/ExportQueue.swift:90-92,198-200`) exactly: a path already targeted by ANY
waiting-OR-running job (`status.IsPending`) cannot be targeted by a second `Enqueue` call — checked
and thrown SYNCHRONOUSLY, before a job is even created, so a caller seeing this exception knows no
job was added (matching the Mac's non-`async` `throws` on the same check).

**One unified `Enqueue` overload** replaces the Mac's `enqueueVideo`/`enqueuePalmierProject` split
(`Export/ExportQueue.swift:96-149`) — possible here specifically because `ExportRequest` (§10) is
already one discriminated shape covering every destination, where the Mac's Swift closures needed
two distinct parameter lists to build two distinct `ExportService` method calls. `IExportQueue`'s
worker loop reads `job.Request.Destination` and calls the one `IExportService.ExportAsync` either
way — see §3's routing table.

Status derivation inside the worker loop mirrors `ExportQueue.run(_:)`/`update(_ phase:)` exactly
(`Export/ExportQueue.swift:235-296`): `ExportAsync` throwing `OperationCanceledException` -> job
status `Canceled`; any other exception -> `Failed` (with `Error` set from the exception message);
returning normally -> `Completed`; the `IProgress<ExportProgress>` callback passed into `ExportAsync`
updates `job.Status` (`Preparing`/`Exporting`) and `job.Progress` as it fires, with the same
"ignore updates once the job has moved to `Canceling`" guard the Mac's own `update(_:for:)` already
has (`jobs[index].status != .canceling` check, `Export/ExportQueue.swift:294`) — a progress
callback racing a just-issued cancel must not un-set the `Canceling` status a moment later.

## 12. `ExportJobStatus` — exact Mac raw-string parity

| C# case | `RawValue()` | Mac `ExportJobStatus` case | Mac raw value |
|---|---|---|---|
| `Waiting` | `"queued"` | `waiting` | `"queued"` |
| `Preparing` | `"preparing"` | `preparing` | `"preparing"` (implicit) |
| `Exporting` | `"rendering"` | `exporting` | `"rendering"` |
| `Canceling` | `"canceling"` | `canceling` | `"canceling"` (implicit) |
| `Completed` | `"completed"` | `completed` | `"completed"` (implicit) |
| `Failed` | `"failed"` | `failed` | `"failed"` (implicit) |
| `Canceled` | `"canceled"` | `canceled` | `"canceled"` (implicit) |

`Waiting`→`"queued"` and `Exporting`→`"rendering"` are the two cases where the C# name and its raw
string genuinely differ — everywhere else the raw value is just the case name lowercased, but ALL
seven must go through `ExportJobStatusExtensions.RawValue()`, never `ToString()`/`nameof()`, so
nothing silently reintroduces a mismatch on those two. This matters concretely for: PowerShell/UI
automation assertions (`PALMIER_AUTOMATION=1` scripted flows, mirroring how such assertions must
already match the Mac's exact strings), analytics event payloads (`Export/ExportService.swift`'s
`Analytics.capture` calls carry format/mode strings the same way), and any future
cross-platform-consistency test that diffs these two raw-string sets directly.

## 13. HDR — explicitly, deliberately absent

`ExportVideoCodec` (`ExportOptions.cs`) has exactly three cases: `H264`, `H265`, `ProRes`. **No
`Hdr` placeholder case exists.** Per the plan's Phase-1 disposition table (§1): "Deferred fast-follow
— the export picker must handle its absence explicitly, not render a dead option." Concretely, this
means:

- The Export UI (M6, out of this document's slice) must not offer an HDR option that exists in the
  enum but throws/no-ops when selected — the option must not be constructible/selectable at all,
  which an enum with no `Hdr` case enforces at the type level rather than relying on UI-layer
  discipline.
- `PE_ExportStart`'s `optionsJson.codec` accepts only `"h264"`/`"h265"`/`"prores"` — an HDR fast-
  follow adds a fourth value AND a real `P010`/BT.2020 render pipeline (§5.3's third variant) at
  the same time it adds the enum case; there is no half-wired HDR path to accidentally reach today.
- The Mac's `HDRVideoExporter.swift`/`hevcHDR` case is the porting reference for that future work —
  not part of this document's scope, which is why §1's matrix table marks it "deferred fast-follow"
  rather than routing it anywhere in §3.

## 14. What this document does NOT ship — open items for the next implementing agent

Explicitly out of this contract's own diff (mirrors docs/audio-playback-v1.md §9's identical
"which future agent owns each of these" framing):

1. **`TimelineSnapshotBuilder.Build`'s render-size-override parameter** (§4.1) — one additive
   optional parameter, mechanical.
2. **Native `ExportSession.h/.cpp`** implementing `PE_ExportStart`/`PE_ExportCancel`'s actual
   bodies: encoder probe (§6), the RGB→NV12/`yuv422p10le` HLSL compute shaders + their `GpuCompositor`
   `ReadbackToNv12`/`ReadbackToYuv422p10le` sibling methods (§5), the staging ring (§5.4), the
   muxer/temp-file lifecycle (§9), the `AudioMixer::RenderRange`-driven audio stream (§7), the
   three-boundary cancellation poll (§8). Wired into `PalmierEngine.vcxproj` at that point (this
   document's header addition needs no `.vcxproj` change — no new `.cpp` file exists yet).
3. **`NativeMethods.cs` P/Invoke declarations** for `PE_ExportStart`/`PE_ExportCancel` and their
   `PE_Export*` structs/enums (`NativeMethods.cs`, `EngineSession.cs`-style wrapper) — deliberately
   not added alongside the native header per §10's "zero P/Invoke dependency" scoping.
4. **Concrete `IExportService`/`IExportQueue` implementations** — a `VideoExportService` (native
   P/Invoke bridge for `Destination == Video`) plus routing to the three already-landed exporters
   for the rest (§3), and a `Channel`-backed `ExportQueue` (§11). Closes §9's FCPXML/XMEML staging
   gap as part of this work.
5. **Export UI (M6)** — the full destination/codec/resolution/HDR-absent picker (`AppTheme`'s
   existing `Export` token category, `AppThemeTokens.cs:409`, per `platforms/windows/AGENTS.md`'s
   styling rule), wired to `IExportQueue`.
6. **Golden fixtures** (§15).

## 15. Test-fixture expectations (for §14's implementing agent)

Per the plan's Stage F "Done" bar and Verification section:

- **Structural validation via `ffprobe`** (`third_party/ffmpeg/bin`) — mirrors the Mac's
  `ExportServiceRoundTripTests` pattern: export a real fixture project, assert the output file's
  codec/container/resolution/fps/duration/stream count via `ffprobe -show_streams -show_format`,
  not just "the process exited 0."
- **A retimed-clip export with retimed audio** — explicitly named in the plan's Stage F bar
  ("plus a retimed-clip export with retimed audio") specifically because §7's audio-policy decision
  is the thing that guarantees this stays in sync: a `speed != 1.0` clip's audio must be
  pitch-preserved (via `RetimeStretcher`, same as preview) AND land at the correct PTS in the muxed
  output.
- **Cancellation mid-export leaves no output file** — direct test of §9's postcondition: start an
  export against a large-enough fixture that cancellation can land mid-loop, call
  `PE_ExportCancel`/cancel the `CancellationToken`, assert `File.Exists(outputPath) == false` AND no
  `.partial`-suffixed sibling file survives.
- **Each of the three v1 codecs** (h264, h265, prores) at `MatchTimeline` and at least one scaled
  resolution — exercises §4.2's width/height resolution path and §6's per-codec encoder selection.
- **`PALMIERENGINE_FORCE_SW_ENCODE=1` in CI** (§6) — the export golden/structural tests must set
  this so their expected `ffprobe` output (in particular `codec_name`, which differs between
  e.g. `h264` from `libx264` vs. `h264_nvenc`/`h264_qsv`) is deterministic across CI runners
  regardless of GPU vendor, mirroring `PALMIERENGINE_FORCE_WARP`'s identical purpose for D3D11
  device creation.
