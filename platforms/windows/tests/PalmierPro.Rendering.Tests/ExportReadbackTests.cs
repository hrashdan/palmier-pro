using Shouldly;
using Xunit;

namespace PalmierPro.Rendering.Tests;

// E5 export GPU readback (export-v1.md §5) — the COLOR-CRITICAL RGB->NV12 / ->yuv422p10le compute
// pass (shaders/ExportNV12.hlsl, shaders/ExportYuv422p10.hlsl) + Map-frame-N-2 staging ring
// (native ExportReadback.h/.cpp), exercised through PE_ExportReadbackConvertForTest on WARP.
//
// The conversion is BT.709, LIMITED ("tv") range, left-sited chroma, over-black un-premultiply.
// Every expected Y'/Cb'/Cr' below is HAND-COMPUTED from the primaries (not recomputed with the
// shader's own formula), so these tests independently pin the math:
//
//   Y_lin = 0.2126*R' + 0.7152*G' + 0.0722*B'                      (gamma R'G'B' in [0,1])
//   8-bit  limited:  Y'  = round(16  + 219*Y_lin)                  in [16,235]
//                    Cb' = round(128 + 224*0.5*(B'-Y_lin)/(1-0.0722))
//                    Cr' = round(128 + 224*0.5*(R'-Y_lin)/(1-0.2126))   in [16,240]
//   10-bit limited:  the same, scaled x4: Y' = round(64 + 876*Y_lin), neutral chroma 512,
//                    stored as the 10-bit code in the low bits of a 16-bit LE word.
//
// [Trait Media] (not GPU) — a WARP compute pass like ColorScopesTests, so it runs under the
// standard `dotnet test --filter "Category!=GPU"` CI command; GPU is reserved for tests needing a
// real swap-chain/display (see the .csproj comment).
[Collection(MediaFixturesCollection.Name)]
public sealed class ExportReadbackTests(MediaFixtures fixtures)
{
    // Fixtures unused (frames are synthesized in-memory) — joining the collection just serializes
    // this class's WARP work with the other GPU-backed tests to avoid device contention.
    private readonly MediaFixtures _fixtures = fixtures;

    // One solid-color GAMMA-premultiplied RGBA16F frame. alpha=1, so premultiplied RGB == straight
    // RGB == the over-black composite the export pass reads (export-v1.md §5 / ExportColor.hlsl).
    private static Half[] SolidFrame(int width, int height, float r, float g, float b)
    {
        var px = new Half[width * height * 4];
        Half hr = (Half)r, hg = (Half)g, hb = (Half)b, ha = (Half)1.0f;
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4 + 0] = hr;
            px[i * 4 + 1] = hg;
            px[i * 4 + 2] = hb;
            px[i * 4 + 3] = ha;
        }
        return px;
    }

    // NV12 plane accessors into the tightly-packed per-frame output.
    private static int Nv12FrameBytes(int w, int h) => w * h + (w / 2) * (h / 2) * 2;
    private static byte Nv12Y(byte[] o, int frame, int w, int h, int x, int y)
        => o[frame * Nv12FrameBytes(w, h) + y * w + x];
    private static byte Nv12Cb(byte[] o, int frame, int w, int h, int cx, int cy)
        => o[frame * Nv12FrameBytes(w, h) + w * h + (cy * (w / 2) + cx) * 2 + 0];
    private static byte Nv12Cr(byte[] o, int frame, int w, int h, int cx, int cy)
        => o[frame * Nv12FrameBytes(w, h) + w * h + (cy * (w / 2) + cx) * 2 + 1];

    // yuv422p10le plane accessors (16-bit LE samples; U/V half-width, FULL height).
    private static int P10FrameBytes(int w, int h) => w * h * 2 + (w / 2) * h * 2 * 2;
    private static int P10Y(byte[] o, int frame, int w, int h, int x, int y)
        => o[frame * P10FrameBytes(w, h) + (y * w + x) * 2] | (o[frame * P10FrameBytes(w, h) + (y * w + x) * 2 + 1] << 8);
    private static int P10U(byte[] o, int frame, int w, int h, int cx, int y)
    {
        int b = frame * P10FrameBytes(w, h) + w * h * 2 + (y * (w / 2) + cx) * 2;
        return o[b] | (o[b + 1] << 8);
    }
    private static int P10V(byte[] o, int frame, int w, int h, int cx, int y)
    {
        int b = frame * P10FrameBytes(w, h) + w * h * 2 + (w / 2) * h * 2 + (y * (w / 2) + cx) * 2;
        return o[b] | (o[b + 1] << 8);
    }

    [Theory]
    [Trait("Category", "Media")]
    // color (R,G,B)         -> expected Y', Cb', Cr' (8-bit limited, hand-computed above)
    [InlineData(0.0f, 0.0f, 0.0f, 16, 128, 128)]   // black:  Y=16+219*0      =16;  neutral chroma
    [InlineData(1.0f, 1.0f, 1.0f, 235, 128, 128)]  // white:  Y=16+219*1      =235; neutral chroma
    [InlineData(0.5f, 0.5f, 0.5f, 126, 128, 128)]  // mid-gray: Y=16+219*0.5  =125.5 (~126); neutral
    [InlineData(1.0f, 0.0f, 0.0f, 63, 102, 240)]   // red:   Y=16+219*0.2126=62.6; Cb=102.3; Cr=240
    [InlineData(0.0f, 1.0f, 0.0f, 173, 42, 26)]    // green: Y=16+219*0.7152=172.6; Cb=41.7; Cr=26.3
    [InlineData(0.0f, 0.0f, 1.0f, 32, 240, 118)]   // blue:  Y=16+219*0.0722=31.8; Cb=240; Cr=117.7
    public void Nv12_SolidColor_MatchesHandComputedBt709Limited(
        float r, float g, float b, int expectedY, int expectedCb, int expectedCr)
    {
        const int w = 8, h = 4;
        using var session = new EngineSession();
        byte[] planes = session.ExportReadbackConvertForTest(
            ExportPixelFormat.Nv12, w, h, SolidFrame(w, h, r, g, b));

        planes.Length.ShouldBe(Nv12FrameBytes(w, h));

        // Luma is full-resolution: every pixel carries the same solid Y'.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Math.Abs(Nv12Y(planes, 0, w, h, x, y) - expectedY)
                    .ShouldBeLessThanOrEqualTo(2, $"Y at ({x},{y}) for ({r},{g},{b})");
            }
        }

        // Chroma is one sample per 2x2 cell; all identical for a solid color.
        for (int cy = 0; cy < h / 2; cy++)
        {
            for (int cx = 0; cx < w / 2; cx++)
            {
                Math.Abs(Nv12Cb(planes, 0, w, h, cx, cy) - expectedCb)
                    .ShouldBeLessThanOrEqualTo(2, $"Cb at cell ({cx},{cy}) for ({r},{g},{b})");
                Math.Abs(Nv12Cr(planes, 0, w, h, cx, cy) - expectedCr)
                    .ShouldBeLessThanOrEqualTo(2, $"Cr at cell ({cx},{cy}) for ({r},{g},{b})");
            }
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Nv12_VerticalStripeEdge_ChromaIsLeftSited()
    {
        // Alternating full-height columns: EVEN x = red, ODD x = blue. Every 2x2 chroma cell
        // therefore straddles a vertical red|blue edge (left column red at 2cx, right column blue
        // at 2cx+1). Left siting (export-v1.md §5.2) averages ONLY the left column, so every cell's
        // chroma must be RED's — NOT blue's, and NOT the box-filter average of the two.
        const int w = 8, h = 4;
        var px = new Half[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool even = (x & 1) == 0;
                int i = (y * w + x) * 4;
                px[i + 0] = (Half)(even ? 1.0f : 0.0f); // R
                px[i + 1] = (Half)0.0f;                 // G
                px[i + 2] = (Half)(even ? 0.0f : 1.0f); // B
                px[i + 3] = (Half)1.0f;                 // A
            }
        }

        using var session = new EngineSession();
        byte[] planes = session.ExportReadbackConvertForTest(ExportPixelFormat.Nv12, w, h, px);

        // Luma is per-pixel (full-res), so it tracks the stripe: even columns red (~63), odd blue (~32).
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int expectedY = (x & 1) == 0 ? 63 : 32;
                Math.Abs(Nv12Y(planes, 0, w, h, x, y) - expectedY)
                    .ShouldBeLessThanOrEqualTo(2, $"Y at ({x},{y})");
            }
        }

        // Chroma: left-sited red (Cb~102, Cr~240) everywhere. A box filter would give (~171,~179),
        // the right (blue) column would give (~240,~118) — both far outside this ±3 window.
        for (int cy = 0; cy < h / 2; cy++)
        {
            for (int cx = 0; cx < w / 2; cx++)
            {
                Math.Abs(Nv12Cb(planes, 0, w, h, cx, cy) - 102)
                    .ShouldBeLessThanOrEqualTo(3, $"left-sited Cb at cell ({cx},{cy})");
                Math.Abs(Nv12Cr(planes, 0, w, h, cx, cy) - 240)
                    .ShouldBeLessThanOrEqualTo(3, $"left-sited Cr at cell ({cx},{cy})");
            }
        }
    }

    [Theory]
    [Trait("Category", "Media")]
    // 10-bit limited codes (0..1023): Y=round(64+876*Y_lin), neutral chroma 512.
    [InlineData(0.0f, 0.0f, 0.0f, 64, 512, 512)]    // black
    [InlineData(1.0f, 1.0f, 1.0f, 940, 512, 512)]   // white: 64+876=940
    [InlineData(0.5f, 0.5f, 0.5f, 502, 512, 512)]   // mid-gray: 64+876*0.5=502
    [InlineData(1.0f, 0.0f, 0.0f, 250, 409, 960)]   // red: 64+876*0.2126=250.2; Cb=409.3; Cr=960
    public void Yuv422p10_SolidColor_MatchesHandComputedBt709Limited(
        float r, float g, float b, int expectedY, int expectedU, int expectedV)
    {
        const int w = 8, h = 4;
        using var session = new EngineSession();
        byte[] planes = session.ExportReadbackConvertForTest(
            ExportPixelFormat.Yuv422p10le, w, h, SolidFrame(w, h, r, g, b));

        planes.Length.ShouldBe(P10FrameBytes(w, h));

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Math.Abs(P10Y(planes, 0, w, h, x, y) - expectedY)
                    .ShouldBeLessThanOrEqualTo(3, $"Y10 at ({x},{y}) for ({r},{g},{b})");
            }
        }

        // 4:2:2 = half width, FULL height (no vertical subsampling) — read the bottom row too.
        for (int y = 0; y < h; y++)
        {
            for (int cx = 0; cx < w / 2; cx++)
            {
                Math.Abs(P10U(planes, 0, w, h, cx, y) - expectedU)
                    .ShouldBeLessThanOrEqualTo(3, $"U10 at ({cx},{y}) for ({r},{g},{b})");
                Math.Abs(P10V(planes, 0, w, h, cx, y) - expectedV)
                    .ShouldBeLessThanOrEqualTo(3, $"V10 at ({cx},{y}) for ({r},{g},{b})");
            }
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Yuv422p10_VerticalStripeEdge_ChromaIsLeftSited()
    {
        // 4:2:2 analog of Nv12_VerticalStripeEdge_ChromaIsLeftSited. Alternating full-height columns:
        // EVEN x = red, ODD x = blue. Every 1x1-wide chroma cell straddles a vertical red|blue edge
        // (left sample red at 2cx, right sample blue at 2cx+1). Left siting (export-v1.md §5.3, tagged
        // AVCHROMA_LOC_LEFT) takes the LEFT sample's chroma, so every cell's chroma must be RED's —
        // NOT blue's, and NOT the horizontal box-average of the pair (which would be center-sited and
        // mismatch the stream's LEFT tag).
        const int w = 8, h = 4;
        var px = new Half[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool even = (x & 1) == 0;
                int i = (y * w + x) * 4;
                px[i + 0] = (Half)(even ? 1.0f : 0.0f); // R
                px[i + 1] = (Half)0.0f;                 // G
                px[i + 2] = (Half)(even ? 0.0f : 1.0f); // B
                px[i + 3] = (Half)1.0f;                 // A
            }
        }

        using var session = new EngineSession();
        byte[] planes = session.ExportReadbackConvertForTest(ExportPixelFormat.Yuv422p10le, w, h, px);

        // Luma is per-pixel (full-res): even columns red (~250), odd columns blue (~127).
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int expectedY = (x & 1) == 0 ? 250 : 127;
                Math.Abs(P10Y(planes, 0, w, h, x, y) - expectedY)
                    .ShouldBeLessThanOrEqualTo(3, $"Y10 at ({x},{y})");
            }
        }

        // Chroma: left-sited RED (U~409, V~960) at every cell, on BOTH rows (4:2:2 = full height).
        // A box filter would give (~685,~715); the right (blue) sample would give (~960,~471) — both
        // hundreds of codes outside this ±6 window. Before the left-siting fix (box-averaging left+
        // right) this asserted ~685/~715 and would fail.
        for (int y = 0; y < h; y++)
        {
            for (int cx = 0; cx < w / 2; cx++)
            {
                Math.Abs(P10U(planes, 0, w, h, cx, y) - 409)
                    .ShouldBeLessThanOrEqualTo(6, $"left-sited U10 at ({cx},{y})");
                Math.Abs(P10V(planes, 0, w, h, cx, y) - 960)
                    .ShouldBeLessThanOrEqualTo(6, $"left-sited V10 at ({cx},{y})");
            }
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void Ring_ReturnsFramesInOrderUnderLoad()
    {
        // 12 solid-gray frames of strictly increasing level g_k = (k+1)/16 (each exactly fp16-
        // representable). Their expected Y' codes span ~30..180 and are >=13 apart, so if the
        // Map-frame-N-2 ring (depth 3, cycling every slot 4x over these 12 frames — export-v1.md
        // §5.4) ever returned a slot out of order OR dropped a tail frame, the value landing at
        // output index k would be another frame's, not k's. This is the ordering-under-load check.
        const int w = 2, h = 2, frameCount = 12;
        var all = new Half[w * h * 4 * frameCount];
        for (int k = 0; k < frameCount; k++)
        {
            float level = (k + 1) / 16.0f;
            Half[] frame = SolidFrame(w, h, level, level, level);
            frame.CopyTo(all, k * w * h * 4);
        }

        using var session = new EngineSession();
        byte[] planes = session.ExportReadbackConvertForTest(ExportPixelFormat.Nv12, w, h, all);

        planes.Length.ShouldBe(Nv12FrameBytes(w, h) * frameCount);

        int previousY = -1;
        for (int k = 0; k < frameCount; k++)
        {
            int expectedY = (int)Math.Round(16 + 219 * ((k + 1) / 16.0)); // gray so Y=Y' directly
            int actualY = Nv12Y(planes, k, w, h, 0, 0);
            Math.Abs(actualY - expectedY)
                .ShouldBeLessThanOrEqualTo(2, $"frame {k}: expected gray Y~{expectedY}, got {actualY}");
            // Strictly increasing across frames — a swap of any two frames breaks monotonicity.
            actualY.ShouldBeGreaterThan(previousY, $"frame {k} Y must exceed frame {k - 1}'s (in-order)");
            previousY = actualY;

            // Neutral chroma for every gray frame, at every position.
            Math.Abs(Nv12Cb(planes, k, w, h, 0, 0) - 128).ShouldBeLessThanOrEqualTo(2, $"frame {k} Cb");
            Math.Abs(Nv12Cr(planes, k, w, h, 0, 0) - 128).ShouldBeLessThanOrEqualTo(2, $"frame {k} Cr");
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void PackedFrameBytes_MatchesPlaneLayout()
    {
        EngineSession.ExportReadbackPackedFrameBytes(ExportPixelFormat.Nv12, 1920, 1080)
            .ShouldBe(1920L * 1080 + 960 * 540 * 2);
        EngineSession.ExportReadbackPackedFrameBytes(ExportPixelFormat.Yuv422p10le, 1920, 1080)
            .ShouldBe(1920L * 1080 * 2 + 960 * 1080 * 2 * 2);
    }
}
