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
    public static async Task<RenderedImage> RenderAsync(byte[] data, string extension, int thumbHeight, int previewSize, int quality, CancellationToken ct, int orientation = 1)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return Render(data, extension, thumbHeight, previewSize, quality, orientation);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <param name="orientation">The EXIF orientation of the file (1 to 8): a camera RAW keeps it beside its embedded preview, not in it.</param>
    public static RenderedImage Render(byte[] data, string extension, int thumbHeight, int previewSize, int quality, int orientation = 1)
    {
        var ext = extension.ToLowerInvariant();
        if (RawFormats.TryGetValue(ext, out var raw))
        {
            using var picture = ReadRaw(data, raw, previewSize, orientation, out var size);
            return Finish(picture, thumbHeight, previewSize, quality, size, animated: false);
        }

        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
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

        // the original size of a JPEG decoded smaller: the decoder told it before scaling
        (uint Width, uint Height)? stored = null;
        if (ext is ".jpg" or ".jpeg" or ".jpe")
        {
            var info = new MagickImageInfo(data);
            stored = (info.Width, info.Height);
        }

        return Finish(image, thumbHeight, previewSize, quality, stored, animated);
    }

    /// <summary>
    /// The camera RAW formats and how ImageMagick (LibRaw) is told which one a file is. Most of them begin like a TIFF: read without the format, ImageMagick takes them
    /// for one and shows the tiny first picture of the file (NEF) or fails (ARW).
    /// </summary>
    internal static readonly Dictionary<string, MagickFormat> RawFormats = new(StringComparer.Ordinal)
    {
        [".dng"] = MagickFormat.Dng, [".cr2"] = MagickFormat.Cr2, [".cr3"] = MagickFormat.Cr3, [".nef"] = MagickFormat.Nef, [".arw"] = MagickFormat.Arw,
        [".orf"] = MagickFormat.Orf, [".rw2"] = MagickFormat.Rw2, [".raf"] = MagickFormat.Raf, [".pef"] = MagickFormat.Pef, [".srw"] = MagickFormat.Srw,
        [".crw"] = MagickFormat.Crw, [".nrw"] = MagickFormat.Nrw, [".mrw"] = MagickFormat.Mrw, [".sr2"] = MagickFormat.Sr2, [".srf"] = MagickFormat.Srf, [".rwl"] = MagickFormat.Rwl,
        [".kdc"] = MagickFormat.Kdc, [".dcr"] = MagickFormat.Dcr, [".erf"] = MagickFormat.Erf, [".3fr"] = MagickFormat.ThreeFr, [".iiq"] = MagickFormat.Iiq, [".mef"] = MagickFormat.Mef,
        [".mos"] = MagickFormat.Mos, [".x3f"] = MagickFormat.X3f,

        // ".raw" of Panasonic and Leica: ImageMagick's own RAW is bare pixels, DCRAW hands the file to LibRaw
        [".raw"] = MagickFormat.Dcraw,
    };

    /// <summary>
    /// A camera RAW: the JPEG the camera put into the file (what its screen showed: its colours, its white balance, fast), turned by the orientation of the file.
    /// Only when it has none of at least half the preview size, the sensor data is developed (slow: seconds) with the white balance of the camera.
    /// <paramref name="size"/> is the size of the sensor picture (as stored, before turning).
    /// </summary>
    private static MagickImage ReadRaw(byte[] data, MagickFormat format, int previewSize, int orientation, out (uint Width, uint Height)? size)
    {
        size = null;
        try
        {
            var info = new MagickImageInfo(data, new MagickReadSettings { Format = format });
            size = (info.Width, info.Height);
        }
        catch (MagickException)
        {
            // the size of the embedded picture then
        }

        var embedded = new MagickReadSettings { Format = format };
        embedded.SetDefines(new ImageMagick.Formats.DngReadDefines { ReadThumbnails = true });
        try
        {
            using var thumbnails = new MagickImageCollection(data, embedded);
            var largest = thumbnails.OrderByDescending(t => (long)t.Width * t.Height).FirstOrDefault();
            if (largest is not null && Math.Max(largest.Width, largest.Height) >= Math.Min(previewSize / 2, 1024))
            {
                var picture = (MagickImage)largest.Clone();
                if (picture.Orientation is ImageMagick.Orientation.Undefined or ImageMagick.Orientation.TopLeft && orientation is >= 2 and <= 8)
                {
                    picture.Orientation = (ImageMagick.Orientation)orientation;
                }

                picture.AutoOrient();
                return picture;
            }
        }
        catch (MagickException)
        {
            // no embedded picture LibRaw can give: develop the sensor data
        }

        var developed = new MagickReadSettings { Format = format };
        developed.SetDefines(new ImageMagick.Formats.DngReadDefines { UseCameraWhiteBalance = true, OutputColor = ImageMagick.Formats.DngOutputColor.SRGB });
        var image = new MagickImage(data, developed);
        image.AutoOrient();
        return image;
    }

    /// <summary>The common end: sRGB, onto white, without metadata, the preview and the tile as JPEG, the picture hash. <paramref name="stored"/>: the size of the original when the picture was decoded smaller.</summary>
    private static RenderedImage Finish(MagickImage image, int thumbHeight, int previewSize, int quality, (uint Width, uint Height)? stored, bool animated)
    {
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
        if (stored is { } original && original.Width > 0 && original.Height > 0)
        {
            var turned = image.Width < image.Height != original.Width < original.Height;
            width = (int)(turned ? original.Height : original.Width);
            height = (int)(turned ? original.Width : original.Height);
        }

        using var preview = image.Clone();
        preview.Resize(new MagickGeometry((uint)previewSize, (uint)previewSize) { Greater = true });
        var previewBytes = Jpeg(preview, quality);

        using var thumb = preview.Clone();
        thumb.Resize(new MagickGeometry((uint)thumbHeight * 4, (uint)thumbHeight) { Greater = true });
        thumb.Sharpen(0, 0.5);
        var thumbBytes = Jpeg(thumb, quality);

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
