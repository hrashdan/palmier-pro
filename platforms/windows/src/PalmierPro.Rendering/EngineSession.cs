using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PalmierPro.Rendering;

// Owns the native PE_SessionHandle. Every MediaSource opened from a session is owned
// natively by that session — disposing the session invalidates them all, so dispose
// media sources first if you still need their state.
public sealed class EngineSession : IDisposable
{
    private nint _handle;

    public EngineSession()
    {
        int status = NativeMethods.PE_CreateSession(out _handle);
        if (status != 0 || _handle == 0)
        {
            throw new EngineException(status, "PE_CreateSession failed.");
        }
    }

    internal bool IsDisposed { get; private set; }

    internal nint Handle => IsDisposed ? throw new ObjectDisposedException(nameof(EngineSession)) : _handle;

    public MediaSource OpenMedia(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int status = NativeMethods.PE_OpenMedia(Handle, path, out nint mediaHandle);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
        try
        {
            return new MediaSource(this, mediaHandle);
        }
        catch
        {
            // MediaSource's constructor (PE_GetMediaInfo) failed, so the caller never got a
            // handle to Dispose — close the native media ourselves to avoid leaking it.
            NativeMethods.PE_CloseMedia(Handle, mediaHandle);
            throw;
        }
    }

    internal string GetLastErrorMessage()
    {
        nint ptr = NativeMethods.PE_GetLastErrorMessage(_handle);
        return ptr == 0 ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    // swapChainPanel must be a WinRT-projected SwapChainPanel (e.g.
    // Microsoft.UI.Xaml.Controls.SwapChainPanel) — see SwapChainPanelInterop. UI-thread
    // call; see palmier_engine.h for the full threading contract.
    public void AttachSwapChain(object swapChainPanel, int width, int height)
    {
        nint panelUnknown = SwapChainPanelInterop.GetNativeUnknown(swapChainPanel);
        int status = NativeMethods.PE_AttachSwapChain(Handle, panelUnknown, width, height);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
    }

    // UI-thread call; quiesces any in-flight Present before resizing (see palmier_engine.h).
    public void ResizeSwapChain(int width, int height)
    {
        int status = NativeMethods.PE_ResizeSwapChain(Handle, width, height);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
    }

    // UI-thread call.
    public void DetachSwapChain()
    {
        int status = NativeMethods.PE_DetachSwapChain(Handle);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
    }

    // May be called off the UI thread (a present loop). Requires a prior AttachSwapChain.
    public void PresentFrameAt(MediaSource media, double timelineSeconds)
    {
        ArgumentNullException.ThrowIfNull(media);
        int status = NativeMethods.PE_PresentFrameAt(Handle, media.Handle, timelineSeconds);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
    }

    // Metadata-only probe (docs/lottie-bake-v1.md §8/§11) — no rasterization, no encode, no disk
    // cache. `lottiePath` must already be a plain-JSON path — a .lottie zip is unzipped C#-side
    // first (§12; see DotLottieExtractor).
    public LottieInfo ProbeLottieMetadata(string lottiePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(lottiePath);
        int status = NativeMethods.PE_ProbeLottieMetadata(Handle, lottiePath, out PE_LottieInfo info);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
        return new LottieInfo(info.DurationSeconds, info.Width, info.Height, info.FrameRate);
    }

    /// Test/diagnostics hook for the E5 export GPU readback (export-v1.md §5). Runs
    /// <paramref name="framesRgba16"/> — one or more width×height GAMMA-premultiplied RGBA16F
    /// accumulator frames (4 <see cref="Half"/> per pixel, row-major, no padding) — through the
    /// native ExportReadback ring (RGB→NV12/yuv422p10le compute pass + Map-frame-N-2 staging ring)
    /// and returns every frame's converted planes, in submission order, tightly packed (see
    /// <see cref="ExportReadbackPackedFrameBytes"/> for the per-frame layout). No encoder, muxer, or
    /// timeline involved — the pure COLOR-CRITICAL conversion, for analytic verification.
    public unsafe byte[] ExportReadbackConvertForTest(
        ExportPixelFormat format, int width, int height, ReadOnlySpan<Half> framesRgba16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentException("width and height must be even (chroma subsampling).");
        }
        int perFramePixelComponents = checked(width * height * 4);
        if (framesRgba16.Length == 0 || framesRgba16.Length % perFramePixelComponents != 0)
        {
            throw new ArgumentException(
                $"framesRgba16 length must be a positive multiple of width*height*4 ({perFramePixelComponents}).",
                nameof(framesRgba16));
        }
        int frameCount = framesRgba16.Length / perFramePixelComponents;

        long perFrameBytes = ExportReadbackPackedFrameBytes(format, width, height);
        var output = new byte[checked(perFrameBytes * frameCount)];

        int status;
        ReadOnlySpan<ushort> asUshort = MemoryMarshal.Cast<Half, ushort>(framesRgba16);
        fixed (ushort* framesPtr = asUshort)
        fixed (byte* outPtr = output)
        {
            status = NativeMethods.PE_ExportReadbackConvertForTest(
                Handle, (int)format, width, height, framesPtr, frameCount, outPtr, output.Length);
        }
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
        return output;
    }

    /// Bytes one converted frame occupies in <see cref="ExportReadbackConvertForTest"/>'s output
    /// (planes tightly packed, plane 0..N-1 in order). NV12: Y(w×h u8) + UV(w/2×h/2×2 u8).
    /// yuv422p10le: Y(w×h) + U(w/2×h) + V(w/2×h), all 16-bit LE samples with the 10-bit code in the
    /// low bits. Mirrors native ExportReadback::PackedFrameBytes.
    public static long ExportReadbackPackedFrameBytes(ExportPixelFormat format, int width, int height)
    {
        long w = width;
        long h = height;
        return format == ExportPixelFormat.Nv12
            ? w * h + (w / 2) * (h / 2) * 2
            : w * h * 2 + (w / 2) * h * 2 * 2;
    }

    /// Test/diagnostics hook for the E5 export encoder/muxer (export-v1.md §6/§7/§9). Encodes
    /// <paramref name="frameCount"/> synthetic moving-gradient frames (plus a <paramref name="sineHz"/>
    /// stereo sine at 48 kHz when <paramref name="withAudio"/>) into <paramref name="outputPath"/>
    /// using <paramref name="codec"/> in <paramref name="container"/> — exercising codec probe,
    /// mp4/mov muxing (+faststart), and AAC audio interleaving. <paramref name="forceSoftware"/>
    /// (default) skips the h264/h265 hardware probe so the resulting codec_name is deterministic.
    /// Returns the encoder actually used and the frame count for ffprobe-based verification.
    public unsafe ExportEncodeReport ExportEncodeForTest(
        ExportCodec codec, string container, int width, int height, int fps, int frameCount,
        bool withAudio, double sineHz, string outputPath, bool forceSoftware = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(container);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);

        PE_ExportResult result;
        int status = NativeMethods.PE_ExportEncodeForTest(
            Handle, (int)codec, container, width, height, fps, frameCount,
            withAudio ? 1 : 0, sineHz, forceSoftware ? 1 : 0, outputPath, &result);
        if (status != 0)
        {
            throw new EngineException(status, GetLastErrorMessage());
        }
        // result is a local (a fixed variable), so its inline buffer needs no `fixed` pin.
        byte* namePtr = result.EncoderName;
        string encoderName = Marshal.PtrToStringUTF8((nint)namePtr) ?? string.Empty;
        return new ExportEncodeReport(result.FramesEncoded, result.UsedHardwareEncoder != 0, encoderName);
    }

    /// One-call bake orchestration (docs/lottie-bake-v1.md §8) — synchronous; callers invoke from a
    /// background Task (mirrors <see cref="ILottieBakeService"/>'s own async surface, which is the
    /// only real caller). `lottiePath` must already be a plain-JSON path (§12). `onProgress` fires
    /// once per rasterized animation frame (not for the hold-tail sample); cancelling `ct` polls the
    /// same way <see cref="MediaSource.ExtractThumbnailsAsync"/>'s cancellation does.
    public unsafe void BakeLottieVideo(
        string lottiePath,
        int targetWidth,
        int targetHeight,
        double holdTailSeconds,
        string outputPath,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(lottiePath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        int[] cancelArray = new int[1];
        GCHandle cancelPin = GCHandle.Alloc(cancelArray, GCHandleType.Pinned);
        GCHandle progressHandle = onProgress is null ? default : GCHandle.Alloc(onProgress);
        using CancellationTokenRegistration registration = ct.Register(() => Volatile.Write(ref cancelArray[0], 1));
        try
        {
            int status;
            int* cancelPtr = (int*)cancelPin.AddrOfPinnedObject();
            status = NativeMethods.PE_BakeLottieVideo(
                Handle,
                lottiePath,
                targetWidth,
                targetHeight,
                holdTailSeconds,
                outputPath,
                onProgress is null ? null : &ProgressTrampoline,
                onProgress is null ? 0 : GCHandle.ToIntPtr(progressHandle),
                cancelPtr);

            if (status == (int)PE_Status.ErrorCancelled || (status != 0 && ct.IsCancellationRequested))
            {
                throw new OperationCanceledException(ct);
            }
            if (status != 0)
            {
                throw new EngineException(status, GetLastErrorMessage());
            }
        }
        finally
        {
            cancelPin.Free();
            if (progressHandle.IsAllocated)
            {
                progressHandle.Free();
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProgressTrampoline(nint userCtx, int framesDone, int framesTotal)
    {
        if (userCtx == 0)
        {
            return;
        }
        GCHandle handle = GCHandle.FromIntPtr(userCtx);
        if (handle.Target is Action<int, int> callback)
        {
            callback(framesDone, framesTotal);
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        NativeMethods.PE_DestroySession(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~EngineSession()
    {
        if (!IsDisposed && _handle != 0)
        {
            NativeMethods.PE_DestroySession(_handle);
        }
    }
}

/// Native ExportReadback pixel format (export-v1.md §5) — mirrors PE_ExportPixelFormat. NV12 is the
/// 8-bit 4:2:0 H.264/H.265 target; Yuv422p10le the 10-bit 4:2:2 ProRes target.
public enum ExportPixelFormat
{
    Nv12 = 0,
    Yuv422p10le = 1,
}

/// Native export codec (export-v1.md §6) — mirrors PE_ExportCodec. H264/H265 mux to mp4,
/// ProRes (422, 10-bit) to mov.
public enum ExportCodec
{
    H264 = 0,
    H265 = 1,
    ProRes = 2,
}

/// Diagnostics returned by <see cref="EngineSession.ExportEncodeForTest"/> (native PE_ExportResult).
/// <paramref name="EncoderName"/> is the ffmpeg encoder used ("libx264"/"h264_nvenc"/"prores_ks"/...).
public readonly record struct ExportEncodeReport(long FramesEncoded, bool UsedHardwareEncoder, string EncoderName);
