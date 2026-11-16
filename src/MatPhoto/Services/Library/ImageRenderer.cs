using ImageMagick;

namespace MatPhoto.Services.Library;

/// <summary>What was made of a picture: the tile and the preview (JPEG), its size as shown, the picture hash, and whether it moves (a GIF or WebP with several frames).</summary>
public sealed record RenderedImage(byte[] Thumb, byte[] Preview, int Width, int Height, long PerceptualHash, bool Animated);

/// <summary>
/// Makes the thumbnails with ImageMagick (Magick.NET): every format a camera or a phone writes (JPEG, HEIC, PNG, WebP, AVIF, TIFF and the RAW formats). The picture is
/// turned as the camera says, converted to sRGB, flattened onto white and written as a progressive JPEG without metadata (no position in a thumbnail). A large JPEG is
/// decoded at a reduced size from the start (much faster). At most two pictures are decoded at the same time (memory).
/// </summary>
public static class ImageRenderer
{
    private static readonly SemaphoreSlim Gate = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 2));

    static ImageRenderer()
    {
        // a broken or hostile file must not take the memory of the server
        ResourceLimits.Memory = 1024UL * 1024 * 1024;
        ResourceLimits.Width = 60000;
        ResourceLimits.Height = 60000;
    }

    /// <summary>Renders the picture in <paramref name="data"/>. Throws <see cref="MagickException"/> for a file ImageMagick cannot read.</summary>
    public static async Task<RenderedImage> RenderAsync(byte[] data, string extension, int thumbHeight, int previewSize, int quality, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return Render(data, extension, thumbHeight, previewSize, quality);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static RenderedImage Render(byte[] data, string extension, int thumbHeight, int previewSize, int quality)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        var ext = extension.ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".jpe")
        {
            // the decoder of JPEG can skip detail it will throw away: decode at about twice the size of the preview
            settings.SetDefines(new ImageMagick.Formats.JpegReadDefines { Size = new MagickGeometry((uint)previewSize * 2, (uint)previewSize * 2) });
        }

        var animated = false;
        if (ext is ".gif" or ".webp")
        {
            using var frames = new MagickImageCollection();
            frames.Ping(data);
            animated = frames.Count > 1;
        }

        using var image = new MagickImage(data, settings);
        image.AutoOrient();
        if (image.GetColorProfile() is not null)
        {
            image.TransformColorSpace(ColorProfiles.SRGB);
        }

        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }

        image.Strip();
        var width = (int)image.Width;
        var height = (int)image.Height;

        using var preview = image.Clone();
        preview.Resize(new MagickGeometry((uint)previewSize, (uint)previewSize) { Greater = true });
        var previewBytes = Jpeg(preview, quality);

        using var thumb = preview.Clone();
        thumb.Resize(new MagickGeometry((uint)thumbHeight * 4, (uint)thumbHeight) { Greater = true });
        thumb.Sharpen(0, 0.5);
        var thumbBytes = Jpeg(thumb, quality);

        // the original size of a JPEG decoded smaller: the decoder told it before scaling
        if (ext is ".jpg" or ".jpeg" or ".jpe")
        {
            var info = new MagickImageInfo(data);
            var turned = image.Width < image.Height != info.Width < info.Height;
            width = (int)(turned ? info.Height : info.Width);
            height = (int)(turned ? info.Width : info.Height);
        }

        return new RenderedImage(thumbBytes, previewBytes, width, height, DifferenceHash(thumb), animated);
    }

    /// <summary>
    /// A 64-bit difference hash (dHash): the picture shrunk to 9 x 8 grey pixels, one bit per pair of neighbours (left brighter than right). Two copies of a photo
    /// (another size, a little compression) differ in very few bits; the distance is the number of bits that differ.
    /// </summary>
    public static long DifferenceHash(IMagickImage<byte> source)
    {
        using var small = source.Clone();
        small.Grayscale();
        small.Resize(new MagickGeometry(9, 8) { IgnoreAspectRatio = true });
        using var pixels = small.GetPixels();
        var values = pixels.ToByteArray(PixelMapping.RGB) ?? [];
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var left = values[((y * 9) + x) * 3];
                var right = values[((y * 9) + x + 1) * 3];
                if (left > right)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return unchecked((long)hash);
    }

    /// <summary>The number of bits two picture hashes differ in (0 = the same picture).</summary>
    public static int Distance(long a, long b) => System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    private static byte[] Jpeg(IMagickImage<byte> image, int quality)
    {
        image.Quality = (uint)quality;
        image.Settings.Interlace = Interlace.Plane;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }
}
