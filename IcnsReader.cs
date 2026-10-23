using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ImageMagick;

namespace IcnsConverter;

/// <summary>One image stored inside an ICNS file.</summary>
public sealed class IcnsImage
{
    public int Size { get; init; }          // pixel width (= height)
    public int Width { get; init; }
    public int Height { get; init; }
    public string Type { get; init; } = ""; // ICNS chunk type, e.g. "ic10"
    public byte[]? Data { get; init; }      // encoded PNG / JPEG 2000 bytes
    public byte[]? Pixels { get; init; }    // decoded RGBA bytes (4 per pixel)
}

/// <summary>
/// Parses the ICNS container and lists every size it holds. Supports both the
/// modern PNG / JPEG 2000 chunks and the legacy 24-bit RGB, ARGB, indexed and
/// monochrome chunks that older (< 10.5) icons use.
/// </summary>
public sealed class IcnsFile
{
    // Chunk type -> nominal pixel size. @2x chunks are listed by their real pixel size.
    private static readonly Dictionary<string, int> ChunkSizes = new(StringComparer.Ordinal)
    {
        // PNG / JPEG 2000 (icp4/icp5 may also hold raw RGB)
        ["icp4"] = 16,  ["icp5"] = 32,  ["icp6"] = 64,
        ["ic07"] = 128, ["ic08"] = 256, ["ic09"] = 512, ["ic10"] = 1024,
        ["ic11"] = 32,  ["ic12"] = 64,  ["ic13"] = 256, ["ic14"] = 512,
        // ARGB / PNG / JPEG 2000
        ["ic04"] = 16, ["ic05"] = 32, ["icsb"] = 18,
        ["icsB"] = 36, ["sb24"] = 24, ["SB24"] = 48,
        // Legacy 24-bit RGB, paired with a separate 8-bit mask chunk
        ["is32"] = 16, ["il32"] = 32, ["ih32"] = 48, ["it32"] = 128,
        // Legacy indexed and monochrome
        ["ics8"] = 16, ["icl8"] = 32, ["ich8"] = 48,
        ["ics4"] = 16, ["icl4"] = 32, ["ich4"] = 48,
        ["ics#"] = 16, ["ICN#"] = 32, ["ich#"] = 48, ["ICON"] = 32,
    };

    // Pixel size -> the 8-bit alpha mask that belongs to the legacy colour chunk.
    private static readonly Dictionary<int, string> MaskTypes = new()
    {
        [16] = "s8mk", [32] = "l8mk", [48] = "h8mk", [128] = "t8mk",
    };

    // The fixed palettes used by the 4- and 8-bit icon chunks (no palette is
    // stored in the file). Taken from the classic Mac OS icon colour tables.
    private static readonly (byte R, byte G, byte B)[] Palette4 =
    {
        (0xFF, 0xFF, 0xFF), (0xFC, 0xF3, 0x05), (0xFF, 0x64, 0x02), (0xDD, 0x08, 0x06),
        (0xF2, 0x08, 0x84), (0x46, 0x00, 0xA5), (0x00, 0x00, 0xD4), (0x02, 0xAB, 0xEA),
        (0x1F, 0xB7, 0x14), (0x00, 0x64, 0x11), (0x56, 0x2C, 0x05), (0x90, 0x71, 0x3A),
        (0xC0, 0xC0, 0xC0), (0x80, 0x80, 0x80), (0x40, 0x40, 0x40), (0x00, 0x00, 0x00),
    };

    private static readonly byte[] Levels6 = { 0xFF, 0xCC, 0x99, 0x66, 0x33, 0x00 };
    private static readonly byte[] Shades10 = { 0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
    private static readonly (byte R, byte G, byte B)[] Palette8 = BuildPalette8();

    public string FilePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(FilePath);

    public bool IsReadable => Images.Count > 0;

    /// <summary>Short size list shown under the file name in the list.</summary>
    public string Summary => Images.Count == 0
        ? (Error ?? "No images found")
        : string.Join(" · ", Images.Select(i => $"{i.Size}px"));

    /// <summary>Badge text for the file row.</summary>
    public string Badge => Images.Count == 0
        ? "Error"
        : $"{Images.Count} size{(Images.Count == 1 ? "" : "s")}";

    public List<IcnsImage> Images { get; } = new();
    public string? Error { get; private set; }

    private IcnsFile(string path) => FilePath = path;

    public override string ToString()
    {
        var file = Path.GetFileName(FilePath);
        if (Images.Count == 0) return $"{file}  -  unreadable";
        var sizes = Images.Select(i => i.Size).OrderBy(s => s).ToList();
        return $"{file}  -  {sizes.Count} sizes ({sizes.First()} to {sizes.Last()})";
    }

    public static IcnsFile Load(string path)
    {
        var f = new IcnsFile(path);
        try
        {
            f.ParseContainer(File.ReadAllBytes(path));
            if (f.Images.Count == 0 && f.Error is null)
                f.Error = "No supported icon images found.";
        }
        catch (Exception ex)
        {
            f.Error = ex.Message;
        }
        return f;
    }

    private void ParseContainer(byte[] b)
    {
        if (b.Length < 8 || b[0] != 'i' || b[1] != 'c' || b[2] != 'n' || b[3] != 's')
        {
            Error = "Not an ICNS file.";
            return;
        }

        var chunks = ReadChunks(b);
        if (chunks.Count == 0)
        {
            Error = "The ICNS file contains no icon entries.";
            return;
        }

        // Collect the alpha masks first: they are stored separately from the
        // legacy 24-bit / indexed colour data.
        var masks = new Dictionary<int, byte[]>();
        foreach (var (type, data) in chunks)
        {
            foreach (var pair in MaskTypes)
            {
                if (type == pair.Value && data.Length >= pair.Key * pair.Key && !masks.ContainsKey(pair.Key))
                    masks[pair.Key] = data;
            }
        }

        // Several chunks can describe the same pixel size (e.g. it32 and ic07).
        // Keep the best-quality one for each size.
        var best = new Dictionary<int, (int Priority, IcnsImage Image)>();
        foreach (var (type, data) in chunks)
        {
            if (!TryDecode(type, data, masks, out var image, out int priority) || image is null)
                continue;

            if (!best.TryGetValue(image.Size, out var current) || priority > current.Priority)
                best[image.Size] = (priority, image);
        }

        foreach (var item in best.Values.OrderByDescending(v => v.Image.Size))
            Images.Add(item.Image);

        if (Images.Count == 0)
        {
            var types = string.Join(", ", chunks.Select(c => c.Type).Distinct().Take(8));
            Error = $"No supported icon images found (chunks: {types}).";
        }
    }

    /// <summary>Reads the chunk list, tolerating a bad header length and resyncing after corrupt chunks.</summary>
    private static List<(string Type, byte[] Data)> ReadChunks(byte[] b)
    {
        var result = new List<(string, byte[])>();
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        // Some writers leave the total length at zero or wrong; trust the file.
        int end = declared >= 8 && declared <= (uint)b.Length ? (int)declared : b.Length;

        int pos = 8;
        while (pos + 8 <= end)
        {
            string type = Encoding.ASCII.GetString(b, pos, 4);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos + 4));

            if (length < 8 || pos + (long)length > end)
            {
                int next = FindNextChunk(b, pos + 4, end);
                if (next < 0) break;
                pos = next;
                continue;
            }

            int len = (int)length;
            result.Add((type, b.AsSpan(pos + 8, len - 8).ToArray()));
            pos += len;
        }
        return result;
    }

    private static int FindNextChunk(byte[] b, int start, int end)
    {
        for (int p = Math.Max(8, start); p + 8 <= end; p++)
        {
            string type = Encoding.ASCII.GetString(b, p, 4);
            if (!ChunkSizes.ContainsKey(type) && !MaskTypes.ContainsValue(type) &&
                type != "TOC " && type != "icnV" && type != "name" && type != "info")
                continue;

            uint length = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 4));
            if (length >= 8 && length <= (uint)(end - p))
                return p;
        }
        return -1;
    }

    private static bool TryDecode(
        string type, byte[] data, IReadOnlyDictionary<int, byte[]> masks,
        out IcnsImage? image, out int priority)
    {
        image = null;
        priority = 0;
        if (!ChunkSizes.TryGetValue(type, out int size))
            return false;

        // Modern chunks: an embedded PNG or JPEG 2000 image. Trust its real
        // dimensions when they are available.
        if (IsPng(data) || IsJp2(data))
        {
            int px = ActualSize(data, size);
            image = new IcnsImage { Size = px, Width = px, Height = px, Type = type, Data = data };
            priority = Priority(type, true);
            return true;
        }

        masks.TryGetValue(size, out var mask);
        byte[]? rgba = null;
        int pixels = size * size;

        switch (type)
        {
            case "is32":
            case "il32":
            case "ih32":
                if (!TryDecodeRgb(data, size, false, mask, out rgba)) return false;
                break;
            case "it32":
                // it32 normally starts with four ignored header bytes, but a
                // few old writers omit them.
                if (!TryDecodeRgb(data, size, true, mask, out rgba) &&
                    !TryDecodeRgb(data, size, false, mask, out rgba)) return false;
                break;
            case "icp4":
            case "icp5":
                // Rare, but these keys may hold raw 24-bit RGB instead of PNG.
                if (!TryDecodeRgb(data, size, false, mask, out rgba)) return false;
                break;
            case "ic04":
            case "ic05":
            case "icsb":
                if (!TryDecodeArgb(data, size, out rgba)) return false;
                break;
            case "ics8":
            case "icl8":
            case "ich8":
                if (!TryDecodeIndexed(data, size, 8, mask, out rgba)) return false;
                break;
            case "ics4":
            case "icl4":
            case "ich4":
                if (!TryDecodeIndexed(data, size, 4, mask, out rgba)) return false;
                break;
            case "ics#":
            case "ICN#":
            case "ich#":
                // Normally these carry an icon plane followed by a mask plane,
                // but a few writers omit the mask.
                if (!TryDecodeMono(data, size, true, out rgba) &&
                    !TryDecodeMono(data, size, false, out rgba)) return false;
                break;
            case "ICON":
                if (!TryDecodeMono(data, size, false, out rgba)) return false;
                break;
            default:
                return false;
        }

        if (rgba is null || rgba.Length != pixels * 4)
            return false;

        image = new IcnsImage { Size = size, Width = size, Height = size, Type = type, Pixels = rgba };
        priority = Priority(type, false);
        return true;
    }

    private static int Priority(string type, bool encoded) => type switch
    {
        "ic10" or "ic09" or "ic14" or "ic08" or "ic13" or "ic07"
            or "icsB" or "SB24" => 100,
        "icp4" or "icp5" or "icp6" => encoded ? 95 : 45,
        "ic04" or "ic05" or "icsb" => encoded ? 95 : 90,
        "it32" or "ih32" or "il32" or "is32" => 60,
        "ich8" or "icl8" or "ics8" => 50,
        "ich4" or "icl4" or "ics4" => 40,
        "ich#" or "ICN#" or "ics#" => 30,
        "ICON" => 10,
        _ => 0,
    };

    /// <summary>Decodes the PackBits-like run-length compression used by RGB and ARGB chunks.</summary>
    private static bool TryUnpack(ReadOnlySpan<byte> src, int expected, out byte[] output)
    {
        output = new byte[expected];
        int i = 0, o = 0;
        while (i < src.Length && o < expected)
        {
            int n = src[i++];
            if (n < 0x80)
            {
                int count = n + 1;
                if (i + count > src.Length) break;
                int take = Math.Min(count, expected - o);
                src.Slice(i, take).CopyTo(output.AsSpan(o));
                i += count;
                o += take;
            }
            else
            {
                if (i >= src.Length) break;
                byte value = src[i++];
                int count = n - 0x80 + 3;
                int take = Math.Min(count, expected - o);
                output.AsSpan(o, take).Fill(value);
                o += take;
            }
        }
        return o == expected;
    }

    private static bool TryDecodeRgb(byte[] data, int size, bool hasHeader, byte[]? mask, out byte[]? rgba)
    {
        rgba = null;
        int pixels = size * size;
        int total = pixels * 3;

        var src = data.AsSpan();
        if (hasHeader)
        {
            if (src.Length < 4) return false;
            src = src[4..];
        }

        byte[] raw;
        if (TryUnpack(src, total, out var unpacked))
            raw = unpacked;
        else if (src.Length == total)
            raw = src.ToArray(); // some writers store the channels uncompressed
        else
            return false;

        rgba = new byte[pixels * 4];
        int gOff = pixels, bOff = pixels * 2;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            rgba[o] = raw[i];
            rgba[o + 1] = raw[gOff + i];
            rgba[o + 2] = raw[bOff + i];
            rgba[o + 3] = mask is not null && i < mask.Length ? mask[i] : (byte)255;
        }
        return true;
    }

    private static bool TryDecodeArgb(byte[] data, int size, out byte[]? rgba)
    {
        rgba = null;
        if (data.Length < 4 || data[0] != 'A' || data[1] != 'R' || data[2] != 'G' || data[3] != 'B')
            return false;

        int pixels = size * size;
        int total = pixels * 4;
        var src = data.AsSpan(4);

        byte[] raw;
        if (TryUnpack(src, total, out var unpacked))
            raw = unpacked;
        else if (src.Length == total)
            raw = src.ToArray();
        else
            return false;

        rgba = new byte[total];
        int rOff = pixels, gOff = pixels * 2, bOff = pixels * 3;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            rgba[o] = raw[rOff + i];
            rgba[o + 1] = raw[gOff + i];
            rgba[o + 2] = raw[bOff + i];
            rgba[o + 3] = raw[i];
        }
        return true;
    }

    private static bool TryDecodeIndexed(byte[] data, int size, int bits, byte[]? mask, out byte[]? rgba)
    {
        rgba = null;
        int rowBytes = bits == 8 ? size : (size + 1) / 2;
        int needed = rowBytes * size;
        if (data.Length < needed) return false;

        var palette = bits == 8 ? Palette8 : Palette4;
        rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index;
                if (bits == 8)
                {
                    index = data[y * rowBytes + x];
                }
                else
                {
                    byte packed = data[y * rowBytes + x / 2];
                    index = (x & 1) == 0 ? packed >> 4 : packed & 0x0F;
                }
                if ((uint)index >= (uint)palette.Length) index = 0;

                var colour = palette[index];
                int i = y * size + x;
                int o = i * 4;
                rgba[o] = colour.R;
                rgba[o + 1] = colour.G;
                rgba[o + 2] = colour.B;
                rgba[o + 3] = mask is not null && i < mask.Length ? mask[i] : (byte)255;
            }
        }
        return true;
    }

    private static bool TryDecodeMono(byte[] data, int size, bool hasMask, out byte[]? rgba)
    {
        rgba = null;
        int rowBytes = (size + 7) / 8;
        int plane = rowBytes * size;
        int needed = hasMask ? plane * 2 : plane;
        if (data.Length < needed) return false;

        rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int shift = 7 - (x & 7);
                int byteIndex = y * rowBytes + x / 8;
                bool on = (data[byteIndex] & (1 << shift)) != 0;
                bool opaque = !hasMask || (data[plane + byteIndex] & (1 << shift)) != 0;

                int o = (y * size + x) * 4;
                byte value = on ? (byte)255 : (byte)0;
                rgba[o] = value;
                rgba[o + 1] = value;
                rgba[o + 2] = value;
                rgba[o + 3] = opaque ? (byte)255 : (byte)0;
            }
        }
        return true;
    }

    private static bool IsPng(ReadOnlySpan<byte> d) =>
        d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 &&
        d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;

    private static bool IsJp2(ReadOnlySpan<byte> d) =>
        (d.Length >= 8 && d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x00 && d[3] == 0x0C &&
         d[4] == 0x6A && d[5] == 0x50 && d[6] == 0x20 && d[7] == 0x20) ||
        (d.Length >= 4 && d[0] == 0xFF && d[1] == 0x4F && d[2] == 0xFF && d[3] == 0x51);

    private static int ActualSize(byte[] data, int fallback)
    {
        if (IsPng(data) && data.Length >= 24)
        {
            uint w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            uint h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
            if (w == h && w is >= 1 and <= 4096) return (int)w;
        }
        if (IsJp2(data) && TryGetJp2Size(data, out int width, out int height) &&
            width == height && width is >= 1 and <= 4096)
            return width;
        return fallback;
    }

    private static bool TryGetJp2Size(byte[] data, out int width, out int height)
    {
        width = height = 0;

        // Raw JPEG 2000 codestream: SIZ marker holds the image size.
        if (data.Length >= 16 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51)
        {
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            return width > 0 && height > 0;
        }

        // JP2 file: walk the boxes and read the ihdr box inside jp2h.
        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            long boxSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            string boxType = Encoding.ASCII.GetString(data, offset + 4, 4);
            if (boxSize < 8 || offset + boxSize > data.Length) return false;

            if (boxType == "jp2h")
            {
                int child = offset + 8;
                int boxEnd = (int)(offset + boxSize);
                while (child + 8 <= boxEnd)
                {
                    long childSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(child));
                    if (childSize < 8 || child + childSize > boxEnd) return false;
                    if (Encoding.ASCII.GetString(data, child + 4, 4) == "ihdr" && childSize >= 16)
                    {
                        height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(child + 8));
                        width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(child + 12));
                        return width > 0 && height > 0;
                    }
                    child += (int)childSize;
                }
            }
            offset += (int)boxSize;
        }
        return false;
    }

    private static (byte R, byte G, byte B)[] BuildPalette8()
    {
        var palette = new List<(byte, byte, byte)>(256);
        foreach (byte r in Levels6)
            foreach (byte g in Levels6)
                foreach (byte b in Levels6)
                    palette.Add((r, g, b));
        foreach (byte shade in Shades10) palette.Add((shade, 0, 0));
        foreach (byte shade in Shades10) palette.Add((0, shade, 0));
        foreach (byte shade in Shades10) palette.Add((0, 0, shade));
        foreach (byte shade in Shades10) palette.Add((shade, shade, shade));
        return palette.ToArray();
    }

    public IMagickImage<byte> Open(IcnsImage img)
    {
        if (img.Data is not null)
            return new MagickImage(img.Data);
        if (img.Pixels is null || img.Width <= 0 || img.Height <= 0)
            throw new InvalidOperationException($"No pixel data for icon chunk {img.Type}.");

        var settings = new PixelReadSettings(
            (uint)img.Width, (uint)img.Height, StorageType.Char, PixelMapping.RGBA);
        return new MagickImage(img.Pixels, settings);
    }
}
