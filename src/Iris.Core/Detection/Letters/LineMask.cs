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

    /// <summary>
    /// Where the mask came from (<c>geom=True</c> of <c>line_mask_info</c> in the Python reference): the frame pixel of the top left corner of the crop the mask was cut from
    /// (the line's box minus its padding, limited to the image). The mask pixel (x, y) is the frame position
    /// <c>(OriginX + (ColumnOffset + x) / 4, OriginY + (RowOffset + y) / 4)</c>. Zero for a mask that was not made from an image.
    /// </summary>
    public int OriginX { get; init; }

    public int OriginY { get; init; }

    /// <summary>The column of the 4x enlarged crop where the (trimmed) mask starts (c0 of the reference).</summary>
    public int ColumnOffset { get; init; }

    /// <summary>The row of the 4x enlarged crop where the (trimmed) mask starts (r0 of the reference).</summary>
    public int RowOffset { get; init; }

    /// <summary>
    /// The colour of every ink pixel (mask value 1) in row-major order, 3 bytes (B, G, R) per pixel, taken from the 4x enlarged crop the mask was cut from;
    /// null unless the mask was made with <c>keepColors</c>. The plate colour is measured on these (<see cref="PlateColors"/>).
    /// </summary>
    public byte[]? InkColors { get; init; }

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
