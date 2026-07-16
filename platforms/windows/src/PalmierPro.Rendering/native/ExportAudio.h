#pragma once

#include "AudioMixer.h"

extern "C"
{
#include <libavutil/frame.h>
#include <libavutil/samplefmt.h>
}

#include <cstdint>
#include <string>
#include <vector>

struct SwrContext;
struct AVAudioFifo;

// E5 export audio (export-v1.md §7's DECISION: reuse the offline AudioMixer path, not a second
// FFmpeg-atempo-based mix). Owns one AudioMixer — so per-clip decode/retime cursors persist
// across chunk calls, the same continuity TimelineSession::audioMixer_ already relies on for
// PE_TimelineRenderAudioRange — plus the libswresample + AVAudioFifo pipeline that turns its
// Float32/48kHz/stereo/interleaved output into the encoder's own sample format, sample rate, and
// fixed-size AVFrame cadence (§7: "resamples AudioMixer::RenderRange's ... output into whatever
// sample format the AAC encoder actually wants, immediately before avcodec_send_frame").
//
// A sibling to ExportReadback (native/ExportReadback.h) for the export pipeline's audio side:
// independent of the muxer/encoder/PE_TimelineHandle, driven by whatever loop pulls chunks of the
// export's total duration. NOTE: the shipping ExportSession (native/ExportSession.cpp) does NOT
// drive this class — it implements §7's reuse-AudioMixer policy directly, calling
// AudioMixer::RenderRange per output frame and handing the Float32 result to ExportEncoder's own
// libswresample+AVAudioFifo path (ExportEncoder::PushAudio). This ExportAudio is a self-contained
// implementation of that same §7 pipeline, exercised on its own by RenderTimelineRangeForTest below
// (PE_ExportAudioRenderForTest). RenderRange's own chunk size is the CALLER's choice and bounds only
// that call's memory, not the whole export's — exactly AudioMixer::RenderRange's own contract.
// PE_TimelineRenderAudioRange has no sub-frame start offset, so successive calls'
// [startFrame, startFrame+sampleCount) ranges must stay contiguous (no gap/overlap) for the
// resampler/retime-stretcher continuity to stay correct.
//
// Not thread-safe on a single instance — the export loop is single-threaded (doc §4.4).
class ExportAudio
{
public:
    // What the resampler produces — must match whatever AVCodecContext the caller's encoder (AAC
    // in v1) was actually opened with. frameSize is the encoder's own fixed frame length in
    // OUTPUT samples (e.g. 1024 for libavcodec's built-in "aac"); must be positive. Every AVFrame
    // RenderRange/Flush appends has exactly this many samples except Flush's own final, possibly
    // shorter tail frame — a fixed-frame-size encoder accepting a short LAST frame is the standard
    // FFmpeg encode-flush convention (its PTS/duration carries the true length, not padding).
    struct OutputFormat
    {
        AVSampleFormat sampleFormat = AV_SAMPLE_FMT_FLTP;
        int32_t sampleRate = 48000;
        int32_t channels = 2;
        int32_t frameSize = 1024;
    };

    explicit ExportAudio(const OutputFormat& outputFormat);
    ~ExportAudio();

    ExportAudio(const ExportAudio&) = delete;
    ExportAudio& operator=(const ExportAudio&) = delete;

    // Mixes [startFrame, startFrame+sampleCount) of `snapshot`'s 48 kHz timeline audio (one
    // AudioMixer::RenderRange call — gain/fade/mute/retime-correct, §7) and pushes the result
    // through the resampler into a FIFO. Appends every outputFormat.frameSize-sample AVFrame the
    // FIFO now has enough samples for to outReadyFrames (submission order; caller owns them —
    // av_frame_free after encoding/use). May append zero, one, or several frames per call.
    bool RenderRange(const TimelineSnapshot& snapshot, int64_t startFrame, int32_t sampleCount,
        std::vector<AVFrame*>& outReadyFrames, std::string& outError);

    // Drains the resampler's internal delay buffer into the FIFO, then flushes the FIFO's
    // trailing partial frame (if any) as one final, un-padded AVFrame shorter than
    // outputFormat.frameSize. Call once after the export's last RenderRange.
    bool Flush(std::vector<AVFrame*>& outReadyFrames, std::string& outError);

    // Test-only convenience: renders [startFrame, startFrame+frameCount) TIMELINE FRAMES of
    // `snapshot`'s audio via chunkFrameCount-frame RenderRange calls (mirrors production's
    // natural video-frame-cadence pull — see class doc above), then Flushes, appending every
    // produced AVFrame (submission order, caller owns them) to outFrames. Exists so
    // PE_ExportAudioRenderForTest can exercise the chunk-boundary/FIFO machinery against a real
    // fixture timeline without duplicating this loop natively per-caller.
    bool RenderTimelineRangeForTest(const TimelineSnapshot& snapshot, int64_t startFrame,
        int64_t frameCount, int32_t chunkFrameCount, std::vector<AVFrame*>& outFrames,
        std::string& outError);

    // Test-only convenience: total PCM bytes RenderTimelineRangeForTest's own output frames
    // occupy when tightly packed (see PE_ExportAudioRenderForTest's doc comment) and
    // outputFormat.sampleRate == 48000 (no rate change — the only ratio these tests exercise):
    // totalSampleCount * channels * bytes-per-sample(sampleFormat), which is the same total either
    // way the samples are laid out (planar: channels planes of totalSampleCount samples each;
    // packed: one plane of totalSampleCount*channels samples). 0 for invalid args.
    static int64_t PackedPcmBytesForTest(int64_t totalSampleCount, const OutputFormat& outputFormat);

private:
    AudioMixer mixer_;
    OutputFormat outputFormat_;

    SwrContext* swr_ = nullptr;
    AVAudioFifo* fifo_ = nullptr;
    int64_t framesEmitted_ = 0; // total OUTPUT samples emitted so far — next AVFrame's pts

    // Interleaved-stereo scratch for AudioMixer::RenderRange, reused/regrown across calls.
    std::vector<float> mixScratch_;

    bool EnsureSwr(std::string& outError);
    bool PushConverted(const uint8_t* inData, int32_t inSampleCount, std::string& outError);
    bool PullFullFrames(std::vector<AVFrame*>& outReadyFrames, std::string& outError);
    AVFrame* AllocFrame(int32_t nbSamples, std::string& outError);
};
