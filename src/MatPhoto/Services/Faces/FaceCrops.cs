using ImageMagick;
using MatPhoto.Services.Library;
using Microsoft.Extensions.Caching.Memory;

namespace MatPhoto.Services.Faces;

/// <summary>
/// The pictures of faces (the round pictures of the people): cut out of the preview of the photo, square, with some room around the face, 192 pixels. Made when they
/// are asked for and kept in memory (up to 24 MB); the browser keeps them too (the address of a face never shows anything else).
/// </summary>
public sealed class FaceCrops(ThumbnailCache cache)
{
    public const int Size = 192;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions { SizeLimit = 24L * 1024 * 1024 });

    public async Task<byte[]?> GetAsync(long faceId, string hash, float x, float y, float width, float height, CancellationToken ct)
    {
        if (_memory.TryGetValue(faceId, out byte[]? kept))
        {
            return kept;
        }

        var preview = await cache.ReadAsync(hash, ThumbKind.Preview, ct);
        if (preview is null)
        {
            return null;
        }

        var data = await Task.Run(() => Cut(preview, x, y, width, height), ct);
        _memory.Set(faceId, data, new MemoryCacheEntryOptions { Size = data.Length, SlidingExpiration = TimeSpan.FromHours(2) });
        return data;
    }

    /// <summary>The square around the face (the face and half of it again on every side), inside the picture.</summary>
    internal static byte[] Cut(byte[] picture, float x, float y, float width, float height)
    {
        using var image = new MagickImage(picture);
        image.AutoOrient();
        var w = (double)image.Width;
        var h = (double)image.Height;
        var side = Math.Min(Math.Max(width * w, height * h) * 1.6, Math.Min(w, h));
        var cx = (x + width / 2) * w;
        var cy = (y + height / 2) * h;
        var left = Math.Clamp(cx - side / 2, 0, w - side);
        var top = Math.Clamp(cy - side / 2, 0, h - side);
        image.Crop(new MagickGeometry((int)left, (int)top, (uint)Math.Max(1, side), (uint)Math.Max(1, side)));
        image.ResetPage();
        image.Resize(new MagickGeometry(Size, Size) { IgnoreAspectRatio = true });
        image.Strip();
        image.Quality = 85;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }
}
