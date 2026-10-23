using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;

namespace IcnsConverter;

public enum OutFormat { Png, Jpg, Webp, Ico, Bmp, Tiff, Gif }
public enum SizeMode { All, Largest, Selected }
public enum MissingMode { Skip, UseLargest, Upscale }

public sealed record ExportOptions(
    OutFormat Format,
    SizeMode Mode,
    HashSet<int> Sizes,
    MissingMode Missing,
    string? OutputFolder,
    int Quality,
    MagickColor Background,
    bool CombineIco);

public static class Exporter
{
    private const int IcoMax = 256;

    public static int Export(IcnsFile file, ExportOptions o, Action<string> log)
    {
        if (file.Images.Count == 0)
        {
            log($"[x] {file.Name}: {file.Error ?? "no images found"}");
            return 0;
        }

        // ---- 1. Decide which sizes to export ----
        var largest = file.Images.OrderByDescending(i => i.Size).First();
        var targets = new List<(int Size, IcnsImage Src, bool Upscale)>();
        void Add(int size, IcnsImage src, bool up)
        {
            if (targets.All(t => t.Size != size)) targets.Add((size, src, up));
        }

        switch (o.Mode)
        {
            case SizeMode.All:
                foreach (var img in file.Images.OrderByDescending(i => i.Size)) Add(img.Size, img, false);
                break;
            case SizeMode.Largest:
                Add(largest.Size, largest, false);
                break;
            default:
                foreach (int want in o.Sizes.OrderByDescending(s => s))
                {
                    var hit = file.Images.FirstOrDefault(i => i.Size == want);
                    if (hit is not null) Add(want, hit, false);
                    else if (o.Missing == MissingMode.UseLargest) Add(largest.Size, largest, false);
                    else if (o.Missing == MissingMode.Upscale) Add(want, largest, true);
                    else log($"[-] {file.Name}: no {want}x{want} image, skipped");
                }
                break;
        }

        if (targets.Count == 0)
        {
            log($"[-] {file.Name}: nothing to export");
            return 0;
        }

        // ---- 2. Output location ----
        var dir = string.IsNullOrWhiteSpace(o.OutputFolder)
            ? (Path.GetDirectoryName(file.FilePath) ?? ".")
            : o.OutputFolder;
        Directory.CreateDirectory(dir);

        string ext = o.Format switch
        {
            OutFormat.Jpg => "jpg", OutFormat.Webp => "webp", OutFormat.Ico => "ico",
            OutFormat.Bmp => "bmp", OutFormat.Tiff => "tiff", OutFormat.Gif => "gif",
            _ => "png"
        };

        int created = 0;

        // ---- 3a. One ICO holding several sizes ----
        if (o.Format == OutFormat.Ico && o.CombineIco)
        {
            try
            {
                using var col = new MagickImageCollection();
                var seen = new HashSet<int>();
                foreach (var t in targets)
                {
                    int px = Math.Min(t.Size, IcoMax);
                    if (!seen.Add(px)) continue;
                    col.Add(Prepare(file, t.Src, px, o));
                }
                var path = Unique(Path.Combine(dir, $"{file.Name}.ico"));
                col.Write(path, MagickFormat.Ico);
                log($"[ok] {file.Name} -> {Path.GetFileName(path)} ({string.Join(", ", seen.OrderBy(s => s))} px)");
                created++;
            }
            catch (Exception ex) { log($"[x] {file.Name}: {ex.Message}"); }
            return created;
        }

        // ---- 3b. One file per size ----
        foreach (var t in targets)
        {
            try
            {
                int px = o.Format == OutFormat.Ico ? Math.Min(t.Size, IcoMax) : t.Size;
                string name = targets.Count > 1 ? $"{file.Name}_{px}" : file.Name;
                using var img = Prepare(file, t.Src, px, o);
                var path = Unique(Path.Combine(dir, $"{name}.{ext}"));
                img.Write(path, ToMagick(o.Format));
                log($"[ok] {file.Name} -> {Path.GetFileName(path)}" +
                    (o.Format == OutFormat.Ico && t.Size > IcoMax ? "  (ICO max is 256, resized)" : ""));
                created++;
            }
            catch (Exception ex) { log($"[x] {file.Name} ({t.Size}px): {ex.Message}"); }
        }
        return created;
    }

    private static IMagickImage<byte> Prepare(IcnsFile file, IcnsImage src, int px, ExportOptions o)
    {
        var img = file.Open(src);
        if (img.Width != (uint)px || img.Height != (uint)px)
            img.Resize(new MagickGeometry((uint)px, (uint)px) { IgnoreAspectRatio = true });

        if (o.Format == OutFormat.Jpg)
        {
            img.ColorAlpha(o.Background);   // fill transparency, JPG has none
        }
        if (o.Format is OutFormat.Jpg or OutFormat.Webp)
            img.Quality = (uint)o.Quality;
        return img;
    }

    private static MagickFormat ToMagick(OutFormat f) => f switch
    {
        OutFormat.Jpg => MagickFormat.Jpeg,
        OutFormat.Webp => MagickFormat.WebP,
        OutFormat.Ico => MagickFormat.Ico,
        OutFormat.Bmp => MagickFormat.Bmp,
        OutFormat.Tiff => MagickFormat.Tiff,
        OutFormat.Gif => MagickFormat.Gif,
        _ => MagickFormat.Png
    };

    /// <summary>Never overwrite: adds " (1)", " (2)"... if the file exists.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            var p = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
