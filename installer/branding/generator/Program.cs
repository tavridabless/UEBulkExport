// Regenerates every image derived from the branding sources in installer/branding:
//   icon-source.webp              -> src/UEBulkExport/Assets/app.ico, logo.png, wizard-small-*.png
//   design/wizard-side.webp       -> wizard-image-*.png       (Welcome and Finished pages, left panel)
//   design/wizard-background.webp -> wizard-back-*.png        (background of every wizard page)
//   design/wizard-hero.webp       -> wizard-install-*.png     (background while files are copied)
//   design/logo-wordmark.webp     -> docs/images/logo.png, logo-dark.png (README header)
//
// The artwork is used as designed: it is only cropped to the aspect ratio Inno Setup reserves
// for each image and scaled to every size Setup picks from at 100-250 % display scaling.
//
// Run from the repository root:
//   dotnet run --project installer/branding/generator -- installer/branding src/UEBulkExport/Assets docs/images

using SkiaSharp;

var branding = args.Length > 0 ? args[0] : "installer/branding";
var assets = args.Length > 1 ? args[1] : "src/UEBulkExport/Assets";
Directory.CreateDirectory(assets);

SKBitmap Load(string relative) =>
    SKBitmap.Decode(Path.Combine(branding, relative)) ?? throw new InvalidDataException($"cannot decode {relative}");

// Halving first, then one final resample: a single jump from 1254 px to 16 px aliases badly.
SKBitmap Scale(SKBitmap b, int w, int h)
{
    var cur = b;
    while (cur.Width / 2 >= w * 2 && cur.Height / 2 >= h * 2)
    {
        var half = cur.Resize(new SKImageInfo(cur.Width / 2, cur.Height / 2, SKColorType.Bgra8888, SKAlphaType.Premul),
            SKFilterQuality.High);
        if (!ReferenceEquals(cur, b)) cur.Dispose();
        cur = half;
    }

    var result = cur.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
    if (!ReferenceEquals(cur, b)) cur.Dispose();
    return result;
}

// Cuts the largest region of the requested aspect ratio; anchor 0 = left/top, 0.5 = centre, 1 = right/bottom.
SKBitmap Crop(SKBitmap b, double aspect, double anchorX, double anchorY)
{
    int w = b.Width, h = b.Height;
    if ((double) w / h > aspect) w = (int) Math.Round(h * aspect);
    else h = (int) Math.Round(w / aspect);

    var x = (int) Math.Round((b.Width - w) * anchorX);
    var y = (int) Math.Round((b.Height - h) * anchorY);
    var cropped = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
    b.ExtractSubset(cropped, new SKRectI(x, y, x + w, y + h));
    return cropped.Copy();
}

byte[] Png(SKBitmap b)
{
    using var img = SKImage.FromBitmap(b);
    return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
}

void SavePng(SKBitmap b, string path) => File.WriteAllBytes(path, Png(b));

void Series(SKBitmap source, string prefix, (int W, int H)[] sizes)
{
    foreach (var old in Directory.GetFiles(branding, prefix + "-*.png")) File.Delete(old);
    foreach (var (w, h) in sizes)
    {
        using var scaled = Scale(source, w, h);
        SavePng(scaled, Path.Combine(branding, $"{prefix}-{w}x{h}.png"));
    }
}

// ---------------------------------------------------------------- application icon
using var icon = Load("icon-source.webp");

int[] iconSizes = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16];
var entries = iconSizes.Select(s => { using var bmp = Scale(icon, s, s); return (s, Png(bmp)); }).ToList();
using (var fs = File.Create(Path.Combine(assets, "app.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort) 0); w.Write((ushort) 1); w.Write((ushort) entries.Count);
    var offset = 6 + 16 * entries.Count;
    foreach (var (s, b) in entries)
    {
        var d = (byte) (s >= 256 ? 0 : s);
        w.Write(d); w.Write(d); w.Write((byte) 0); w.Write((byte) 0);
        w.Write((ushort) 1); w.Write((ushort) 32); w.Write((uint) b.Length); w.Write((uint) offset);
        offset += b.Length;
    }
    foreach (var (_, b) in entries) w.Write(b);
}

// 512 px keeps the in-app logo crisp at 64 logical px on 250 % displays.
using (var logo = Scale(icon, 512, 512)) SavePng(logo, Path.Combine(assets, "logo.png"));

// ---------------------------------------------------------------- installer
// Sizes are the image areas Inno Setup 6.7 reserves at 100, 125, 150, 175, 200, 225 and 250 %.

// Header icon on the inner pages: square, transparent corners show the background through.
Series(icon, "wizard-small", [(58, 58), (77, 77), (97, 97), (116, 116), (124, 124), (143, 143), (159, 159)]);

// Welcome and Finished pages: left panel, 164:314.
using (var side = Load("design/wizard-side.webp"))
using (var cropped = Crop(side, 164.0 / 314, 0.5, 0.5))
    Series(cropped, "wizard-image", [(202, 386), (269, 515), (336, 643), (403, 772), (430, 824), (498, 953), (534, 1022)]);

// Whole-wizard background, 497:360. The design carries the icon at its right edge; the icon
// already sits in the header on the inner pages, so the left, icon-free part is used.
(int, int)[] backSizes = [(596, 432), (796, 576), (994, 720), (1193, 864), (1272, 922)];
using (var back = Load("design/wizard-background.webp"))
using (var cropped = Crop(back, 497.0 / 360, 0, 0.5))
    Series(cropped, "wizard-back", backSizes);

// Background while files are copied: the hero scene, anchored left so the icon and tiles stay in.
using (var hero = Load("design/wizard-hero.webp"))
using (var cropped = Crop(hero, 497.0 / 360, 0, 0.5))
    Series(cropped, "wizard-install", backSizes);


// ---------------------------------------------------------------- README artwork
// The wordmark is dark navy on transparency: fine on GitHub's light theme, nearly invisible on
// the dark one. A second copy with the navy lettering turned near-white is served to dark mode
// through <picture>. The icon keeps its colours in both, it has its own light glass tile.
var docs = args.Length > 2 ? args[2] : "docs/images";
Directory.CreateDirectory(docs);

using (var mark = Load("design/logo-wordmark.webp"))
{
    // Trim the generous transparent margins, keep a little air around the artwork.
    int left = mark.Width, top = mark.Height, right = 0, bottom = 0;
    for (var y = 0; y < mark.Height; y++)
        for (var x = 0; x < mark.Width; x++)
            if (mark.GetPixel(x, y).Alpha > 8)
            {
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }

    const int pad = 16;
    left = Math.Max(0, left - pad); top = Math.Max(0, top - pad);
    right = Math.Min(mark.Width - 1, right + pad); bottom = Math.Min(mark.Height - 1, bottom + pad);

    using var trimmed = new SKBitmap(right - left + 1, bottom - top + 1, SKColorType.Bgra8888, SKAlphaType.Premul);
    mark.ExtractSubset(trimmed, new SKRectI(left, top, right + 1, bottom + 1));
    using var light = trimmed.Copy();
    SavePng(light, Path.Combine(docs, "logo.png"));

    // The icon tile is as tall as the artwork and square, so everything right of it is lettering.
    var iconRight = light.Height + pad;
    using var dark = light.Copy();
    for (var y = 0; y < dark.Height; y++)
        for (var x = iconRight; x < dark.Width; x++)
        {
            var c = dark.GetPixel(x, y);
            // Navy has no bright channel; the blue-to-violet half of the word always has one.
            if (c.Alpha > 0 && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) < 150)
                dark.SetPixel(x, y, new SKColor(0xEE, 0xF2, 0xFF, c.Alpha));
        }
    SavePng(dark, Path.Combine(docs, "logo-dark.png"));
}

Console.WriteLine("done");
