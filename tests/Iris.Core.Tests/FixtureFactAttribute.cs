using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// A test that needs a saved RSI page. When the page has not been captured yet the test is SKIPPED (not failed).
/// Capture the pages with:  dotnet run --project tools\Iris.CaptureFixtures
/// They are stored in tests/Iris.Core.Tests/Fixtures/rsi/ (git-ignored: they are copies of third-party pages).
/// </summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string file)
    {
        if (!File.Exists(FixturePath(file)))
            Skip = $"fixture {file} missing: run tools/Iris.CaptureFixtures";
    }

    /// <summary>Where the build copies the fixture files to (next to the test assembly).</summary>
    public static string FixturePath(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rsi", file);

    public static string Read(string file) => File.ReadAllText(FixturePath(file));
}
