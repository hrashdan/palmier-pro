#include "ExportColor.hlsl"

// GAMMA-encoded, premultiplied fp16 accumulator -> NV12 (BT.709, LIMITED range, left-sited 4:2:0).
// export-v1.md §5.1/§5.2. No linearization: the accumulator is already gamma-encoded and this pass
// stays in that space (matching CIContext(workingColorSpace: NSNull()) on the Mac).
//
// One thread per CHROMA cell (cx,cy). Its 2x2 luma block covers (2cx,2cy),(2cx+1,2cy),(2cx,2cy+1),
// (2cx+1,2cy+1): each gets its OWN Y' (luma is full-res). The single (Cb',Cr') sample is LEFT-SITED
// — average ONLY the left column, (2cx,2cy) and (2cx,2cy+1), then run the matrix ONCE on that
// averaged RGB. Because RGB->Y'CbCr is linear, averaging-then-matrix equals matrix-then-averaging
// and is half the work (§5.2). "Left" siting is the MPEG-2/H.264/libswscale default, so an unlabeled
// NV12 stream is decoded correctly with no chroma-siting side channel.
//
// width/height are guaranteed even (ExportResolution rounds each dimension down to even, §4.2), so
// ChromaWidth=width/2, ChromaHeight=height/2 and every cell is a complete 2x2 block.
Texture2D<float4> AccumTex : register(t0);
RWTexture2D<float>  YPlane  : register(u0); // R8_UNORM,  width x height
RWTexture2D<float2> UVPlane : register(u1); // R8G8_UNORM, (width/2) x (height/2), .x=Cb .y=Cr

cbuffer ExportParams : register(b0)
{
    uint Width;
    uint Height;
    uint ChromaWidth;
    uint ChromaHeight;
};

[numthreads(8, 8, 1)]
void ExportNV12CS(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= ChromaWidth || id.y >= ChromaHeight)
    {
        return;
    }

    uint x0 = id.x * 2u;
    uint y0 = id.y * 2u;

    float3 tl = ExportOverBlackGammaRgb(AccumTex[int2(x0,      y0)]);
    float3 tr = ExportOverBlackGammaRgb(AccumTex[int2(x0 + 1u, y0)]);
    float3 bl = ExportOverBlackGammaRgb(AccumTex[int2(x0,      y0 + 1u)]);
    float3 br = ExportOverBlackGammaRgb(AccumTex[int2(x0 + 1u, y0 + 1u)]);

    YPlane[int2(x0,      y0)]      = ExportY8(tl);
    YPlane[int2(x0 + 1u, y0)]      = ExportY8(tr);
    YPlane[int2(x0,      y0 + 1u)] = ExportY8(bl);
    YPlane[int2(x0 + 1u, y0 + 1u)] = ExportY8(br);

    float3 leftColumnAvg = 0.5 * (tl + bl);
    UVPlane[int2(id.x, id.y)] = ExportCbCr8(leftColumnAvg);
}
