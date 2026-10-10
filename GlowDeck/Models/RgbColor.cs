using System.Globalization;

namespace GlowDeck.Models;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public uint ToOpenRgbValue() =>
        (uint)(R | (G << 8) | (B << 16));

    public RgbColor Scale(double amount)
    {
        var scale = Math.Clamp(amount, 0d, 1d);
        return new(
            (byte)Math.Round(R * scale),
            (byte)Math.Round(G * scale),
            (byte)Math.Round(B * scale));
    }

    public static RgbColor Lerp(RgbColor from, RgbColor to, double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);
        return new(
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 1d) + 1d) % 1d;
        var s = Math.Clamp(saturation, 0d, 1d);
        var v = Math.Clamp(value, 0d, 1d);
        var chroma = v * s;
        var section = h * 6d;
        var intermediate = chroma * (1d - Math.Abs((section % 2d) - 1d));
        var (r, g, b) = section switch
        {
            < 1d => (chroma, intermediate, 0d),
            < 2d => (intermediate, chroma, 0d),
            < 3d => (0d, chroma, intermediate),
            < 4d => (0d, intermediate, chroma),
            < 5d => (intermediate, 0d, chroma),
            _ => (chroma, 0d, intermediate)
        };
        var match = v - chroma;
        return new(
            (byte)Math.Round((r + match) * 255d),
            (byte)Math.Round((g + match) * 255d),
            (byte)Math.Round((b + match) * 255d));
    }

    public static bool TryParse(string? value, out RgbColor color)
    {
        color = default;
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length != 6 ||
            !byte.TryParse(text.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(text.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(text.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return false;
        }

        color = new(red, green, blue);
        return true;
    }
}
