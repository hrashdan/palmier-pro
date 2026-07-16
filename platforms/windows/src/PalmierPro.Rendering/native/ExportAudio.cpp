#include "ExportAudio.h"

extern "C"
{
#include <libavutil/audio_fifo.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libavutil/mathematics.h>
#include <libswresample/swresample.h>
}

#include <algorithm>
#include <cmath>
#include <cstring>

namespace
{
    std::string AvErrorToString(int errnum)
    {
        char buf[AV_ERROR_MAX_STRING_SIZE] = {0};
        av_strerror(errnum, buf, sizeof(buf));
        return std::string(buf);
    }

    AVChannelLayout ChannelLayoutFor(int32_t channels)
    {
        if (channels == 1)
        {
            AVChannelLayout mono = AV_CHANNEL_LAYOUT_MONO;
            return mono;
        }
        AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO; // v1 export audio is always stereo in, stereo out
        return stereo;
    }
}

ExportAudio::ExportAudio(const OutputFormat& outputFormat) : outputFormat_(outputFormat) {}

ExportAudio::~ExportAudio()
{
    if (swr_)
    {
        swr_free(&swr_);
    }
    if (fifo_)
    {
        av_audio_fifo_free(fifo_);
        fifo_ = nullptr;
    }
}

int64_t ExportAudio::PackedPcmBytesForTest(int64_t totalSampleCount, const OutputFormat& outputFormat)
{
    if (totalSampleCount <= 0 || outputFormat.channels <= 0)
    {
        return 0;
    }
    int bytesPerSample = av_get_bytes_per_sample(outputFormat.sampleFormat);
    if (bytesPerSample <= 0)
    {
        return 0;
    }
    return totalSampleCount * outputFormat.channels * bytesPerSample;
}

bool ExportAudio::EnsureSwr(std::string& outError)
{
    if (swr_)
    {
        return true;
    }
    if (outputFormat_.sampleRate <= 0 || outputFormat_.channels <= 0 || outputFormat_.frameSize <= 0)
    {
        outError = "ExportAudio: invalid output format";
        return false;
    }

    AVChannelLayout inLayout = AV_CHANNEL_LAYOUT_STEREO; // AudioMixer::RenderRange's own fixed output shape
    AVChannelLayout outLayout = ChannelLayoutFor(outputFormat_.channels);
    int ret = swr_alloc_set_opts2(&swr_, &outLayout, outputFormat_.sampleFormat, outputFormat_.sampleRate,
        &inLayout, AV_SAMPLE_FMT_FLT, static_cast<int>(AudioMixer::kMixSampleRate), 0, nullptr);
    if (ret < 0 || !swr_)
    {
        outError = "swr_alloc_set_opts2 (export audio) failed: " + AvErrorToString(ret);
        return false;
    }
    ret = swr_init(swr_);
    if (ret < 0)
    {
        outError = "swr_init (export audio) failed: " + AvErrorToString(ret);
        swr_free(&swr_);
        return false;
    }

    fifo_ = av_audio_fifo_alloc(outputFormat_.sampleFormat, outputFormat_.channels, outputFormat_.frameSize);
    if (!fifo_)
    {
        outError = "av_audio_fifo_alloc (export audio) failed";
        swr_free(&swr_);
        return false;
    }
    return true;
}

AVFrame* ExportAudio::AllocFrame(int32_t nbSamples, std::string& outError)
{
    AVFrame* frame = av_frame_alloc();
    if (!frame)
    {
        outError = "av_frame_alloc (export audio) failed";
        return nullptr;
    }
    frame->format = outputFormat_.sampleFormat;
    frame->sample_rate = outputFormat_.sampleRate;
    AVChannelLayout layout = ChannelLayoutFor(outputFormat_.channels);
    av_channel_layout_copy(&frame->ch_layout, &layout);
    frame->nb_samples = nbSamples;
    int ret = av_frame_get_buffer(frame, 0);
    if (ret < 0)
    {
        outError = "av_frame_get_buffer (export audio) failed: " + AvErrorToString(ret);
        av_frame_free(&frame);
        return nullptr;
    }
    return frame;
}

bool ExportAudio::PushConverted(const uint8_t* inData, int32_t inSampleCount, std::string& outError)
{
    const uint8_t* inPlanes[1] = {inData};
    int64_t delay = swr_get_delay(swr_, static_cast<int64_t>(AudioMixer::kMixSampleRate));
    int64_t maxOutSamples = av_rescale_rnd(delay + inSampleCount, outputFormat_.sampleRate,
        static_cast<int64_t>(AudioMixer::kMixSampleRate), AV_ROUND_UP);
    if (maxOutSamples <= 0)
    {
        return true; // nothing to push yet (shouldn't happen with real input, but not an error)
    }

    uint8_t** convertedData = nullptr;
    int convertedLinesize = 0;
    int allocRet = av_samples_alloc_array_and_samples(&convertedData, &convertedLinesize,
        outputFormat_.channels, static_cast<int>(maxOutSamples), outputFormat_.sampleFormat, 0);
    if (allocRet < 0)
    {
        outError = "av_samples_alloc_array_and_samples (export audio) failed: " + AvErrorToString(allocRet);
        return false;
    }

    int converted = swr_convert(swr_, convertedData, static_cast<int>(maxOutSamples), inPlanes, inSampleCount);
    bool ok = true;
    if (converted < 0)
    {
        outError = "swr_convert (export audio) failed: " + AvErrorToString(converted);
        ok = false;
    }
    else if (converted > 0)
    {
        if (av_audio_fifo_realloc(fifo_, av_audio_fifo_size(fifo_) + converted) < 0)
        {
            outError = "av_audio_fifo_realloc (export audio) failed";
            ok = false;
        }
        else if (av_audio_fifo_write(fifo_, reinterpret_cast<void**>(convertedData), converted) < converted)
        {
            outError = "av_audio_fifo_write (export audio) short write";
            ok = false;
        }
    }

    av_freep(&convertedData[0]);
    av_freep(&convertedData);
    return ok;
}

bool ExportAudio::PullFullFrames(std::vector<AVFrame*>& outReadyFrames, std::string& outError)
{
    while (av_audio_fifo_size(fifo_) >= outputFormat_.frameSize)
    {
        AVFrame* frame = AllocFrame(outputFormat_.frameSize, outError);
        if (!frame)
        {
            return false;
        }
        if (av_audio_fifo_read(fifo_, reinterpret_cast<void**>(frame->data), outputFormat_.frameSize) <
            outputFormat_.frameSize)
        {
            outError = "av_audio_fifo_read (export audio) short read";
            av_frame_free(&frame);
            return false;
        }
        frame->pts = framesEmitted_;
        framesEmitted_ += outputFormat_.frameSize;
        outReadyFrames.push_back(frame);
    }
    return true;
}

bool ExportAudio::RenderRange(const TimelineSnapshot& snapshot, int64_t startFrame, int32_t sampleCount,
    std::vector<AVFrame*>& outReadyFrames, std::string& outError)
{
    if (sampleCount <= 0)
    {
        outError = "ExportAudio::RenderRange: invalid sampleCount";
        return false;
    }
    if (!EnsureSwr(outError))
    {
        return false;
    }

    mixScratch_.resize(static_cast<size_t>(sampleCount) * AudioMixer::kChannels);
    if (!mixer_.RenderRange(snapshot, startFrame, sampleCount, mixScratch_.data(), outError))
    {
        return false;
    }
    if (!PushConverted(reinterpret_cast<const uint8_t*>(mixScratch_.data()), sampleCount, outError))
    {
        return false;
    }
    return PullFullFrames(outReadyFrames, outError);
}

bool ExportAudio::Flush(std::vector<AVFrame*>& outReadyFrames, std::string& outError)
{
    if (!swr_)
    {
        return true; // RenderRange was never called — nothing buffered anywhere
    }

    int64_t delaySamples = swr_get_delay(swr_, outputFormat_.sampleRate);
    if (delaySamples > 0)
    {
        uint8_t** convertedData = nullptr;
        int convertedLinesize = 0;
        int allocRet = av_samples_alloc_array_and_samples(&convertedData, &convertedLinesize,
            outputFormat_.channels, static_cast<int>(delaySamples), outputFormat_.sampleFormat, 0);
        if (allocRet < 0)
        {
            outError = "av_samples_alloc_array_and_samples (export audio flush) failed: " + AvErrorToString(allocRet);
            return false;
        }

        int converted = swr_convert(swr_, convertedData, static_cast<int>(delaySamples), nullptr, 0);
        bool ok = true;
        if (converted < 0)
        {
            outError = "swr_convert (export audio flush) failed: " + AvErrorToString(converted);
            ok = false;
        }
        else if (converted > 0)
        {
            if (av_audio_fifo_realloc(fifo_, av_audio_fifo_size(fifo_) + converted) < 0 ||
                av_audio_fifo_write(fifo_, reinterpret_cast<void**>(convertedData), converted) < converted)
            {
                outError = "av_audio_fifo write (export audio flush) failed";
                ok = false;
            }
        }
        av_freep(&convertedData[0]);
        av_freep(&convertedData);
        if (!ok)
        {
            return false;
        }
    }

    if (!PullFullFrames(outReadyFrames, outError))
    {
        return false;
    }

    // Trailing partial frame: NOT padded to frameSize (class doc) — a fixed-frame-size encoder
    // accepts a short last frame; this is the standard FFmpeg encode-flush convention.
    int32_t leftover = av_audio_fifo_size(fifo_);
    if (leftover > 0)
    {
        AVFrame* frame = AllocFrame(leftover, outError);
        if (!frame)
        {
            return false;
        }
        if (av_audio_fifo_read(fifo_, reinterpret_cast<void**>(frame->data), leftover) < leftover)
        {
            outError = "av_audio_fifo_read (export audio flush tail) short read";
            av_frame_free(&frame);
            return false;
        }
        frame->pts = framesEmitted_;
        framesEmitted_ += leftover;
        outReadyFrames.push_back(frame);
    }
    return true;
}

bool ExportAudio::RenderTimelineRangeForTest(const TimelineSnapshot& snapshot, int64_t startFrame,
    int64_t frameCount, int32_t chunkFrameCount, std::vector<AVFrame*>& outFrames, std::string& outError)
{
    if (frameCount <= 0 || chunkFrameCount <= 0)
    {
        outError = "ExportAudio::RenderTimelineRangeForTest: invalid frameCount/chunkFrameCount";
        return false;
    }

    auto freeAll = [&](std::vector<AVFrame*>& frames) {
        for (AVFrame*& f : frames)
        {
            av_frame_free(&f);
        }
        frames.clear();
    };

    const double samplesPerFrame = static_cast<double>(AudioMixer::kMixSampleRate) / snapshot.Fps();
    for (int64_t chunkStart = 0; chunkStart < frameCount; chunkStart += chunkFrameCount)
    {
        int64_t thisFrames = std::min<int64_t>(chunkFrameCount, frameCount - chunkStart);
        int64_t rangeStartFrame = startFrame + chunkStart;
        int64_t rangeStartSample = static_cast<int64_t>(std::llround(rangeStartFrame * samplesPerFrame));
        int64_t rangeEndSample =
            static_cast<int64_t>(std::llround((rangeStartFrame + thisFrames) * samplesPerFrame));
        int32_t sampleCount = static_cast<int32_t>(rangeEndSample - rangeStartSample);
        if (sampleCount <= 0)
        {
            continue;
        }

        std::vector<AVFrame*> chunkFrames;
        if (!RenderRange(snapshot, rangeStartFrame, sampleCount, chunkFrames, outError))
        {
            freeAll(chunkFrames);
            freeAll(outFrames);
            return false;
        }
        for (AVFrame* f : chunkFrames)
        {
            outFrames.push_back(f);
        }
    }

    std::vector<AVFrame*> tailFrames;
    if (!Flush(tailFrames, outError))
    {
        freeAll(tailFrames);
        freeAll(outFrames);
        return false;
    }
    for (AVFrame* f : tailFrames)
    {
        outFrames.push_back(f);
    }
    return true;
}
