#include "ExportColor.hlsl"

// GAMMA-encoded, premultiplied fp16 accumulator -> yuv422p10le (BT.709, LIMITED range, left-sited
// 4:2:2). export-v1.md §5.3 — the ProRes target. Structurally different from NV12: THREE separate
// 10-bit planes (Y full-res; U and V half-WIDTH, FULL height — 4:2:2 has no vertical subsampling),
// each a 16-bit word carrying the 10-bit code in its low bits (R16_UNORM, code/65535 — see
// ExportColor.hlsl's ExportY10Norm). The matrix math and the LEFT-sited chroma siting are shared
// with the NV12 pass (via ExportColor.hlsl); only the output stage and the missing vertical
// subsampling differ.
//
// One thread per CHROMA cell (cx,y): x0=2cx, full-resolution row y. Its 2x1 pair (2cx,y),(2cx+1,y)
// each gets its own Y'; the single (U,V) is LEFT-SITED — the chroma of the LEFT sample (2cx,y)
// ONLY, matrix run once — matching the AVCHROMA_LOC_LEFT tag ExportEncoder writes and mirroring
// NV12's left-column discipline. (A horizontal box-average of the pair would be horizontally
// CENTER-sited: its effective sample lands a half-luma-pixel to the RIGHT of where a LEFT-tagged
// decoder reads it — a subtle chroma shift / fringing on sharp vertical chroma edges.) width is
// guaranteed even (§4.2) so ChromaWidth=width/2 and every cell is a complete 2x1 pair;
// ChromaHeight=height (no vertical subsampling).
Texture2D<float4> AccumTex : register(t0);
RWTexture2D<float> YPlane : register(u0); // R16_UNORM, width x height
RWTexture2D<float> UPlane : register(u1); // R16_UNORM, (width/2) x height
RWTexture2D<float> VPlane : register(u2); // R16_UNORM, (width/2) x height

cbuffer ExportParams : register(b0)
{
    uint Width;
    uint Height;
    uint ChromaWidth;
    uint ChromaHeight;
};

[numthreads(8, 8, 1)]
void ExportYuv422p10CS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ChromaWidth || id.y >= ChromaHeight)
    {
        return;
    }

    uint x0 = id.x * 2u;
    uint y = id.y;

    float3 left  = ExportOverBlackGammaRgb(AccumTex[int2(x0,      y)]);
    float3 right = ExportOverBlackGammaRgb(AccumTex[int2(x0 + 1u, y)]);

    YPlane[int2(x0,      y)] = ExportY10Norm(left);
    YPlane[int2(x0 + 1u, y)] = ExportY10Norm(right);

    // LEFT-sited chroma: the left sample of the pair only (mirrors ExportNV12's left-column average;
    // NOT a horizontal box-average, which would be center-sited — see header). §5.3.
    float2 cbcr = ExportCbCr10Norm(left);
    UPlane[int2(id.x, y)] = cbcr.x;
    VPlane[int2(id.x, y)] = cbcr.y;
}
