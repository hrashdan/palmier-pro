// Shared BT.709 limited-range RGB->Y'CbCr math for the export conversion passes
// (ExportNV12.hlsl / ExportYuv422p10.hlsl), resolved via #include by GpuCompositor's
// ID3DInclude handler like Common.hlsl. export-v1.md §5.1/§5.3.
//
// Over-black un-premultiply: the compositor accumulator (R16G16B16A16_FLOAT) is GAMMA-ENCODED,
// PREMULTIPLIED, and cleared to OPAQUE black before compositing (GpuCompositor::
// ComposeToAccumulator) — exactly like the Mac composes export frames over CIImage(color:.black)
// (FrameRenderer.swift). Compositing a premultiplied pixel over black is `premul.rgb + (1-a)*0`,
// so the premultiplied RGB already IS the straight over-black color: no divide-by-alpha, and the
// accumulator is opaque (a=1) everywhere regardless. This is export-v1.md §5's "un-premultiply
// (over black)" — the whole reason the pass reads only RGB and discards A.

// Rec.709 luma coefficients — the SAME constants used across this codebase's other BT.709 math
// (ScopesHistogram.hlsl:61, Clarity.hlsl, GradeCurves.hlsl, ...). Do NOT introduce a second,
// differently-rounded set for this pass (export-v1.md §5.1).
static const float kKr = 0.2126;
static const float kKg = 0.7152;
static const float kKb = 0.0722;

// Straight, gamma-encoded RGB over black — see header. Reads a premultiplied accumulator texel.
float3 ExportOverBlackGammaRgb(float4 premultiplied)
{
    return premultiplied.rgb;
}

// Y_lin = Kr*R' + Kg*G' + Kb*B'  (gamma R'G'B' in [0,1]; the "linear combination" the range/offset
// scaling below is applied to — NOT a linearized signal; this pass stays in gamma space end to end).
float ExportLuma(float3 rgb)
{
    return kKr * rgb.r + kKg * rgb.g + kKb * rgb.b;
}

// 8-bit limited-range luma, normalized for an R8_UNORM target: 16/255 + (219/255)*Y_lin
// -> code round(16 + 219*Y_lin) in [16,235] (export-v1.md §5.1). UNORM store saturates <0 / >1.
float ExportY8(float3 rgb)
{
    return 16.0 / 255.0 + (219.0 / 255.0) * ExportLuma(rgb);
}

// 8-bit limited-range chroma, normalized for an R8G8_UNORM target (.x=Cb', .y=Cr').
// 128/255 + (224/255)*0.5*(B'-Y_lin)/(1-Kb) and the Cr' analog (export-v1.md §5.1).
float2 ExportCbCr8(float3 rgb)
{
    float yl = ExportLuma(rgb);
    float cb = 128.0 / 255.0 + (224.0 / 255.0) * 0.5 * (rgb.b - yl) / (1.0 - kKb);
    float cr = 128.0 / 255.0 + (224.0 / 255.0) * 0.5 * (rgb.r - yl) / (1.0 - kKr);
    return float2(cb, cr);
}

// 10-bit limited-range luma code (64 + 876*Y_lin, black=64 white=940) normalized into R16_UNORM's
// [0,1] as code/65535 — so the raw stored uint16 IS the 10-bit code in the low 10 bits, which is
// exactly how yuv422p10le packs a sample (10 bits in the low bits of a 16-bit LE word). §5.3.
float ExportY10Norm(float3 rgb)
{
    return (64.0 + 876.0 * ExportLuma(rgb)) / 65535.0;
}

// 10-bit limited-range chroma codes (neutral 512, range [64,960]) normalized into R16_UNORM's
// [0,1] (.x=Cb' code/65535, .y=Cr' code/65535). §5.3 — same derivation as ExportCbCr8, scaled x4.
float2 ExportCbCr10Norm(float3 rgb)
{
    float yl = ExportLuma(rgb);
    float cb = 512.0 + 896.0 * 0.5 * (rgb.b - yl) / (1.0 - kKb);
    float cr = 512.0 + 896.0 * 0.5 * (rgb.r - yl) / (1.0 - kKr);
    return float2(cb, cr) / 65535.0;
}
