using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// A test that needs the native OpenCV library. The tests project carries the Windows library (OpenCvSharp4.runtime.win), so on
/// other systems the test is SKIPPED (not failed). On Windows the test is never skipped: a broken native OpenCV there must fail loudly.
/// </summary>
public sealed class OpenCvFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Problem = new(CheckNativeLibrary);

    public OpenCvFactAttribute()
    {
        var problem = Problem.Value;
        if (problem is not null)
            Skip = problem;
    }

    private static string? CheckNativeLibrary()
    {
        if (OperatingSystem.IsWindows())
            return null; // do not probe: if the library is broken, the test itself fails with the real error

        try
        {
            using var probe = new Mat(1, 1, MatType.CV_8UC1);
            return null;
        }
        catch (Exception e)
        {
            return $"native OpenCV library is not available on this system (the tests project carries the Windows one): {e.GetType().Name}: {e.Message}";
        }
    }
}
