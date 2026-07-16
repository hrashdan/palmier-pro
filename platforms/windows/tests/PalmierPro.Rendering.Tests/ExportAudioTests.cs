using PalmierPro.Core.Models;
using PalmierPro.Services.Engine;
using Shouldly;
using Xunit;

namespace PalmierPro.Rendering.Tests;

// E5 export audio slice (export-v1.md §7's DECISION: reuse the offline AudioMixer path). Drives
// PE_ExportAudioRenderForTest — native ExportAudio's chunked AudioMixer::RenderRange -> resample ->
// fixed-size encoder AVFrame path — against the same real fixture timelines
// TimelineAudioMixerTests/RetimeStretcherTests already use, and checks it against
// TimelineSession.RenderAudioRange's own direct output: same gain/fade/mute model, same
// pitch-preserving retime, chunked through a resampler+FIFO instead of one monolithic call.
[Collection(MediaFixturesCollection.Name)]
public sealed class ExportAudioTests(MediaFixtures fixtures)
{
    private const int Fps = 30;
    private const int MixRate = 48000;
    private const int SamplesPerFrame = MixRate / Fps; // exact for 30 fps — export-v1.md §7's chunking constraint
    private const int ClipDurationFrames = 60; // the 2 s fixture, whole

    private TimelineSession OpenSine(EngineSession session, double volume, int fadeInFrames, bool muted, double speed = 1.0, int? durationFrames = null)
    {
        var clip = new Clip("sine", startFrame: 0, durationFrames: durationFrames ?? ClipDurationFrames)
        {
            Id = "CLIP-A",
            MediaType = ClipType.Audio,
            SourceClipType = ClipType.Audio,
            Volume = volume,
            FadeInFrames = fadeInFrames,
            Speed = speed,
        };
        var track = new Track(ClipType.Audio, [clip]) { Id = "TRACK-A", Muted = muted };
        var timeline = new Timeline
        {
            Id = "TL-A", Fps = Fps, Width = MediaFixtures.VideoWidth, Height = MediaFixtures.VideoHeight,
            Tracks = [track],
        };
        var project = new ProjectFile([timeline], timeline.Id, [timeline.Id]);
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry("sine", "sine", ClipType.Audio,
                PalmierPro.Core.Models.MediaSource.External(fixtures.AudioOnlyPath), duration: ClipDurationFrames)],
        };
        var resolver = new MediaResolver(() => manifest, () => null);

        var result = TimelineSnapshotBuilder.Build(project, "TL-A", resolver);
        result.OfflineMediaRefs.ShouldBeEmpty();
        byte[] json = TimelineSnapshotSerializer.ToJsonBytes(result.Snapshot);
        return TimelineSession.Open(session, json);
    }

    private static float Peak(float[] interleaved, int start, int count)
    {
        float peak = 0f;
        int end = Math.Min(interleaved.Length, (start + count) * 2);
        for (int i = start * 2; i < end; i++)
        {
            peak = Math.Max(peak, Math.Abs(interleaved[i]));
        }
        return peak;
    }

    private static int CountZeroCrossings(float[] interleaved, int start, int count)
    {
        int crossings = 0;
        float prev = interleaved[start * 2];
        int end = Math.Min(interleaved.Length / 2, start + count);
        for (int i = start + 1; i < end; i++)
        {
            float cur = interleaved[i * 2];
            if ((prev < 0f) != (cur < 0f))
            {
                crossings++;
            }
            prev = cur;
        }
        return crossings;
    }

    // Unpacks ExportAudioSampleFormat.Flt (packed float32) bytes into an interleaved-stereo
    // float[] — the exact same shape RenderAudioRange already returns, so the two are directly
    // comparable sample-for-sample.
    private static float[] UnpackFlt(byte[] pcm, int sampleCount)
    {
        var floats = new float[sampleCount * 2];
        Buffer.BlockCopy(pcm, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }

    [Fact]
    [Trait("Category", "Media")]
    public void RenderAudioForExportTest_MatchesRenderAudioRange_NearExactly()
    {
        using var session = new EngineSession();

        // 8 timeline frames (~267 ms) per internal RenderRange call — small enough that a 1-second
        // render crosses several chunk boundaries, exercising the resampler/FIFO's own bookkeeping,
        // not just a single monolithic call.
        const int chunkFrames = 8;
        const int totalFrames = Fps; // 1 second
        const int totalSamples = totalFrames * SamplesPerFrame;

        float[] direct;
        using (TimelineSession reference = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false))
        {
            direct = reference.RenderAudioRange(startFrame: 0, frameCount: totalSamples);
        }

        byte[] exported;
        using (TimelineSession exportTimeline = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false))
        {
            exported = exportTimeline.RenderAudioForExportTest(
                startFrame: 0, frameCount: totalFrames, chunkFrameCount: chunkFrames, totalSampleCount: totalSamples,
                outputFormat: ExportAudioSampleFormat.Flt, outputSampleRate: MixRate, outputChannels: 2, outputFrameSize: 960);
        }
        float[] viaExport = UnpackFlt(exported, totalSamples);

        Peak(direct, 0, totalSamples).ShouldBeGreaterThan(0.01f); // sanity: actually decoded audio

        // Identity-rate (48 kHz -> 48 kHz), packed-float passthrough: chunking through
        // ExportAudio's resampler/FIFO must reproduce AudioMixer::RenderRange's own direct output,
        // same clip decode/gain/fade math, same samples — near-exact float equality.
        for (int i = 0; i < direct.Length; i++)
        {
            Math.Abs(direct[i] - viaExport[i]).ShouldBeLessThan(1e-5f);
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void RenderAudioForExportTest_GainAndFade_MatchDirectMixer()
    {
        using var session = new EngineSession();
        const int chunkFrames = 6; // deliberately not a divisor of Fps, to cross uneven chunk boundaries
        const int totalFrames = Fps;
        const int totalSamples = totalFrames * SamplesPerFrame;

        // Half gain: export path halves amplitude exactly like the direct mixer (docs §1).
        using (TimelineSession full = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false))
        {
            byte[] pcm = full.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            Peak(UnpackFlt(pcm, totalSamples), 0, totalSamples).ShouldBeGreaterThan(0.1f);
        }
        float halfPeak;
        using (TimelineSession half = OpenSine(session, volume: 0.5, fadeInFrames: 0, muted: false))
        {
            byte[] pcm = half.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            halfPeak = Peak(UnpackFlt(pcm, totalSamples), 0, totalSamples);
        }
        float fullPeak;
        using (TimelineSession full = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false))
        {
            byte[] pcm = full.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            fullPeak = Peak(UnpackFlt(pcm, totalSamples), 0, totalSamples);
        }
        (halfPeak / fullPeak).ShouldBeInRange(0.45f, 0.55f);

        // Muted track: the export path must stay bit-exact silence too (docs §6.1), same as
        // RenderAudioRange's own guarantee — chunking must not leak a partially-computed block.
        using (TimelineSession muted = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: true))
        {
            byte[] pcm = muted.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            Peak(UnpackFlt(pcm, totalSamples), 0, totalSamples).ShouldBe(0f);
        }

        // Fade-in: peaks must climb monotonically across the export path exactly like the direct
        // mixer (RenderAudioRange_FadeIn_RampsUpFromSilence's own assertions, replayed here).
        using (TimelineSession fading = OpenSine(session, volume: 1.0, fadeInFrames: 30, muted: false))
        {
            byte[] pcm = fading.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            float[] mix = UnpackFlt(pcm, totalSamples);

            const int window = 4800;
            float[] peaks = new float[5];
            for (int w = 0; w < 5; w++)
            {
                peaks[w] = Peak(mix, w * (MixRate / 5), window);
            }
            peaks[0].ShouldBeLessThan(0.2f);
            for (int w = 1; w < 5; w++)
            {
                peaks[w].ShouldBeGreaterThan(peaks[w - 1]);
            }
            peaks[4].ShouldBeGreaterThan(peaks[0] * 4f);
        }
    }

    [Fact]
    [Trait("Category", "Media")]
    public void RenderAudioForExportTest_RetimedClip_PreservesPitchLikeDirectMixer()
    {
        using var session = new EngineSession();
        const int chunkFrames = 5; // small, uneven chunk size — stresses the retime stretcher's
                                    // cross-call continuity through ExportAudio's own AudioMixer instance
        const int totalFrames = Fps; // 1 s on the 2x-speed clip's timeline span
        const int totalSamples = totalFrames * SamplesPerFrame;

        int referenceCrossings;
        using (TimelineSession reference = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false))
        {
            float[] mix = reference.RenderAudioRange(startFrame: 0, frameCount: MixRate);
            referenceCrossings = CountZeroCrossings(mix, 0, MixRate);
        }

        // 2x speed, 30-frame on-timeline duration: SourceFramesConsumed = 30 * 2 = 60, exactly the
        // fixture's whole 2 s of source (same mapping RetimeStretcherTests already relies on).
        using (TimelineSession doubled = OpenSine(session, volume: 1.0, fadeInFrames: 0, muted: false, speed: 2.0, durationFrames: ClipDurationFrames / 2))
        {
            byte[] pcm = doubled.RenderAudioForExportTest(0, totalFrames, chunkFrames, totalSamples,
                ExportAudioSampleFormat.Flt, MixRate, 2, 960);
            float[] wholeSpan = UnpackFlt(pcm, totalSamples);

            Peak(wholeSpan, 0, totalSamples).ShouldBeGreaterThan(0.01f);
            int doubledCrossings = CountZeroCrossings(wholeSpan, 0, totalSamples);

            // Same dominant frequency as 1x through the export path too — pitch preserved by the
            // SAME RetimeStretcher a naive per-chunk resample-to-speed would not reproduce.
            double ratio = (double)doubledCrossings / referenceCrossings;
            ratio.ShouldBeInRange(0.85, 1.15);
        }
    }
}
