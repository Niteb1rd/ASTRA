using System.Text.RegularExpressions;
using OpenCvSharp;

namespace Iris.Replay;

/// <summary>One frame to run the detector on. The consumer disposes <see cref="Image"/>.</summary>
/// <param name="Clip">The clip (video file name, or folder name) the frame belongs to.</param>
/// <param name="Number">Frame number: the video frame (counted from 1), or the number in the image file name (f00041.jpg is 41; without digits: its position).</param>
/// <param name="Name">What to call the frame: the image file name, or clip#number for a video frame.</param>
public readonly record struct SourceFrame(string Clip, int Number, string Name, Mat Image);

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

    private static List<string> ImagesIn(string folder) =>
        Directory.EnumerateFiles(folder).Where(IsImage).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The number at the end of a file name without its extension (f00041 gives 41), or null.</summary>
    public static int? TrailingNumber(string fileName)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(fileName), @"(\d+)(?!.*\d)");
        return match.Success && int.TryParse(match.Value, out var n) ? n : null;
    }

    /// <summary>
    /// The frames of a clip, every <paramref name="every"/>th one. Video frames are decoded one at a time (frames that are skipped
    /// are only advanced over, not decoded). Throws InvalidOperationException when a video cannot be opened. Images that cannot be
    /// read are skipped and counted in <paramref name="unreadable"/>.
    /// </summary>
    public static IEnumerable<SourceFrame> Frames(ClipInput clip, int every, Action<string>? unreadable = null)
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

            var number = 0;
            while (capture.Grab())
            {
                number++;
                if ((number - 1) % every != 0)
                    continue;
                var frame = new Mat();
                if (!capture.Retrieve(frame) || frame.Empty())
                {
                    frame.Dispose();
                    continue;
                }

                yield return new SourceFrame(clip.Clip, number, $"{clip.Clip}#{number}", frame);
            }

            yield break;
        }

        for (var index = 0; index < clip.Images.Count; index++)
        {
            if (index % every != 0)
                continue;
            var path = clip.Images[index];
            var image = Cv2.ImRead(path, ImreadModes.Color);
            if (image.Empty())
            {
                image.Dispose();
                unreadable?.Invoke(path);
                continue;
            }

            var fileName = Path.GetFileName(path);
            yield return new SourceFrame(clip.Clip, TrailingNumber(fileName) ?? index + 1, fileName, image);
        }
    }
}
