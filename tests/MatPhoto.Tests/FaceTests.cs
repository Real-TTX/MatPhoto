using ImageMagick;
using ImageMagick.Drawing;
using MatPhoto.Services.Faces;
using Xunit;
using Xunit.Abstractions;

namespace MatPhoto.Tests;

/// <summary>
/// The face recognition: the geometry (the similarity onto the template, the boxes that overlap), the grouping, and the models themselves on a picture without a face.
/// With <c>MATPHOTO_FACE_PROBE</c> set to a folder of JPEGs, <see cref="Probe"/> writes the pictures with their faces drawn into <c>out/</c> and prints how alike the
/// faces are (to look at the models with real photos).
/// </summary>
public sealed class FaceTests(ITestOutputHelper output)
{
    private static readonly string Models = Path.Combine(AppContext.BaseDirectory, "models");

    [Fact]
    public void The_similarity_brings_the_points_onto_the_template()
    {
        // the template turned by 30°, scaled by 2 and shifted: the similarity must undo exactly that
        (float X, float Y)[] template = [(38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f)];
        var angle = MathF.PI / 6;
        var moved = template.Select(p => (X: 2 * (p.X * MathF.Cos(angle) - p.Y * MathF.Sin(angle)) + 100, Y: 2 * (p.X * MathF.Sin(angle) + p.Y * MathF.Cos(angle)) + 40)).ToArray();
        var (a, b, tx, ty) = FaceEngine.Similarity(moved, template);
        for (var i = 0; i < template.Length; i++)
        {
            var x = a * moved[i].X - b * moved[i].Y + tx;
            var y = b * moved[i].X + a * moved[i].Y + ty;
            Assert.InRange(x, template[i].X - 0.01f, template[i].X + 0.01f);
            Assert.InRange(y, template[i].Y - 0.01f, template[i].Y + 0.01f);
        }
    }

    [Fact]
    public void Boxes_that_overlap_keep_the_surest()
    {
        var points = new (float, float)[5];
        var faces = new List<DetectedFace>
        {
            new(10, 10, 100, 100, 0.9f, points),
            new(15, 12, 100, 100, 0.95f, points),
            new(300, 300, 80, 80, 0.85f, points),
        };
        var kept = FaceEngine.Suppress(faces, 0.3f);
        Assert.Equal([0.95f, 0.85f], kept.Select(k => k.Score));
    }

    [Fact]
    public void The_detector_reads_its_grid()
    {
        // one cell of the stride 32 (row 2, column 3) is sure: a face of 64 × 64 around the centre of that cell
        var cells = (640 / 32) * (640 / 32);
        var cls = new float[cells];
        var obj = new float[cells];
        var box = new float[cells * 4];
        var kps = new float[cells * 10];
        var idx = 2 * 20 + 3;
        cls[idx] = 1;
        obj[idx] = 0.81f;
        box[idx * 4] = 0.5f;
        box[idx * 4 + 1] = 0.5f;
        box[idx * 4 + 2] = MathF.Log(2);
        box[idx * 4 + 3] = MathF.Log(2);
        var face = Assert.Single(FaceEngine.Decode([(32, cls, obj, box, kps)], 0.8f));
        Assert.Equal(0.9f, face.Score, 3);
        Assert.Equal(3.5f * 32 - 32, face.X, 3);
        Assert.Equal(2.5f * 32 - 32, face.Y, 3);
        Assert.Equal(64, face.Width, 3);
        Assert.Equal((3 * 32f, 2 * 32f), face.Landmarks[0]);
    }

    [Fact]
    public void Alike_faces_become_a_person_and_a_new_face_joins_its_person()
    {
        var random = new Random(3);
        float[] Somebody() => FaceEngine.Normalize(Enumerable.Range(0, 128).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
        float[] Near(float[] centre) => FaceEngine.Normalize(centre.Select(v => v + (float)(random.NextDouble() - 0.5) * 0.04f).ToArray());
        var anna = Somebody();
        var ben = Somebody();
        var faces = new List<GroupFace>
        {
            new(1, null, Near(anna)), new(2, null, Near(anna)), new(3, null, Near(anna)),
            new(4, null, Near(ben)), new(5, null, Near(ben)), // only two: Ben waits
            new(6, 70, Near(anna), Confirmed: true),           // Anna is a person already (number 70)
            new(7, null, Somebody(), Ignored: true),
        };

        var changes = FaceGrouping.Group(faces, [1, 2, 3, 4, 5, 7], new HashSet<(long, long)>());
        Assert.Equal(70, changes[1]);
        Assert.Equal(70, changes[2]);
        Assert.Equal(70, changes[3]);
        Assert.False(changes.ContainsKey(4));
        Assert.False(changes.ContainsKey(7));

        // a third face of Ben: now he is a new person (a negative number), all three of them
        faces.Add(new GroupFace(8, null, Near(ben)));
        var next = FaceGrouping.Group(faces, [8], new HashSet<(long, long)>());
        Assert.Equal(3, next.Count);
        Assert.True(next[8] < 0);
        Assert.Equal(next[8], next[4]);
        Assert.Equal(next[8], next[5]);

        // a face that is "not Anna" does not go to her
        var rejected = FaceGrouping.Group([new(1, null, Near(anna)), new(6, 70, Near(anna))], [1], new HashSet<(long, long)> { (1, 70) });
        Assert.Empty(rejected);
    }

    [Fact]
    public void A_picture_without_a_face_has_none()
    {
        using var engine = new FaceEngine(Models);
        if (!engine.IsAvailable)
        {
            return; // the models are not there (node scripts/models.js)
        }

        using var image = new MagickImage("plasma:#ff8040-#2040a0", 800, 600);
        image.Format = MagickFormat.Jpeg;
        Assert.Empty(engine.Analyze(image.ToByteArray()));
    }

    [Fact]
    public void Probe()
    {
        var folder = Environment.GetEnvironmentVariable("MATPHOTO_FACE_PROBE");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        using var engine = new FaceEngine(Models);
        Assert.True(engine.IsAvailable, "the models are missing");
        var target = Path.Combine(folder, "out");
        Directory.CreateDirectory(target);
        var all = new List<(string Name, FoundFace Face)>();
        foreach (var file in Directory.GetFiles(folder, "*.jpg").Order())
        {
            var started = DateTime.UtcNow;
            var faces = engine.Analyze(File.ReadAllBytes(file));
            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            var name = Path.GetFileNameWithoutExtension(file)[..8];
            output.WriteLine($"{name}: {faces.Count} faces in {ms:0} ms  " + string.Join("  ", faces.Select(f => $"{f.Score:0.00}@{f.X:0.00},{f.Y:0.00} {f.Width:0.00}")));
            using var image = new MagickImage(file);
            image.AutoOrient();
            var drawables = new Drawables().StrokeColor(MagickColors.Magenta).StrokeWidth(4).FillColor(MagickColors.Transparent);
            for (var i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                drawables.Rectangle(f.X * image.Width, f.Y * image.Height, (f.X + f.Width) * image.Width, (f.Y + f.Height) * image.Height);
                drawables.FontPointSize(40).Text(f.X * image.Width, f.Y * image.Height - 6, $"{name[..3]}{i}");
                all.Add(($"{name[..3]}{i}", f));
            }

            drawables.Draw(image);
            image.Resize(1000, 1000);
            image.Write(Path.Combine(target, name + ".jpg"));
        }

        output.WriteLine("pairs with a cosine of 0.30 or more:");
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                var cos = all[i].Face.Embedding.Zip(all[j].Face.Embedding, (x, y) => x * y).Sum();
                if (cos >= 0.30f)
                {
                    output.WriteLine($"  {all[i].Name} ~ {all[j].Name}: {cos:0.000}");
                }
            }
        }
    }
}
