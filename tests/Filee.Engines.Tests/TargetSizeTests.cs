// ImageOptions.TargetKb: an output squeezed under a size cap, the highest fitting quality first, then shrinking.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class TargetSizeTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    /// <summary>An 800×600 Gaussian-noise picture: noise is incompressible, so the limits below have real work to do.</summary>
    private static string NoisyImage(string folder)
    {
        using var image = new MagickImage(MagickColors.Navy, 800, 600);
        image.AddNoise(NoiseType.Gaussian);
        var path = Path.Combine(folder, "noisy.png");
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static void AssertUnder(string output, int kb)
    {
        var size = new FileInfo(output).Length;
        Assert.True(size <= (long)kb * 1024, $"{output} is {size} bytes, asked to fit under {kb} KB");
        using var result = new MagickImage(output);
        Assert.True(result.Width > 0 && result.Height > 0, output);
    }

    [Theory]
    [InlineData("jpg", 100)]
    [InlineData("jpg", 40)]
    [InlineData("webp", 80)]
    [InlineData("avif", 120)]
    public async Task Quality_targets_come_under_the_requested_size(string target, int kb)
    {
        var dir = fx.NewFolder();
        var source = NoisyImage(dir);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target, Image = { Quality = 90, TargetKb = kb } });

        AssertDone(job);
        AssertUnder(job.Outputs.Single(), kb);
    }

    [Fact]
    public async Task Jpg_keeps_every_pixel_when_a_milder_limit_fits_through_quality_alone()
    {
        var dir = fx.NewFolder();
        var source = NoisyImage(dir);

        // 300 KB is generous for the ~200 KB a quality-90 JPG of the noise takes: no shrinking needed.
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "jpg", Image = { Quality = 90, TargetKb = 300 } });

        AssertDone(job);
        var output = job.Outputs.Single();
        AssertUnder(output, 300);
        using var result = new MagickImage(output);
        Assert.Equal((800u, 600u), (result.Width, result.Height));
    }

    [Fact]
    public async Task Png_has_no_quality_to_lose_so_the_picture_shrinks_instead()
    {
        var dir = fx.NewFolder();
        var source = NoisyImage(dir);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png", Image = { TargetKb = 60 } });

        AssertDone(job);
        AssertUnder(job.Outputs.Single(), 60);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.True(result.Width < 800 || result.Height < 600, $"PNG stayed {result.Width}×{result.Height}: nothing shrank it");
    }

    [Fact]
    public async Task Lossless_webp_ignores_quality_and_shrinks_the_picture()
    {
        var dir = fx.NewFolder();
        var source = NoisyImage(dir);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "webp", Image = { TargetKb = 100, WebpLossless = true } });

        AssertDone(job);
        AssertUnder(job.Outputs.Single(), 100);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.True(result.Width < 800 || result.Height < 600, "A lossless WebP only shrinks; its pixels are otherwise untouchable");
    }

    [Fact]
    public async Task No_limit_means_the_preset_behaves_as_before()
    {
        var dir = fx.NewFolder();
        var source = NoisyImage(dir);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "jpg", Image = { Quality = 90 } });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal((800u, 600u), (result.Width, result.Height));
    }
}
