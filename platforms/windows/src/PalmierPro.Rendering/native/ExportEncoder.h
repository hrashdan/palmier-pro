#pragma once

#include "include/palmier_engine.h"

#include <cstdint>
#include <string>

extern "C"
{
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/audio_fifo.h>
#include <libswresample/swresample.h>
}

class EngineSession;

// E5 video-export encoder/muxer (export-v1.md §6/§7/§9). One muxed output file: a single video
// stream (h264 via libx264/*_nvenc/*_qsv, h265 via libx265/*_nvenc/*_qsv, or ProRes 422 via
// prores_ks) plus one optional AAC audio stream, interleaved by PTS. Consumes the exact planar
// Y'CbCr the ExportReadback compute pass produces (NV12 for h264/h265, yuv422p10le for prores —
// §5.2/§5.3) and Float32/48k/stereo PCM the AudioMixer::RenderRange offline mix produces (§7).
//
// A SIBLING to AlphaVideoEncoder (export-v1.md §6): same FFmpeg wiring pattern (AVFormatContext/
// AVCodecContext/AVStream setup, the mov-muxer timebase discipline, EncodeAndMux), but this class
// is what the alpha encoder deliberately is not — non-alpha, two-stream (video + audio), and
// hardware-capable. It writes to `outputPath` directly (no temp/rename of its own — same as
// AlphaVideoEncoder; the future ExportSession, export-v1.md §9/§14.2, owns the atomic-rename
// discipline around it).
//
// Encoder selection (§6), probed once in Open before the first frame: for h264/h265, opportunistic
// hardware in order h264_nvenc/hevc_nvenc then h264_qsv/hevc_qsv — each attempted via avcodec_open2
// and dropped silently (logged, not an error) if it fails (no such GPU, driver too old, build
// lacks it), falling through to software libx264/libx265, which always succeeds. ProRes is always
// software prores_ks. PALMIERENGINE_FORCE_SW_ENCODE=1 (read via GetEnvironmentVariableA, this
// codebase's env-override convention) OR Config.forceSoftware skips straight to software for
// h264/h265, so CI produces a deterministic, GPU-vendor-independent codec_name.
//
// Not thread-safe on a single instance — the export loop is single-threaded (callers serialize
// PushVideoFrame/PushAudio, same discipline as AlphaVideoEncoder). Video and audio may be pushed in
// any order; av_interleaved_write_frame orders them by each stream's own timebase-scaled DTS.
class ExportEncoder
{
public:
    enum class Codec
    {
        H264,
        H265,
        ProRes,
    };

    struct Config
    {
        Codec codec = Codec::H264;
        std::string container;  // "mp4" (h264/h265) | "mov" (prores) — mismatch is PE_ERROR_INVALID_ARGUMENT
        std::string outputPath; // native-separator absolute path; written directly (no temp/rename here)
        int32_t width = 0;      // positive, EVEN (chroma subsampling)
        int32_t height = 0;     // positive, EVEN
        AVRational fps = {30, 1}; // source timeline fps verbatim (§4.2); video stream timebase = 1/fps
        bool hasAudio = false;
        int32_t audioSampleRate = 48000; // AudioMixer::kMixSampleRate
        int32_t audioChannels = 2;       // AudioMixer::kChannels (interleaved stereo Float32)
        bool forceSoftware = false;      // OR'd with the PALMIERENGINE_FORCE_SW_ENCODE env override
    };

    explicit ExportEncoder(EngineSession* owner);
    ~ExportEncoder();

    ExportEncoder(const ExportEncoder&) = delete;
    ExportEncoder& operator=(const ExportEncoder&) = delete;

    // Selects+opens the encoder(s), opens the container, writes the header. PE_ERROR_INVALID_ARGUMENT
    // for bad dims / codec-container mismatch; PE_ERROR_FILE_OPEN_FAILED if outputPath can't be
    // opened; PE_ERROR_ENCODE_FAILED for any avcodec/avformat setup failure.
    int32_t Open(const Config& config);

    // One converted video frame in the codec's plane layout (ExportReadback::Frame, tightly packed
    // per plane at planeRowBytes[p] x planeRowCount[p]):
    //   h264/h265: planeCount=2 — plane0 Y (R8, width x height), plane1 CbCr interleaved (width/2 x height/2, 2 B/texel)
    //   prores:    planeCount=3 — Y, U, V (each 16-bit LE, U/V are width/2 x height)
    // frameIndex MUST increase by exactly 1 from 0 — it is the frame's PTS in units of 1/fps.
    int32_t PushVideoFrame(const uint8_t* const* planeData, const int32_t* planeRowBytes,
        const int32_t* planeRowCount, int32_t planeCount, int64_t frameIndex);

    // `sampleCount` interleaved Float32 stereo samples at Config.audioSampleRate — one
    // AudioMixer::RenderRange chunk (§7). Resampled to the AAC encoder's FLTP via libswresample,
    // FIFO-buffered, and drained a full AAC frame (1024 samples) at a time. No-op if Config.hasAudio
    // was false. Copies its input before returning.
    int32_t PushAudio(const float* interleaved, int32_t sampleCount);

    // Flushes both encoders, writes the container trailer (faststart-relocates the mp4 moov atom) —
    // the file is complete/playable only once this returns PE_OK. Frees every FFmpeg resource; the
    // instance must not be used again.
    int32_t Close();

    // Cancellation path: frees FFmpeg resources WITHOUT writing a trailer (output left incomplete).
    // Always succeeds; the instance must not be used again.
    void Abort();

    // Diagnostics/telemetry only (§4.4/§6) — valid after Open. "h264_nvenc"/"libx264"/"prores_ks"/...
    bool UsedHardwareEncoder() const { return usedHardwareEncoder_; }
    const std::string& EncoderName() const { return encoderName_; }

private:
    EngineSession* owner_;

    AVFormatContext* formatCtx_ = nullptr;

    AVStream* videoStream_ = nullptr;
    AVCodecContext* videoCtx_ = nullptr;
    AVFrame* videoFrame_ = nullptr;
    AVPixelFormat videoPixFmt_ = AV_PIX_FMT_NONE;

    AVStream* audioStream_ = nullptr;
    AVCodecContext* audioCtx_ = nullptr;
    SwrContext* swr_ = nullptr;
    AVAudioFifo* audioFifo_ = nullptr;
    int64_t nextAudioPts_ = 0;

    AVPacket* packet_ = nullptr;

    Codec codec_ = Codec::H264;
    int32_t width_ = 0;
    int32_t height_ = 0;
    AVRational fps_ = {30, 1};
    bool hasAudio_ = false;
    int32_t audioSampleRate_ = 48000;
    int32_t audioChannels_ = 2;
    bool forceSoftware_ = false;

    bool headerWritten_ = false;
    bool usedHardwareEncoder_ = false;
    std::string encoderName_;

    // Quality defaults mirroring the Mac's AVAssetExportSession presets (§6, ExportService.swift's
    // exportPresetName): the size-named / *HighestQuality presets are all quality-targeted (Apple
    // picks the bitrate internally), so the FFmpeg-equivalent is CRF, not a fixed bitrate. HEVC's
    // CRF scale runs a few points higher than H.264's for equal perceptual quality, hence 20 vs 18.
    // See ExportEncoder.cpp's ConfigureSoftware* for the full table + hardware analogs.
    static constexpr int kH264Crf = 18;
    static constexpr int kH265Crf = 20;
    static constexpr int64_t kAacBitRate = 256000; // high-quality stereo AAC

    bool SelectAndOpenVideoEncoder(std::string& outError);
    bool TryOpenCandidate(const char* encoderName, AVPixelFormat preferredPixFmt,
        AVPixelFormat fallbackPixFmt, bool hardware, std::string& outError);
    void ApplyQualityOptions(const AVCodec* encoder, AVDictionary** opts) const;
    bool OpenAudioEncoder(std::string& outError);
    bool BuildVideoFrame(const uint8_t* const* planeData, const int32_t* planeRowBytes,
        const int32_t* planeRowCount, int32_t planeCount, std::string& outError);
    bool DrainAudioFifo(bool flushPartial, std::string& outError);
    bool EncodeAndMux(AVCodecContext* ctx, AVStream* stream, AVFrame* frame, std::string& outError);
    void ReleaseResources();
    void SetLastError(const std::string& message);

    static bool EnvForceSoftwareEncode();
};
