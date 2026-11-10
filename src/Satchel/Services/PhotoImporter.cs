using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>
/// Turns image files into gallery photos: upright (EXIF orientation applied), at most 2048 px, a small thumbnail,
/// the date the photo was taken, and a guess whether it's a screenshot. Safe to run on a worker thread.
/// </summary>
public static class PhotoImporter
{
    public const int MaxSize = 2048;
    public const int ThumbSize = 420;

    public static Photo? FromFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var info = new FileInfo(path);
            return FromBytes(bytes, Path.GetFileName(path), info.LastWriteTime);
        }
        catch
        {
            return null;
        }
    }

    public static Photo? FromBytes(byte[] bytes, string fileName, DateTime fallbackDate)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
            var taken = fallbackDate;
            int rotation = 0;
            if (frame is BitmapFrame bf && bf.Metadata is BitmapMetadata meta)
            {
                try
                {
                    if (meta.DateTaken is { Length: > 0 } dt &&
                        (DateTime.TryParse(dt, CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) || DateTime.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)))
                        taken = d;
                }
                catch { /* no date */ }
                try
                {
                    var orientation = meta.GetQuery("System.Photo.Orientation");
                    rotation = Convert.ToInt32(orientation) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
                }
                catch { /* no orientation */ }
            }
            if (rotation != 0)
            {
                var rotated = new TransformedBitmap(frame, new RotateTransform(rotation));
                rotated.Freeze();
                frame = rotated;
            }
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
            var name = fileName.ToLowerInvariant();
            bool screenshot = name.Contains("screenshot") || name.Contains("screen shot") || name.Contains("snip") || name.StartsWith("capture") ||
                              (png && (frame.PixelWidth, frame.PixelHeight) is (1920, 1080) or (2560, 1440) or (3840, 2160) or (1366, 768) or (1440, 900) or (2880, 1800));
            var data = Encode(Scale(frame, MaxSize), screenshot && png);
            var thumb = Encode(Scale(frame, ThumbSize), false, quality: 80);
            return new Photo
            {
                FileName = Path.GetFileNameWithoutExtension(fileName),
                Data = data,
                Thumb = thumb,
                Width = frame.PixelWidth,
                Height = frame.PixelHeight,
                Size = data.Length,
                TakenAt = taken,
                IsScreenshot = screenshot
            };
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Scale(BitmapSource source, int max)
    {
        double scale = Math.Min(1.0, (double)max / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource result = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(result, PixelFormats.Bgr24, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>Screenshots stay sharp PNGs; photos become JPEG.</summary>
    private static byte[] Encode(BitmapSource image, bool png, int quality = 88)
    {
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
