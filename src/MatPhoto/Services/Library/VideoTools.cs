using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MatPhoto.Services.Library;

/// <summary>What ffprobe says about a video.</summary>
public sealed record VideoInfo(int Width, int Height, int? DurationMs, string? Codec, DateTime? TakenDate, double? Latitude, double? Longitude, string? Make, string? Model);

/// <summary>
/// ffmpeg and ffprobe (installed in the image): the facts of a video and a frame for its thumbnail. Without them (a development machine) a video gets the facts
/// MetadataExtractor reads and a thumbnail of its own (<see cref="IsAvailable"/> is false); nothing fails because of it.
/// </summary>
public sealed class VideoTools(ILogger<VideoTools> log)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private readonly Lazy<bool> _available = new(() => Probe("ffprobe") && Probe("ffmpeg"));

    public bool IsAvailable => _available.Value;

    /// <summary>The facts of the video file (null when ffprobe is missing or cannot read it).</summary>
    public async Task<VideoInfo?> InfoAsync(string file, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var (code, output) = await RunAsync("ffprobe", ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file], ct);
        if (code != 0 || output.Length == 0)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            var video = root.TryGetProperty("streams", out var streams)
                ? streams.EnumerateArray().FirstOrDefault(s => s.TryGetProperty("codec_type", out var t) && t.GetString() == "video")
                : default;
            int width = 0, height = 0;
            string? codec = null;
            if (video.ValueKind == JsonValueKind.Object)
            {
                width = video.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                height = video.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                codec = video.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (Rotation(video) is 90 or 270 or -90 or -270)
                {
                    (width, height) = (height, width);
                }
            }

            int? duration = null;
            DateTime? taken = null;
            double? lat = null, lon = null;
            string? make = null, model = null;
            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    duration = (int)Math.Round(seconds * 1000);
                }

                if (format.TryGetProperty("tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateObject())
                    {
                        var value = tag.Value.GetString();
                        switch (tag.Name.ToLowerInvariant())
                        {
                            case "com.apple.quicktime.creationdate" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local):
                                taken = local.DateTime;
                                break;
                            case "creation_time" when taken is null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc) && utc.Year > 1971:
                                taken = MetadataReader.ToLocal(utc);
                                break;
                            case "location" or "com.apple.quicktime.location.iso6709" or "location-eng" when MetadataReader.ParseIso6709(value) is { } position:
                                (lat, lon) = position;
                                break;
                            case "com.apple.quicktime.make" or "make":
                                make = value;
                                break;
                            case "com.apple.quicktime.model" or "model":
                                model = value;
                                break;
                        }
                    }
                }
            }

            return new VideoInfo(width, height, duration, codec, taken is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Unspecified) : null, lat, lon, make, model);
        }
        catch (JsonException ex)
        {
            log.LogDebug(ex, "ffprobe answered something that is not JSON.");
            return null;
        }
    }

    /// <summary>One frame of the video as a JPEG (at one second, or at the start of a shorter video); null when ffmpeg is missing or fails.</summary>
    public async Task<byte[]?> FrameAsync(string file, int? durationMs, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var at = durationMs is > 2000 ? "1" : "0";
        var output = Path.Combine(Path.GetTempPath(), $"matphoto-frame-{Guid.NewGuid():n}.jpg");
        try
        {
            var (code, _) = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss", at, "-i", file, "-frames:v", "1", "-q:v", "3", "-y", output], ct);
            return code == 0 && File.Exists(output) ? await File.ReadAllBytesAsync(output, ct) : null;
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate) && int.TryParse(rotate.GetString(), out var r))
        {
            return r;
        }

        if (stream.TryGetProperty("side_data_list", out var side))
        {
            foreach (var item in side.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetInt32(out var value))
                {
                    return value;
                }
            }
        }

        return 0;
    }

    private static bool Probe(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{tool} did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await errors;
        return (process.ExitCode, await output);
    }
}
