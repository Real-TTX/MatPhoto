using ImageMagick;
using MatPhoto.Services.Library;
using Xunit;
using Xunit.Abstractions;

namespace MatPhoto.Tests;

/// <summary>
/// The camera RAW files. With <c>MATPHOTO_RAW_PROBE</c> set to a folder of RAW files (CR2, CR3, NEF, ARW, DNG, RAF, RW2, ORF …), <see cref="Probe"/> renders each one
/// as the indexer does, writes the previews into <c>out/</c> and prints their size, how long it took and how bright they are (a black picture is near 0).
/// </summary>
public sealed class RawTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_raw_format_is_handed_to_LibRaw_by_its_name()
    {
        // without the name ImageMagick takes most of them for a TIFF (the tiny first picture of a NEF, an error for an ARW)
        var raws = MediaFormats.AcceptList.Split(',').Where(e => e.StartsWith('.') && MediaFormats.Of("x" + e)?.IsRaw == true).ToList();
        Assert.True(raws.Count >= 20, string.Join(" ", raws));
        Assert.All(raws, e => Assert.True(ImageRenderer.RawFormats.ContainsKey(e), e));
        Assert.All(ImageRenderer.RawFormats.Keys, e => Assert.True(MediaFormats.Of("x" + e)?.IsRaw == true, e));
    }

    [Fact]
    public void Probe()
    {
        var folder = Environment.GetEnvironmentVariable("MATPHOTO_RAW_PROBE");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var target = Path.Combine(folder, "out");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(folder).Where(f => MediaFormats.Of(f)?.IsRaw == true).Order())
        {
            var started = DateTime.UtcNow;
            try
            {
                var bytes = File.ReadAllBytes(file);
                using var memory = new MemoryStream(bytes);
                var meta = MetadataReader.Read(memory, MediaFormats.Of(file)!);
                var rendered = ImageRenderer.Render(bytes, Path.GetExtension(file), 240, 2048, 82, meta.Orientation);
                File.WriteAllBytes(Path.Combine(target, Path.GetFileNameWithoutExtension(file) + ".jpg"), rendered.Preview);
                using var preview = new MagickImage(rendered.Preview);
                var mean = preview.Statistics().GetChannel(PixelChannel.Composite)?.Mean ?? 0;
                output.WriteLine($"{Path.GetFileName(file),-12} {rendered.Width} x {rendered.Height}, preview {preview.Width} x {preview.Height}, orientation {meta.Orientation}, mean {mean / Quantum.Max * 100:0.0} %, {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
            }
            catch (MagickException e)
            {
                output.WriteLine($"{Path.GetFileName(file),-12} FAILED {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
