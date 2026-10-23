using OpenCvSharp;
using Xunit;

namespace Iris.Core.Tests;

/// <summary>
/// A test that needs the native OpenCV library. The tests project carries the Windows library (OpenCvSharp4.runtime.win), so on
/// other systems the test is SKIPPED (not failed).
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
        try
        {
            using var probe = new Mat(1, 1, MatType.CV_8UC1);
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            return "native OpenCV library is not available on this system (the tests project carries the Windows one)";
        }
    }
}
