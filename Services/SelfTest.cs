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
                case "startest":  // debug: separação de estrelas (starless + stars)
                    if (args.Length < 2) throw new ArgumentException("uso: startest <path>");
                    StarTest(args[1]);
                    break;
                case "resample":
                    Resample();
                    break;
                case "nrlinear":
                    NrLinear();
                    break;
                case "starproc":
                    StarProc();
                    break;
                case "reducestars":
                    ReduceStarsTest();
                    break;
                case "abtest":  // comparativos A/B das features novas (NR linear, contraste local, redução estrelas)
                    if (args.Length < 2) throw new ArgumentException("uso: abtest <path>");
                    AbTest(args[1]);
                    break;
                case "wcstest":
                    WcsTest();
                    break;
                case "fovtest":
                    FovTest();
                    break;
                case "settingstest":
                    SettingsTest();
                    break;
                case "solvetest":
                    if (args.Length < 2) throw new ArgumentException("uso: solvetest <path>");
                    SolveTest(args[1]);
                    break;
                case "dsoparse":
                    DsoParse();
                    break;
                case "annotbuild":
                    AnnotBuild();
                    break;
                case "annotatetest":
                    if (args.Length < 2) throw new ArgumentException("uso: annotatetest <path>");
                    AnnotateTest(args[1]);
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

    static void StarTest(string path)
    {
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        var p = ToneParams.Defaults;
        AstroPipeline.Stretch(img, p);
        AstroPipeline.Scnr(img, p.Scnr);
        AstroPipeline.SaturationAndCurve(img, p.Saturation);

        const int w = 1280, h = 960;
        int x0 = (img.Width - w) / 2, y0 = (img.Height - h) / 2;
        var crop = new LinearImage { Width = w, Height = h, Data = new float[w * h * 3] };
        for (int y = 0; y < h; y++)
            Array.Copy(img.Data, ((y0 + y) * img.Width + x0) * 3, crop.Data, y * w * 3, w * 3);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var starless = StarRemoval.Starless(crop);
        sw.Stop();
        var stars = StarRemoval.StarsLayer(crop, starless);
        File.WriteAllBytes("testdata/cs_starless.jpg", PreviewRenderer.EncodeJpeg(starless, 95));
        File.WriteAllBytes("testdata/cs_stars.jpg", PreviewRenderer.EncodeJpeg(stars, 95));
        Console.WriteLine($"  Starless {w}x{h}: {sw.ElapsedMilliseconds} ms -> testdata/cs_starless.jpg, cs_stars.jpg");
    }

    /// <summary>Comparativos A/B (antes/depois) das 3 features visuais novas, num crop
    /// central. Escreve pares JPEG em testdata/ab_*.jpg. Tone consistente dentro de cada par.</summary>
    static void AbTest(string path)
    {
        Console.WriteLine($"== A/B das features novas: {path} ==");
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);
        var p = ToneParams.Defaults;

        const int w = 1280, h = 960;
        int x0 = (img.Width - w) / 2, y0 = (img.Height - h) / 2;
        LinearImage CropLin()
        {
            var c = new LinearImage { Width = w, Height = h, Data = new float[w * h * 3] };
            for (int y = 0; y < h; y++)
                Array.Copy(img.Data, ((long)(y0 + y) * img.Width + x0) * 3, c.Data, (long)y * w * 3, w * 3);
            return c;
        }

        // ---- A/B 1: NR linear (pré-stretch). Tone igual nos dois (mid fixo) ----
        var nrA = CropLin();
        var nrB = CropLin();
        AstroPipeline.DenoiseLinear(nrB, 1.0);
        double mid = AstroPipeline.ComputeMtfMid(nrA, p);   // mesmo midpoint p/ comparação justa
        foreach (var (img2, tag) in new[] { (nrA, "a_off"), (nrB, "b_on") })
        {
            AstroPipeline.Stretch(img2, p, fixedMid: mid);
            AstroPipeline.Scnr(img2, p.Scnr);
            AstroPipeline.SaturationAndCurve(img2, p.Saturation);
            File.WriteAllBytes($"testdata/ab_nrlinear_{tag}.jpg", PreviewRenderer.EncodeJpeg(img2, 92));
        }
        Console.WriteLine("  NR linear  -> testdata/ab_nrlinear_a_off.jpg / _b_on.jpg");

        // ---- separação de estrelas (uma vez) sobre o crop esticado ----
        var toned = CropLin();
        AstroPipeline.Stretch(toned, p);
        AstroPipeline.Scnr(toned, p.Scnr);
        AstroPipeline.SaturationAndCurve(toned, p.Saturation);
        var sw = Stopwatch.StartNew();
        var starless = StarRemoval.Starless(toned);
        var stars = StarRemoval.StarsLayer(toned, starless);
        sw.Stop();
        Console.WriteLine($"  separação {w}x{h}: {sw.ElapsedMilliseconds} ms");

        // ---- A/B 2: contraste local do fundo ----
        var lcA = new StarWorkflow { Starless = starless, Stars = stars };
        var lcB = new StarWorkflow { Starless = starless, Stars = stars, LocalContrast = 0.8 };
        File.WriteAllBytes("testdata/ab_localcontrast_a_off.jpg", PreviewRenderer.EncodeImage(lcA.Compose(), 92));
        File.WriteAllBytes("testdata/ab_localcontrast_b_on.jpg", PreviewRenderer.EncodeImage(lcB.Compose(), 92));
        Console.WriteLine("  contraste local -> testdata/ab_localcontrast_a_off.jpg / _b_on.jpg");

        // ---- A/B 3: redução + saturação de estrelas ----
        var srA = new StarWorkflow { Starless = starless, Stars = stars };
        var srB = new StarWorkflow { Starless = starless, Stars = stars, StarReduction = 0.9, StarSaturation = 1.3 };
        File.WriteAllBytes("testdata/ab_starreduce_a_off.jpg", PreviewRenderer.EncodeImage(srA.Compose(), 92));
        File.WriteAllBytes("testdata/ab_starreduce_b_on.jpg", PreviewRenderer.EncodeImage(srB.Compose(), 92));
        Console.WriteLine("  redução estrelas -> testdata/ab_starreduce_a_off.jpg / _b_on.jpg");
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

    static LinearImage MakeSynthetic(int w, int h)
    {
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                data[i + 0] = (float)x / (w - 1);
                data[i + 1] = (float)y / (h - 1);
                data[i + 2] = (float)(x + y) / (w + h - 2);
            }
        return new LinearImage { Width = w, Height = h, Data = data };
    }

    static void Resample()
    {
        Console.WriteLine("== Resample (drizzle) ==");
        var img = MakeSynthetic(64, 48);

        var r1 = PreviewRenderer.Resample(img, 1);
        if (r1.Width != 64 || r1.Height != 48)
            throw new Exception($"factor 1 mudou dims: {r1.Width}x{r1.Height}");

        var r2 = PreviewRenderer.Resample(img, 2);
        if (r2.Width != 32 || r2.Height != 24)
            throw new Exception($"factor 2: esperado 32x24, obtido {r2.Width}x{r2.Height}");

        Console.WriteLine($"  factor1={r1.Width}x{r1.Height} factor2={r2.Width}x{r2.Height} -> OK");
    }

    static LinearImage MakeNoisy(int w, int h)
    {
        var rng = new Random(1);
        var data = new float[w * h * 3];
        for (int i = 0; i < data.Length; i++)
            data[i] = (float)Math.Clamp(0.3 + (rng.NextDouble() - 0.5) * 0.4, 0, 1);
        return new LinearImage { Width = w, Height = h, Data = data };
    }

    static void NrLinear()
    {
        Console.WriteLine("== NR linear (no-op + efeito) ==");
        var img = MakeNoisy(128, 96);

        var a = img.Clone();
        AstroPipeline.DenoiseLinear(a, 0);
        for (int i = 0; i < a.Data.Length; i++)
            if (a.Data[i] != img.Data[i])
                throw new Exception("strength 0 alterou os dados (devia ser no-op)");

        var b = img.Clone();
        AstroPipeline.DenoiseLinear(b, 1);
        double diff = 0;
        for (int i = 0; i < b.Data.Length; i++) diff += Math.Abs(b.Data[i] - img.Data[i]);
        if (diff <= 0) throw new Exception("strength 1 não alterou nada");

        Console.WriteLine($"  no-op OK; strength1 soma|Δ|={diff:F2} -> OK");
    }

    static void StarProc()
    {
        Console.WriteLine("== Contraste local (no-op + efeito) ==");
        var img = MakeNoisy(128, 96);

        var a = img.Clone();
        AstroPipeline.LocalContrast(a, 0);
        for (int i = 0; i < a.Data.Length; i++)
            if (a.Data[i] != img.Data[i])
                throw new Exception("LocalContrast 0 não é no-op");

        var b = img.Clone();
        AstroPipeline.LocalContrast(b, 0.8);
        double diff = 0;
        for (int i = 0; i < b.Data.Length; i++) diff += Math.Abs(b.Data[i] - img.Data[i]);
        if (diff <= 0) throw new Exception("LocalContrast 0.8 não alterou nada");

        Console.WriteLine($"  no-op OK; soma|Δ|={diff:F2} -> OK");
    }

    static LinearImage MakeStarField(int w, int h)
    {
        var data = new float[w * h * 3];                 // fundo preto
        (int cx, int cy)[] stars = { (60, 60), (180, 90), (120, 200) };
        foreach (var (cx, cy) in stars)
            for (int y = -6; y <= 6; y++)
                for (int x = -6; x <= 6; x++)
                {
                    int px = cx + x, py = cy + y;
                    if (px < 0 || py < 0 || px >= w || py >= h) continue;
                    if (x * x + y * y > 36) continue;     // disco r=6
                    int i = (py * w + px) * 3;
                    data[i] = data[i + 1] = data[i + 2] = 0.9f;
                }
        return new LinearImage { Width = w, Height = h, Data = data };
    }

    static void ReduceStarsTest()
    {
        Console.WriteLine("== Redução de estrelas (no-op + encolhe) ==");
        var stars = MakeStarField(256, 256);

        var r0 = StarRemoval.ReduceStars(stars, 0);
        for (int i = 0; i < r0.Data.Length; i++)
            if (r0.Data[i] != stars.Data[i])
                throw new Exception("amount 0 não é no-op");

        var r1 = StarRemoval.ReduceStars(stars, 1);
        double s0 = 0, s1 = 0;
        for (int i = 0; i < stars.Data.Length; i++) { s0 += stars.Data[i]; s1 += r1.Data[i]; }
        if (s1 >= s0) throw new Exception($"erosão não reduziu energia: {s1:F1} >= {s0:F1}");

        Console.WriteLine($"  no-op OK; soma estrelas {s0:F0} -> {s1:F0} (menor) -> OK");
    }

    static void WcsTest()
    {
        Console.WriteLine("== WcsSolution (parse + projeção TAN) ==");
        const string header =
            "CTYPE1  = 'RA---TAN'\nCTYPE2  = 'DEC--TAN'\n" +
            "CRPIX1  =  3.057500000000E+003\nCRPIX2  =  2.040500000000E+003\n" +
            "CRVAL1  =  2.458169582032E+002\nCRVAL2  = -2.495790306081E+001\n" +
            "CD1_1   = -1.835458761921E-004\nCD1_2   =  1.318058558158E-003\n" +
            "CD2_1   =  1.319524801447E-003\nCD2_2   =  1.846106534218E-004\nPLTSOLVD=T\n";

        var w = WcsSolution.Parse(header) ?? throw new Exception("Parse devolveu null");

        // o pixel de referência projeta-se para si próprio
        var (px, py) = w.WorldToPixel(245.8169582032, -24.9579030608);
        if (Math.Abs(px - 3057.5) > 0.01 || Math.Abs(py - 2040.5) > 0.01)
            throw new Exception($"WorldToPixel(CRVAL) = ({px:F3},{py:F3}), esperado (3057.5,2040.5)");

        // round-trip pixel -> world -> pixel
        var (ra, dec) = w.PixelToWorld(1000, 1500);
        var (rx, ry) = w.WorldToPixel(ra, dec);
        if (Math.Abs(rx - 1000) > 0.01 || Math.Abs(ry - 1500) > 0.01)
            throw new Exception($"round-trip falhou: ({rx:F3},{ry:F3}) != (1000,1500)");

        Console.WriteLine($"  centro = {w.CenterRaDeg:F4}, {w.CenterDecDeg:F4}");
        Console.WriteLine($"  escala = {w.ScaleArcsecPerPixel:F3} arcsec/px, orientação = {w.OrientationDeg:F2}°");
        Console.WriteLine("  WorldToPixel(CRVAL)≈CRPIX OK; round-trip OK");
    }

    static void FovTest()
    {
        Console.WriteLine("== PlateSolve.FovHeightDeg + FindAstap ==");
        // RedCat 51 (≈247mm efetiva) + Canon RP (5.74µm), altura 4080 px ≈ 5.5°
        double fov = PlateSolve.FovHeightDeg(247.0, 5.74, 4080);
        if (fov < 5.0 || fov > 6.0)
            throw new Exception($"FOV {fov:F2}° fora do esperado (~5.5°)");
        // inputs inválidos → 0 (modo auto)
        if (PlateSolve.FovHeightDeg(0, 5.74, 4080) != 0)
            throw new Exception("focal 0 devia dar FOV 0 (auto)");
        Console.WriteLine($"  FOV(247mm,5.74µm,4080px) = {fov:F2}° ; astap = {PlateSolve.FindAstap(null) ?? "(não encontrado)"}");
    }

    static void SettingsTest()
    {
        Console.WriteLine("== AppSettings round-trip ==");
        var s = AppSettings.Load();
        double origFocal = s.FocalLengthMm;
        s.FocalLengthMm = 247.0; s.PixelSizeUm = 5.74; s.Save();
        var s2 = AppSettings.Load();
        if (Math.Abs(s2.FocalLengthMm - 247.0) > 1e-6 || Math.Abs(s2.PixelSizeUm - 5.74) > 1e-6)
            throw new Exception($"persistência falhou: {s2.FocalLengthMm}/{s2.PixelSizeUm}");
        s.FocalLengthMm = origFocal; s.Save();   // restaurar
        Console.WriteLine("  guardou e releu focal/píxel OK");
    }

    static void SolveTest(string path)
    {
        Console.WriteLine($"== Plate solve end-to-end: {path} ==");
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);

        var sw = Stopwatch.StartNew();
        bool ok = PlateSolve.TrySolve(img, null, 247.0, 5.74, out var wcs, out var status);
        sw.Stop();
        Console.WriteLine($"  {status} em {sw.ElapsedMilliseconds} ms");
        if (!ok || wcs is null) throw new Exception($"solve falhou: {status}");
        Console.WriteLine($"  centro = {wcs.CenterRaDeg:F4}, {wcs.CenterDecDeg:F4}");
        Console.WriteLine($"  escala = {wcs.ScaleArcsecPerPixel:F2}\"/px · FOV {wcs.FovWidthDeg(img.Width):F1}×{wcs.FovHeightDeg(img.Height):F1}° · orient {wcs.OrientationDeg:F1}°");
    }

    static void DsoParse()
    {
        Console.WriteLine("== FieldAnnotation.ParseDeepSky ==");
        const string txt =
            "ASTAP DEEPSKY (cabeçalho a ignorar)\n" +
            "RA[0..864000], DEC, name, length, width, orient\n" +
            "57,324000,NP_2000\n" +                       // polo: ignorar (sem coords úteis? ainda parseia, mas sem tamanho=estrela)
            "593645,-95155,Antares/α_Sco\n" +             // estrela nomeada (sem tamanho)
            "590155,-95489,M4/NGC6121,360\n" +            // DSO circular 36'
            "658140,-67140,M24/IC4715,1200,450,45\n";     // DSO elipse 120x45 @45

        var objs = FieldAnnotation.ParseDeepSky(txt);
        var antares = objs.Find(o => o.Name.StartsWith("Antares"))!;
        var m4 = objs.Find(o => o.Name.StartsWith("M4"))!;
        var m24 = objs.Find(o => o.Name.StartsWith("M24"))!;

        if (Math.Abs(antares.RaDeg - 247.3521) > 0.01 || Math.Abs(antares.DecDeg + 26.4319) > 0.01)
            throw new Exception($"Antares mal projetado: {antares.RaDeg},{antares.DecDeg}");
        if (!antares.IsStar) throw new Exception("Antares devia ser estrela (sem tamanho)");
        if (m4.IsStar || Math.Abs(m4.LengthArcmin - 36.0) > 0.01)
            throw new Exception($"M4 devia ser DSO 36': {m4.LengthArcmin} star={m4.IsStar}");
        if (Math.Abs(m24.WidthArcmin - 45.0) > 0.01 || Math.Abs(m24.AngleDeg - 45.0) > 0.01)
            throw new Exception($"M24 elipse errada: {m24.WidthArcmin}/{m24.AngleDeg}");
        Console.WriteLine($"  {objs.Count} objetos; Antares⭑ {antares.RaDeg:F2},{antares.DecDeg:F2}; M4 {m4.LengthArcmin:F1}' -> OK");
    }

    static void AnnotBuild()
    {
        Console.WriteLine("== FieldAnnotation.Build (projeção + grelha) ==");
        const string header =
            "CTYPE1='RA---TAN'\nCTYPE2='DEC--TAN'\n" +
            "CRPIX1=3.057500000000E+003\nCRPIX2=2.040500000000E+003\n" +
            "CRVAL1=2.458169582032E+002\nCRVAL2=-2.495790306081E+001\n" +
            "CD1_1=-1.835458761921E-004\nCD1_2=1.318058558158E-003\n" +
            "CD2_1=1.319524801447E-003\nCD2_2=1.846106534218E-004\nPLTSOLVD=T\n";
        var wcs = WcsSolution.Parse(header)!;
        int W = 6114, H = 4080;

        // astapDir null → sem catálogo → só grelha
        var noCat = FieldAnnotation.Build(wcs, W, H, null);
        if (noCat.Objects.Count != 0) throw new Exception("sem catálogo devia ter 0 objetos");
        if (noCat.Grid.Count == 0) throw new Exception("devia ter linhas de grelha");
        if (noCat.Width != W || noCat.Height != H) throw new Exception("dims erradas");

        // com o catálogo real do ASTAP (M4 ~245.9/-26.5 está no campo de Rho Oph)
        string? astapDir = System.IO.Path.GetDirectoryName(PlateSolve.FindAstap(null) ?? "");
        var full = FieldAnnotation.Build(wcs, W, H, astapDir);
        Console.WriteLine($"  só grelha: {noCat.Grid.Count} linhas | com catálogo: {full.Objects.Count} objetos no campo");
        if (astapDir != null && full.Objects.Count == 0)
            throw new Exception("esperava objetos no campo de Rho Oph (M4, etc.)");
        // todos os objetos dentro da imagem
        foreach (var o in full.Objects)
            if (o.X < 0 || o.Y < 0 || o.X > W || o.Y > H)
                throw new Exception($"objeto fora da imagem: {o.Name} ({o.X},{o.Y})");
        Console.WriteLine("  -> OK");
    }

    static void AnnotateTest(string path)
    {
        Console.WriteLine($"== Anotação end-to-end: {path} ==");
        var img = TiffIO.LoadFloat(path);
        AstroPipeline.Normalize(img);
        img = AstroPipeline.Crop(img, 0.012);
        AstroPipeline.ExtractBackground(img, radial: true);
        AstroPipeline.ColorCalibrate(img);

        if (!PlateSolve.TrySolve(img, null, 247.0, 5.74, out var wcs, out var st) || wcs is null)
            throw new Exception($"solve falhou: {st}");
        string? astapDir = System.IO.Path.GetDirectoryName(PlateSolve.FindAstap(null) ?? "");
        var ov = FieldAnnotation.Build(wcs, img.Width, img.Height, astapDir);
        int stars = ov.Objects.Count(o => o.IsStar), dso = ov.Objects.Count - stars;
        Console.WriteLine($"  {dso} DSO, {stars} estrelas nomeadas, {ov.Grid.Count} linhas de grelha no campo");

        // queima o overlay num JPEG esticado (escala overlay→proxy) para inspeção
        var p = ToneParams.Defaults;
        AstroPipeline.Stretch(img, p); AstroPipeline.Scnr(img, p.Scnr); AstroPipeline.SaturationAndCurve(img, p.Saturation);
        var proxy = PreviewRenderer.MakeProxy(img);
        double k = (double)proxy.Width / ov.Width;
        using var mat = proxy.AsMat();
        using var bgr = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(mat, bgr, OpenCvSharp.ColorConversionCodes.RGB2BGR);
        using var u8 = new OpenCvSharp.Mat();
        bgr.ConvertTo(u8, OpenCvSharp.MatType.CV_8UC3, 255.0);
        foreach (var g in ov.Grid)
            for (int i = 1; i < g.Points.Count; i++)
                OpenCvSharp.Cv2.Line(u8, new((int)(g.Points[i - 1].X * k), (int)(g.Points[i - 1].Y * k)),
                    new((int)(g.Points[i].X * k), (int)(g.Points[i].Y * k)), new(165, 110, 58), 1);
        foreach (var o in ov.Objects)
            OpenCvSharp.Cv2.Ellipse(u8, new((int)(o.X * k), (int)(o.Y * k)),
                new((int)(Math.Max(o.WidthPx * k / 2, 3)), (int)(Math.Max(o.HeightPx * k / 2, 3))),
                o.AngleDeg, 0, 360, o.IsStar ? new(255, 208, 159) : new(65, 179, 224), 1);
        OpenCvSharp.Cv2.ImEncode(".jpg", u8, out byte[] buf);
        File.WriteAllBytes("testdata/annotate_result.jpg", buf);
        Console.WriteLine("  -> testdata/annotate_result.jpg");
    }
}
