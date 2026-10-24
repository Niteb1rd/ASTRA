using System.Text.RegularExpressions;
using OpenCvSharp;

namespace Iris.Replay;

/// <summary>One frame to run the detector on. The consumer disposes <see cref="Image"/>.</summary>
/// <param name="Clip">The clip (video file name, or folder name) the frame belongs to.</param>
/// <param name="Number">Frame number: the video frame (counted from 1), or the number in the image file name (f00041.jpg is 41; without digits: its position).</param>
/// <param name="Name">What to call the frame: the image file name, or clip#number for a video frame.</param>
/// <param name="TimeMs">Where the frame is in the clip, in milliseconds: from the video's frame rate, or for image files from the
/// frame number and the rate given to <see cref="FrameSources.Frames"/> (the tracker needs time, not only order).</param>
public readonly record struct SourceFrame(string Clip, int Number, string Name, Mat Image, double TimeMs = 0.0);

/// <summary>
/// Keeps frames by TIME: a frame is kept when its timestamp has advanced at least 1000/N ms since the last kept one (the first frame is
/// always kept). With a 60 or 120 fps video and N = 5 this reads every 12th or 24th frame, as the app does (it reads 5 times a second);
/// without a rate every frame is kept. One sampler per clip. Not thread safe.
/// </summary>
public sealed class FrameSampler
{
    // A frame that is due only by the rounding of 1000/fps in floating point (199.99999999 of 200 ms) still counts as due.
    private const double ToleranceMs = 0.001;

    private readonly double _intervalMs;
    private double _lastKeptMs = double.NaN;

    /// <summary>A sampler for <paramref name="fps"/> frames a second; null keeps every frame.</summary>
    public FrameSampler(double? fps)
    {
        if (fps is not null && !(fps.Value > 0))
            throw new ArgumentOutOfRangeException(nameof(fps), "The frame rate must be above 0.");
        _intervalMs = fps is null ? 0.0 : 1000.0 / fps.Value;
    }

    /// <summary>True when a frame at <paramref name="timeMs"/> is to be kept (it does not change the sampler: call <see cref="Kept"/> when the frame was used).</summary>
    public bool Due(double timeMs) =>
        _intervalMs <= 0.0 || double.IsNaN(_lastKeptMs) || timeMs - _lastKeptMs >= _intervalMs - ToleranceMs;

    /// <summary>Notes that the frame at <paramref name="timeMs"/> was kept: the next one is due 1000/N ms after it.</summary>
    public void Kept(double timeMs) => _lastKeptMs = timeMs;
}

/// <summary>A clip to replay: a video file, or a list of image files in order.</summary>
public sealed record ClipInput(string Clip, string? VideoPath, IReadOnlyList<string> Images);

/// <summary>Finds the clips of an input path and reads their frames.</summary>
public static class FrameSources
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) => HasExtension(path, VideoExtensions);

    public static bool IsImage(string path) => HasExtension(path, ImageExtensions);

    /// <summary>
    /// A video file is one clip; a single image is one clip; a folder gives one clip for its images (named after the folder), one
    /// for each video in it, and one for each sub folder that has images. Throws FileNotFoundException when there is nothing at
    /// <paramref name="input"/>, and InvalidOperationException when there are no frames in it.
    /// </summary>
    public static IReadOnlyList<ClipInput> Discover(string input)
    {
        var clips = new List<ClipInput>();
        if (File.Exists(input))
        {
            var name = Path.GetFileNameWithoutExtension(input);
            if (IsVideo(input))
                clips.Add(new ClipInput(name, input, Array.Empty<string>()));
            else if (IsImage(input))
                clips.Add(new ClipInput(name, null, new[] { input }));
            else
                throw new InvalidOperationException($"{input} is neither a video nor an image.");
            return clips;
        }

        if (!Directory.Exists(input))
            throw new FileNotFoundException($"Nothing at {input}.");

        var full = Path.GetFullPath(input);
        var images = ImagesIn(full);
        if (images.Count > 0)
            clips.Add(new ClipInput(new DirectoryInfo(full).Name, null, images));
        foreach (var video in Directory.EnumerateFiles(full).Where(IsVideo).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            clips.Add(new ClipInput(Path.GetFileNameWithoutExtension(video), video, Array.Empty<string>()));
        foreach (var folder in Directory.EnumerateDirectories(full).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var inside = ImagesIn(folder);
            if (inside.Count > 0)
                clips.Add(new ClipInput(new DirectoryInfo(folder).Name, null, inside));
        }

        if (clips.Count == 0)
            throw new InvalidOperationException($"No video or image files in {input}.");
        return clips;
    }

    /// <summary>
    /// The image files of a folder in frame order: by the number at the end of the name (f2 before f10; names without one last), then
    /// by name. Plain name order would put f10 before f2, and TimeMs (from the frame number) would run backwards.
    /// </summary>
    private static List<string> ImagesIn(string folder) =>
        Directory.EnumerateFiles(folder).Where(IsImage)
            .OrderBy(p => TrailingNumber(Path.GetFileName(p)) is null)
            .ThenBy(p => TrailingNumber(Path.GetFileName(p)) ?? 0)
            .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The number at the end of a file name without its extension (f00041 gives 41), or null.</summary>
    public static int? TrailingNumber(string fileName)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"(\d+)(?!.*\d)");
        return match.Success && int.TryParse(match.Value, out var n) ? n : null;
    }

    /// <summary>
    /// The frames of a clip, every <paramref name="every"/>th one, and of those only the ones <paramref name="sampler"/> finds due (by time:
    /// see <see cref="FrameSampler"/>; null keeps all). Every frame of a video is decoded: <c>Grab()</c> runs the FFmpeg
    /// decoder, and only the conversion to an image (<c>Retrieve</c>) is skipped for the frames in between, so <c>--every</c> and <c>--fps</c> save
    /// detection time, not decoding time. Throws InvalidOperationException when a video cannot be opened. Images that cannot be
    /// read are skipped and reported to <paramref name="unreadable"/> (with the path). <paramref name="imageFps"/> is the frame rate that the
    /// numbers in image file names stand for (10 for the frames of the training kit).
    /// </summary>
    public static IEnumerable<SourceFrame> Frames(ClipInput clip, int every, Action<string>? unreadable = null, double imageFps = 10.0, FrameSampler? sampler = null)
    {
        if (clip.VideoPath is not null)
        {
            using var capture = new VideoCapture(clip.VideoPath);
            if (!capture.IsOpened())
            {
                throw new InvalidOperationException(
                    $"Cannot open the video {clip.VideoPath}. The OpenCV runtime may lack the decoder for this format: " +
                    "cut the video into frames first (ffmpeg -i clip.mp4 -vf fps=10 frames\\%05d.jpg) and give the folder.");
            }

            var fps = capture.Get(VideoCaptureProperties.Fps);
            if (!(fps > 0 && fps < 1000))
                fps = 30.0; // the container does not say: the tracker only needs a plausible clock
            var number = 0;
            while (capture.Grab())
            {
                number++;
                if ((number - 1) % every != 0)
                    continue;
                var timeMs = (number - 1) * 1000.0 / fps;
                if (sampler is not null && !sampler.Due(timeMs))
                    continue;
                var frame = new Mat();
                if (!capture.Retrieve(frame) || frame.Empty())
                {
                    frame.Dispose();
                    continue;
                }

                sampler?.Kept(timeMs);
                yield return new SourceFrame(clip.Clip, number, $"{clip.Clip}#{number}", frame, timeMs);
            }

            yield break;
        }

        for (var index = 0; index < clip.Images.Count; index++)
        {
            if (index % every != 0)
                continue;
            var path = clip.Images[index];
            var fileName = Path.GetFileName(path);
            var frameNumber = TrailingNumber(fileName) ?? index + 1;
            var timeMs = (frameNumber - 1) * 1000.0 / imageFps;
            if (sampler is not null && !sampler.Due(timeMs))
                continue;
            var image = ReadImage(path);
            if (image is null)
            {
                unreadable?.Invoke(path);
                continue;
            }

            sampler?.Kept(timeMs);
            yield return new SourceFrame(clip.Clip, frameNumber, fileName, image, timeMs);
        }
    }

    /// <summary>
    /// Reads an image file as BGR. The bytes are read with .NET and decoded with <c>ImDecode</c>, because <c>ImRead</c> cannot open
    /// paths with non-ASCII characters on Windows. Null when the file cannot be read or is not an image.
    /// </summary>
    internal static Mat? ReadImage(string path)
    {
        try
        {
            var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
            if (!image.Empty())
                return image;
            image.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OpenCVException)
        {
        }

        return null;
    }
}
