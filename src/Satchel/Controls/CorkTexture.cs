using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Satchel.Controls;

/// <summary>Procedurally generated, seamlessly tiling cork and wood textures (no image assets needed).</summary>
public static class CorkTexture
{
    private const int Size = 320;
    private static ImageBrush? _cork;
    private static Brush? _wood;

    public static ImageBrush Cork => _cork ??= CreateCork();
    public static Brush Wood => _wood ??= CreateWood();

    private static ImageBrush CreateCork()
    {
        var rng = new Random(20261109);
        // Two octaves of periodic value noise for soft blotches.
        var lattice1 = Lattice(rng, 10);
        var lattice2 = Lattice(rng, 40);
        var pixels = new byte[Size * Size * 4];
        var baseColor = (R: 196.0, G: 150.0, B: 102.0);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double n = Sample(lattice1, 10, x, y) * 0.65 + Sample(lattice2, 40, x, y) * 0.35; // 0..1
                double grain = (rng.NextDouble() - 0.5) * 22;
                double shade = (n - 0.5) * 34 + grain;
                int i = (y * Size + x) * 4;
                pixels[i + 2] = Clamp(baseColor.R + shade * 1.0);
                pixels[i + 1] = Clamp(baseColor.G + shade * 0.92);
                pixels[i + 0] = Clamp(baseColor.B + shade * 0.78);
                pixels[i + 3] = 255;
            }
        }
        // Granules: dark pits and light flecks, wrapped around the edges so the tile is seamless.
        for (int k = 0; k < 1500; k++) Granule(pixels, rng, dark: true);
        for (int k = 0; k < 700; k++) Granule(pixels, rng, dark: false);

        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, Size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Size, Size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private static void Granule(byte[] pixels, Random rng, bool dark)
    {
        int cx = rng.Next(Size), cy = rng.Next(Size);
        double r = dark ? 0.6 + rng.NextDouble() * 1.6 : 0.5 + rng.NextDouble() * 1.1;
        double strength = dark ? -(30 + rng.NextDouble() * 45) : 18 + rng.NextDouble() * 26;
        int ir = (int)Math.Ceiling(r);
        for (int dy = -ir; dy <= ir; dy++)
        {
            for (int dx = -ir; dx <= ir; dx++)
            {
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                double f = 1 - d / (r + 0.4);
                int x = (cx + dx + Size) % Size, y = (cy + dy + Size) % Size;
                int i = (y * Size + x) * 4;
                pixels[i + 2] = Clamp(pixels[i + 2] + strength * f);
                pixels[i + 1] = Clamp(pixels[i + 1] + strength * f * 0.9);
                pixels[i + 0] = Clamp(pixels[i + 0] + strength * f * 0.75);
            }
        }
    }

    private static double[,] Lattice(Random rng, int cells)
    {
        var l = new double[cells, cells];
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
                l[x, y] = rng.NextDouble();
        return l;
    }

    private static double Sample(double[,] lattice, int cells, int px, int py)
    {
        double fx = (double)px / Size * cells, fy = (double)py / Size * cells;
        int x0 = (int)fx, y0 = (int)fy;
        double tx = Smooth(fx - x0), ty = Smooth(fy - y0);
        int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
        x0 %= cells;
        y0 %= cells;
        double a = lattice[x0, y0] + (lattice[x1, y0] - lattice[x0, y0]) * tx;
        double b = lattice[x0, y1] + (lattice[x1, y1] - lattice[x0, y1]) * tx;
        return a + (b - a) * ty;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static Brush CreateWood()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x9A, 0x6B, 0x45), 0),
                new GradientStop(Color.FromRgb(0x7A, 0x51, 0x32), 0.35),
                new GradientStop(Color.FromRgb(0x8E, 0x60, 0x3D), 0.6),
                new GradientStop(Color.FromRgb(0x6B, 0x45, 0x2A), 1)
            }
        };
        brush.Freeze();
        return brush;
    }
}
