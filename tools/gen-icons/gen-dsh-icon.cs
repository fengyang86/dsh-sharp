// DSH-Sharp icon: official DSH favicon on dark rounded backdrop
#:package SkiaSharp@*
#:property PublishAot=false

using SkiaSharp;

const string OutDir = @"D:\dsh-plugins\dsh-sharp\assets\icon-candidates";
Directory.CreateDirectory(OutDir);

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
var bgTop = new SKColor(0x10, 0x16, 0x28);
var bgBottom = new SKColor(0x1E, 0x27, 0x45);

// DSH official favicon path (from apps/web/public/favicon.svg)
string dshPath = File.ReadAllText(@"C:\Users\杨峰\AppData\Local\Temp\dsh-icon-gen\dsh-path.txt");

using var logoPath = SKPath.ParseSvgPathData(dshPath);
var logoBounds = logoPath.Bounds;
Console.WriteLine($"logo bounds: {logoBounds}");

static byte[] DrawPng(int size, Action<SKCanvas, float> draw)
{
    using var bmp = new SKBitmap(size, size);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);
    draw(canvas, size);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static void WriteIco(string path, Dictionary<int, byte[]> pngs)
{
    var ordered = pngs.OrderBy(kv => kv.Key).ToList();
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write((short)0);
    bw.Write((short)1);
    bw.Write((short)ordered.Count);
    int offset = 6 + 16 * ordered.Count;
    foreach (var (s, data) in ordered)
    {
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write((int)data.Length);
        bw.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in ordered) bw.Write(data);
    File.WriteAllBytes(path, ms.ToArray());
}

// Candidate D: official DSH whale logo on dark rounded backdrop
Dictionary<int, byte[]> D = new();
foreach (var s in sizes)
{
    D[s] = DrawPng(s, (canvas, size) =>
    {
        // backdrop: dark rounded square with subtle vertical gradient
        var bg = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, size), new[] { bgTop, bgBottom }, SKShaderTileMode.Clamp);
        using var bgPaint = new SKPaint { Shader = bg, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(0, 0, size, size), size * 0.22f, size * 0.22f, bgPaint);

        // whale logo: fit into ~74% of canvas, centered, preserving aspect ratio
        float target = size * 0.74f;
        float scale = target / Math.Max(logoBounds.Width, logoBounds.Height);
        float dx = (size - logoBounds.Width * scale) / 2f - logoBounds.Left * scale;
        float dy = (size - logoBounds.Height * scale) / 2f - logoBounds.Top * scale;
        canvas.Save();
        canvas.Translate(dx, dy);
        canvas.Scale(scale);
        using var logoPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(logoPath, logoPaint);
        canvas.Restore();
    });
}
File.WriteAllBytes(Path.Combine(OutDir, "D-dsh-whale.png"), D[256]);
WriteIco(Path.Combine(OutDir, "D-dsh-whale.ico"), D);

// preview: A B C D
{
    const int cell = 256, gap = 24, pad = 28;
    int width = pad * 2 + cell * 4 + gap * 3;
    int height = pad * 2 + cell;
    using var bmp = new SKBitmap(width, height);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(new SKColor(0xF2, 0xF4, 0xF8));
    void Paste(int idx, byte[] png)
    {
        using var img = SKImage.FromEncodedData(png);
        using var bmp2 = SKBitmap.FromImage(img);
        float x = pad + idx * (cell + gap);
        canvas.DrawBitmap(bmp2, new SKRect(x, pad, x + cell, pad + cell));
    }
    Paste(0, File.ReadAllBytes(Path.Combine(OutDir, "A-gradient-S.png")));
    Paste(1, File.ReadAllBytes(Path.Combine(OutDir, "B-shell-window.png")));
    Paste(2, File.ReadAllBytes(Path.Combine(OutDir, "C-code-D.png")));
    Paste(3, D[256]);
    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.Create(Path.Combine(OutDir, "preview.png"));
    data.SaveTo(fs);
}

Console.WriteLine("done: " + OutDir);
