namespace Iris.Core.Detection.Letters;

/// <summary>
/// The text pixels of one nameplate line, at 4x the screen resolution: <see cref="Height"/> rows of <see cref="Width"/> bytes,
/// each 1 (ink) or 0, row-major. Trimmed to the ink on all four sides.
/// </summary>
public sealed class LineMask
{
    public LineMask(int height, int width, byte[] data)
    {
        if (height <= 0 || width <= 0 || data.Length != height * width)
            throw new ArgumentException("Mask data does not match its size.");
        Height = height;
        Width = width;
        Data = data;
    }

    public int Height { get; }

    public int Width { get; }

    /// <summary>Row-major pixels, 0 or 1.</summary>
    public byte[] Data { get; }

    /// <summary>Pixel at (x, y); 0 or 1.</summary>
    public byte this[int y, int x] => Data[y * Width + x];

    /// <summary>
    /// Builds a mask from a bit-packed string (most significant bit first, row-major: numpy.packbits order), as stored
    /// in the test fixtures.
    /// </summary>
    public static LineMask FromPackedBits(int height, int width, byte[] bits)
    {
        var data = new byte[checked(height * width)];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)((bits[i >> 3] >> (7 - (i & 7))) & 1);
        return new LineMask(height, width, data);
    }
}
