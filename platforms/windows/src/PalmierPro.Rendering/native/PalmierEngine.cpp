#include "include/palmier_engine.h"
#include "AlphaVideoEncoder.h"
#include "EngineSession.h"
#include "ExportEncoder.h"
#include "ExportReadback.h"
#include "FontRegistry.h"
#include "MediaSource.h"
#include "TimelineRegistry.h"
#include "TimelineSession.h"

#include <windows.h>

#include <cmath>
#include <cstring>
#include <memory>
#include <mutex>
#include <new>
#include <vector>

namespace
{
    EngineSession* AsSession(PE_SessionHandle h) { return reinterpret_cast<EngineSession*>(h); }
    MediaSource* AsMedia(PE_MediaHandle h) { return reinterpret_cast<MediaSource*>(h); }

    // Handle-only timeline ABI entry points have no session parameter to validate
    // through, so they resolve via TimelineRegistry instead — see its header comment.
    TimelineSession* ResolveTimeline(PE_TimelineHandle h)
    {
        return TimelineRegistry::Resolve(reinterpret_cast<TimelineSession*>(h));
    }

    // Same reasoning as ResolveTimeline, for PE_AlphaEncoderHandle — see AlphaVideoEncoder.h.
    AlphaVideoEncoder* AsEncoder(PE_AlphaEncoderHandle h)
    {
        return AlphaVideoEncoder::Resolve(reinterpret_cast<AlphaVideoEncoder*>(h));
    }
}

unsigned int PalmierEngine_GetVersion(void)
{
    return 1;
}

int32_t PE_CreateSession(PE_SessionHandle* outSession)
{
    if (!outSession)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    auto* session = new (std::nothrow) EngineSession();
    if (!session)
    {
        return PE_ERROR_UNKNOWN;
    }
    *outSession = reinterpret_cast<PE_SessionHandle>(session);
    return PE_OK;
}

int32_t PE_DestroySession(PE_SessionHandle session)
{
    if (!session)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    delete AsSession(session);
    return PE_OK;
}

const char* PE_GetLastErrorMessage(PE_SessionHandle session)
{
    if (!session)
    {
        return "";
    }
    return AsSession(session)->LastErrorMessage();
}

int32_t PE_OpenMedia(PE_SessionHandle session, const char* utf8Path, PE_MediaHandle* outMedia)
{
    if (!session || !utf8Path || !outMedia)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->OpenMedia(utf8Path, outMedia);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_CloseMedia(PE_SessionHandle session, PE_MediaHandle media)
{
    if (!session || !media)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    return AsSession(session)->CloseMedia(media);
}

int32_t PE_GetMediaInfo(PE_SessionHandle session, PE_MediaHandle media, PE_MediaInfo* outInfo)
{
    if (!session || !media || !outInfo)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    MediaSource* m = s->Resolve(media);
    if (!m)
    {
        s->SetLastError("invalid media handle");
        return PE_ERROR_INVALID_HANDLE;
    }
    *outInfo = m->Info();
    return PE_OK;
}

int32_t PE_DecodeFrameAt(PE_SessionHandle session, PE_MediaHandle media, double timelineSeconds, PE_FrameBuffer* outFrame)
{
    if (!session || !media || !outFrame)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    MediaSource* m = s->Resolve(media);
    if (!m)
    {
        s->SetLastError("invalid media handle");
        return PE_ERROR_INVALID_HANDLE;
    }

    std::string error;
    try
    {
        if (!m->DecodeFrameAt(timelineSeconds, *outFrame, error))
        {
            s->SetLastError(error);
            return PE_ERROR_DECODE_FAILED;
        }
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
    s->ClearLastError();
    return PE_OK;
}

int32_t PE_ExtractThumbnails(
    PE_SessionHandle session,
    PE_MediaHandle media,
    const double* times,
    int32_t count,
    int32_t width,
    int32_t height,
    PE_ThumbnailCallback callback,
    void* userCtx,
    const int32_t* cancelFlag)
{
    if (!session || !media || !times || !callback || count <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    MediaSource* m = s->Resolve(media);
    if (!m)
    {
        s->SetLastError("invalid media handle");
        return PE_ERROR_INVALID_HANDLE;
    }

    std::string error;
    try
    {
        if (!m->ExtractThumbnails(times, count, width, height, callback, userCtx, cancelFlag, error))
        {
            s->SetLastError(error);
            return PE_ERROR_DECODE_FAILED;
        }
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }

    if (cancelFlag && *cancelFlag != 0)
    {
        return PE_ERROR_CANCELLED;
    }
    s->ClearLastError();
    return PE_OK;
}

int32_t PE_ExtractPeakEnvelope(
    PE_SessionHandle session,
    PE_MediaHandle media,
    double startSeconds,
    double durationSeconds,
    double peaksPerSecond,
    float* outBuffer,
    int32_t cap,
    int32_t* outCount)
{
    if (!session || !media || !outBuffer || !outCount || cap <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    MediaSource* m = s->Resolve(media);
    if (!m)
    {
        s->SetLastError("invalid media handle");
        return PE_ERROR_INVALID_HANDLE;
    }

    std::string error;
    try
    {
        if (!m->ExtractPeakEnvelope(startSeconds, durationSeconds, peaksPerSecond, outBuffer, cap, *outCount, error))
        {
            s->SetLastError(error);
            return PE_ERROR_DECODE_FAILED;
        }
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
    s->ClearLastError();
    return PE_OK;
}

int32_t PE_AttachSwapChain(PE_SessionHandle session, void* swapChainPanelUnknown, int32_t width, int32_t height)
{
    if (!session || !swapChainPanelUnknown || width <= 0 || height <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->AttachSwapChain(swapChainPanelUnknown, width, height);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_ResizeSwapChain(PE_SessionHandle session, int32_t width, int32_t height)
{
    if (!session || width <= 0 || height <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->ResizeSwapChain(width, height);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_DetachSwapChain(PE_SessionHandle session)
{
    if (!session)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->DetachSwapChain();
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_PresentFrameAt(PE_SessionHandle session, PE_MediaHandle media, double timelineSeconds)
{
    if (!session || !media)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->PresentFrameAt(media, timelineSeconds);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_RenderFrameToFile(PE_SessionHandle session, PE_MediaHandle media, double timelineSeconds, const char* utf8PngPath)
{
    if (!session || !media || !utf8PngPath)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->RenderFrameToFile(media, timelineSeconds, utf8PngPath);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_OpenTimeline(PE_SessionHandle session, const char* utf8SnapshotJson, PE_TimelineHandle* outTimeline)
{
    if (!session || !utf8SnapshotJson || !outTimeline)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->OpenTimeline(utf8SnapshotJson, outTimeline);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_UpdateTimeline(PE_TimelineHandle timeline, const char* utf8SnapshotJson)
{
    if (!utf8SnapshotJson)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->Update(utf8SnapshotJson, error))
        {
            return PE_ERROR_INVALID_ARGUMENT;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineRefreshParams(PE_TimelineHandle timeline, const char* utf8SnapshotJson)
{
    if (!utf8SnapshotJson)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->RefreshParams(utf8SnapshotJson, error))
        {
            return PE_ERROR_INVALID_ARGUMENT;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_CloseTimeline(PE_SessionHandle session, PE_TimelineHandle timeline)
{
    if (!session || !timeline)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    try
    {
        return s->CloseTimeline(timeline);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineSeek(PE_TimelineHandle timeline, int64_t frame, int32_t mode)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return t->Seek(frame, mode);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineAttachSwapChain(PE_TimelineHandle timeline, void* swapChainPanelUnknown, int32_t width, int32_t height)
{
    if (!swapChainPanelUnknown || width <= 0 || height <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        return t->AttachSwapChain(swapChainPanelUnknown, width, height, error);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineResizeSwapChain(PE_TimelineHandle timeline, int32_t width, int32_t height)
{
    if (width <= 0 || height <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        return t->ResizeSwapChain(width, height, error);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineDetachSwapChain(PE_TimelineHandle timeline)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        return t->DetachSwapChain(error);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineRenderFrameToFile(PE_TimelineHandle timeline, int64_t frame, const char* utf8PngPath)
{
    if (!utf8PngPath)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->RenderFrameToFile(frame, utf8PngPath, error))
        {
            return PE_ERROR_ENCODE_FAILED;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineComputeColorScopes(PE_TimelineHandle timeline, int64_t frame, PE_ColorScopesResult* outResult)
{
    if (!outResult)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->ComputeColorScopes(frame, *outResult, error))
        {
            return PE_ERROR_UNKNOWN;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineRenderAudioRange(PE_TimelineHandle timeline, int64_t startFrame, int32_t frameCount, float* outInterleavedStereo)
{
    if (!outInterleavedStereo || frameCount <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->RenderAudioRange(startFrame, frameCount, outInterleavedStereo, error))
        {
            return PE_ERROR_UNKNOWN;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineGetAudioLevels(PE_TimelineHandle timeline, float* outLeftPeak, float* outLeftRms, float* outRightPeak, float* outRightRms)
{
    if (!outLeftPeak || !outLeftRms || !outRightPeak || !outRightRms)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    t->GetAudioLevels(*outLeftPeak, *outLeftRms, *outRightPeak, *outRightRms);
    return PE_OK;
}

int32_t PE_TimelineSetPlayheadCallback(PE_TimelineHandle timeline, PE_PlayheadCallback callback, void* userCtx)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    t->SetPlayheadCallback(callback, userCtx);
    return PE_OK;
}

const char* PE_TimelineGetUnprocessableMediaRefsJson(PE_TimelineHandle timeline)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return "[]";
    }
    return t->UnprocessableMediaRefsJson();
}

// --- E4.5 playback / A/V clock (docs/audio-playback-v1.md §3, §4) ---------------------------

int32_t PE_TimelineSetRate(PE_TimelineHandle timeline, double rate)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return t->SetRate(rate); // PE_ERROR_INVALID_ARGUMENT for rate ∉ {0.0, 1.0} (doc §4)
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelinePlay(PE_TimelineHandle timeline)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return t->Play();
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelinePause(PE_TimelineHandle timeline)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return t->Pause();
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineGetClockFrame(PE_TimelineHandle timeline, int64_t* outFrame)
{
    if (!outFrame)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return t->GetClockFrame(outFrame);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineSetIsPlayingCallback(PE_TimelineHandle timeline, PE_IsPlayingCallback callback, void* userCtx)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    t->SetIsPlayingCallback(callback, userCtx);
    return PE_OK;
}

// E4.5 scrub slice (docs/audio-playback-v1.md §5, §9). Never fails on content grounds — a valid
// handle always returns PE_OK, even when the grain is silence (no audible clip at `frame`, or a
// transient decode failure); a persistently failing source surfaces through
// PE_TimelineGetUnprocessableMediaRefsJson instead, exactly like a failing video decode already does.
int32_t PE_TimelineScrubAudio(PE_TimelineHandle timeline, int64_t frame, int32_t direction)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        t->ScrubAudioAt(frame, direction);
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineStopScrubAudio(PE_TimelineHandle timeline)
{
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        t->StopScrubAudio();
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_TimelineRenderScrubGrain(PE_TimelineHandle timeline, int64_t frame, int32_t direction, float* outInterleavedStereo)
{
    if (!outInterleavedStereo)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        if (!t->RenderScrubGrain(frame, direction, outInterleavedStereo, error))
        {
            return PE_ERROR_INVALID_HANDLE;
        }
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_DebugTimelineUsingAudioClock(PE_TimelineHandle timeline, int32_t* outUsingAudioClock)
{
    if (!outUsingAudioClock)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    *outUsingAudioClock = t->DebugUsingAudioClock() ? 1 : 0;
    return PE_OK;
}

int32_t PE_DebugResolveFontFamily(const char* storedFontName, char* outFamilyNameUtf8, int32_t capacity)
{
    if (!storedFontName || !outFamilyNameUtf8 || capacity <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }

    std::string initError;
    if (!FontRegistry::Instance().EnsureInitialized(initError))
    {
        return PE_ERROR_UNKNOWN;
    }

    std::wstring family = FontRegistry::Instance().ResolveFamily(storedFontName);
    int len = WideCharToMultiByte(CP_UTF8, 0, family.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (len <= 0 || len > capacity)
    {
        return PE_ERROR_BUFFER_TOO_SMALL;
    }
    WideCharToMultiByte(CP_UTF8, 0, family.c_str(), -1, outFamilyNameUtf8, capacity, nullptr, nullptr);
    return PE_OK;
}

// --- Lottie bake / alpha video encode (Stage E / E4.7, docs/lottie-bake-v1.md §7, §8) -------

int32_t PE_EncodeAlphaVideoOpen(PE_SessionHandle session, const char* utf8OutputPath, int32_t width, int32_t height, PE_AlphaEncoderHandle* outEncoder)
{
    if (!session || !utf8OutputPath || !outEncoder)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    auto encoder = std::make_unique<AlphaVideoEncoder>(s);
    int32_t status;
    try
    {
        status = encoder->Open(utf8OutputPath, width, height);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
    if (status != PE_OK)
    {
        return status; // encoder (and any partial FFmpeg state) is destroyed with the unique_ptr
    }
    *outEncoder = reinterpret_cast<PE_AlphaEncoderHandle>(encoder.release());
    return PE_OK;
}

int32_t PE_EncodeAlphaVideoPushFrame(PE_AlphaEncoderHandle encoder, const uint8_t* bgraData, int32_t strideBytes, double presentationSeconds)
{
    if (!bgraData || strideBytes <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    AlphaVideoEncoder* e = AsEncoder(encoder);
    if (!e)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return e->PushFrame(bgraData, strideBytes, presentationSeconds);
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_EncodeAlphaVideoClose(PE_AlphaEncoderHandle encoder)
{
    AlphaVideoEncoder* e = AsEncoder(encoder);
    if (!e)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    int32_t status;
    try
    {
        status = e->Close();
    }
    catch (const std::exception&)
    {
        status = PE_ERROR_UNKNOWN;
    }
    delete e; // invalid handle from here regardless of status — see palmier_engine.h
    return status;
}

int32_t PE_EncodeAlphaVideoAbort(PE_AlphaEncoderHandle encoder)
{
    AlphaVideoEncoder* e = AsEncoder(encoder);
    if (!e)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    e->Abort();
    delete e;
    return PE_OK;
}

int32_t PE_ExportReadbackConvertForTest(
    PE_SessionHandle session,
    int32_t format,
    int32_t width,
    int32_t height,
    const uint16_t* framesRgba16,
    int32_t frameCount,
    uint8_t* outPlanes,
    int32_t outPlanesBytes)
{
    if (!session || !framesRgba16 || !outPlanes || frameCount <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    if (format != PE_EXPORT_PIXEL_FORMAT_NV12 && format != PE_EXPORT_PIXEL_FORMAT_YUV422P10LE)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    auto fmt = format == PE_EXPORT_PIXEL_FORMAT_NV12
        ? ExportReadback::Format::NV12
        : ExportReadback::Format::Yuv422p10le;

    int64_t perFrameBytes = ExportReadback::PackedFrameBytes(fmt, width, height);
    if (perFrameBytes <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    if (static_cast<int64_t>(outPlanesBytes) != perFrameBytes * frameCount)
    {
        return PE_ERROR_BUFFER_TOO_SMALL;
    }

    EngineSession* s = AsSession(session);
    try
    {
        std::lock_guard<std::recursive_mutex> lock(s->GraphicsMutex());
        std::string error;
        ID3D11Device* device = s->EnsureGraphicsDeviceShared(error);
        ID3D11DeviceContext* context = s->GraphicsContext();
        if (!device || !context)
        {
            s->SetLastError(error.empty() ? "no graphics device" : error);
            return PE_ERROR_UNKNOWN;
        }

        // Synthetic accumulator: a gamma-premultiplied RGBA16F texture we re-upload each frame.
        Microsoft::WRL::ComPtr<ID3D11Texture2D> accumTex;
        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(width);
        td.Height = static_cast<UINT>(height);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(device->CreateTexture2D(&td, nullptr, &accumTex)))
        {
            s->SetLastError("CreateTexture2D(export test accumulator) failed");
            return PE_ERROR_UNKNOWN;
        }
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> accumSrv;
        if (FAILED(device->CreateShaderResourceView(accumTex.Get(), nullptr, &accumSrv)))
        {
            s->SetLastError("CreateShaderResourceView(export test accumulator) failed");
            return PE_ERROR_UNKNOWN;
        }

        ExportReadback readback(device, context, fmt, width, height, 3);

        auto writeFrame = [&](const ExportReadback::Frame& f) {
            int64_t off = f.frameIndex * perFrameBytes;
            for (int32_t p = 0; p < f.planeCount; ++p)
            {
                const std::vector<uint8_t>& plane = f.planes[static_cast<size_t>(p)];
                std::memcpy(outPlanes + off, plane.data(), plane.size());
                off += static_cast<int64_t>(plane.size());
            }
        };

        UINT srcRowPitch = static_cast<UINT>(width) * 4u * sizeof(uint16_t);
        for (int32_t k = 0; k < frameCount; ++k)
        {
            const uint16_t* frameData = framesRgba16 + static_cast<size_t>(k) * width * height * 4;
            context->UpdateSubresource(accumTex.Get(), 0, nullptr, frameData, srcRowPitch, 0);

            bool hasFrame = false;
            ExportReadback::Frame frame;
            if (!readback.Submit(accumSrv.Get(), k, hasFrame, frame, error))
            {
                s->SetLastError(error);
                return PE_ERROR_UNKNOWN;
            }
            if (hasFrame)
            {
                writeFrame(frame);
            }
        }

        std::vector<ExportReadback::Frame> tail;
        if (!readback.Drain(tail, error))
        {
            s->SetLastError(error);
            return PE_ERROR_UNKNOWN;
        }
        for (const ExportReadback::Frame& f : tail)
        {
            writeFrame(f);
        }
        return PE_OK;
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_ExportAudioRenderForTest(
    PE_TimelineHandle timeline,
    int64_t startFrame,
    int64_t frameCount,
    int32_t chunkFrameCount,
    int32_t outputSampleFormat,
    int32_t outputSampleRate,
    int32_t outputChannels,
    int32_t outputFrameSize,
    uint8_t* outPcm,
    int32_t outPcmBytes,
    int32_t* outSampleCount)
{
    if (!outPcm || outPcmBytes <= 0 || !outSampleCount || frameCount <= 0 || chunkFrameCount <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }

    ExportAudio::OutputFormat format;
    switch (outputSampleFormat)
    {
        case PE_EXPORT_AUDIO_FMT_FLT: format.sampleFormat = AV_SAMPLE_FMT_FLT; break;
        case PE_EXPORT_AUDIO_FMT_FLTP: format.sampleFormat = AV_SAMPLE_FMT_FLTP; break;
        case PE_EXPORT_AUDIO_FMT_S16: format.sampleFormat = AV_SAMPLE_FMT_S16; break;
        default: return PE_ERROR_INVALID_ARGUMENT;
    }
    format.sampleRate = outputSampleRate;
    format.channels = outputChannels;
    format.frameSize = outputFrameSize;
    if (format.sampleRate <= 0 || format.channels <= 0 || format.frameSize <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }

    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        std::string error;
        int32_t sampleCount = 0;
        if (!t->RenderAudioForExportTest(
                startFrame, frameCount, chunkFrameCount, format, outPcm, outPcmBytes, sampleCount, error))
        {
            return PE_ERROR_UNKNOWN;
        }
        *outSampleCount = sampleCount;
        return PE_OK;
    }
    catch (const std::exception&)
    {
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_ExportEncodeForTest(
    PE_SessionHandle session,
    int32_t codec,
    const char* utf8Container,
    int32_t width,
    int32_t height,
    int32_t fps,
    int32_t frameCount,
    int32_t withAudio,
    double sineHz,
    int32_t forceSoftware,
    const char* utf8OutputPath,
    PE_ExportResult* outResult)
{
    if (!session || !utf8Container || !utf8OutputPath || frameCount <= 0 || fps <= 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }

    ExportEncoder::Codec ec;
    switch (codec)
    {
        case PE_EXPORT_CODEC_H264: ec = ExportEncoder::Codec::H264; break;
        case PE_EXPORT_CODEC_H265: ec = ExportEncoder::Codec::H265; break;
        case PE_EXPORT_CODEC_PRORES: ec = ExportEncoder::Codec::ProRes; break;
        default: return PE_ERROR_INVALID_ARGUMENT;
    }

    EngineSession* s = AsSession(session);
    const bool proRes = ec == ExportEncoder::Codec::ProRes;
    const int32_t chromaW = width / 2;
    const int32_t sampleRate = 48000;
    const int32_t channels = 2;

    try
    {
        ExportEncoder encoder(s);
        ExportEncoder::Config cfg;
        cfg.codec = ec;
        cfg.container = utf8Container;
        cfg.outputPath = utf8OutputPath;
        cfg.width = width;
        cfg.height = height;
        cfg.fps = AVRational{fps, 1};
        cfg.hasAudio = withAudio != 0;
        cfg.audioSampleRate = sampleRate;
        cfg.audioChannels = channels;
        cfg.forceSoftware = forceSoftware != 0;

        int32_t st = encoder.Open(cfg);
        if (st != PE_OK)
        {
            return st;
        }

        std::vector<uint8_t> plane0, plane1, plane2;
        const uint8_t* planeData[3] = {};
        int32_t rowBytes[3] = {};
        int32_t rowCount[3] = {};
        int32_t planeCount;
        if (!proRes) // NV12
        {
            plane0.resize(static_cast<size_t>(width) * height);
            plane1.resize(static_cast<size_t>(chromaW) * (height / 2) * 2);
            rowBytes[0] = width;       rowCount[0] = height;
            rowBytes[1] = chromaW * 2; rowCount[1] = height / 2;
            planeCount = 2;
        }
        else // yuv422p10le
        {
            plane0.resize(static_cast<size_t>(width) * height * 2);
            plane1.resize(static_cast<size_t>(chromaW) * height * 2);
            plane2.resize(static_cast<size_t>(chromaW) * height * 2);
            rowBytes[0] = width * 2;   rowCount[0] = height;
            rowBytes[1] = chromaW * 2; rowCount[1] = height;
            rowBytes[2] = chromaW * 2; rowCount[2] = height;
            planeCount = 3;
        }

        int64_t totalAudioSamples = cfg.hasAudio
            ? std::llround(static_cast<double>(frameCount) * sampleRate / fps)
            : 0;
        int64_t audioCursor = 0;
        std::vector<float> audioChunk;

        for (int32_t k = 0; k < frameCount; ++k)
        {
            if (!proRes)
            {
                for (int32_t y = 0; y < height; ++y)
                {
                    uint8_t* row = plane0.data() + static_cast<size_t>(y) * width;
                    for (int32_t x = 0; x < width; ++x)
                    {
                        row[x] = static_cast<uint8_t>(16 + ((x + y + k * 4) % 220));
                    }
                }
                std::memset(plane1.data(), 128, plane1.size()); // neutral Cb/Cr
                planeData[0] = plane0.data();
                planeData[1] = plane1.data();
            }
            else
            {
                for (int32_t y = 0; y < height; ++y)
                {
                    uint16_t* row = reinterpret_cast<uint16_t*>(plane0.data()) + static_cast<size_t>(y) * width;
                    for (int32_t x = 0; x < width; ++x)
                    {
                        row[x] = static_cast<uint16_t>(64 + ((x + y + k * 4) % 800));
                    }
                }
                uint16_t* u = reinterpret_cast<uint16_t*>(plane1.data());
                uint16_t* v = reinterpret_cast<uint16_t*>(plane2.data());
                size_t chromaCount = static_cast<size_t>(chromaW) * height;
                for (size_t i = 0; i < chromaCount; ++i) { u[i] = 512; v[i] = 512; }
                planeData[0] = plane0.data();
                planeData[1] = plane1.data();
                planeData[2] = plane2.data();
            }

            st = encoder.PushVideoFrame(planeData, rowBytes, rowCount, planeCount, k);
            if (st != PE_OK)
            {
                return st;
            }

            if (cfg.hasAudio)
            {
                int64_t end = std::llround(static_cast<double>(k + 1) * totalAudioSamples / frameCount);
                int32_t n = static_cast<int32_t>(end - audioCursor);
                if (n > 0)
                {
                    audioChunk.resize(static_cast<size_t>(n) * channels);
                    for (int32_t i = 0; i < n; ++i)
                    {
                        double t = static_cast<double>(audioCursor + i) / sampleRate;
                        float sample = static_cast<float>(0.25 * std::sin(2.0 * 3.14159265358979323846 * sineHz * t));
                        audioChunk[static_cast<size_t>(i) * 2] = sample;
                        audioChunk[static_cast<size_t>(i) * 2 + 1] = sample;
                    }
                    st = encoder.PushAudio(audioChunk.data(), n);
                    if (st != PE_OK)
                    {
                        return st;
                    }
                    audioCursor = end;
                }
            }
        }

        st = encoder.Close();
        if (st != PE_OK)
        {
            return st;
        }

        if (outResult)
        {
            outResult->framesEncoded = frameCount;
            outResult->usedHardwareEncoder = encoder.UsedHardwareEncoder() ? 1 : 0;
            std::memset(outResult->encoderName, 0, sizeof(outResult->encoderName));
            const std::string& name = encoder.EncoderName();
            size_t cap = sizeof(outResult->encoderName) - 1;
            size_t copyLen = name.size() < cap ? name.size() : cap;
            std::memcpy(outResult->encoderName, name.data(), copyLen);
        }
        return PE_OK;
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

// --- Export (Stage F / E5) — the shipping export ABI (export-v1.md §4-§9) ----------------------

int32_t PE_ExportStart(
    PE_SessionHandle session,
    PE_TimelineHandle timeline,
    const char* utf8OptionsJson,
    PE_ExportCallbacks callbacks,
    PE_ExportResult* outResult)
{
    if (!session || !timeline || !utf8OptionsJson)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    EngineSession* s = AsSession(session);
    TimelineSession* t = ResolveTimeline(timeline);
    if (!t)
    {
        s->SetLastError("PE_ExportStart: invalid timeline handle");
        return PE_ERROR_INVALID_HANDLE;
    }
    try
    {
        return s->ExportStart(t, utf8OptionsJson, callbacks, outResult);
    }
    catch (const std::exception& ex)
    {
        s->SetLastError(ex.what());
        return PE_ERROR_UNKNOWN;
    }
}

int32_t PE_ExportCancel(PE_SessionHandle session)
{
    if (!session)
    {
        return PE_ERROR_INVALID_ARGUMENT;
    }
    return AsSession(session)->ExportCancel();
}
