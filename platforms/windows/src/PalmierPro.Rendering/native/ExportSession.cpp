#include "ExportSession.h"

#include "AudioMixer.h"
#include "EngineSession.h"
#include "ExportEncoder.h"
#include "ExportReadback.h"
#include "TimelineSession.h"
#include "TimelineSnapshot.h"

#include "third_party/simdjson/simdjson.h"

#include <windows.h>

#include <atomic>
#include <cmath>
#include <memory>
#include <string>
#include <vector>

using simdjson::dom::element;
using simdjson::dom::parser;

namespace
{
    // Every output frame's audio is pulled at video-frame cadence — the natural per-frame chunk the
    // Mac's AVAssetWriter interleaving already implies (export-v1.md §7). AudioMixer/export audio is
    // always 48 kHz stereo Float32 (AudioMixer::kMixSampleRate/kChannels).
    constexpr int32_t kAudioSampleRate = static_cast<int32_t>(AudioMixer::kMixSampleRate);
    constexpr int32_t kAudioChannels = static_cast<int32_t>(AudioMixer::kChannels);

    // Total output frames = the exclusive end of the last clip/text clip across every track — the
    // Windows mirror of Timeline.totalFrames the Mac's composition duration derives from.
    int64_t TotalOutputFrames(const TimelineSnapshot& snapshot)
    {
        int64_t total = 0;
        for (const SnapshotTrack& track : snapshot.tracks)
        {
            for (const SnapshotClip& clip : track.clips)
            {
                total = std::max(total, clip.EndFrameExclusive());
            }
            for (const SnapshotTextClip& text : track.textClips)
            {
                total = std::max(total, text.EndFrameExclusive());
            }
        }
        return total;
    }

    // Any non-empty Audio track means the export carries an AAC stream — muted tracks still count
    // (they mix to silence, but the Mac's composition would still expose an audio track). A purely
    // video timeline gets a video-only file (matching a source with no audio on the Mac).
    bool SnapshotHasAudio(const TimelineSnapshot& snapshot)
    {
        for (const SnapshotTrack& track : snapshot.tracks)
        {
            if (track.type == SnapshotTrackType::Audio && !track.clips.empty())
            {
                return true;
            }
        }
        return false;
    }

    // ".{stem}-{unique}.partial{.ext}" next to outputPath, so the final MoveFileEx is a same-volume
    // atomic rename (export-v1.md §9 / the Mac's stagingURL placement, ExportService.swift:457-464).
    std::string StagingPathFor(const std::string& outputPath)
    {
        size_t slash = outputPath.find_last_of("\\/");
        std::string dir = slash == std::string::npos ? std::string() : outputPath.substr(0, slash + 1);
        std::string name = slash == std::string::npos ? outputPath : outputPath.substr(slash + 1);
        size_t dot = name.find_last_of('.');
        std::string stem = dot == std::string::npos ? name : name.substr(0, dot);
        std::string ext = dot == std::string::npos ? std::string() : name.substr(dot); // includes the dot

        static std::atomic<uint64_t> counter{0};
        uint64_t unique = counter.fetch_add(1, std::memory_order_relaxed);
        char suffix[64]{};
        _snprintf_s(suffix, sizeof(suffix), _TRUNCATE, "%lu-%llu-%llu",
            static_cast<unsigned long>(GetCurrentProcessId()),
            static_cast<unsigned long long>(GetTickCount64()),
            static_cast<unsigned long long>(unique));
        return dir + "." + stem + "-" + suffix + ".partial" + ext;
    }

    void DeleteIfExists(const std::string& path)
    {
        if (!path.empty())
        {
            DeleteFileA(path.c_str());
        }
    }
}

ExportSession::ExportSession(EngineSession* owner, TimelineSession* timeline)
    : owner_(owner), timeline_(timeline)
{
}

ExportSession::~ExportSession() = default;

bool ExportSession::ParseOptions(const std::string& json, Options& out, std::string& outError) const
{
    parser p;
    auto parsed = p.parse(json);
    if (parsed.error())
    {
        outError = "PE_ExportStart: options JSON is not valid JSON";
        return false;
    }
    element root = parsed.value();

    std::string_view codecSv;
    std::string_view containerSv;
    std::string_view outputSv;
    int64_t width = 0, height = 0, fps = 0;
    if (root["codec"].get(codecSv) || root["container"].get(containerSv) ||
        root["outputPath"].get(outputSv) || root["width"].get(width) ||
        root["height"].get(height) || root["fps"].get(fps))
    {
        outError = "PE_ExportStart: options JSON is missing a required field";
        return false;
    }

    out.codec.assign(codecSv);
    out.container.assign(containerSv);
    out.outputPath.assign(outputSv);
    out.width = static_cast<int32_t>(width);
    out.height = static_cast<int32_t>(height);
    out.fps = static_cast<int32_t>(fps);

    if (out.width <= 0 || out.height <= 0 || (out.width & 1) != 0 || (out.height & 1) != 0 || out.fps <= 0)
    {
        outError = "PE_ExportStart: width/height must be positive and even, fps positive";
        return false;
    }
    if (out.outputPath.empty())
    {
        outError = "PE_ExportStart: outputPath is empty";
        return false;
    }
    return true;
}

int32_t ExportSession::Run(const std::string& utf8OptionsJson, const PE_ExportCallbacks& callbacks,
    const std::atomic<int32_t>& cancelFlag, PE_ExportResult* outResult)
{
    auto reportError = [&](const std::string& message) {
        owner_->SetLastError(message);
    };
    auto isCancelled = [&]() { return cancelFlag.load(std::memory_order_relaxed) != 0; };

    Options options;
    std::string error;
    if (!ParseOptions(utf8OptionsJson, options, error))
    {
        reportError(error);
        return PE_ERROR_INVALID_ARGUMENT;
    }

    // Codec/container pairing (§4.2): h264|h265 -> mp4, prores -> mov. Any other pairing is rejected.
    ExportEncoder::Codec codec;
    ExportReadback::Format readbackFormat;
    if (options.codec == "h264")
    {
        codec = ExportEncoder::Codec::H264;
        readbackFormat = ExportReadback::Format::NV12;
    }
    else if (options.codec == "h265")
    {
        codec = ExportEncoder::Codec::H265;
        readbackFormat = ExportReadback::Format::NV12;
    }
    else if (options.codec == "prores")
    {
        codec = ExportEncoder::Codec::ProRes;
        readbackFormat = ExportReadback::Format::Yuv422p10le;
    }
    else
    {
        reportError("PE_ExportStart: unknown codec '" + options.codec + "' (h264|h265|prores)");
        return PE_ERROR_INVALID_ARGUMENT;
    }
    const bool wantsMov = codec == ExportEncoder::Codec::ProRes;
    if ((wantsMov && options.container != "mov") || (!wantsMov && options.container != "mp4"))
    {
        reportError("PE_ExportStart: codec/container mismatch (h264|h265->mp4, prores->mov)");
        return PE_ERROR_INVALID_ARGUMENT;
    }

    // Validate against the ALREADY-OPEN snapshot (§4.1): its outputWidth/Height must equal the
    // caller-resolved options width/height — native asserts the invariant, never re-derives it.
    std::shared_ptr<const TimelineSnapshot> snapshot = timeline_->CurrentSnapshot();
    if (!snapshot)
    {
        reportError("PE_ExportStart: timeline has no open snapshot");
        return PE_ERROR_INVALID_HANDLE;
    }
    if (snapshot->outputWidth != options.width || snapshot->outputHeight != options.height)
    {
        reportError("PE_ExportStart: options width/height must equal the open snapshot's output size");
        return PE_ERROR_INVALID_ARGUMENT;
    }

    const int64_t totalFrames = TotalOutputFrames(*snapshot);
    if (totalFrames <= 0)
    {
        reportError("PE_ExportStart: timeline is empty (no frames to export)");
        return PE_ERROR_INVALID_ARGUMENT;
    }
    const bool hasAudio = SnapshotHasAudio(*snapshot);
    const double fps = snapshot->Fps();
    const double samplesPerFrame = static_cast<double>(kAudioSampleRate) / fps;

    if (isCancelled())
    {
        return PE_ERROR_CANCELLED;
    }

    // --- PREPARING: open the encoder against the temp path, build the readback ring --------------
    const std::string stagingPath = StagingPathFor(options.outputPath);

    ExportEncoder encoder(owner_);
    ExportEncoder::Config cfg;
    cfg.codec = codec;
    cfg.container = options.container;
    cfg.outputPath = stagingPath;
    cfg.width = options.width;
    cfg.height = options.height;
    cfg.fps = AVRational{options.fps, 1};
    cfg.hasAudio = hasAudio;
    cfg.audioSampleRate = kAudioSampleRate;
    cfg.audioChannels = kAudioChannels;
    int32_t status = encoder.Open(cfg);
    if (status != PE_OK)
    {
        DeleteIfExists(stagingPath); // Open may have created the file before failing (§9)
        return status; // encoder already set the session's last-error message
    }

    // GPU device/context for the readback ring — shared with the timeline's compositor (§5). Export
    // is GPU-only (the RGB->NV12 convert has no CPU path, like color scopes); a null device fails.
    std::string deviceError;
    ID3D11Device* device = owner_->EnsureGraphicsDeviceShared(deviceError);
    ID3D11DeviceContext* context = owner_->GraphicsContext();
    if (!device || !context)
    {
        encoder.Abort();
        DeleteIfExists(stagingPath);
        reportError("PE_ExportStart: no D3D11 device available for export: " + deviceError);
        return PE_ERROR_UNKNOWN;
    }
    ExportReadback readback(device, context, readbackFormat, options.width, options.height, 3);

    AudioMixer audioMixer;
    std::vector<float> audioScratch;

    // Setup done — cross into the per-frame loop (§4.3: onPhase fires at most once, here).
    if (callbacks.onPhase)
    {
        callbacks.onPhase(callbacks.userCtx, PE_EXPORT_PHASE_EXPORTING);
    }

    auto fail = [&](const std::string& message) -> int32_t {
        encoder.Abort();
        DeleteIfExists(stagingPath);
        reportError(message);
        return PE_ERROR_ENCODE_FAILED;
    };
    auto cancel = [&]() -> int32_t {
        encoder.Abort();
        DeleteIfExists(stagingPath);
        return PE_ERROR_CANCELLED;
    };

    auto encodeVideoFrame = [&](const ExportReadback::Frame& frame) -> bool {
        const uint8_t* planeData[3] = {};
        int32_t rowBytes[3] = {};
        int32_t rowCount[3] = {};
        for (int32_t pl = 0; pl < frame.planeCount; ++pl)
        {
            planeData[pl] = frame.planes[static_cast<size_t>(pl)].data();
            rowBytes[pl] = frame.rowBytes[static_cast<size_t>(pl)];
            rowCount[pl] = frame.rowCount[static_cast<size_t>(pl)];
        }
        return encoder.PushVideoFrame(planeData, rowBytes, rowCount, frame.planeCount, frame.frameIndex) == PE_OK;
    };

    auto pushAudioForFrame = [&](int64_t frame) -> bool {
        if (!hasAudio)
        {
            return true;
        }
        int64_t startSample = static_cast<int64_t>(std::llround(static_cast<double>(frame) * samplesPerFrame));
        int64_t endSample = static_cast<int64_t>(std::llround(static_cast<double>(frame + 1) * samplesPerFrame));
        int32_t n = static_cast<int32_t>(endSample - startSample);
        if (n <= 0)
        {
            return true;
        }
        audioScratch.assign(static_cast<size_t>(n) * kAudioChannels, 0.0f);
        std::string mixError;
        if (!audioMixer.RenderRange(*snapshot, frame, n, audioScratch.data(), mixError))
        {
            reportError("PE_ExportStart: audio mix failed: " + mixError);
            return false;
        }
        return encoder.PushAudio(audioScratch.data(), n) == PE_OK;
    };

    // --- EXPORTING: sequential compose -> convert -> encode, one output frame at a time ----------
    for (int64_t k = 0; k < totalFrames; ++k)
    {
        // Decode boundary (§8.1): before this frame's compose (which drives per-clip decode).
        if (isCancelled())
        {
            return cancel();
        }

        bool hasFrame = false;
        ExportReadback::Frame converted;
        {
            std::lock_guard<std::recursive_mutex> gfxLock(owner_->GraphicsMutex());
            ID3D11ShaderResourceView* accumSrv = nullptr;
            int32_t composeW = 0, composeH = 0;
            std::string composeError;
            if (!timeline_->ComposeExportFrameLocked(*snapshot, k, &cancelFlag, accumSrv, composeW, composeH, composeError))
            {
                if (isCancelled())
                {
                    return cancel();
                }
                return fail("PE_ExportStart: compose failed at frame " + std::to_string(k) + ": " + composeError);
            }
            // Dispatch boundary (§8.2): after compose, before the RGB->NV12 dispatch + CopyResource.
            if (isCancelled())
            {
                return cancel();
            }
            std::string readbackError;
            if (!readback.Submit(accumSrv, k, hasFrame, converted, readbackError))
            {
                return fail("PE_ExportStart: readback failed at frame " + std::to_string(k) + ": " + readbackError);
            }
        }

        if (hasFrame)
        {
            // Encode boundary (§8.3): before avcodec_send_frame for this frame's video packet.
            if (isCancelled())
            {
                return cancel();
            }
            if (!encodeVideoFrame(converted))
            {
                return fail("PE_ExportStart: video encode failed at frame " + std::to_string(converted.frameIndex));
            }
        }

        // Encode boundary (§8.3) for the interleaved audio chunk — pulled contiguously per frame.
        if (isCancelled())
        {
            return cancel();
        }
        if (!pushAudioForFrame(k))
        {
            return fail(owner_->LastErrorMessage());
        }

        if (callbacks.onProgress)
        {
            callbacks.onProgress(callbacks.userCtx, static_cast<double>(k + 1) / static_cast<double>(totalFrames));
        }
    }

    // Drain tail (§5.4): the last kMapLag frames the pipelined Submit never returned.
    std::vector<ExportReadback::Frame> tail;
    std::string drainError;
    {
        std::lock_guard<std::recursive_mutex> gfxLock(owner_->GraphicsMutex());
        if (!readback.Drain(tail, drainError))
        {
            return fail("PE_ExportStart: readback drain failed: " + drainError);
        }
    }
    for (const ExportReadback::Frame& frame : tail)
    {
        if (isCancelled())
        {
            return cancel();
        }
        if (!encodeVideoFrame(frame))
        {
            return fail("PE_ExportStart: video encode failed at drain frame " + std::to_string(frame.frameIndex));
        }
    }

    // Finalize: flush encoders + write the container trailer. Only on PE_OK is the temp file complete.
    status = encoder.Close();
    if (status != PE_OK)
    {
        DeleteIfExists(stagingPath); // encoder already set the last-error message
        return status;
    }

    // Atomic publish (§9): same-volume rename onto outputPath. Nothing ever appears at outputPath
    // until this succeeds; on failure the temp file is removed and outputPath is left untouched.
    if (!MoveFileExA(stagingPath.c_str(), options.outputPath.c_str(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED))
    {
        DWORD lastError = GetLastError();
        DeleteIfExists(stagingPath);
        reportError("PE_ExportStart: failed to move export into place (error " + std::to_string(lastError) + ")");
        return PE_ERROR_UNKNOWN;
    }

    if (outResult)
    {
        outResult->framesEncoded = totalFrames;
        outResult->usedHardwareEncoder = encoder.UsedHardwareEncoder() ? 1 : 0;
        std::memset(outResult->encoderName, 0, sizeof(outResult->encoderName));
        const std::string& name = encoder.EncoderName();
        size_t cap = sizeof(outResult->encoderName) - 1;
        size_t copyLen = name.size() < cap ? name.size() : cap;
        std::memcpy(outResult->encoderName, name.data(), copyLen);
    }
    return PE_OK;
}
