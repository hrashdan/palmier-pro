#pragma once

#include "include/palmier_engine.h"

#include <atomic>
#include <cstdint>
#include <string>

class EngineSession;
class TimelineSession;

// E5 export orchestrator (export-v1.md §4-§9) — the per-export driver behind PE_ExportStart. Owns
// the whole synchronous compose->convert->encode loop for the Video destination: it pulls each
// output frame from the timeline's GPU compositor at export res/fps, runs it through the
// ExportReadback ring (RGB->NV12/yuv422p10le, §5), interleaves AudioMixer::RenderRange audio (§7),
// and muxes both into an ExportEncoder-owned container (§6). It writes to an internal temp file and
// only atomically renames it onto optionsJson.outputPath after the muxer trailer is written (§9) —
// any error OR cancellation leaves no file at outputPath at all.
//
// Cancellation (§8): polled through the session-scoped std::atomic PE_ExportCancel sets, checked at
// the decode / dispatch / encode boundaries once per output frame — prompt (<~1 frame) stop, then
// §9's temp-file cleanup, then PE_ERROR_CANCELLED.
//
// One instance per PE_ExportStart call, constructed and driven by EngineSession::ExportStart (which
// enforces the single-active-export-per-session invariant, §4.1). Not reused; not thread-safe (the
// export loop is single-threaded — PE_ExportCancel only touches the atomic, from another thread).
class ExportSession
{
public:
    ExportSession(EngineSession* owner, TimelineSession* timeline);
    ~ExportSession();

    ExportSession(const ExportSession&) = delete;
    ExportSession& operator=(const ExportSession&) = delete;

    // Parses utf8OptionsJson (§4.2), validates it against `timeline`'s open snapshot, then runs the
    // full export. On PE_OK, *outResult (if non-null) is written and outputPath exists complete; on
    // any other status (including PE_ERROR_CANCELLED) no file exists at outputPath. `cancelFlag` is
    // the owning session's export-cancel atomic (0 = run, non-zero = cancel requested).
    int32_t Run(const std::string& utf8OptionsJson, const PE_ExportCallbacks& callbacks,
        const std::atomic<int32_t>& cancelFlag, PE_ExportResult* outResult);

private:
    struct Options
    {
        std::string codec;      // "h264" | "h265" | "prores"
        std::string container;  // "mp4" | "mov"
        int32_t width = 0;
        int32_t height = 0;
        int32_t fps = 30;
        std::string outputPath;
    };

    bool ParseOptions(const std::string& json, Options& out, std::string& outError) const;

    EngineSession* owner_;
    TimelineSession* timeline_;
};
