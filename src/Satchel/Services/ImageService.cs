using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Satchel.Services;

/// <summary>Loads, downsizes and encodes images so they stay small inside the database.</summary>
public static class ImageService
{
    public const int AvatarSize = 256;
    public const int PhotoSize = 1400;
    public const int CoverSize = 600;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".webp", ".heic", ".jfif"];

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static byte[]? PickImage(int maxSize = AvatarSize, bool photo = false)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.ico;*.tif;*.tiff;*.webp|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return null;
        return FromFile(dialog.FileName, maxSize, photo);
    }

    public static byte[]? FromFile(string path, int maxSize, bool photo = true)
    {
        try { return Resize(File.ReadAllBytes(path), maxSize, photo); }
        catch { return null; }
    }

    public static byte[]? FromClipboard(int maxSize = AvatarSize, bool photo = false)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile);
                if (file != null && File.Exists(file)) return FromFile(file, maxSize, photo);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource img)
                return Encode(Scale(img, maxSize), photo);
        }
        catch
        {
            // clipboard busy or unsupported format
        }
        return null;
    }

    /// <summary>Image files dropped/pasted via a data object (files first, then bitmap data).</summary>
    public static List<byte[]> FromDataObject(IDataObject data, int maxSize, bool photo = true)
    {
        var result = new List<byte[]>();
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files.Where(IsImageFile))
                    if (FromFile(f, maxSize, photo) is { } bytes) result.Add(bytes);
                if (result.Count > 0) return result;
            }
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
                result.Add(Encode(Scale(bmp, maxSize), photo));
            else if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                if (Resize(png.ToArray(), maxSize, photo) is { } bytes) result.Add(bytes);
            }
        }
        catch
        {
            // unsupported or corrupt data — ignore
        }
        return result;
    }

    public static bool HasImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files) return files.Any(IsImageFile);
            return data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG");
        }
        catch { return false; }
    }

    /// <summary>Downsizes to <paramref name="maxSize"/> px on the longest side. Photos become JPEG unless they use transparency.</summary>
    public static byte[]? Resize(byte[] data, int maxSize, bool photo = false)
    {
        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // Icons contain several frames; take the largest.
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        return Encode(Scale(frame, maxSize), photo);
    }

    public static byte[] FromBitmap(BitmapSource source, int maxSize, bool photo = true) => Encode(Scale(source, maxSize), photo);

    /// <summary>Decodes stored bytes into a frozen bitmap (optionally decoded at a smaller width).</summary>
    public static BitmapSource? ToBitmap(byte[]? data, int decodeWidth = 0)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.StreamSource = new MemoryStream(data);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static Size? PixelSize(byte[]? data) => ToBitmap(data) is { } b ? new Size(b.PixelWidth, b.PixelHeight) : null;

    private static BitmapSource Scale(BitmapSource source, int maxSize)
    {
        double scale = Math.Min(1.0, (double)maxSize / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource result = source;
        if (scale < 1.0)
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            result = scaled;
        }
        var converted = new FormatConvertedBitmap(result, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static byte[] Encode(BitmapSource image, bool photo)
    {
        BitmapEncoder encoder = photo && !HasTransparency(image)
            ? new JpegBitmapEncoder { QualityLevel = 86 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static bool HasTransparency(BitmapSource image)
    {
        if (image.Format != PixelFormats.Pbgra32 && image.Format != PixelFormats.Bgra32) return false;
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 250) return true;
        return false;
    }

    // kept for callers that only need the file list type
    internal static StringCollection? FileList() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null;
}
