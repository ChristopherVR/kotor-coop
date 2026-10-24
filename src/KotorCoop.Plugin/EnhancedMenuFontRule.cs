using System.Globalization;

namespace KotorCoop.Plugin;

/// <summary>Original TXI metrics remain the source of truth, including fonts the vendor skips.</summary>
internal static class EnhancedMenuFontRule
{
    internal readonly record struct Metrics(float Height, float Baseline, float Width, float Right, float Bottom)
    {
        public Metrics Scale(float factor) => new(Height * factor, Baseline * factor,
            Width * factor, Right * factor, Bottom * factor);

        public bool IsValid => float.IsFinite(Height) && Height is > 0 and <= 8
            && float.IsFinite(Baseline) && Baseline is >= 0 and <= 8
            && float.IsFinite(Width) && Width is > 0 and <= 128
            && float.IsFinite(Right) && Math.Abs(Right) <= 8
            && float.IsFinite(Bottom) && Math.Abs(Bottom) <= 8;
    }

    public static bool TryParse(ReadOnlySpan<char> text, out int characters, out Metrics metrics)
    {
        characters = 0;
        metrics = default;
        float height = 0, baseline = 0, width = 0, right = 0, bottom = 0;
        int found = 0;
        while (!text.IsEmpty)
        {
            int newline = text.IndexOf('\n');
            ReadOnlySpan<char> line = (newline < 0 ? text : text[..newline]).Trim();
            text = newline < 0 ? [] : text[(newline + 1)..];
            int separator = line.IndexOfAny(' ', '\t');
            if (separator < 0) continue;
            ReadOnlySpan<char> key = line[..separator], value = line[(separator + 1)..].Trim();
            if (key.Equals("numchars", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out characters)) return false;
                found |= 1;
                continue;
            }
            int field = key.Equals("fontheight", StringComparison.OrdinalIgnoreCase) ? 2
                : key.Equals("baselineheight", StringComparison.OrdinalIgnoreCase) ? 4
                : key.Equals("texturewidth", StringComparison.OrdinalIgnoreCase) ? 8
                : key.Equals("spacingR", StringComparison.OrdinalIgnoreCase) ? 16
                : key.Equals("spacingB", StringComparison.OrdinalIgnoreCase) ? 32 : 0;
            if (field == 0) continue;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)) return false;
            switch (field)
            {
                case 2: height = number; break;
                case 4: baseline = number; break;
                case 8: width = number; break;
                case 16: right = number; break;
                case 32: bottom = number; break;
            }
            found |= field;
        }
        metrics = new(height, baseline, width, right, bottom);
        return (found & 15) == 15 && characters is > 0 and <= 512 && metrics.IsValid;
    }

    public static bool HasUniformScale(Metrics original, Metrics current)
    {
        if (!original.IsValid || !current.IsValid) return false;
        float factor = current.Height / original.Height;
        if (!float.IsFinite(factor) || factor is < 0.25f or > 8) return false;
        Metrics expected = original.Scale(factor);
        return Near(expected.Baseline, current.Baseline) && Near(expected.Width, current.Width)
            && Near(expected.Right, current.Right) && Near(expected.Bottom, current.Bottom);
    }

    private static bool Near(float expected, float actual) =>
        Math.Abs(expected - actual) <= Math.Max(0.00001f, Math.Abs(expected) * 0.0001f);
}
