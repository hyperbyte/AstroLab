// AstroLab — auto-teste de consola da Tarefa 1 (SPEC/05 §1).
// Uso: dotnet run -- tiff-test [caminho-do-autosave.tif]
//   - round-trip sintético save/load (sempre)
//   - se for dado um caminho: dims + min/max/mediana por canal do ficheiro real

using System.Diagnostics;

namespace AstroLab.Services;

public static class SelfTest
{
    public static int Run(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "tiff-test":
                    SyntheticRoundTrip();
                    if (args.Length > 1) InspectReal(args[1]);
                    else Console.WriteLine("\n(sem caminho) — dotnet run -- tiff-test <path>");
                    break;
                case "phasea":
                    if (args.Length < 2) throw new ArgumentException("uso: phasea <path> [--no-radial]");
                    PhaseA(args[1], radial: !args.Contains("--no-radial"));
                    break;
                case "phaseb":
                    if (args.Length < 2) throw new ArgumentException("uso: phaseb <path> [out.jpg]");
                    PhaseB(args[1], args.Length > 2 ? args[2] : "testdata/cs_proxy.jpg");
                    break;
                case "fullb":  // debug: Fase A+B a FULL-RES (sem proxy), p/ comparar com o Python
                    if (args.Length < 3) throw new ArgumentException("uso: fullb <path> <out.jpg>");
                    FullB(args[1], args[2]);
                    break;
                case "bench":  // debug: cronometra cada etapa da Fase B sobre o proxy
                    if (args.Length < 2) throw new ArgumentException("uso: bench <path>");
                    Bench(args[1]);
                    break;
                case "comatest":  // debug: canto sup-esq 600×600, coma off vs on
                    if (args.Length < 2) throw new ArgumentException("uso: comatest <path>");
                    ComaTest(args[1]);
                    break;
                case "aitest":  // debug: canto sup-esq 600×600 com deconvolução IA
                    if (args.Length < 2) throw new ArgumentException("uso: aitest <path>");
                    AiTest(args[1]);
                    break;
                default:
                    throw new ArgumentException($"comando desconhecido: {args[0]}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALHOU: {ex}");
            return 1;
        }
    }

    static void PhaseA(string path, bool radial)
    {
        Console.WriteLine($"== Fase A (radial={radial}): {path} ==");
        var img = TiffIO.LoadFloat(path);
        Console.WriteLine($"  carregado: {img.Height}x{img.Width}x3");

        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        Console.WriteLine($"  crop -> {img.Height}x{img.Width}");

        var sw = Stopwatch.StartNew();
        AstroPipeline.ExtractBackground(img, radial: radial);
        sw.Stop();
        Console.WriteLine($"  ExtractBackground: {sw.ElapsedMilliseconds} ms");
        PrintMedians("MEDIAN_POST_BG", img);

        sw.Restart();
        AstroPipeline.ColorCalibrate(img);
        sw.Stop();
        Console.WriteLine($"  ColorCalibrate: {sw.ElapsedMilliseconds} ms");
        PrintMedians("MEDIAN_POST_CC", img);
    }

    static void PhaseB(string path, string outJpg)
    {
        Console.WriteLine($"== Fase A+B (defaults) -> {outJpg} ==");
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);

        var proxy = PreviewRenderer.MakeProxy(img);
        Console.WriteLine($"  proxy: {proxy.Height}x{proxy.Width} (lado maior {Math.Max(proxy.Width, proxy.Height)})");

        // mediana global pós-Fase-B (MTF mira o fundo em sky=0.12)
        var probe = proxy.Clone();
        var p = ToneParams.Defaults;
        AstroPipeline.Stretch(probe, p);
        AstroPipeline.Scnr(probe, p.Scnr);
        AstroPipeline.SaturationAndCurve(probe, p.Saturation);
        float medB = AstroPipeline.MedianOf((float[])probe.Data.Clone());
        Console.WriteLine($"  mediana global pós-Fase-B = {medB:F4} (alvo sky={p.Sky})");

        var sw = Stopwatch.StartNew();
        byte[] jpg = PreviewRenderer.Render(proxy, p);
        sw.Stop();
        File.WriteAllBytes(outJpg, jpg);
        Console.WriteLine($"  render proxy: {sw.ElapsedMilliseconds} ms, JPEG {jpg.Length / 1024} KB -> {outJpg}");
    }

    static void FullB(string path, string outJpg)
    {
        Console.WriteLine($"== Fase A+B full-res (defaults, sem NR) -> {outJpg} ==");
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        byte[] jpg = PreviewRenderer.Render(img, ToneParams.Defaults, jpegQuality: 93);
        File.WriteAllBytes(outJpg, jpg);
        Console.WriteLine($"  {img.Height}x{img.Width}, JPEG {jpg.Length / 1024} KB -> {outJpg}");
    }

    static void ComaTest(string path)
    {
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        var proxy = PreviewRenderer.MakeProxy(img);
        double mid = AstroPipeline.ComputeMtfMid(proxy, ToneParams.Defaults);

        const int cw = 600, ch = 600, x0 = 0, y0 = 0;   // canto superior-esquerdo
        var p = ToneParams.Defaults;
        foreach (var (tag, coma) in new[] { ("off", false), ("on", true) })
        {
            var crop = new LinearImage { Width = cw, Height = ch, Data = new float[cw * ch * 3] };
            for (int y = 0; y < ch; y++)
                Array.Copy(img.Data, ((y0 + y) * img.Width + x0) * 3, crop.Data, y * cw * 3, cw * 3);
            AstroPipeline.Stretch(crop, p, fixedMid: mid);
            AstroPipeline.Scnr(crop, p.Scnr);
            AstroPipeline.Denoise(crop, p.NoiseReduction);
            if (coma) AstroPipeline.ComaCorrect(crop, 2.0, x0, y0, img.Width, img.Height);
            AstroPipeline.SaturationAndCurve(crop, p.Saturation);
            File.WriteAllBytes($"testdata/corner_{tag}.jpg", PreviewRenderer.EncodeJpeg(crop, 95));
            Console.WriteLine($"  testdata/corner_{tag}.jpg");
        }
    }

    static void AiTest(string path)
    {
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        var proxy = PreviewRenderer.MakeProxy(img);
        double mid = AstroPipeline.ComputeMtfMid(proxy, ToneParams.Defaults);

        const int cw = 600, ch = 600;   // canto sup-esq (mesma receita do comatest)
        var crop = new LinearImage { Width = cw, Height = ch, Data = new float[cw * ch * 3] };
        for (int y = 0; y < ch; y++)
            Array.Copy(img.Data, (y * img.Width) * 3, crop.Data, y * cw * 3, cw * 3);

        var p = ToneParams.Defaults;
        AstroPipeline.Stretch(crop, p, fixedMid: mid);
        AstroPipeline.Scnr(crop, p.Scnr);
        AstroPipeline.Denoise(crop, p.NoiseReduction);
        AstroPipeline.SaturationAndCurve(crop, p.Saturation);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AiSharpen.Sharpen(crop, 1.0);
        sw.Stop();
        Console.WriteLine($"  AiSharpen {cw}x{ch}: {sw.ElapsedMilliseconds} ms");
        File.WriteAllBytes("testdata/corner_ai.jpg", PreviewRenderer.EncodeJpeg(crop, 95));
        Console.WriteLine("  testdata/corner_ai.jpg");
    }

    static void Bench(string path)
    {
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        var proxy = PreviewRenderer.MakeProxy(img);
        Console.WriteLine($"== bench Fase B sobre proxy {proxy.Height}x{proxy.Width} ==");
        var p = ToneParams.Defaults;

        for (int rep = 0; rep < 4; rep++)
        {
            var w = proxy.Clone();
            var t = System.Diagnostics.Stopwatch.StartNew();
            AstroPipeline.Stretch(w, p); long t1 = t.ElapsedMilliseconds;
            AstroPipeline.Scnr(w, p.Scnr); long t2 = t.ElapsedMilliseconds;
            AstroPipeline.SaturationAndCurve(w, p.Saturation); long t3 = t.ElapsedMilliseconds;
            var jpg = PreviewRenderer.Render(proxy, p); long t4 = t.ElapsedMilliseconds;
            Console.WriteLine($"  rep{rep}: stretch={t1} scnr={t2 - t1} sat={t3 - t2} render(total)={t4 - t3} | full Render={t4 - t3}ms");
        }
    }

    static void PrintMedians(string tag, LinearImage img)
    {
        long n = (long)img.Width * img.Height;
        var med = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var ch = new float[n];
            for (long i = 0; i < n; i++) ch[i] = img.Data[i * 3 + c];
            med[c] = AstroPipeline.MedianOf(ch);
        }
        Console.WriteLine($"{tag} {med[0]:F6} {med[1]:F6} {med[2]:F6}");
    }

    static void SyntheticRoundTrip()
    {
        Console.WriteLine("== Round-trip sintético (uint16) ==");
        const int w = 64, h = 48;
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                data[i + 0] = (float)x / (w - 1);             // R rampa horizontal
                data[i + 1] = (float)y / (h - 1);             // G rampa vertical
                data[i + 2] = (float)(x + y) / (w + h - 2);   // B diagonal
            }
        var img = new LinearImage { Width = w, Height = h, Data = data };

        string tmp = Path.Combine(Path.GetTempPath(), "astrolab_roundtrip.tif");
        TiffIO.Save16Bit(img, tmp);
        var back = TiffIO.LoadFloat(tmp);

        if (back.Width != w || back.Height != h)
            throw new Exception($"Dimensões diferentes: {back.Width}x{back.Height} != {w}x{h}");

        float maxErr = 0;
        for (int i = 0; i < data.Length; i++)
            maxErr = Math.Max(maxErr, Math.Abs(data[i] - back.Data[i]));

        // tolerância = 1 passo de quantização de 16 bits
        const float tol = 1.0f / 65535f + 1e-6f;
        Console.WriteLine($"  {w}x{h}, erro máx abs = {maxErr:E3} (tol {tol:E3}) -> "
                          + (maxErr <= tol ? "OK" : "FALHA"));
        if (maxErr > tol) throw new Exception("Round-trip excedeu a tolerância de quantização.");
        File.Delete(tmp);
    }

    static void InspectReal(string path)
    {
        Console.WriteLine($"\n== Ficheiro real: {path} ==");
        if (!File.Exists(path)) throw new FileNotFoundException(path);

        var sw = Stopwatch.StartNew();
        var img = TiffIO.LoadFloat(path);
        sw.Stop();

        long n = (long)img.Width * img.Height;
        Console.WriteLine($"  Dimensões : {img.Width} x {img.Height}  ({n / 1e6:F1} MP)");
        Console.WriteLine($"  Load      : {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  {"Canal",-6}{"min",14}{"max",14}{"mediana",14}");

        string[] name = { "R", "G", "B" };
        for (int c = 0; c < 3; c++)
        {
            var ch = new float[n];
            float min = float.MaxValue, max = float.MinValue;
            for (long i = 0; i < n; i++)
            {
                float v = img.Data[i * 3 + c];
                ch[i] = v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            float med = AstroPipeline.MedianOf(ch); // destrói a cópia (ok)
            Console.WriteLine($"  {name[c],-6}{min,14:E4}{max,14:E4}{med,14:E4}");
        }
    }
}
