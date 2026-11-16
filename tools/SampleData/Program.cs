using ImageMagick;
using ImageMagick.Drawing;

// The sample photos of the local stack: three events in album folders (Hamburg, Lake Garda, a Christmas without GPS), an inbox for the import (two days in
// Munich and Berlin, screenshots, a photo that is in an album already), a resized copy (a similar photo) and a panorama. Every photo has the date, the camera and
// (most) the position in its EXIF, like a phone writes them.
//   dotnet run --project tools/SampleData [-- <folder>]        (default: ./sample-data)
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "sample-data");
Directory.CreateDirectory(root);
var random = new Random(42);
var made = 0;

void Photo(string folder, string name, DateTime taken, (double Lat, double Lon)? where, string make = "Apple", string model = "iPhone 15 Pro", int width = 1600, int height = 1200,
    string? lens = null)
{
    var dir = Path.Combine(root, folder);
    Directory.CreateDirectory(dir);
    using var image = new MagickImage($"plasma:{Colour()}-{Colour()}", (uint)width, (uint)height);
    image.Draw(new Drawables()
        .FontPointSize(Math.Max(28, height / 14))
        .FillColor(MagickColors.White)
        .StrokeColor(new MagickColor(0, 0, 0, 120))
        .StrokeWidth(2)
        .Gravity(Gravity.South)
        .Text(0, height / 12.0, $"{folder.Split('/').Last()} · {taken:yyyy-MM-dd HH:mm}"));
    var exif = new ExifProfile();
    exif.SetValue(ExifTag.Make, make);
    exif.SetValue(ExifTag.Model, model);
    exif.SetValue(ExifTag.DateTimeOriginal, taken.ToString("yyyy:MM:dd HH:mm:ss"));
    exif.SetValue(ExifTag.DateTimeDigitized, taken.ToString("yyyy:MM:dd HH:mm:ss"));
    exif.SetValue(ExifTag.FNumber, new Rational(18, 10));
    exif.SetValue(ExifTag.ExposureTime, new Rational(1, (uint)random.Next(60, 1000)));
    exif.SetValue(ExifTag.ISOSpeedRatings, [(ushort)random.Next(50, 800)]);
    exif.SetValue(ExifTag.FocalLength, new Rational(686, 100));
    if (lens is not null)
    {
        exif.SetValue(ExifTag.LensModel, lens);
    }

    if (where is { } p)
    {
        exif.SetValue(ExifTag.GPSLatitudeRef, p.Lat >= 0 ? "N" : "S");
        exif.SetValue(ExifTag.GPSLatitude, Dms(Math.Abs(p.Lat)));
        exif.SetValue(ExifTag.GPSLongitudeRef, p.Lon >= 0 ? "E" : "W");
        exif.SetValue(ExifTag.GPSLongitude, Dms(Math.Abs(p.Lon)));
    }

    image.SetProfile(exif);
    image.Quality = 78;
    image.Write(Path.Combine(dir, name));
    made++;
}

void Screenshot(string folder, string name, DateTime when)
{
    var dir = Path.Combine(root, folder);
    Directory.CreateDirectory(dir);
    using var image = new MagickImage(new MagickColor("#f4f2ef"), 590, 1278);
    image.Draw(new Drawables()
        .FillColor(new MagickColor("#b81f8a")).Rectangle(0, 0, 590, 120)
        .FillColor(MagickColors.White).FontPointSize(40).Text(30, 80, "Screenshot")
        .FillColor(new MagickColor("#555")).FontPointSize(28).Text(30, 220, when.ToString("dd.MM.yyyy HH:mm")));
    image.Write(Path.Combine(dir, name), MagickFormat.Png);
    made++;
}

Rational[] Dms(double value)
{
    var degrees = Math.Floor(value);
    var minutes = Math.Floor((value - degrees) * 60);
    var seconds = (value - degrees - minutes / 60) * 3600;
    return [new Rational((uint)degrees, 1), new Rational((uint)minutes, 1), new Rational((uint)Math.Round(seconds * 100), 100)];
}

string Colour() => $"#{random.Next(0x202020, 0xEFEFEF):x6}";

// an album: a wedding in Hamburg
var hamburg = (53.5511, 9.9937);
var wedding = new DateTime(2023, 11, 9, 13, 30, 0);
for (var i = 0; i < 10; i++)
{
    Photo("2023.11.09 Hochzeit Julius", $"IMG_{4100 + i}.JPG", wedding.AddMinutes(i * 17), hamburg, lens: i == 3 ? "iPhone 15 Pro front camera 2.69mm f/1.9" : null);
}

// an album below a year folder: Lake Garda, with a panorama and a resized copy of a photo (similar)
var garda = (45.6389, 10.7181);
var holiday = new DateTime(2024, 7, 14, 10, 0, 0);
for (var i = 0; i < 12; i++)
{
    Photo("2024/2024.07.14 Gardasee", $"PXL_20240714_{100000 + i * 731:D6}.jpg", holiday.AddHours(i * 5), (garda.Item1 + i * 0.002, garda.Item2), "Google", "Pixel 8");
}

Photo("2024/2024.07.14 Gardasee", "PANO_20240715_180000.jpg", holiday.AddDays(1).AddHours(8), garda, "Google", "Pixel 8", 3600, 1000);
using (var source = new MagickImage(Path.Combine(root, "2024/2024.07.14 Gardasee", "PXL_20240714_100000.jpg")))
{
    source.Resize(800, 600);
    source.Quality = 60;
    source.Write(Path.Combine(root, "2024/2024.07.14 Gardasee", "PXL_20240714_100000 (klein).jpg"));
    made++;
}

// an album without positions: Christmas
var christmas = new DateTime(2022, 12, 24, 17, 0, 0);
for (var i = 0; i < 6; i++)
{
    Photo("2022.12.24 Weihnachten", $"DSC0{1200 + i}.JPG", christmas.AddMinutes(i * 25), null, "SONY", "ILCE-7M3");
}

// the inbox: two days in Munich and in Berlin, screenshots, and a photo that is in an album already
var munich = (48.1374, 11.5755);
var berlin = (52.5200, 13.4050);
for (var i = 0; i < 8; i++)
{
    Photo("Inbox", $"IMG_20250503_{120000 + i * 1500:D6}.jpg", new DateTime(2025, 5, 3, 12, 0, 0).AddMinutes(i * 15), munich, "samsung", "Galaxy S24");
}

for (var i = 0; i < 7; i++)
{
    Photo("Inbox", $"IMG_20250621_{180000 + i * 1000:D6}.jpg", new DateTime(2025, 6, 21, 18, 0, 0).AddMinutes(i * 10), berlin, "samsung", "Galaxy S24");
}

Screenshot("Inbox", "Screenshot_20250504-091500.png", new DateTime(2025, 5, 4, 9, 15, 0));
Screenshot("Inbox", "Screenshot_20250622-221000.png", new DateTime(2025, 6, 22, 22, 10, 0));
File.Copy(Path.Combine(root, "2023.11.09 Hochzeit Julius", "IMG_4100.JPG"), Path.Combine(root, "Inbox", "IMG_4100 Kopie.JPG"), overwrite: true);
made++;

Console.WriteLine($"{made} sample files in {root}");
