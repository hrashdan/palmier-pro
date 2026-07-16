#include "ExportEncoder.h"
#include "EngineSession.h"

#include <windows.h>

#include <algorithm>
#include <cstring>
#include <vector>

extern "C"
{
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libavutil/samplefmt.h>
}

namespace
{
    std::string AvErrorToString(int errnum)
    {
        char buf[AV_ERROR_MAX_STRING_SIZE] = {0};
        av_strerror(errnum, buf, sizeof(buf));
        return std::string(buf);
    }

    // True if `encoder` accepts `pixFmt` as an input format (FFmpeg 8 config API). A NULL config
    // list means "all formats supported" (per avcodec_get_supported_config's contract).
    bool EncoderSupportsPixFmt(const AVCodec* encoder, AVPixelFormat pixFmt)
    {
        const void* configs = nullptr;
        int count = 0;
        if (avcodec_get_supported_config(nullptr, encoder, AV_CODEC_CONFIG_PIX_FORMAT, 0, &configs, &count) < 0)
        {
            return false;
        }
        if (!configs)
        {
            return true;
        }
        const auto* fmts = static_cast<const AVPixelFormat*>(configs);
        for (int i = 0; i < count; ++i)
        {
            if (fmts[i] == pixFmt)
            {
                return true;
            }
        }
        return false;
    }

    void CopyPlane(uint8_t* dst, int dstLinesize, const uint8_t* src, int srcRowBytes, int rows)
    {
        int bytesPerRow = std::min(dstLinesize, srcRowBytes);
        for (int y = 0; y < rows; ++y)
        {
            std::memcpy(dst + static_cast<size_t>(y) * dstLinesize,
                src + static_cast<size_t>(y) * srcRowBytes,
                static_cast<size_t>(bytesPerRow));
        }
    }
}

ExportEncoder::ExportEncoder(EngineSession* owner) : owner_(owner) {}

ExportEncoder::~ExportEncoder()
{
    ReleaseResources();
}

void ExportEncoder::SetLastError(const std::string& message)
{
    if (owner_)
    {
        owner_->SetLastError(message);
    }
}

bool ExportEncoder::EnvForceSoftwareEncode()
{
    char buf[8]{};
    DWORD len = GetEnvironmentVariableA("PALMIERENGINE_FORCE_SW_ENCODE", buf, sizeof(buf));
    return len > 0 && len < sizeof(buf) && buf[0] == '1';
}

void ExportEncoder::ApplyQualityOptions(const AVCodec* encoder, AVDictionary** opts) const
{
    std::string name = encoder->name;
    if (name == "libx264" || name == "libx265")
    {
        av_dict_set_int(opts, "crf", codec_ == Codec::H264 ? kH264Crf : kH265Crf, 0);
        av_dict_set(opts, "preset", "medium", 0);
    }
    else if (name.find("nvenc") != std::string::npos)
    {
        // NVENC has no CRF — its constant-quality analog is VBR + cq. p5 ~ "medium" preset.
        av_dict_set(opts, "rc", "vbr", 0);
        av_dict_set_int(opts, "cq", codec_ == Codec::H264 ? kH264Crf + 1 : kH265Crf + 1, 0);
        av_dict_set(opts, "preset", "p5", 0);
    }
    else if (name.find("qsv") != std::string::npos)
    {
        // QSV's ICQ constant-quality mode is driven by global_quality (set on the context below).
        av_dict_set(opts, "preset", "medium", 0);
    }
    else if (name == "prores_ks")
    {
        // "422" (standard) profile — matches AVAssetExportPresetAppleProRes422 (§6). Set via the
        // private option because prores_ks ignores AVCodecContext::profile and defaults to auto (HQ).
        av_dict_set(opts, "profile", "standard", 0);
    }
}

bool ExportEncoder::TryOpenCandidate(const char* encoderName, AVPixelFormat preferredPixFmt,
    AVPixelFormat fallbackPixFmt, bool hardware, std::string& outError)
{
    const AVCodec* encoder = avcodec_find_encoder_by_name(encoderName);
    if (!encoder)
    {
        outError = std::string(encoderName) + " not present in this ffmpeg build";
        return false;
    }

    // libx265 rejects NV12 (planar input only); libx264/*_nvenc/*_qsv accept it. Pick whichever the
    // chosen encoder supports so BuildVideoFrame knows whether to de-interleave the readback chroma.
    AVPixelFormat pixFmt = EncoderSupportsPixFmt(encoder, preferredPixFmt) ? preferredPixFmt : fallbackPixFmt;

    AVCodecContext* ctx = avcodec_alloc_context3(encoder);
    if (!ctx)
    {
        outError = "avcodec_alloc_context3 failed";
        return false;
    }

    ctx->width = width_;
    ctx->height = height_;
    ctx->pix_fmt = pixFmt;
    ctx->time_base = av_inv_q(fps_);
    ctx->framerate = fps_;
    ctx->sample_aspect_ratio = AVRational{1, 1};
    ctx->color_range = AVCOL_RANGE_MPEG; // limited ("tv") range, export-v1.md §5.1
    ctx->color_primaries = AVCOL_PRI_BT709;
    ctx->color_trc = AVCOL_TRC_BT709;
    ctx->colorspace = AVCOL_SPC_BT709;
    ctx->chroma_sample_location = AVCHROMA_LOC_LEFT; // §5.2
    // ProRes 422 ("standard") profile is set via prores_ks's private "profile" option in
    // ApplyQualityOptions — prores_ks ignores AVCodecContext::profile and otherwise auto-selects HQ.
    std::string name = encoder->name;
    if (name.find("qsv") != std::string::npos)
    {
        ctx->global_quality = (codec_ == Codec::H264 ? kH264Crf + 5 : kH265Crf + 5); // QSV ICQ value
    }
    if (formatCtx_->oformat->flags & AVFMT_GLOBALHEADER)
    {
        ctx->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    }

    AVDictionary* opts = nullptr;
    ApplyQualityOptions(encoder, &opts);
    int ret = avcodec_open2(ctx, encoder, &opts);
    av_dict_free(&opts);
    if (ret < 0)
    {
        outError = std::string("avcodec_open2(") + encoderName + ") failed: " + AvErrorToString(ret);
        avcodec_free_context(&ctx);
        return false;
    }

    videoCtx_ = ctx;
    videoPixFmt_ = pixFmt;
    usedHardwareEncoder_ = hardware;
    encoderName_ = encoder->name;
    return true;
}

bool ExportEncoder::SelectAndOpenVideoEncoder(std::string& outError)
{
    struct Candidate { const char* name; bool hardware; };

    if (codec_ == Codec::ProRes)
    {
        return TryOpenCandidate("prores_ks", AV_PIX_FMT_YUV422P10LE, AV_PIX_FMT_YUV422P10LE, false, outError);
    }

    // NV12 preferred everywhere; yuv420p fallback (libx265) — BuildVideoFrame handles both (§5.2).
    const bool isH264 = codec_ == Codec::H264;
    std::vector<Candidate> candidates;
    if (!forceSoftware_)
    {
        candidates.push_back({isH264 ? "h264_nvenc" : "hevc_nvenc", true});
        candidates.push_back({isH264 ? "h264_qsv" : "hevc_qsv", true});
    }
    candidates.push_back({isH264 ? "libx264" : "libx265", false});

    std::string lastError;
    for (const Candidate& c : candidates)
    {
        if (TryOpenCandidate(c.name, AV_PIX_FMT_NV12, AV_PIX_FMT_YUV420P, c.hardware, lastError))
        {
            return true;
        }
        if (c.hardware)
        {
            std::string msg = "PalmierEngine export: " + std::string(c.name) + " unavailable ("
                + lastError + "), falling back\n";
            OutputDebugStringA(msg.c_str());
        }
    }
    outError = "no video encoder could be opened: " + lastError;
    return false;
}

bool ExportEncoder::OpenAudioEncoder(std::string& outError)
{
    const AVCodec* encoder = avcodec_find_encoder_by_name("aac");
    if (!encoder)
    {
        outError = "aac encoder not present in this ffmpeg build";
        return false;
    }

    audioStream_ = avformat_new_stream(formatCtx_, nullptr);
    if (!audioStream_)
    {
        outError = "avformat_new_stream(audio) failed";
        return false;
    }

    audioCtx_ = avcodec_alloc_context3(encoder);
    if (!audioCtx_)
    {
        outError = "avcodec_alloc_context3(aac) failed";
        return false;
    }
    audioCtx_->sample_fmt = AV_SAMPLE_FMT_FLTP;
    audioCtx_->sample_rate = audioSampleRate_;
    audioCtx_->bit_rate = kAacBitRate;
    audioCtx_->time_base = AVRational{1, audioSampleRate_};
    av_channel_layout_default(&audioCtx_->ch_layout, audioChannels_);
    if (formatCtx_->oformat->flags & AVFMT_GLOBALHEADER)
    {
        audioCtx_->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    }

    int ret = avcodec_open2(audioCtx_, encoder, nullptr);
    if (ret < 0)
    {
        outError = "avcodec_open2(aac) failed: " + AvErrorToString(ret);
        return false;
    }
    ret = avcodec_parameters_from_context(audioStream_->codecpar, audioCtx_);
    if (ret < 0)
    {
        outError = "avcodec_parameters_from_context(audio) failed: " + AvErrorToString(ret);
        return false;
    }
    audioStream_->time_base = audioCtx_->time_base;

    AVChannelLayout inLayout;
    av_channel_layout_default(&inLayout, audioChannels_);
    ret = swr_alloc_set_opts2(&swr_, &audioCtx_->ch_layout, AV_SAMPLE_FMT_FLTP, audioSampleRate_,
        &inLayout, AV_SAMPLE_FMT_FLT, audioSampleRate_, 0, nullptr);
    av_channel_layout_uninit(&inLayout);
    if (ret < 0 || !swr_ || swr_init(swr_) < 0)
    {
        outError = "swr init (audio) failed";
        return false;
    }

    audioFifo_ = av_audio_fifo_alloc(AV_SAMPLE_FMT_FLTP, audioChannels_, 1);
    if (!audioFifo_)
    {
        outError = "av_audio_fifo_alloc failed";
        return false;
    }
    return true;
}

int32_t ExportEncoder::Open(const Config& config)
{
    if (config.width <= 0 || config.height <= 0 || (config.width % 2) != 0 || (config.height % 2) != 0)
    {
        SetLastError("ExportEncoder::Open: width/height must be positive and even");
        return PE_ERROR_INVALID_ARGUMENT;
    }
    const bool wantsMov = config.codec == Codec::ProRes;
    if ((wantsMov && config.container != "mov") || (!wantsMov && config.container != "mp4"))
    {
        SetLastError("ExportEncoder::Open: codec/container mismatch (h264|h265->mp4, prores->mov)");
        return PE_ERROR_INVALID_ARGUMENT;
    }

    codec_ = config.codec;
    width_ = config.width;
    height_ = config.height;
    fps_ = (config.fps.num > 0 && config.fps.den > 0) ? config.fps : AVRational{30, 1};
    hasAudio_ = config.hasAudio;
    audioSampleRate_ = config.audioSampleRate > 0 ? config.audioSampleRate : 48000;
    audioChannels_ = config.audioChannels > 0 ? config.audioChannels : 2;
    forceSoftware_ = config.forceSoftware || EnvForceSoftwareEncode();

    int ret = avformat_alloc_output_context2(&formatCtx_, nullptr, config.container.c_str(),
        config.outputPath.c_str());
    if (ret < 0 || !formatCtx_)
    {
        SetLastError("avformat_alloc_output_context2 failed: " + AvErrorToString(ret));
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }

    std::string error;
    if (!SelectAndOpenVideoEncoder(error))
    {
        SetLastError(error);
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }

    videoStream_ = avformat_new_stream(formatCtx_, nullptr);
    if (!videoStream_)
    {
        SetLastError("avformat_new_stream(video) failed");
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }
    ret = avcodec_parameters_from_context(videoStream_->codecpar, videoCtx_);
    if (ret < 0)
    {
        SetLastError("avcodec_parameters_from_context(video) failed: " + AvErrorToString(ret));
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }
    videoStream_->time_base = videoCtx_->time_base;
    videoStream_->avg_frame_rate = fps_;

    if (hasAudio_ && !OpenAudioEncoder(error))
    {
        SetLastError(error);
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }

    if (!(formatCtx_->oformat->flags & AVFMT_NOFILE))
    {
        ret = avio_open(&formatCtx_->pb, config.outputPath.c_str(), AVIO_FLAG_WRITE);
        if (ret < 0)
        {
            SetLastError("avio_open failed: " + AvErrorToString(ret));
            ReleaseResources();
            return PE_ERROR_FILE_OPEN_FAILED;
        }
    }

    AVDictionary* muxOpts = nullptr;
    if (config.container == "mp4")
    {
        av_dict_set(&muxOpts, "movflags", "+faststart", 0); // moov atom at the front (§7)
    }
    ret = avformat_write_header(formatCtx_, &muxOpts);
    av_dict_free(&muxOpts);
    if (ret < 0)
    {
        SetLastError("avformat_write_header failed: " + AvErrorToString(ret));
        ReleaseResources();
        return PE_ERROR_ENCODE_FAILED;
    }
    headerWritten_ = true;

    videoFrame_ = av_frame_alloc();
    if (!videoFrame_)
    {
        SetLastError("av_frame_alloc failed");
        ReleaseResources();
        return PE_ERROR_UNKNOWN;
    }
    videoFrame_->format = videoPixFmt_;
    videoFrame_->width = width_;
    videoFrame_->height = height_;
    videoFrame_->color_range = AVCOL_RANGE_MPEG;
    ret = av_frame_get_buffer(videoFrame_, 0);
    if (ret < 0)
    {
        SetLastError("av_frame_get_buffer(video) failed: " + AvErrorToString(ret));
        ReleaseResources();
        return PE_ERROR_UNKNOWN;
    }

    packet_ = av_packet_alloc();
    if (!packet_)
    {
        SetLastError("av_packet_alloc failed");
        ReleaseResources();
        return PE_ERROR_UNKNOWN;
    }
    return PE_OK;
}

bool ExportEncoder::EncodeAndMux(AVCodecContext* ctx, AVStream* stream, AVFrame* frame, std::string& outError)
{
    int ret = avcodec_send_frame(ctx, frame);
    if (ret < 0)
    {
        outError = "avcodec_send_frame failed: " + AvErrorToString(ret);
        return false;
    }
    while (true)
    {
        ret = avcodec_receive_packet(ctx, packet_);
        if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
        {
            break;
        }
        if (ret < 0)
        {
            outError = "avcodec_receive_packet failed: " + AvErrorToString(ret);
            return false;
        }
        av_packet_rescale_ts(packet_, ctx->time_base, stream->time_base);
        packet_->stream_index = stream->index;
        ret = av_interleaved_write_frame(formatCtx_, packet_); // takes ownership + blanks packet_
        if (ret < 0)
        {
            outError = "av_interleaved_write_frame failed: " + AvErrorToString(ret);
            return false;
        }
    }
    return true;
}

bool ExportEncoder::BuildVideoFrame(const uint8_t* const* planeData, const int32_t* planeRowBytes,
    const int32_t* planeRowCount, int32_t planeCount, std::string& outError)
{
    int ret = av_frame_make_writable(videoFrame_);
    if (ret < 0)
    {
        outError = "av_frame_make_writable failed: " + AvErrorToString(ret);
        return false;
    }

    if (videoPixFmt_ == AV_PIX_FMT_NV12)
    {
        if (planeCount < 2)
        {
            outError = "ExportEncoder: NV12 frame needs 2 planes";
            return false;
        }
        CopyPlane(videoFrame_->data[0], videoFrame_->linesize[0], planeData[0], planeRowBytes[0], planeRowCount[0]);
        CopyPlane(videoFrame_->data[1], videoFrame_->linesize[1], planeData[1], planeRowBytes[1], planeRowCount[1]);
    }
    else if (videoPixFmt_ == AV_PIX_FMT_YUV420P)
    {
        // From NV12 input: Y is a direct copy; de-interleave the packed CbCr plane into U and V.
        if (planeCount < 2)
        {
            outError = "ExportEncoder: yuv420p frame needs the 2 NV12 source planes";
            return false;
        }
        CopyPlane(videoFrame_->data[0], videoFrame_->linesize[0], planeData[0], planeRowBytes[0], planeRowCount[0]);
        int chromaWidth = width_ / 2;
        int chromaRows = height_ / 2;
        const uint8_t* uv = planeData[1];
        int uvRowBytes = planeRowBytes[1];
        for (int y = 0; y < chromaRows; ++y)
        {
            const uint8_t* srcRow = uv + static_cast<size_t>(y) * uvRowBytes;
            uint8_t* uRow = videoFrame_->data[1] + static_cast<size_t>(y) * videoFrame_->linesize[1];
            uint8_t* vRow = videoFrame_->data[2] + static_cast<size_t>(y) * videoFrame_->linesize[2];
            for (int cx = 0; cx < chromaWidth; ++cx)
            {
                uRow[cx] = srcRow[2 * cx];
                vRow[cx] = srcRow[2 * cx + 1];
            }
        }
    }
    else // AV_PIX_FMT_YUV422P10LE — three 16-bit planes, direct copy
    {
        if (planeCount < 3)
        {
            outError = "ExportEncoder: yuv422p10le frame needs 3 planes";
            return false;
        }
        for (int p = 0; p < 3; ++p)
        {
            CopyPlane(videoFrame_->data[p], videoFrame_->linesize[p], planeData[p], planeRowBytes[p], planeRowCount[p]);
        }
    }
    return true;
}

int32_t ExportEncoder::PushVideoFrame(const uint8_t* const* planeData, const int32_t* planeRowBytes,
    const int32_t* planeRowCount, int32_t planeCount, int64_t frameIndex)
{
    if (!formatCtx_ || !videoCtx_ || !videoFrame_ || !packet_)
    {
        SetLastError("ExportEncoder::PushVideoFrame: encoder is not open");
        return PE_ERROR_INVALID_HANDLE;
    }
    if (!planeData || !planeRowBytes || !planeRowCount || planeCount <= 0)
    {
        SetLastError("ExportEncoder::PushVideoFrame: invalid plane arguments");
        return PE_ERROR_INVALID_ARGUMENT;
    }

    std::string error;
    if (!BuildVideoFrame(planeData, planeRowBytes, planeRowCount, planeCount, error))
    {
        SetLastError(error);
        return PE_ERROR_ENCODE_FAILED;
    }
    videoFrame_->pts = frameIndex;
    if (!EncodeAndMux(videoCtx_, videoStream_, videoFrame_, error))
    {
        SetLastError(error);
        return PE_ERROR_ENCODE_FAILED;
    }
    return PE_OK;
}

bool ExportEncoder::DrainAudioFifo(bool flushPartial, std::string& outError)
{
    int frameSize = audioCtx_->frame_size > 0 ? audioCtx_->frame_size : 1024;
    int threshold = flushPartial ? 1 : frameSize;
    while (av_audio_fifo_size(audioFifo_) >= threshold)
    {
        int n = std::min(frameSize, av_audio_fifo_size(audioFifo_));
        AVFrame* frame = av_frame_alloc();
        if (!frame)
        {
            outError = "av_frame_alloc(audio) failed";
            return false;
        }
        frame->nb_samples = n;
        frame->format = AV_SAMPLE_FMT_FLTP;
        frame->sample_rate = audioSampleRate_;
        av_channel_layout_copy(&frame->ch_layout, &audioCtx_->ch_layout);
        int ret = av_frame_get_buffer(frame, 0);
        if (ret < 0)
        {
            outError = "av_frame_get_buffer(audio) failed: " + AvErrorToString(ret);
            av_frame_free(&frame);
            return false;
        }
        if (av_audio_fifo_read(audioFifo_, reinterpret_cast<void**>(frame->data), n) < n)
        {
            outError = "av_audio_fifo_read short read";
            av_frame_free(&frame);
            return false;
        }
        frame->pts = nextAudioPts_;
        nextAudioPts_ += n;
        bool ok = EncodeAndMux(audioCtx_, audioStream_, frame, outError);
        av_frame_free(&frame);
        if (!ok)
        {
            return false;
        }
    }
    return true;
}

int32_t ExportEncoder::PushAudio(const float* interleaved, int32_t sampleCount)
{
    if (!hasAudio_)
    {
        return PE_OK;
    }
    if (!formatCtx_ || !audioCtx_ || !swr_ || !audioFifo_)
    {
        SetLastError("ExportEncoder::PushAudio: audio stream is not open");
        return PE_ERROR_INVALID_HANDLE;
    }
    if (!interleaved || sampleCount <= 0)
    {
        return PE_OK;
    }

    uint8_t** conv = nullptr;
    int convLinesize = 0;
    int ret = av_samples_alloc_array_and_samples(&conv, &convLinesize, audioChannels_, sampleCount,
        AV_SAMPLE_FMT_FLTP, 0);
    if (ret < 0 || !conv)
    {
        SetLastError("av_samples_alloc(audio) failed: " + AvErrorToString(ret));
        return PE_ERROR_ENCODE_FAILED;
    }

    const uint8_t* inData[1] = {reinterpret_cast<const uint8_t*>(interleaved)};
    int converted = swr_convert(swr_, conv, sampleCount, inData, sampleCount);
    int32_t status = PE_OK;
    std::string error;
    if (converted < 0)
    {
        SetLastError("swr_convert failed: " + AvErrorToString(converted));
        status = PE_ERROR_ENCODE_FAILED;
    }
    else if (converted > 0)
    {
        if (av_audio_fifo_write(audioFifo_, reinterpret_cast<void**>(conv), converted) < converted)
        {
            SetLastError("av_audio_fifo_write short write");
            status = PE_ERROR_ENCODE_FAILED;
        }
        else if (!DrainAudioFifo(false, error))
        {
            SetLastError(error);
            status = PE_ERROR_ENCODE_FAILED;
        }
    }

    if (conv)
    {
        av_freep(&conv[0]);
        av_freep(&conv);
    }
    return status;
}

int32_t ExportEncoder::Close()
{
    if (!formatCtx_ || !videoCtx_)
    {
        SetLastError("ExportEncoder::Close: encoder is not open");
        ReleaseResources();
        return PE_ERROR_INVALID_HANDLE;
    }

    std::string error;
    bool ok = true;

    if (hasAudio_ && audioCtx_)
    {
        // Resample any samples swr still holds buffered, then flush the remaining partial FIFO frame.
        if (ok)
        {
            uint8_t** conv = nullptr;
            int convLinesize = 0;
            int pending = swr_get_out_samples(swr_, 0);
            if (pending > 0
                && av_samples_alloc_array_and_samples(&conv, &convLinesize, audioChannels_, pending,
                       AV_SAMPLE_FMT_FLTP, 0) >= 0)
            {
                int drained = swr_convert(swr_, conv, pending, nullptr, 0);
                if (drained > 0)
                {
                    av_audio_fifo_write(audioFifo_, reinterpret_cast<void**>(conv), drained);
                }
            }
            if (conv)
            {
                av_freep(&conv[0]);
                av_freep(&conv);
            }
            ok = DrainAudioFifo(true, error);
        }
        if (ok)
        {
            ok = EncodeAndMux(audioCtx_, audioStream_, nullptr, error);
        }
    }

    if (ok)
    {
        ok = EncodeAndMux(videoCtx_, videoStream_, nullptr, error); // flush buffered video packets
    }
    if (ok && headerWritten_)
    {
        int ret = av_write_trailer(formatCtx_);
        if (ret < 0)
        {
            error = "av_write_trailer failed: " + AvErrorToString(ret);
            ok = false;
        }
    }
    if (!ok)
    {
        SetLastError(error);
    }

    ReleaseResources();
    return ok ? PE_OK : PE_ERROR_ENCODE_FAILED;
}

void ExportEncoder::Abort()
{
    ReleaseResources();
}

void ExportEncoder::ReleaseResources()
{
    if (packet_)
    {
        av_packet_free(&packet_);
    }
    if (videoFrame_)
    {
        av_frame_free(&videoFrame_);
    }
    if (audioFifo_)
    {
        av_audio_fifo_free(audioFifo_);
        audioFifo_ = nullptr;
    }
    if (swr_)
    {
        swr_free(&swr_);
    }
    if (videoCtx_)
    {
        avcodec_free_context(&videoCtx_);
    }
    if (audioCtx_)
    {
        avcodec_free_context(&audioCtx_);
    }
    if (formatCtx_)
    {
        if (formatCtx_->pb && formatCtx_->oformat && !(formatCtx_->oformat->flags & AVFMT_NOFILE))
        {
            avio_closep(&formatCtx_->pb);
        }
        avformat_free_context(formatCtx_);
        formatCtx_ = nullptr;
    }
    videoStream_ = nullptr;
    audioStream_ = nullptr;
    videoPixFmt_ = AV_PIX_FMT_NONE;
    nextAudioPts_ = 0;
    headerWritten_ = false;
}
