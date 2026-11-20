using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MatPhoto.Services.Faces;

/// <summary>A face found on a picture: its box relative to the picture (0 to 1), how sure the detector was, and its features (128 numbers of length 1).</summary>
public sealed record FoundFace(float X, float Y, float Width, float Height, float Score, float[] Embedding);

/// <summary>A face as the detector sees it, in pixels of the picture it looked at: the box and five points (the eyes, the nose, the corners of the mouth).</summary>
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score, (float X, float Y)[] Landmarks);

/// <summary>
/// The recognition of faces, on this server and on the CPU, with two small models of the OpenCV model zoo (ONNX Runtime):
/// <list type="bullet">
/// <item><b>YuNet</b> (MIT, 0.2 MB) finds the faces of a picture scaled into 640 × 640 pixels, with five points of each;</item>
/// <item><b>SFace</b> (Apache 2.0, 37 MB) turns a face – turned and scaled by its five points onto the template of 112 × 112 pixels – into 128 numbers; two faces of
/// the same person have a cosine of 0.363 or more.</item>
/// </list>
/// The models lie in the folder <c>models</c> beside the app (the image downloads them, <c>scripts/models.js</c> for a developer); without them the recognition is
/// not available. The sessions are made once and used by every thread.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    public const string DetectorFile = "face_detection_yunet_2023mar.onnx";
    public const string RecognizerFile = "face_recognition_sface_2021dec.onnx";

    /// <summary>The side of the square the detector looks at.</summary>
    public const int DetectorSize = 640;

    /// <summary>The side of the face the recognizer reads.</summary>
    public const int FaceSize = 112;

    /// <summary>Faces the detector is less sure about are left out.</summary>
    public const float MinScore = 0.82f;

    /// <summary>Faces smaller than this (pixels of the preview) are too small to tell who it is.</summary>
    public const int MinFacePixels = 40;

    private static readonly int[] Strides = [8, 16, 32];

    // where the eyes, the nose and the corners of the mouth lie in a face of 112 × 112 (the template of ArcFace that SFace was trained with)
    private static readonly (float X, float Y)[] Template =
        [(38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f)];

    private readonly InferenceSession? _detector;
    private readonly InferenceSession? _recognizer;

    public FaceEngine(string folder)
    {
        Folder = folder;
        var detector = Path.Combine(folder, DetectorFile);
        var recognizer = Path.Combine(folder, RecognizerFile);
        if (!File.Exists(detector) || !File.Exists(recognizer))
        {
            return;
        }

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        _detector = new InferenceSession(detector, options);
        _recognizer = new InferenceSession(recognizer, options);
    }

    /// <summary>The folder the models are looked for in.</summary>
    public string Folder { get; }

    /// <summary>Both models are there.</summary>
    public bool IsAvailable => _detector is not null && _recognizer is not null;

    /// <summary>The faces of a picture (a JPEG preview, upright): where they are and their features. Pictures without faces answer an empty list.</summary>
    public IReadOnlyList<FoundFace> Analyze(byte[] picture)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("The models of the face recognition are missing.");
        }

        using var image = new MagickImage(picture);
        image.AutoOrient();
        var width = (int)image.Width;
        var height = (int)image.Height;
        if (width < MinFacePixels || height < MinFacePixels)
        {
            return [];
        }

        // the detector: the picture scaled into the square (top left, the rest black), as BGR 0..255
        var scale = Math.Min(1f, (float)DetectorSize / Math.Max(width, height));
        const int Plane = DetectorSize * DetectorSize;
        var data = new float[3 * Plane];
        using (var small = image.Clone())
        {
            if (scale < 1f)
            {
                small.Resize((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
            }

            var sw = (int)small.Width;
            var sh = (int)small.Height;
            using var pixels = small.GetPixels();
            var bgr = pixels.ToByteArray(PixelMapping.BGR) ?? [];
            for (var y = 0; y < sh; y++)
            {
                for (var x = 0; x < sw; x++)
                {
                    var i = (y * sw + x) * 3;
                    var p = y * DetectorSize + x;
                    data[p] = bgr[i];
                    data[Plane + p] = bgr[i + 1];
                    data[2 * Plane + p] = bgr[i + 2];
                }
            }
        }

        var input = new DenseTensor<float>(data, [1, 3, DetectorSize, DetectorSize]);

        List<DetectedFace> faces;
        using (var results = _detector!.Run([NamedOnnxValue.CreateFromTensor("input", input)]))
        {
            float[] Output(string name) => results.First(r => r.Name == name).AsTensor<float>().ToArray();
            faces = Decode(Strides.Select(s => (s, Output($"cls_{s}"), Output($"obj_{s}"), Output($"bbox_{s}"), Output($"kps_{s}"))).ToList(), MinScore);
        }

        faces = Suppress(faces, 0.3f);
        if (faces.Count == 0)
        {
            return [];
        }

        // the faces in pixels of the picture: big enough to tell who it is
        var found = new List<FoundFace>();
        byte[]? rgb = null;
        foreach (var f in faces.Take(40))
        {
            var face = new DetectedFace(f.X / scale, f.Y / scale, f.Width / scale, f.Height / scale, f.Score, f.Landmarks.Select(p => (p.X / scale, p.Y / scale)).ToArray());
            if (Math.Min(face.Width, face.Height) < MinFacePixels)
            {
                continue;
            }

            if (rgb is null)
            {
                using var pixels = image.GetPixels();
                rgb = pixels.ToByteArray(PixelMapping.RGB) ?? [];
            }

            var aligned = Align(rgb, width, height, face.Landmarks);
            var embedding = Embed(aligned);
            var x = Math.Clamp(face.X / width, 0f, 1f);
            var y = Math.Clamp(face.Y / height, 0f, 1f);
            found.Add(new FoundFace(x, y, Math.Clamp(face.Width / width, 0f, 1f - x), Math.Clamp(face.Height / height, 0f, 1f - y), face.Score, embedding));
        }

        return found;
    }

    /// <summary>The features of an aligned face (RGB, 112 × 112): the 128 numbers of SFace, made of length 1.</summary>
    private float[] Embed(byte[] aligned)
    {
        const int Plane = FaceSize * FaceSize;
        var data = new float[3 * Plane];
        for (var p = 0; p < Plane; p++)
        {
            data[p] = aligned[p * 3];
            data[Plane + p] = aligned[p * 3 + 1];
            data[2 * Plane + p] = aligned[p * 3 + 2];
        }

        var input = new DenseTensor<float>(data, [1, 3, FaceSize, FaceSize]);
        using var results = _recognizer!.Run([NamedOnnxValue.CreateFromTensor("data", input)]);
        return Normalize(results.First().AsTensor<float>().ToArray());
    }

    /// <summary>
    /// The faces in the outputs of YuNet (2023mar) for the strides 8, 16 and 32: per cell of the grid a score (√(class × object)), the box (centre offset and
    /// log size, in cells) and five points (offsets, in cells); only scores of <paramref name="threshold"/> or more.
    /// </summary>
    internal static List<DetectedFace> Decode(IReadOnlyList<(int Stride, float[] Cls, float[] Obj, float[] Box, float[] Kps)> outputs, float threshold)
    {
        var faces = new List<DetectedFace>();
        foreach (var (stride, cls, obj, box, kps) in outputs)
        {
            var cols = DetectorSize / stride;
            for (var idx = 0; idx < cls.Length; idx++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[idx], 0f, 1f) * Math.Clamp(obj[idx], 0f, 1f));
                if (score < threshold)
                {
                    continue;
                }

                var r = idx / cols;
                var c = idx % cols;
                var cx = (c + box[idx * 4]) * stride;
                var cy = (r + box[idx * 4 + 1]) * stride;
                var w = MathF.Exp(box[idx * 4 + 2]) * stride;
                var h = MathF.Exp(box[idx * 4 + 3]) * stride;
                var points = new (float X, float Y)[5];
                for (var n = 0; n < 5; n++)
                {
                    points[n] = ((kps[idx * 10 + 2 * n] + c) * stride, (kps[idx * 10 + 2 * n + 1] + r) * stride);
                }

                faces.Add(new DetectedFace(cx - w / 2, cy - h / 2, w, h, score, points));
            }
        }

        return faces;
    }

    /// <summary>Of boxes that overlap more than <paramref name="overlap"/> (intersection over union) only the surest stays.</summary>
    internal static List<DetectedFace> Suppress(List<DetectedFace> faces, float overlap)
    {
        var kept = new List<DetectedFace>();
        foreach (var f in faces.OrderByDescending(f => f.Score))
        {
            if (kept.All(k => IntersectionOverUnion(k, f) <= overlap))
            {
                kept.Add(f);
            }
        }

        return kept;
    }

    private static float IntersectionOverUnion(DetectedFace a, DetectedFace b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    /// <summary>
    /// The similarity (turn, scale, shift; no mirror) that brings the five points of a face onto the template, by least squares: <c>x' = a·x − b·y + tx</c>,
    /// <c>y' = b·x + a·y + ty</c>.
    /// </summary>
    internal static (float A, float B, float Tx, float Ty) Similarity((float X, float Y)[] from, (float X, float Y)[] to)
    {
        var n = from.Length;
        float fx = 0, fy = 0, tx = 0, ty = 0;
        for (var i = 0; i < n; i++)
        {
            fx += from[i].X;
            fy += from[i].Y;
            tx += to[i].X;
            ty += to[i].Y;
        }

        fx /= n;
        fy /= n;
        tx /= n;
        ty /= n;
        double dot = 0, cross = 0, norm = 0;
        for (var i = 0; i < n; i++)
        {
            double sx = from[i].X - fx, sy = from[i].Y - fy, dx = to[i].X - tx, dy = to[i].Y - ty;
            dot += sx * dx + sy * dy;
            cross += sx * dy - sy * dx;
            norm += sx * sx + sy * sy;
        }

        if (norm < 1e-9)
        {
            return (1, 0, tx - fx, ty - fy);
        }

        var a = (float)(dot / norm);
        var b = (float)(cross / norm);
        return (a, b, tx - (a * fx - b * fy), ty - (b * fx + a * fy));
    }

    /// <summary>The face turned and scaled onto the template: 112 × 112 RGB, every pixel read back from the picture (bilinear; black outside of it).</summary>
    internal static byte[] Align(byte[] rgb, int width, int height, (float X, float Y)[] landmarks)
    {
        var (a, b, tx, ty) = Similarity(landmarks, Template);
        var det = a * a + b * b;
        var output = new byte[FaceSize * FaceSize * 3];
        for (var v = 0; v < FaceSize; v++)
        {
            for (var u = 0; u < FaceSize; u++)
            {
                // the pixel of the picture this pixel of the face comes from: the inverse of the similarity
                var du = u - tx;
                var dv = v - ty;
                var x = (a * du + b * dv) / det;
                var y = (-b * du + a * dv) / det;
                var x0 = (int)MathF.Floor(x);
                var y0 = (int)MathF.Floor(y);
                var wx = x - x0;
                var wy = y - y0;
                var o = (v * FaceSize + u) * 3;
                for (var ch = 0; ch < 3; ch++)
                {
                    float Pixel(int px, int py) => px < 0 || py < 0 || px >= width || py >= height ? 0 : rgb[(py * width + px) * 3 + ch];
                    var value = Pixel(x0, y0) * (1 - wx) * (1 - wy) + Pixel(x0 + 1, y0) * wx * (1 - wy) + Pixel(x0, y0 + 1) * (1 - wx) * wy + Pixel(x0 + 1, y0 + 1) * wx * wy;
                    output[o + ch] = (byte)Math.Clamp(MathF.Round(value), 0, 255);
                }
            }
        }

        return output;
    }

    /// <summary>The vector made of length 1 (then the dot product of two is their cosine).</summary>
    public static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
        {
            sum += v * v;
        }

        var length = (float)Math.Sqrt(sum);
        return length < 1e-9f ? vector : vector.Select(v => v / length).ToArray();
    }

    public void Dispose()
    {
        _detector?.Dispose();
        _recognizer?.Dispose();
    }
}
