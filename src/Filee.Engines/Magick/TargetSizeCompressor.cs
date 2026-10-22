// Compresses a picture under a size limit (ImageOptions.TargetKb): binary-searches the highest quality that fits,
// then shrinks the picture, until the encoded file is small enough — for forms and applications that cap photo sizes.

using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>
/// Brings one frame under <see cref="ImageOptions.TargetKb"/> bytes, in place, before the encoder writes it.
/// Quality-searchable targets (JPG, WebP, AVIF, JPEG XL, JPEG 2000) keep every pixel and only give up sharpness;
/// lossless targets (PNG, GIF) and a lossless WebP shrink the picture instead. Icons and multi-page writes never
/// reach this class (see <see cref="ImageEncoder"/>).
/// </summary>
internal static class TargetSizeCompressor
{
    /// <summary>Targets whose quality setting trades file size; PNG and GIF are lossless, so only shrinking helps.</summary>
    private static readonly HashSet<string> QualityTargets = ["jpg", "webp", "avif", "jxl", "jp2"];

    /// <summary>Picture pixels given up per shrink round; 0.8^12 rounds ≈ 7% of the original, past any usable photo.</summary>
    private const double ShrinkPerRound = 0.8;
    private const int MaxShrinkRounds = 12;

    /// <summary>
    /// Makes <paramref name="image"/> encode under <paramref name="options"/>.TargetKb bytes: seeks the highest
    /// fitting quality first (pixel dimensions untouched), then shrinks the picture and seeks again if even the
    /// lowest quality is too big. Metadata rides along with "keep metadata" on, but the size cap beats it: an
    /// oversized picture loses its profiles and comments first (an EXIF thumbnail or GPS block of a phone photo
    /// can outweigh its pixels), keeping only the colour profile - a few hundred bytes, and dropping it would
    /// shift every colour. The image's quality setting is left at what will be written.
    /// </summary>
    /// <param name="targetFormat">Preset target id (the encoder settings depend on it through <paramref name="options"/>).</param>
    internal static void Fit(IMagickImage<byte> image, string targetFormat, ImageOptions options)
    {
        var targetBytes = (long)options.TargetKb * 1024;
        var format = ImageEncoder.OutputFormat(targetFormat, options).Format;
        var takesQuality = TakesQuality(targetFormat, options);
        var top = (int)Math.Clamp(options.Quality, 1, 100);

        if (EncodedSize(image, format) > targetBytes)
            StripMetadata(image);
        if (EncodedSize(image, format) > targetBytes && takesQuality)
            SeekQuality(image, format, top, targetBytes);
        for (var round = 0; round < MaxShrinkRounds && EncodedSize(image, format) > targetBytes; round++)
        {
            image.Resize((uint)Math.Max(1, Math.Floor(image.Width * ShrinkPerRound)),
                (uint)Math.Max(1, Math.Floor(image.Height * ShrinkPerRound)));
            if (takesQuality)
                SeekQuality(image, format, top, targetBytes);
        }
    }

    /// <summary>Removes the profiles and text attributes that would be written with the picture, except the ICC one.</summary>
    private static void StripMetadata(IMagickImage<byte> image)
    {
        var colorProfile = image.GetColorProfile();
        image.Strip();
        if (colorProfile is not null)
            image.SetProfile(colorProfile);
    }

    /// <summary>Quality-seeking formats, minus a lossless WebP (its quality setting does nothing there).</summary>
    private static bool TakesQuality(string targetFormat, ImageOptions options) =>
        QualityTargets.Contains(targetFormat) && !(targetFormat == "webp" && options.WebpLossless);

    /// <summary>
    /// Sets <paramref name="image"/>'s quality to the highest value in [1, <paramref name="top"/>] whose encoding
    /// fits, or to 1 when none does (the caller then shrinks the picture). ~7 encodings for a quality of 90.
    /// </summary>
    private static void SeekQuality(IMagickImage<byte> image, MagickFormat format, int top, long targetBytes)
    {
        var (best, low, high) = (1, 1, top);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            image.Quality = (uint)mid;
            if (EncodedSize(image, format) <= targetBytes)
            {
                best = mid;
                low = mid + 1;
            }
            else
                high = mid - 1;
        }
        image.Quality = (uint)best;
    }

    /// <summary>Bytes the frame would take written as <paramref name="format"/> with its current quality and settings.</summary>
    private static long EncodedSize(IMagickImage<byte> image, MagickFormat format) => image.ToByteArray(format).LongLength;
}
