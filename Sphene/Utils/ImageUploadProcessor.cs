using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sphene.Utils;

/// <summary>
/// Prepares picked image files for upload: decodes, downscales to a sane
/// resolution and re-encodes to a storage-friendly format instead of
/// rejecting large sources outright. Still frames become WebP (quality is
/// stepped down until the byte cap fits), multi-frame sources stay GIF so
/// animation survives the resize.
/// </summary>
public static class ImageUploadProcessor
{
    /// <summary>A fully prepared upload payload.</summary>
    /// <param name="Compressed">True when the source had to be re-encoded or
    /// downscaled - the UI can surface that the file was adjusted.</param>
    public sealed record Prepared(byte[] Bytes, string Base64, string FileName,
        string ContentType, int Width, int Height, bool Compressed);

    /// <summary>File extensions the file pickers should advertise.</summary>
    public const string PickerFilter = ".png,.jpg,.jpeg,.gif,.webp,.bmp";

    /// <summary>Absolute ceiling - beyond this even the best compression is rejected.</summary>
    private const long HardLimitBytes = 4L * 1024 * 1024;

    /// <summary>WebP quality ladder - tried top-down until the soft cap fits.</summary>
    private static readonly int[] QualitySteps = [85, 70, 55, 40, 25];

    /// <summary>
    /// Reads <paramref name="filePath"/>, decodes and prepares an upload payload.
    /// Returns null when the file is not a readable/supported image or stays
    /// above <see cref="HardLimitBytes"/> after every compression step.
    /// </summary>
    /// <param name="maxDimension">Longest edge in pixels - larger sources downscale.</param>
    /// <param name="softLimitBytes">Target byte cap the encoder tries to fit under.</param>
    public static async Task<Prepared?> PrepareAsync(string filePath,
        int maxDimension = 1920, long softLimitBytes = 900L * 1024)
    {
        var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        var format = Image.DetectFormat(bytes);
        if (format == null)
        {
            return null;
        }
        var supported = new[] { "png", "jpg", "jpeg", "gif", "webp", "bmp" };
        if (!supported.Any(ext => format.FileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }
        using var image = Image.Load<Rgba32>(bytes);
        return image.Frames.Count > 1
            ? PrepareAnimated(filePath, image, maxDimension, softLimitBytes)
            : PrepareStill(filePath, image, maxDimension, softLimitBytes);
    }

    /// <summary>
    /// Encodes an already-decoded image the same way <see cref="PrepareAsync"/>
    /// does - used by the photo studio so editor output lands on the identical
    /// upload format without a disk roundtrip.
    /// </summary>
    /// <param name="fileNameBase">Name without extension for the produced file.</param>
    public static Prepared? PrepareFromImage(Image<Rgba32> image, string fileNameBase,
        int maxDimension = 1920, long softLimitBytes = 900L * 1024)
    {
        var (w, h) = ClampToDimension(image.Width, image.Height, maxDimension);
        var compressed = w != image.Width || h != image.Height;
        using var working = image.Clone(x => x.Resize(w, h));
        return EncodeStill(working, fileNameBase, softLimitBytes, compressed);
    }

    /// <summary>Still image path - resize once, then walk the WebP quality ladder.</summary>
    private static Prepared? PrepareStill(string filePath, Image<Rgba32> image,
        int maxDimension, long softLimitBytes)
    {
        var (w, h) = ClampToDimension(image.Width, image.Height, maxDimension);
        var compressed = w != image.Width || h != image.Height;
        using var working = image.Clone(x => x.Resize(w, h));
        return EncodeStill(working, Path.GetFileNameWithoutExtension(filePath),
            softLimitBytes, compressed);
    }

    /// <summary>Runs the WebP quality ladder, the halved-retry and the hard-cap
    /// last resort on a working image.</summary>
    private static Prepared? EncodeStill(Image<Rgba32> working, string fileNameBase,
        long softLimitBytes, bool compressed)
    {
        foreach (var quality in QualitySteps)
        {
            using var ms = new MemoryStream();
            working.SaveAsWebp(ms, new WebpEncoder { Quality = quality });
            var encoded = ms.ToArray();
            if (encoded.Length <= softLimitBytes)
            {
                return Result(fileNameBase, encoded, "image/webp", ".webp",
                    working.Width, working.Height, compressed || quality < QualitySteps[0]);
            }
        }

        // Still too heavy - halve resolution once and retry the middle rungs.
        var halfW = Math.Max(64, working.Width / 2);
        var halfH = Math.Max(1, (int)MathF.Round(working.Height * (halfW / (float)working.Width)));
        using var shrunk = working.Clone(x => x.Resize(halfW, halfH));
        foreach (var quality in new[] { 70, 50, 30 })
        {
            using var ms = new MemoryStream();
            shrunk.SaveAsWebp(ms, new WebpEncoder { Quality = quality });
            var encoded = ms.ToArray();
            if (encoded.Length <= softLimitBytes)
            {
                return Result(fileNameBase, encoded, "image/webp", ".webp",
                    shrunk.Width, shrunk.Height, true);
            }
        }

        // Last resort: smallest encode we can produce - hard cap still applies.
        using var final = new MemoryStream();
        shrunk.SaveAsWebp(final, new WebpEncoder { Quality = 20 });
        var last = final.ToArray();
        return last.Length <= HardLimitBytes
            ? Result(fileNameBase, last, "image/webp", ".webp", shrunk.Width, shrunk.Height, true)
            : null;
    }

    /// <summary>Animated path - GIF stays GIF; downscale until the cap fits.</summary>
    private static Prepared? PrepareAnimated(string filePath, Image<Rgba32> image,
        int maxDimension, long softLimitBytes)
    {
        var (width, height) = ClampToDimension(image.Width, image.Height, maxDimension);
        var nameBase = Path.GetFileNameWithoutExtension(filePath);
        byte[] encoded;
        while (true)
        {
            using var output = image.Clone(x => x.Resize(width, height));
            using var ms = new MemoryStream();
            output.SaveAsGif(ms);
            encoded = ms.ToArray();
            if (encoded.Length <= softLimitBytes)
            {
                return Result(nameBase, encoded, "image/gif", ".gif", width, height,
                    width != image.Width || height != image.Height);
            }
            if (width <= 240)
            {
                break;
            }
            width /= 2;
            height = Math.Max(1, height / 2);
        }
        return encoded.Length <= HardLimitBytes
            ? Result(nameBase, encoded, "image/gif", ".gif", width, height, true)
            : null;
    }

    /// <summary>Scales (w, h) so the longest edge is at most <paramref name="max"/>.</summary>
    private static (int w, int height) ClampToDimension(int w, int h, int max)
    {
        var longest = Math.Max(w, h);
        if (longest <= max)
        {
            return (w, h);
        }
        var factor = max / (float)longest;
        return (Math.Max(1, (int)MathF.Round(w * factor)),
            Math.Max(1, (int)MathF.Round(h * factor)));
    }

    private static Prepared Result(string fileNameBase, byte[] bytes, string contentType,
        string extension, int width, int height, bool compressed)
    {
        return new Prepared(bytes, Convert.ToBase64String(bytes), fileNameBase + extension,
            contentType, width, height, compressed);
    }
}
