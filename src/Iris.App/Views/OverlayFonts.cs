using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Iris.App.Views;

/// <summary>
/// The font of the overlay text: Quantico Bold from the app's resources (Assets\Fonts\Quantico-Bold.ttf, see README "Credits"),
/// or Segoe UI Bold when that file is not part of the build or cannot be read. A missing or damaged font is never an error.
/// </summary>
internal static class OverlayFonts
{
    private const string QuanticoFamilyUri = "pack://application:,,,/Assets/Fonts/#Quantico";
    private const string FallbackFamily = "Segoe UI";

    private static Typeface? _bold;

    /// <summary>True when the overlay found and uses Quantico.</summary>
    public static bool UsingQuantico { get; private set; }

    public static Typeface Bold => _bold ??= Load();

    private static Typeface Load()
    {
        try
        {
            var quantico = new Typeface(new FontFamily(QuanticoFamilyUri), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            // WPF does not fail for a missing font family: it silently shows its default font. So check what it really found.
            if (quantico.TryGetGlyphTypeface(out var glyphs)
                && glyphs.FamilyNames.Values.Any(name => name.IndexOf("Quantico", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                UsingQuantico = true;
                return quantico;
            }
        }
        catch (Exception)
        {
            // Any failure (a missing or damaged resource: IOException, FormatException / FileFormatException, UriFormatException, ...) means the
            // same: use the fallback font. The overlay must never fail because of its font.
        }

        UsingQuantico = false;
        return new Typeface(new FontFamily(FallbackFamily), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    }
}
