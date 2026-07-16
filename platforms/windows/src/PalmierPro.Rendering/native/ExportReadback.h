#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <string>
#include <vector>

// E5 export GPU readback (export-v1.md §5) — the COLOR-CRITICAL stage that turns the compositor's
// GAMMA-encoded, PREMULTIPLIED R16G16B16A16_FLOAT accumulator into planar Y'CbCr the FFmpeg encoder
// muxes. Owns the RGB->NV12 / ->yuv422p10le compute pass (shaders/ExportNV12.hlsl,
// shaders/ExportYuv422p10.hlsl) plus the staging-texture ring that reads the result back without
// stalling the D3D11 pipeline.
//
// A sibling to GpuCompositor::ReadbackToBgra8 / Scopes (docs/color-scopes-v1.md): it never
// composites or decodes anything itself — it is handed the already-composited accumulator SRV and
// only converts + reads it back. The future ExportSession (export-v1.md §14.2) owns one of these per
// export and drives Submit()/Drain() from its per-frame loop; this class is deliberately independent
// of TimelineSnapshot/ClipFrameProvider/FFmpeg so it can be exercised on its own (see the
// PE_ExportReadbackConvertForTest hook).
//
// Color contract (export-v1.md §5, verified against FrameRenderer.swift's over-black export
// compose): BT.709 primaries, LIMITED ("tv") range, left-sited chroma. The accumulator's
// premultiplied RGB already equals the straight color composited over black, so the pass reads RGB
// and discards A — no divide-by-alpha, no linearization (stays in gamma space end to end). See
// shaders/ExportColor.hlsl for the matrix + un-premultiply rationale.
//
// Not thread-safe on a single instance — the export loop is single-threaded, and every method here
// submits to the shared ID3D11DeviceContext, which the caller already serializes (EngineSession's
// GraphicsMutex).
class ExportReadback
{
public:
    // NV12 (8-bit 4:2:0, H.264/H.265) and yuv422p10le (10-bit 4:2:2, ProRes) — export-v1.md §5.2/§5.3.
    // P010 (§5.3's deferred HDR third variant) would slot in here as a new case, not a redesign.
    enum class Format
    {
        NV12,
        Yuv422p10le,
    };

    // One converted frame's planes, read back from a ring slot. Rows are TIGHTLY packed
    // (rowBytes[p] = plane width * bytes-per-texel — the staging Map's own RowPitch padding is
    // already removed). NV12: plane 0 = Y (R8, w x h), plane 1 = Cb/Cr interleaved (R8G8, w/2 x h/2).
    // yuv422p10le: plane 0 = Y, 1 = U, 2 = V, each 16-bit LE words with the 10-bit code in the low
    // bits (Y is w x h; U/V are w/2 x h).
    struct Frame
    {
        int64_t frameIndex = -1;
        int32_t planeCount = 0;
        std::array<std::vector<uint8_t>, 3> planes;
        std::array<int32_t, 3> rowBytes{};
        std::array<int32_t, 3> rowCount{};
    };

    // `device`/`context` are borrowed (owned by EngineSession, which outlives this). width/height
    // must be positive and EVEN (chroma subsampling — the C# caller already rounds down to even,
    // §4.2). ringDepth must be >= 3 so the Map-frame-N-2 pipelining never aliases a slot still being
    // written (§5.4); values below 3 are clamped up to 3.
    ExportReadback(ID3D11Device* device, ID3D11DeviceContext* context, Format format,
        int32_t width, int32_t height, int32_t ringDepth = 3);
    ~ExportReadback();

    ExportReadback(const ExportReadback&) = delete;
    ExportReadback& operator=(const ExportReadback&) = delete;

    // Converts `accumSrv` (the gamma-premultiplied R16G16B16A16_FLOAT accumulator, width x height)
    // for output frame `frameIndex` and issues its CopyResource into ring slot frameIndex % ringDepth
    // — NOT waited on. Then, once at least kMapLag (=2) frames have been submitted, Maps the slot
    // whose copy was issued kMapLag iterations ago (frame N-2), fills `outFrame`, and sets
    // outHasFrame=true; for the first kMapLag submits outHasFrame=false (nothing is old enough to read
    // yet — see Drain for the tail). frameIndex MUST increase by exactly 1 per call starting at 0.
    bool Submit(ID3D11ShaderResourceView* accumSrv, int64_t frameIndex,
        bool& outHasFrame, Frame& outFrame, std::string& outError);

    // Drains the final (up to kMapLag) frames the pipelined Submit never returned — the tail with no
    // N+1/N+2 to hide behind. A real, brief Map stall is unavoidable and acceptable only here
    // (§5.4). Call once after the last Submit; appends the pending frames to `outFrames` in
    // submission order. Skipping this silently drops the export's last two frames.
    bool Drain(std::vector<Frame>& outFrames, std::string& outError);

    // Bytes one frame occupies when its planes are written tightly packed, plane 0..N-1 in order
    // (the PE_ExportReadbackConvertForTest wire layout). 0 for invalid args.
    static int64_t PackedFrameBytes(Format format, int32_t width, int32_t height);

private:
    struct PlaneDesc
    {
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        int32_t width = 0;
        int32_t height = 0;
        int32_t bytesPerTexel = 0;
    };

    static constexpr int64_t kMapLag = 2; // Map frame N-2 (export-v1.md §5.4)

    ID3D11Device* device_;         // not owned
    ID3D11DeviceContext* context_; // not owned

    Format format_;
    int32_t width_;
    int32_t height_;
    int32_t chromaWidth_;
    int32_t chromaHeight_;
    int32_t ringDepth_;
    int32_t planeCount_ = 0;
    std::array<PlaneDesc, 3> planes_{};

    std::string shadersDir_;
    Microsoft::WRL::ComPtr<ID3D11ComputeShader> convertCs_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> constantBuffer_; // 16 bytes

    // The compute pass's UAV convert targets (one per plane) — reused every frame; each CopyResource
    // snapshots them into a ring slot before the next dispatch overwrites them.
    std::array<Microsoft::WRL::ComPtr<ID3D11Texture2D>, 3> convertTex_{};
    std::array<Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView>, 3> convertUav_{};

    // ring[slot][plane] — STAGING/CPU_ACCESS_READ copies, cycled by frame index (§5.4).
    std::vector<std::array<Microsoft::WRL::ComPtr<ID3D11Texture2D>, 3>> ring_;

    int64_t submitCount_ = 0;
    bool resourcesReady_ = false;

    bool ResolveShadersDir(std::string& outError);
    bool EnsureResources(std::string& outError);
    void UpdateConstantBuffer();
    bool MapSlot(int32_t slot, int64_t frameIndex, Frame& outFrame, std::string& outError);
};
