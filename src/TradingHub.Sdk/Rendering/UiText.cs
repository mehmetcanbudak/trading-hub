using System.Collections.Concurrent;
using SkiaSharp;

namespace TradingHub.Rendering
{
    /// <summary>
    /// Translates Skia-rendered UI text and selects a system font when the requested font lacks its glyphs.
    /// The application supplies the translator; without one, text is preserved.
    /// </summary>
    public static class UiText
    {
        /// <summary>Application-supplied display translator; null leaves source text unchanged.</summary>
        public static Func<string, string?>? Translate { get; set; }

        private static readonly ConcurrentDictionary<(string Family, SKFontStyleWeight Weight, int Char), SKTypeface?> Fallbacks = new();
        private static readonly ConcurrentDictionary<(SKTypeface Typeface, float Size, bool Embolden, SKFontEdging Edging), SKFont> Fonts = new();

        /// <summary>Translated display text, or the original when no translation exists.</summary>
        public static string Tr(string text) => Translate is { } t && !string.IsNullOrEmpty(text) && t(text) is { } r ? r : text;

        /// <summary>Draws translated text with system-font fallback for missing glyphs.</summary>
        public static void DrawUiText(this SKCanvas canvas, string? text, float x, float y, SKFont font, SKPaint paint)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = Tr(text);
            canvas.DrawText(text, x, y, FontFor(text, font) ?? font, paint);
        }

        /// <summary>Measures the same translated text and font used by <see cref="DrawUiText"/>.</summary>
        public static float MeasureUiText(this SKFont font, string? text) =>
            string.IsNullOrEmpty(text) ? 0 : Context.RenderContext.MeasureWidth(font, text);

        /// <summary>
        /// Returns a cached system font for missing glyphs, or null when the requested font already supports the text.
        /// </summary>
        public static SKFont? FontFor(string text, SKFont font)
        {
            var typeface = font.Typeface ?? SKTypeface.Default;
            int missing = FirstMissing(text, typeface);
            if (missing < 0) return null;
            var key = (typeface.FamilyName ?? "", (SKFontStyleWeight)typeface.FontWeight, missing);
            var other = Fallbacks.GetOrAdd(key, k => SKFontManager.Default.MatchCharacter(k.Family, k.Weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null, k.Char));
            if (other == null) return null;
            return Fonts.GetOrAdd((other, font.Size, font.Embolden, font.Edging), static (k, source) =>
                new SKFont(k.Typeface, k.Size) { Edging = k.Edging, Subpixel = source.Subpixel, Hinting = source.Hinting, Embolden = k.Embolden }, font);
        }

        // First unsupported CJK/non-BMP character, or -1 when no fallback is required.
        private static int FirstMissing(string text, SKTypeface typeface)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x2E80) continue; // Latin text, accents and common symbols are supported by the requested families.
                int cp = char.IsHighSurrogate(c) && i + 1 < text.Length ? char.ConvertToUtf32(c, text[++i]) : c;
                if (typeface.GetGlyph(cp) == 0) return cp;
            }

            return -1;
        }
    }
}
