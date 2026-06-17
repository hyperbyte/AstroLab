# Melhorias de estrelas, contraste local, NR linear e drizzle/resample — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adicionar NR linear opcional na Fase A, contraste local + redução/cor de estrelas no workflow de separação, e um prompt de drizzle com resample por fator ao abrir.

**Architecture:** As operações de tone vivem no núcleo (`AstroPipeline`); o workflow de estrelas vive no `StarWorkflow` e partilha `ProcessBackground`/`ProcessStars` entre o preview (proxy) e o export (full-res). O resample acontece logo após o load na Fase A. A junção `starless + stars` é sempre por **screen**.

**Tech Stack:** .NET 10, Blazor Server, OpenCvSharp4, ONNX Runtime (DirectML).

## Global Constraints

- Imagens são RGB float intercalado 0–1 (`LinearImage.Data`), 3 canais.
- A equivalência ao Python (`groundtruth.py`) **NÃO** se aplica — a Fase A está descongelada (decisão 2026-06-17).
- Junção final starless+stars: **sempre** `StarRemoval.Screen`.
- Não há framework de testes unitários. A verificação programática faz-se via subcomandos de consola em `Services/SelfTest.cs` (asserts que fazem `throw`), executados com `dotnet run -- <cmd>`; cada subcomando novo tem de ser adicionado à lista em `Program.cs:5`. Verificação visual final na app a correr.
- Defaults dos novos controlos = no-op: `LinearDenoise=0`, `LocalContrast=0`, `StarReduction=0`, `StarSaturation=1.0`, `DrizzleFactor=1`.
- Cultura invariante já é tratada pelo `SliderControl`; não acrescentar parsing.

---

### Task 1: Resample por drizzle (núcleo + selftest)

**Files:**
- Modify: `Services/PreviewRenderer.cs` (novo método `Resample`)
- Modify: `Services/ProcessingSession.cs` (campo `DrizzleFactor` + aplicar na Fase A)
- Modify: `Services/SelfTest.cs` (subcomando `resample` + helper sintético)
- Modify: `Program.cs:5` (registar `resample`)

**Interfaces:**
- Produces: `PreviewRenderer.Resample(LinearImage img, int factor) -> LinearImage` (factor≤1 devolve `img` tal-e-qual; senão reduz por `factor` com interpolação de área).
- Produces: `ProcessingSession.DrizzleFactor` (int, default 1).
- Consumes: `PreviewRenderer.MakeProxy(LinearImage, int maxSide)` (já existe).

- [ ] **Step 1: Adicionar helper sintético + subcomando de teste em SelfTest**

Em `Services/SelfTest.cs`, adicionar ao `switch` (antes de `default:`):

```csharp
                case "resample":
                    Resample();
                    break;
```

E adicionar os métodos (junto aos outros `static void`):

```csharp
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
```

- [ ] **Step 2: Registar o subcomando em Program.cs**

Em `Program.cs:5`, acrescentar `"resample"` à condição:

```csharp
if (args.Length > 0 && args[0] is "tiff-test" or "phasea" or "phaseb" or "fullb" or "bench" or "comatest" or "aitest" or "startest" or "resample")
    return SelfTest.Run(args);
```

- [ ] **Step 3: Correr o teste para confirmar que FALHA na compilação**

Run: `dotnet run -- resample`
Expected: erro de compilação — `PreviewRenderer` não contém `Resample`.

- [ ] **Step 4: Implementar `Resample` em PreviewRenderer**

Em `Services/PreviewRenderer.cs`, adicionar (a seguir a `MakeProxy`):

```csharp
    /// <summary>Reamostra por um fator de drizzle inteiro (≥2 reduz por área; ≤1 = no-op).
    /// Corre na Fase A, antes do resto. Ver design 2026-06-17.</summary>
    public static LinearImage Resample(LinearImage img, int factor)
    {
        if (factor <= 1) return img;
        int maxSide = (int)Math.Round(Math.Max(img.Width, img.Height) / (double)factor);
        return MakeProxy(img, maxSide);
    }
```

- [ ] **Step 5: Correr o teste para confirmar que PASSA**

Run: `dotnet run -- resample`
Expected: `factor1=64x48 factor2=32x24 -> OK`

- [ ] **Step 6: Adicionar `DrizzleFactor` e aplicar na Fase A**

Em `Services/ProcessingSession.cs`, junto a `Crop` (após a propriedade `Crop`):

```csharp
    /// <summary>Fator de drizzle/oversampling; >1 reduz a imagem no início da Fase A. Default 1.</summary>
    public int DrizzleFactor { get; set; } = 1;
```

E em `RunPhaseA`, dentro do `Task.Run`, logo a seguir ao load:

```csharp
                progress.Report(("a carregar TIF…", 0.05));
                var img = TiffIO.LoadFloat(path);

                if (DrizzleFactor > 1)
                {
                    progress.Report(("a reamostrar (drizzle)…", 0.15));
                    img = PreviewRenderer.Resample(img, DrizzleFactor);
                }

                progress.Report(("a normalizar…", 0.22));
                AstroPipeline.Normalize(img);
```

- [ ] **Step 7: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded` (0 erros).

- [ ] **Step 8: Commit**

```bash
git add Services/PreviewRenderer.cs Services/ProcessingSession.cs Services/SelfTest.cs Program.cs
git commit -m "feat: resample por fator de drizzle no inicio da Fase A"
```

---

### Task 2: NR linear na Fase A (núcleo + selftest)

**Files:**
- Modify: `Services/AstroPipeline.cs` (novo `DenoiseLinear`)
- Modify: `Services/ProcessingSession.cs` (campo `LinearDenoise` + aplicar após `ColorCalibrate`)
- Modify: `Services/SelfTest.cs` (subcomando `nrlinear` + helper de ruído)
- Modify: `Program.cs:5` (registar `nrlinear`)

**Interfaces:**
- Produces: `AstroPipeline.DenoiseLinear(LinearImage img, double strength)` (in-place; strength≤0 = no-op).
- Produces: `ProcessingSession.LinearDenoise` (double, default 0).

- [ ] **Step 1: Adicionar helper de ruído + subcomando de teste**

Em `Services/SelfTest.cs`, adicionar ao `switch`:

```csharp
                case "nrlinear":
                    NrLinear();
                    break;
```

E os métodos:

```csharp
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
```

- [ ] **Step 2: Registar o subcomando em Program.cs**

Em `Program.cs:5`, acrescentar `"nrlinear"`:

```csharp
if (args.Length > 0 && args[0] is "tiff-test" or "phasea" or "phaseb" or "fullb" or "bench" or "comatest" or "aitest" or "startest" or "resample" or "nrlinear")
    return SelfTest.Run(args);
```

- [ ] **Step 3: Correr para confirmar que FALHA na compilação**

Run: `dotnet run -- nrlinear`
Expected: erro de compilação — `AstroPipeline` não contém `DenoiseLinear`.

- [ ] **Step 4: Implementar `DenoiseLinear`**

Em `Services/AstroPipeline.cs`, adicionar a seguir a `Denoise`:

```csharp
    /// <summary>NR LINEAR (pré-stretch), gentil: chroma gaussian σ=2 + luma bilateral
    /// suave, blend por strength. Corre na Fase A (1×, full-res). strength≤0 = no-op.
    /// Mais gentil que Denoise porque em linear o sinal está comprimido nas sombras.</summary>
    public static void DenoiseLinear(LinearImage img, double strength)
    {
        if (strength <= 0) return;
        int H = img.Height, W = img.Width, N = H * W;
        var d = img.Data;
        float s = (float)Math.Clamp(strength, 0, 1);

        var luma = new float[N];
        var chroma = new float[N * 3];
        for (int i = 0; i < N; i++)
        {
            luma[i] = 0.2126f * d[i * 3] + 0.7152f * d[i * 3 + 1] + 0.0722f * d[i * 3 + 2];
            for (int c = 0; c < 3; c++) chroma[i * 3 + c] = d[i * 3 + c] - luma[i];
        }

        // cromático: gaussian σ=2 por canal, blend 0.9·s
        var plane = new float[N];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < N; i++) plane[i] = chroma[i * 3 + c];
            using var m = Mat.FromPixelData(H, W, MatType.CV_32FC1, plane);
            using var ms = new Mat();
            Cv2.GaussianBlur(m, ms, new Size(), 2);
            ms.GetArray(out float[] sm);
            float w = 0.9f * s;
            for (int i = 0; i < N; i++) chroma[i * 3 + c] = chroma[i * 3 + c] * (1 - w) + sm[i] * w;
        }

        // luminância: bilateral suave, blend 0.6·s
        float[] ls;
        using (var mL = Mat.FromPixelData(H, W, MatType.CV_32FC1, luma))
        using (var m1 = new Mat())
        {
            Cv2.BilateralFilter(mL, m1, 5, 0.02, 4);
            m1.GetArray(out ls);
        }
        float wl = 0.6f * s;
        for (int i = 0; i < N; i++)
        {
            float l = luma[i] * (1 - wl) + ls[i] * wl;
            for (int c = 0; c < 3; c++) d[i * 3 + c] = Math.Clamp(l + chroma[i * 3 + c], 0f, 1f);
        }
    }
```

- [ ] **Step 5: Correr para confirmar que PASSA**

Run: `dotnet run -- nrlinear`
Expected: `no-op OK; strength1 soma|Δ|=… -> OK`

- [ ] **Step 6: Adicionar `LinearDenoise` e aplicar na Fase A**

Em `Services/ProcessingSession.cs`, junto a `DrizzleFactor`:

```csharp
    /// <summary>Força de NR linear aplicada na Fase A (pré-stretch). 0 = off. Default 0.</summary>
    public double LinearDenoise { get; set; } = 0;
```

E em `RunPhaseA`, a seguir ao `ColorCalibrate` e antes do proxy:

```csharp
                progress.Report(("a calibrar cor…", 0.80));
                AstroPipeline.ColorCalibrate(img);

                if (LinearDenoise > 0)
                {
                    progress.Report(("a reduzir ruído (linear)…", 0.88));
                    AstroPipeline.DenoiseLinear(img, LinearDenoise);
                }

                progress.Report(("a gerar proxy…", 0.95));
```

- [ ] **Step 7: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 8: Commit**

```bash
git add Services/AstroPipeline.cs Services/ProcessingSession.cs Services/SelfTest.cs Program.cs
git commit -m "feat: NR linear opcional na Fase A (apos calibracao de cor)"
```

---

### Task 3: Contraste local + processamento de fundo no StarWorkflow (núcleo + selftest)

**Files:**
- Modify: `Services/AstroPipeline.cs` (novo `LocalContrast`)
- Modify: `Services/StarWorkflow.cs` (campo `LocalContrast`, método `ProcessBackground`, usar em `Compose`/`ApplyToFull`)
- Modify: `Services/SelfTest.cs` (subcomando `starproc`)
- Modify: `Program.cs:5` (registar `starproc`)

**Interfaces:**
- Produces: `AstroPipeline.LocalContrast(LinearImage img, double amount)` (in-place; amount≤0 = no-op).
- Produces: `StarWorkflow.LocalContrast` (double, default 0).
- Produces: `StarWorkflow.ProcessBackground(LinearImage img)` (in-place: ganho + saturação + contraste local).
- Consumes: `StarWorkflow.AdjustBackground(LinearImage, double gain, double sat)` (já existe, static).

- [ ] **Step 1: Adicionar subcomando de teste**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "starproc":
                    StarProc();
                    break;
```

E o método (usa `MakeNoisy` da Task 2):

```csharp
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
```

- [ ] **Step 2: Registar o subcomando em Program.cs**

Em `Program.cs:5`, acrescentar `"starproc"`:

```csharp
if (args.Length > 0 && args[0] is "tiff-test" or "phasea" or "phaseb" or "fullb" or "bench" or "comatest" or "aitest" or "startest" or "resample" or "nrlinear" or "starproc")
    return SelfTest.Run(args);
```

- [ ] **Step 3: Correr para confirmar que FALHA na compilação**

Run: `dotnet run -- starproc`
Expected: erro de compilação — `AstroPipeline` não contém `LocalContrast`.

- [ ] **Step 4: Implementar `LocalContrast`**

Em `Services/AstroPipeline.cs`, a seguir a `ComaCorrect`:

```csharp
    /// <summary>Contraste local: unsharp de raio GRANDE na luminância, ratiométrico
    /// (preserva cor). amount≤0 = no-op. sigma escala com o lado maior (efeito igual
    /// proxy↔full). Usa OpenCvSharp.</summary>
    public static void LocalContrast(LinearImage img, double amount)
    {
        if (amount <= 0) return;
        int H = img.Height, W = img.Width, N = W * H;
        var d = img.Data;
        float sigma = (float)(Math.Max(W, H) * 0.02);

        var luma = new float[N];
        for (int i = 0; i < N; i++)
            luma[i] = 0.2126f * d[i * 3] + 0.7152f * d[i * 3 + 1] + 0.0722f * d[i * 3 + 2];

        using var m = Mat.FromPixelData(H, W, MatType.CV_32FC1, luma);
        using var mb = new Mat();
        Cv2.GaussianBlur(m, mb, new Size(), sigma);
        mb.GetArray(out float[] blur);

        float a = (float)amount;
        Parallel.For(0, N, i =>
        {
            float l = luma[i];
            float ln = Math.Clamp(l + a * (l - blur[i]), 0f, 1f);
            float ratio = l > 1e-6f ? ln / l : 1f;
            for (int c = 0; c < 3; c++) d[i * 3 + c] = Math.Clamp(d[i * 3 + c] * ratio, 0f, 1f);
        });
    }
```

- [ ] **Step 5: Correr para confirmar que PASSA**

Run: `dotnet run -- starproc`
Expected: `no-op OK; soma|Δ|=… -> OK`

- [ ] **Step 6: Adicionar `LocalContrast` + `ProcessBackground` ao StarWorkflow**

Em `Services/StarWorkflow.cs`, adicionar campo junto a `Gain`/`Saturation`:

```csharp
    public double LocalContrast = 0;  // contraste local do fundo (unsharp raio grande)
```

Adicionar o método (a seguir a `BackgroundBase`):

```csharp
    /// <summary>Processa o FUNDO in-place: ganho + saturação + contraste local.
    /// Partilhado entre preview (proxy) e export (full-res).</summary>
    public void ProcessBackground(LinearImage img)
    {
        AdjustBackground(img, Gain, Saturation);
        AstroPipeline.LocalContrast(img, LocalContrast);
    }
```

- [ ] **Step 7: Usar `ProcessBackground` em `Compose` e `ApplyToFull`**

Em `Services/StarWorkflow.cs`, substituir o corpo de `Compose`:

```csharp
    public LinearImage Compose()
    {
        var bg = BackgroundBase.Clone();
        ProcessBackground(bg);
        return StarRemoval.Screen(bg, Stars);
    }
```

E em `ApplyToFull`, substituir a última linha `AdjustBackground(fullStarless, Gain, Saturation);` por:

```csharp
        ProcessBackground(fullStarless);
```

- [ ] **Step 8: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 9: Commit**

```bash
git add Services/AstroPipeline.cs Services/StarWorkflow.cs Services/SelfTest.cs Program.cs
git commit -m "feat: contraste local do fundo via ProcessBackground partilhado"
```

---

### Task 4: Redução + cor de estrelas no StarWorkflow (núcleo + selftest)

**Files:**
- Modify: `Services/StarRemoval.cs` (novo `ReduceStars` + imports OpenCvSharp/Marshal)
- Modify: `Services/StarWorkflow.cs` (campos `StarReduction`/`StarSaturation`, método `ProcessStars`, usar em `Compose`)
- Modify: `Services/ExportService.cs` (recombinar via `ProcessStars`)
- Modify: `Services/SelfTest.cs` (subcomando `reducestars` + campo de estrelas sintético)
- Modify: `Program.cs:5` (registar `reducestars`)

**Interfaces:**
- Produces: `StarRemoval.ReduceStars(LinearImage stars, double amount) -> LinearImage` (devolve nova imagem; amount≤0 = clone inalterado).
- Produces: `StarWorkflow.StarReduction` (double, default 0), `StarWorkflow.StarSaturation` (double, default 1.0).
- Produces: `StarWorkflow.ProcessStars(LinearImage stars) -> LinearImage` (redução + saturação; não muta o input).
- Consumes: `StarRemoval.Screen`, `StarRemoval.Starless`, `StarRemoval.StarsLayer` (já existem).

- [ ] **Step 1: Adicionar campo de estrelas sintético + subcomando de teste**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "reducestars":
                    ReduceStarsTest();
                    break;
```

E os métodos:

```csharp
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
```

- [ ] **Step 2: Registar o subcomando em Program.cs**

Em `Program.cs:5`, acrescentar `"reducestars"`:

```csharp
if (args.Length > 0 && args[0] is "tiff-test" or "phasea" or "phaseb" or "fullb" or "bench" or "comatest" or "aitest" or "startest" or "resample" or "nrlinear" or "starproc" or "reducestars")
    return SelfTest.Run(args);
```

- [ ] **Step 3: Correr para confirmar que FALHA na compilação**

Run: `dotnet run -- reducestars`
Expected: erro de compilação — `StarRemoval` não contém `ReduceStars`.

- [ ] **Step 4: Implementar `ReduceStars`**

Em `Services/StarRemoval.cs`, adicionar os imports no topo (a seguir aos `using` existentes):

```csharp
using OpenCvSharp;
using System.Runtime.InteropServices;
```

E o método (a seguir a `Screen`):

```csharp
    /// <summary>Reduz estrelas por erosão morfológica + blend. amount≤0 devolve cópia
    /// inalterada. O raio do kernel escala com a resolução (efeito igual proxy↔full).</summary>
    public static LinearImage ReduceStars(LinearImage stars, double amount)
    {
        if (amount <= 0) return stars.Clone();
        int W = stars.Width, H = stars.Height;
        int r = Math.Max(1, (int)Math.Round(Math.Max(W, H) * 0.0006));

        using var src = stars.AsMat();                   // CV_32FC3 RGB; stars.Data vivo neste escopo
        using var ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1));
        using var er = new Mat();
        Cv2.Erode(src, er, ker);

        var eroded = new float[(long)W * H * 3];
        Marshal.Copy(er.Data, eroded, 0, eroded.Length);

        float a = (float)Math.Clamp(amount, 0, 1);
        var sd = stars.Data;
        var outD = new float[(long)W * H * 3];
        for (int i = 0; i < outD.Length; i++) outD[i] = sd[i] * (1 - a) + eroded[i] * a;
        return new LinearImage { Width = W, Height = H, Data = outD };
    }
```

- [ ] **Step 5: Correr para confirmar que PASSA**

Run: `dotnet run -- reducestars`
Expected: `no-op OK; soma estrelas … -> … (menor) -> OK`

- [ ] **Step 6: Adicionar params + `ProcessStars` ao StarWorkflow e usar em `Compose`**

Em `Services/StarWorkflow.cs`, junto a `LocalContrast`:

```csharp
    public double StarReduction = 0;     // redução de estrelas (erosão), 0–1
    public double StarSaturation = 1.0;  // saturação da camada de estrelas
```

Adicionar o método (a seguir a `ProcessBackground`):

```csharp
    /// <summary>Processa as ESTRELAS: redução (erosão) + saturação. Devolve nova imagem
    /// (não muta o input). Partilhado entre preview e export.</summary>
    public LinearImage ProcessStars(LinearImage stars)
    {
        var s = StarRemoval.ReduceStars(stars, StarReduction);
        AdjustBackground(s, 1.0, StarSaturation);
        return s;
    }
```

Atualizar `Compose` para processar as estrelas:

```csharp
    public LinearImage Compose()
    {
        var bg = BackgroundBase.Clone();
        ProcessBackground(bg);
        return StarRemoval.Screen(bg, ProcessStars(Stars));
    }
```

- [ ] **Step 7: Usar `ProcessStars` no export**

Em `Services/ExportService.cs`, no ramo `if (stars is not null)`, substituir a linha
`img = StarRemoval.Screen(starless, starsLayer);` por:

```csharp
                img = StarRemoval.Screen(starless, stars.ProcessStars(starsLayer));
```

- [ ] **Step 8: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 9: Commit**

```bash
git add Services/StarRemoval.cs Services/StarWorkflow.cs Services/ExportService.cs Services/SelfTest.cs Program.cs
git commit -m "feat: reducao (erosao) e saturacao de estrelas via ProcessStars"
```

---

### Task 5: UI — sliders de NR linear, contraste local, redução e saturação de estrelas

**Files:**
- Modify: `Components/Pages/Editor.razor` (sliders + handlers + reset)

**Interfaces:**
- Consumes: `ProcessingSession.LinearDenoise`, `ProcessingSession.ReprocessAsync`, `StarWorkflow.LocalContrast`, `StarWorkflow.StarReduction`, `StarWorkflow.StarSaturation` (Tasks 2–4).
- Consumes: `OnStarParam(Action)`, `RunPhaseA(Func<Task>)`, `MakeProgress()`, `ComposeStarPreview()` (já existem em Editor.razor).

- [ ] **Step 1: Adicionar o slider de NR linear na secção "Fundo"**

Em `Components/Pages/Editor.razor`, dentro da `<section>` do "Fundo", a seguir à `<div class="slider-note">` do toggle radial, adicionar:

```razor
            <SliderControl Label="NR linear (antes do stretch)" Min="0" Max="1" Step="0.05" Default="0"
                           Disabled="@_busy"
                           Note="recorre a Fase A (mais lento) — reduz ruído no domínio linear"
                           Value="Session.LinearDenoise"
                           ValueChanged="OnLinearDenoise" />
```

- [ ] **Step 2: Adicionar o handler `OnLinearDenoise`**

No bloco `@code` de `Editor.razor`, junto a `OnRadialToggle`:

```csharp
    async Task OnLinearDenoise(double v)
    {
        Session.LinearDenoise = v;
        if (Session.IsLoaded)
            await RunPhaseA(() => Session.ReprocessAsync(MakeProgress()));
    }
```

- [ ] **Step 3: Adicionar os três sliders de estrelas no bloco "Modo fundo"**

Em `Components/Pages/Editor.razor`, dentro do `<div class="star-mode">`, a seguir ao slider "Saturação fundo" e antes do botão "Sair", adicionar:

```razor
                    <SliderControl Label="Contraste local (fundo)" Min="0" Max="1" Step="0.05" Default="0"
                                   Value="Session.Stars.LocalContrast"
                                   ValueChanged="@(v => OnStarParam(() => Session.Stars!.LocalContrast = v))" />
                    <SliderControl Label="Redução de estrelas" Min="0" Max="1" Step="0.05" Default="0"
                                   Value="Session.Stars.StarReduction"
                                   ValueChanged="@(v => OnStarParam(() => Session.Stars!.StarReduction = v))" />
                    <SliderControl Label="Saturação estrelas" Min="0" Max="2.5" Step="0.05" Default="1.0"
                                   Value="Session.Stars.StarSaturation"
                                   ValueChanged="@(v => OnStarParam(() => Session.Stars!.StarSaturation = v))" />
```

- [ ] **Step 4: Repor os params de estrelas no `ResetDefaults`**

Em `Components/Pages/Editor.razor`, substituir o método `ResetDefaults` por:

```csharp
    async Task ResetDefaults()
    {
        Session.Params = ToneParams.Defaults;
        if (Session.Stars is not null)
        {
            Session.Stars.Gain = 1.0;
            Session.Stars.Saturation = 1.0;
            Session.Stars.LocalContrast = 0;
            Session.Stars.StarReduction = 0;
            Session.Stars.StarSaturation = 1.0;
            ComposeStarPreview();
            await InvokeAsync(StateHasChanged);
            return;
        }
        await ScheduleRender();
        await InvokeAsync(StateHasChanged);   // atualiza posições dos sliders
    }
```

- [ ] **Step 5: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 6: Verificação visual na app**

Run: `dotnet run`
Esperado (com a app aberta no browser, um TIF carregado):
- Slider "NR linear" a 0 → sem mudança; subir → reprocessa (barra de progresso) e o ruído de fundo suaviza.
- Após "Separar estrelas", os 3 sliders novos aparecem; a 0/default não alteram a imagem; subir cada um mostra efeito (contraste do fundo, estrelas menores, cor das estrelas).
- "Repor defaults" em modo fundo volta tudo ao estado base e atualiza o preview.

- [ ] **Step 7: Commit**

```bash
git add Components/Pages/Editor.razor
git commit -m "feat: sliders de NR linear, contraste local e processamento de estrelas"
```

---

### Task 6: UI — prompt de drizzle ao abrir

**Files:**
- Modify: `Components/Pages/Editor.razor` (modal de drizzle + interceção em `OpenFromPath`)

**Interfaces:**
- Consumes: `ProcessingSession.DrizzleFactor`, `ProcessingSession.Radial`, `ProcessingSession.OpenAsync` (Task 1 + existentes).
- Consumes: `RunPhaseA`, `MakeProgress`, `RecentFiles`, `_path`, `_radial` (já existem em Editor.razor).

- [ ] **Step 1: Intercetar `OpenFromPath` para abrir o modal**

Em `Components/Pages/Editor.razor`, substituir o método `OpenFromPath` por:

```csharp
    async Task OpenFromPath()
    {
        _error = null;
        if (!File.Exists(_path)) { _error = "Ficheiro não encontrado."; return; }
        _drizzleOpen = true;                 // pergunta o drizzle antes de correr a Fase A
        await InvokeAsync(StateHasChanged);
    }

    async Task ConfirmDrizzle(int factor)
    {
        _drizzleOpen = false;
        Session.Radial = _radial;
        Session.DrizzleFactor = factor;
        await RunPhaseA(() => Session.OpenAsync(_path, MakeProgress()), () =>
        {
            RecentFiles.Add(_path);
            _recents = RecentFiles.Load();
        });
    }
```

- [ ] **Step 2: Adicionar o campo de estado `_drizzleOpen`**

No bloco `@code` de `Editor.razor`, junto aos outros campos de modais (ex.: a seguir a `_nrOpen`):

```csharp
    bool _drizzleOpen;
```

- [ ] **Step 3: Adicionar o markup do modal**

Em `Components/Pages/Editor.razor`, a seguir ao bloco `@if (_nrOpen) { … }` (perto do fim, antes de `@code`), adicionar:

```razor
@if (_drizzleOpen)
{
    <div class="modal-backdrop" @onclick="@(() => _drizzleOpen = false)">
        <div class="modal modal-narrow" @onclick:stopPropagation="true">
            <div class="modal-head">
                <span>Drizzle / Resample</span>
                <button class="slider-reset" @onclick="@(() => _drizzleOpen = false)">🗙</button>
            </div>
            <div class="slider-note">
                Foi aplicado drizzle no empilhamento? Imagens drizzladas ou sobreamostradas
                (incl. de fontes não-DSLR) são reduzidas antes do processamento para recuperar SNR.
            </div>
            <div class="open-row" style="margin-top:.8rem">
                <button class="btn" @onclick="@(() => ConfirmDrizzle(1))">Nenhum</button>
                <button class="btn" @onclick="@(() => ConfirmDrizzle(2))">2×</button>
                <button class="btn" @onclick="@(() => ConfirmDrizzle(3))">3×</button>
            </div>
        </div>
    </div>
}
```

- [ ] **Step 4: Confirmar build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 5: Verificação visual na app**

Run: `dotnet run`
Esperado:
- Abrir por caminho, 📂, recente ou upload → aparece o modal de drizzle.
- "Nenhum" → carrega com as dimensões originais (igual ao comportamento atual).
- "2×"/"3×" → a barra de progresso mostra "a reamostrar (drizzle)…"; a imagem carregada tem dimensões ÷N (ver rodapé `W × H`); RAM proporcionalmente menor.

- [ ] **Step 6: Commit**

```bash
git add Components/Pages/Editor.razor
git commit -m "feat: prompt de drizzle (Nenhum/2x/3x) ao abrir ficheiro"
```

---

### Task 7: Reescrever o README.md (remover Python, documentar features novas)

**Files:**
- Modify: `README.md` (reescrita completa)

**Interfaces:**
- Sem código. Depende do estado final das Tasks 1–6 (documenta as features novas e os subcomandos de teste).

**Contexto:** o `groundtruth.py` deixou de ser regra (decisão 2026-06-17). O README atual tem referências a Python em 3 sítios (à validação groundtruth, ao requisito opcional Python 3, e à pasta `ReferenceCode/`) que têm de **desaparecer**. Além disso falta documentar: separação de estrelas, redução/cor de estrelas, contraste local, NR linear e o prompt de drizzle/resample.

- [ ] **Step 1: Substituir o conteúdo do README.md**

Reescrever `README.md` por completo com o seguinte conteúdo (preserva a secção de Licença/atribuições tal como está):

````markdown
# AstroLab

Aplicação local **.NET 10 / Blazor Server** para processamento interativo de stacks
lineares de astrofotografia (`Autosave.tif` do DeepSkyStacker). Abres um TIF, ajustas o
processamento com sliders em tempo real sobre um preview, e exportas em full-res
(TIF 16-bit + JPEG).

## Funcionalidades

- **Prompt de drizzle ao abrir** — escolhe Nenhum / 2× / 3×; imagens drizzladas ou
  sobreamostradas são reamostradas (÷N, por área) antes do processamento.
- **Fase A** (1× ao abrir): normalização, crop, extração de background (polinómio de 2ª
  ordem + termos radiais para vinhetagem, com fit robusto), calibração de cor, e
  **redução de ruído linear opcional** (pré-stretch).
- **Fase B** (tempo real sobre proxy 1536 px, ~100 ms/render): stretch arcsinh + MTF,
  SCNR, saturação seletiva, black point.
- **Redução de ruído** mascarada (bilateral + gaussiano) com pré-visualização a 100%.
- **Inspetor de campo 1:1** — grelha 3×3 (cantos, bordas e centro) que se adapta à janela.
- **Separação de estrelas** (StarNet/darkstar via ONNX) com workflow de fundo:
  clone stamp, ganho, saturação e **contraste local** no fundo sem estrelas;
  **redução (erosão) e saturação** na camada de estrelas; recombinação por *screen*.
- **Deconvolução estelar por IA** (classe BlurXTerminator), via ONNX Runtime + DirectML (GPU).
- **Export** TIF 16-bit (deflate) + JPEG q93, com barra de progresso.
- Abertura por **caminho**, **diálogo nativo** ou **upload**; tema escuro; recentes.

## Requisitos

- **Windows x64** (depende de `OpenCvSharp4.runtime.win`, ONNX Runtime DirectML e do
  diálogo nativo `comdlg32`).
- **.NET 10 SDK** — https://dotnet.microsoft.com/download
- **GPU compatível com DirectX 12** (recomendado) para a deconvolução por IA e a separação
  de estrelas. Sem GPU, correm em CPU (lento); o resto da app não precisa de GPU.

## Compilar e correr

```bash
# a partir da raiz do projeto
dotnet run -c Release
```

A app abre automaticamente o browser em **http://localhost:5151**. Para parar: `Ctrl+C`.

Em Windows, podes também fazer **duplo-clique em `AstroLab.cmd`**.

O primeiro arranque restaura os pacotes NuGet e compila (~10–20 s); os seguintes são rápidos.

> **Nota:** a app fixa a porta `localhost:5151` e ativa os *static web assets* no código, por
> isso funciona em qualquer ambiente (Development ou Production) e em qualquer forma de
> lançamento (`dotnet run`, DLL, ou exe publicado).

### Publicar (opcional)

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## Utilização

1. Indica o caminho do `Autosave.tif` (ou usa **📂 Procurar** / **upload**) e clica **Abrir**.
2. Responde ao prompt de **drizzle** (Nenhum / 2× / 3×).
3. Aguarda a Fase A (barra de progresso no preview).
4. Ajusta os sliders (Stretch, Céu, Black point, SCNR, Saturação, NR, NR linear).
5. Opcional: **Separar estrelas** e afinar o fundo (clone stamp, ganho, saturação,
   contraste local) e as estrelas (redução, saturação).
6. Usa **Inspeção 1:1** para avaliar foco/estrelas e ligar a **Deconvolução IA**.
7. **Exportar** gera `{prefixo}_16bit.tif` e `{prefixo}.jpg`.

### Modos CLI de teste

```bash
dotnet run -c Release -- tiff-test    <Autosave.tif>   # I/O + round-trip
dotnet run -c Release -- phasea       <Autosave.tif>   # medianas pós-background
dotnet run -c Release -- phaseb       <Autosave.tif>   # JPEG do proxy (defaults)
dotnet run -c Release -- bench        <Autosave.tif>   # tempos da Fase B
dotnet run -c Release -- startest     <Autosave.tif>   # separação de estrelas
dotnet run -c Release -- resample                       # resample por drizzle (sintético)
dotnet run -c Release -- nrlinear                       # NR linear (sintético)
dotnet run -c Release -- starproc                       # contraste local (sintético)
dotnet run -c Release -- reducestars                    # redução de estrelas (sintético)
```

## Estrutura

```
Components/      páginas e componentes Blazor (Editor, SliderControl)
Services/        TiffIO, AstroPipeline (núcleo), PreviewRenderer, ExportService,
                 ProcessingSession, StarRemoval/StarWorkflow, AiSharpen (ONNX),
                 NativeFileDialog, SelfTest
Models/          modelos ONNX (separação de estrelas e deconvolução estelar)
SPEC/            especificação do projeto
wwwroot/         tema (app.css), favicon, JS
```

## Licença

Copyright (C) 2026 hyperbyte (https://github.com/hyperbyte)

Este programa é software livre: podes redistribuí-lo e/ou modificá-lo nos termos da
**GNU General Public License versão 3** (GPL-3.0), conforme publicada pela Free Software
Foundation. Este programa é distribuído na expectativa de ser útil, mas **SEM QUALQUER
GARANTIA**. Vê o ficheiro [`LICENSE`](LICENSE) para o texto completo.

Os componentes de terceiros abaixo mantêm as suas próprias licenças (todas compatíveis
com a GPL-3.0); os respetivos avisos de copyright são preservados.

### Atribuições e licenças de terceiros

Esta aplicação usa componentes de terceiros, com gratidão:

| Componente | Versão | Licença | Fonte |
|---|---|---|---|
| .NET / ASP.NET Core / Blazor | 10 | MIT | https://github.com/dotnet |
| OpenCvSharp4 (+ runtime.win) | 4.13.0 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| OpenCV (binários nativos) | 4.x | Apache-2.0 | https://opencv.org |
| BitMiracle.LibTiff.NET | 2.4.660 | BSD (estilo libtiff) | https://github.com/BitMiracle/libtiff.net |
| Microsoft.ML.OnnxRuntime.DirectML | 1.24.4 | MIT | https://github.com/microsoft/onnxruntime |
| DirectML (redistribuível Microsoft) | — | Microsoft (via ONNX Runtime) | https://github.com/microsoft/DirectML |
| Bootstrap (template, em `wwwroot/lib`) | 5.x | MIT | https://github.com/twbs/bootstrap |

**Modelos de IA:** os modelos de separação de estrelas e de *sharpening* estelar em
`Models/` provêm do projeto **Cosmic Clarity** de Seti Astro (**licença MIT**), incluídos
para conveniência. Fonte: https://github.com/setiastro/cosmicclarity

> As licenças MIT/Apache/BSD exigem a preservação dos respetivos avisos de copyright. Os
> textos completos das licenças estão disponíveis nos repositórios acima.
````

- [ ] **Step 2: Confirmar que não restam referências a Python**

Run: `grep -ni "python\|groundtruth\|ReferenceCode" README.md`
Expected: sem resultados (saída vazia).

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: reescrever README (remover Python/groundtruth, documentar features novas)"
```

---

## Self-Review (preenchido)

**Cobertura do spec:**
- NR linear (spec §Algoritmos) → Task 2 + slider Task 5. ✓
- Contraste local → Task 3 + slider Task 5. ✓
- Redução + cor de estrelas → Task 4 + sliders Task 5. ✓
- Refactor `ProcessBackground`/`ProcessStars` partilhado proxy↔export → Tasks 3, 4 (Compose + ApplyToFull + ExportService). ✓
- Junção sempre por screen → mantida em `Compose` e `ExportService` (Task 4). ✓
- Drizzle/Resample + prompt → Task 1 (núcleo) + Task 6 (modal). ✓
- Escala de kernel/sigma proxy↔full → `ReduceStars` (×0.0006·maxSide) e `LocalContrast` (σ=0.02·maxSide). ✓

**Consistência de tipos:** `Resample(LinearImage,int)->LinearImage`, `DenoiseLinear(LinearImage,double)`, `LocalContrast(LinearImage,double)`, `ReduceStars(LinearImage,double)->LinearImage`, `ProcessBackground(LinearImage)`, `ProcessStars(LinearImage)->LinearImage` — usados de forma idêntica em todas as tasks. ✓

**Placeholders:** nenhum — todos os steps têm código/comando concretos. ✓
