using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// A test that needs the REAL line crops of the underscore / hyphen parity fixtures (<c>training-data/fixtures-us</c>: one PNG and one JSON per case). Those crops are cut from game footage
/// (the test character's own name and contact codes), so they are NOT in the repository. The folder is named by the environment variable <see cref="Variable"/>; the test is SKIPPED (not failed)
/// when the variable is not set, the folder is missing, or the native OpenCV library is not available (the same rule as <see cref="LocalO0FixtureFactAttribute"/>).
/// </summary>
public sealed class LocalUsFixtureFactAttribute : FactAttribute
{
    public const string Variable = "IRIS_US_FIXTURES";

    public LocalUsFixtureFactAttribute()
    {
        var folder = Folder;
        if (string.IsNullOrWhiteSpace(folder))
            Skip = $"local only: set {Variable} to the training kit's fixtures-us folder to run this test";
        else if (!Directory.Exists(folder))
            Skip = $"local only: {Variable} points to a folder that does not exist ({folder})";
        else if (OpenCvFactAttribute.NativeLibraryProblem is { } problem)
            Skip = problem;
    }

    /// <summary>The folder named by the environment variable, or null.</summary>
    public static string? Folder => Environment.GetEnvironmentVariable(Variable);
}
