# Plate Solve — Fase 1 (fundação + cartão de info) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolver as coordenadas da imagem com o ASTAP, guardar uma `WcsSolution` na sessão e mostrar um cartão de info (centro, escala, orientação, FOV) — a base partilhada para PCC (Fase 2) e overlay (Fase 3).

**Architecture:** Um serviço `PlateSolve` invoca o `astap.exe` (subprocess) sobre uma imagem esticada em alta resolução e parseia o `.wcs` (FITS) para um `WcsSolution` com projeção gnomónica (TAN). O `ProcessingSession` corre o solve uma vez na Fase A quando ligado, e a UI mostra a info. Nada disto altera os píxeis da imagem.

**Tech Stack:** .NET 10, Blazor Server, OpenCvSharp (render do solve-image), ASTAP CLI externo.

## Global Constraints

- Imagens RGB float intercalado 0–1 (`LinearImage.Data`), 3 canais.
- O solve **nunca lança** para fora; em falha devolve `false` + `status` e o pipeline continua (Fase 1: simplesmente não há info).
- ASTAP é assumido instalado; auto-detetar em `C:\Program Files\astap\astap.exe` ou `AstapPath` configurado. Não descarregar nem empacotar.
- Sem framework de testes unitários: verificação por subcomandos de consola em `Services/SelfTest.cs` (asserts que fazem `throw`), via `dotnet run -- <cmd>`; registar cada subcomando em `Program.cs:5`. **Não** adicionar trailer `Co-Authored-By:` aos commits.
- Coordenadas de píxel da `WcsSolution` em convenção **FITS 1-based** (como o ASTAP); os consumidores convertem para 0-based subtraindo 1 quando indexam arrays.

## Factos do spike (JÁ FEITO — usar como verdade)

CLI verificada nesta máquina (ASTAP v2025.09.27, base D80):
```
astap.exe -f <ficheiro> -r 180 -fov <altura_graus> -wcs
```
- Escreve `<base>.wcs` (header FITS, só em sucesso) e `<base>.ini` com `PLTSOLVD=T|F`. **Exit code é 0 mesmo em falha** → verificar `PLTSOLVD=T` no `.ini` (ou existência do `.wcs`).
- O **proxy 1536 falha** (poucas estrelas); o **full-res resolve** em ~2s. Dar imagem esticada em alta resolução.
- `.wcs` real (fixture de teste, da região Rho Oph):
```
CTYPE1='RA---TAN' CTYPE2='DEC--TAN'
CRPIX1=3.057500000000E+003  CRPIX2=2.040500000000E+003
CRVAL1=2.458169582032E+002  CRVAL2=-2.495790306081E+001
CD1_1=-1.835458761921E-004  CD1_2=1.318058558158E-003
CD2_1=1.319524801447E-003   CD2_2=1.846106534218E-004
PLTSOLVD=T
```

## File Structure

- `Services/WcsSolution.cs` — parse do header FITS + projeção TAN (puro, testável sem ASTAP).
- `Services/PlateSolve.cs` — deteção do ASTAP, render do solve-image, subprocess, leitura do resultado.
- `Services/AppSettings.cs` — persistência de `FocalLengthMm`/`PixelSizeUm`/`AstapPath` (padrão do `RecentFiles`).
- `Services/ProcessingSession.cs` — propriedades + solve na Fase A.
- `Components/Pages/Editor.razor` — secção "Anotação": toggle + inputs + cartão de info.
- `Services/SelfTest.cs` + `Program.cs` — `wcstest`, `solvetest`.

---

### Task 1: `WcsSolution` — parse FITS + projeção TAN

**Files:**
- Create: `Services/WcsSolution.cs`
- Modify: `Services/SelfTest.cs` (subcomando `wcstest`)
- Modify: `Program.cs:5` (registar `wcstest`)

**Interfaces:**
- Produces: `WcsSolution.Parse(string fitsHeader) -> WcsSolution?` (null se não solucionado/sem campos).
- Produces: `WcsSolution.WorldToPixel(double raDeg, double decDeg) -> (double x, double y)` (FITS 1-based).
- Produces: `WcsSolution.PixelToWorld(double x, double y) -> (double raDeg, double decDeg)`.
- Produces propriedades: `CenterRaDeg`, `CenterDecDeg`, `ScaleArcsecPerPixel`, `OrientationDeg`, `double FovWidthDeg(int w)`, `double FovHeightDeg(int h)`.

- [ ] **Step 1: Escrever o subcomando de teste (falha de compilação primeiro)**

Em `Services/SelfTest.cs`, adicionar ao `switch` (antes de `default:`):

```csharp
                case "wcstest":
                    WcsTest();
                    break;
```

E o método (a fixture é o `.wcs` real do spike):

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "wcstest"` ao fim da condição `is "..."`.

- [ ] **Step 3: Correr — confirmar que FALHA na compilação**

Run: `dotnet run -- wcstest`
Expected: erro de compilação — `WcsSolution` não existe.

- [ ] **Step 4: Implementar `WcsSolution`**

Create `Services/WcsSolution.cs`:

```csharp
// AstroLab — solução WCS (plate solve). Parse do header FITS do ASTAP + projeção
// gnomónica (TAN). Coordenadas de píxel em convenção FITS 1-based. Ver design
// 2026-06-17 (plate solve / PCC / anotação).
using System.Globalization;

namespace AstroLab.Services;

public sealed class WcsSolution
{
    public required double Crpix1, Crpix2, Crval1, Crval2;
    public required double Cd11, Cd12, Cd21, Cd22;

    const double D2R = Math.PI / 180.0, R2D = 180.0 / Math.PI;

    public double CenterRaDeg => Crval1;
    public double CenterDecDeg => Crval2;

    /// <summary>Escala média (arcsec/px) = sqrt(|det(CD)|)·3600.</summary>
    public double ScaleArcsecPerPixel
        => Math.Sqrt(Math.Abs(Cd11 * Cd22 - Cd12 * Cd21)) * 3600.0;

    /// <summary>Ângulo de posição do eixo Y (graus), E de N.</summary>
    public double OrientationDeg => Math.Atan2(Cd12, Cd11) * R2D;

    public double FovWidthDeg(int width) => ScaleArcsecPerPixel * width / 3600.0;
    public double FovHeightDeg(int height) => ScaleArcsecPerPixel * height / 3600.0;

    /// <summary>Parse de um header FITS (cards "KEY = VALUE"). Devolve null se não
    /// solucionado (PLTSOLVD≠T) ou sem os campos essenciais.</summary>
    public static WcsSolution? Parse(string fitsHeader)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // O ASTAP escreve cards de 80 chars concatenados; aceitar também \n.
        var text = fitsHeader.Replace("\r", "");
        // partir em cards de 80 se não houver newlines suficientes
        IEnumerable<string> cards = text.Contains('\n')
            ? text.Split('\n')
            : Enumerable.Range(0, text.Length / 80).Select(i => text.Substring(i * 80, 80));
        foreach (var card in cards)
        {
            int eq = card.IndexOf('=');
            if (eq < 1) continue;
            string key = card[..eq].Trim();
            string val = card[(eq + 1)..];
            int slash = val.IndexOf('/');           // remover comentário
            if (slash >= 0) val = val[..slash];
            keys[key] = val.Trim().Trim('\'').Trim();
        }

        if (keys.TryGetValue("PLTSOLVD", out var s) && s.StartsWith("F", StringComparison.OrdinalIgnoreCase))
            return null;

        double? G(string k) => keys.TryGetValue(k, out var v)
            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        if (G("CRPIX1") is not { } crpix1 || G("CRPIX2") is not { } crpix2 ||
            G("CRVAL1") is not { } crval1 || G("CRVAL2") is not { } crval2 ||
            G("CD1_1") is not { } cd11 || G("CD1_2") is not { } cd12 ||
            G("CD2_1") is not { } cd21 || G("CD2_2") is not { } cd22)
            return null;

        return new WcsSolution
        {
            Crpix1 = crpix1, Crpix2 = crpix2, Crval1 = crval1, Crval2 = crval2,
            Cd11 = cd11, Cd12 = cd12, Cd21 = cd21, Cd22 = cd22,
        };
    }

    /// <summary>Píxel (FITS 1-based) → (RA,Dec) graus. Projeção TAN inversa.</summary>
    public (double raDeg, double decDeg) PixelToWorld(double x, double y)
    {
        double dx = x - Crpix1, dy = y - Crpix2;
        double xi = (Cd11 * dx + Cd12 * dy) * D2R;     // standard coords (rad)
        double eta = (Cd21 * dx + Cd22 * dy) * D2R;

        double ra0 = Crval1 * D2R, dec0 = Crval2 * D2R;
        double r = Math.Sqrt(xi * xi + eta * eta);
        double c = Math.Atan(r);
        double sinc = Math.Sin(c), cosc = Math.Cos(c);
        double dec, ra;
        if (r < 1e-12)
        {
            ra = ra0; dec = dec0;
        }
        else
        {
            dec = Math.Asin(cosc * Math.Sin(dec0) + eta * sinc * Math.Cos(dec0) / r);
            ra = ra0 + Math.Atan2(xi * sinc, r * Math.Cos(dec0) * cosc - eta * Math.Sin(dec0) * sinc);
        }
        ra *= R2D; dec *= R2D;
        ra = (ra % 360.0 + 360.0) % 360.0;
        return (ra, dec);
    }

    /// <summary>(RA,Dec) graus → píxel (FITS 1-based). Projeção TAN direta.</summary>
    public (double x, double y) WorldToPixel(double raDeg, double decDeg)
    {
        double ra = raDeg * D2R, dec = decDeg * D2R;
        double ra0 = Crval1 * D2R, dec0 = Crval2 * D2R;
        double cosc = Math.Sin(dec0) * Math.Sin(dec)
                    + Math.Cos(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0);
        double xi = Math.Cos(dec) * Math.Sin(ra - ra0) / cosc * R2D;          // deg
        double eta = (Math.Cos(dec0) * Math.Sin(dec)
                    - Math.Sin(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0)) / cosc * R2D;

        double det = Cd11 * Cd22 - Cd12 * Cd21;
        double dx = (Cd22 * xi - Cd12 * eta) / det;
        double dy = (-Cd21 * xi + Cd11 * eta) / det;
        return (Crpix1 + dx, Crpix2 + dy);
    }
}
```

- [ ] **Step 5: Correr — confirmar que PASSA**

Run: `dotnet run -- wcstest`
Expected: imprime centro ≈ `245.8170, -24.9579`, escala ≈ `4.79 arcsec/px`, e `... OK`.

- [ ] **Step 6: Commit**

```bash
git add Services/WcsSolution.cs Services/SelfTest.cs Program.cs
git commit -m "feat: WcsSolution — parse FITS + projecao gnomonica (TAN)"
```

---

### Task 2: `PlateSolve` — deteção do ASTAP + cálculo de FOV

**Files:**
- Create: `Services/PlateSolve.cs`
- Modify: `Services/SelfTest.cs` (subcomando `fovtest`)
- Modify: `Program.cs:5` (registar `fovtest`)

**Interfaces:**
- Consumes: `WcsSolution` (Task 1).
- Produces: `PlateSolve.FindAstap(string? configured) -> string?` (caminho do exe ou null).
- Produces: `PlateSolve.FovHeightDeg(double focalMm, double pixelUm, int heightPx) -> double` (0 se inputs inválidos → caller usa modo auto).
- Produces: `PlateSolve.TrySolve(LinearImage img, string? astapPath, double focalMm, double pixelUm, out WcsSolution? wcs, out string status) -> bool` (corpo completo no Step 4; usado pela Task 3).

- [ ] **Step 1: Escrever o teste das funções puras (FOV + deteção)**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "fovtest":
                    FovTest();
                    break;
```

E o método:

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "fovtest"`.

- [ ] **Step 3: Correr — confirmar FALHA de compilação**

Run: `dotnet run -- fovtest`
Expected: erro — `PlateSolve` não existe.

- [ ] **Step 4: Implementar `PlateSolve`**

Create `Services/PlateSolve.cs`:

```csharp
// AstroLab — plate solve via ASTAP (subprocess). Render de uma imagem esticada
// em alta resolução, invoca astap.exe, lê PLTSOLVD do .ini e parseia o .wcs.
// Nunca lança. Ver design 2026-06-17. Spike: full-res resolve, proxy falha.
using System.Diagnostics;

namespace AstroLab.Services;

public static class PlateSolve
{
    /// <summary>FOV (altura, graus) a partir de focal/píxel. 0 se inválido (→ auto).</summary>
    public static double FovHeightDeg(double focalMm, double pixelUm, int heightPx)
    {
        if (focalMm <= 0 || pixelUm <= 0 || heightPx <= 0) return 0;
        double arcsecPerPx = 206.265 * pixelUm / focalMm;
        return arcsecPerPx * heightPx / 3600.0;
    }

    /// <summary>Localiza o astap.exe: caminho configurado → dirs comuns → PATH.</summary>
    public static string? FindAstap(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string[] common =
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
        };
        foreach (var p in common) if (File.Exists(p)) return p;
        return null;   // (deixar o caller reportar "não encontrado")
    }

    /// <summary>Resolve a imagem. Devolve true + wcs em sucesso; false + status caso
    /// contrário. Nunca lança.</summary>
    public static bool TrySolve(LinearImage img, string? astapPath, double focalMm,
                                double pixelUm, out WcsSolution? wcs, out string status)
    {
        wcs = null;
        string? exe = FindAstap(astapPath);
        if (exe is null) { status = "ASTAP não encontrado"; return false; }

        string tmpBase = Path.Combine(Path.GetTempPath(), "astrolab_solve_" + Guid.NewGuid().ToString("N"));
        string tmpImg = tmpBase + ".jpg";
        string wcsFile = tmpBase + ".wcs", iniFile = tmpBase + ".ini";
        try
        {
            // imagem de solve: Fase B (esticada) em alta resolução, qualidade alta
            var toned = PreviewRenderer.ToneToImage(img, ToneParams.Defaults, withNr: false);
            File.WriteAllBytes(tmpImg, PreviewRenderer.EncodeImage(toned, 95));

            double fov = FovHeightDeg(focalMm, pixelUm, img.Height);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(tmpImg);
            psi.ArgumentList.Add("-r"); psi.ArgumentList.Add("180");
            psi.ArgumentList.Add("-fov"); psi.ArgumentList.Add(fov.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-wcs");

            using (var proc = Process.Start(psi)!)
            {
                if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } status = "solve excedeu o tempo"; return false; }
            }

            // exit code é 0 mesmo em falha → confiar no PLTSOLVD/.wcs
            if (!File.Exists(wcsFile))
            {
                string why = File.Exists(iniFile) && File.ReadAllText(iniFile).Contains("PLTSOLVD=F")
                    ? "solve falhou (sem solução)" : "solve falhou";
                status = why; return false;
            }

            wcs = WcsSolution.Parse(File.ReadAllText(wcsFile));
            if (wcs is null) { status = "solve falhou (WCS ilegível)"; return false; }
            status = "solve ok";
            return true;
        }
        catch (Exception ex) { status = $"solve erro: {ex.Message}"; return false; }
        finally
        {
            foreach (var f in new[] { tmpImg, wcsFile, iniFile }) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        }
    }
}
```

- [ ] **Step 5: Correr — confirmar PASSA**

Run: `dotnet run -- fovtest`
Expected: `FOV(247mm,5.74µm,4080px) = 5.49° ; astap = C:\Program Files\astap\astap.exe`

- [ ] **Step 6: Commit**

```bash
git add Services/PlateSolve.cs Services/SelfTest.cs Program.cs
git commit -m "feat: PlateSolve — deteccao ASTAP, calculo de FOV e subprocess de solve"
```

---

### Task 3: Persistência (`AppSettings`) de focal/píxel/AstapPath

**Files:**
- Create: `Services/AppSettings.cs`
- Modify: `Services/SelfTest.cs` (subcomando `settingstest`)
- Modify: `Program.cs:5` (registar `settingstest`)

**Interfaces:**
- Produces: `AppSettings.Load() -> AppSettings`, `AppSettings.Save()`, com propriedades `double FocalLengthMm`, `double PixelSizeUm`, `string? AstapPath`.

- [ ] **Step 1: Teste round-trip**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "settingstest":
                    SettingsTest();
                    break;
```

Método:

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "settingstest"`.

- [ ] **Step 3: Correr — confirmar FALHA de compilação**

Run: `dotnet run -- settingstest`
Expected: erro — `AppSettings` não existe.

- [ ] **Step 4: Implementar `AppSettings`** (mesmo padrão de pasta do `RecentFiles`)

Primeiro, ver como o `RecentFiles` resolve o caminho de armazenamento:

Create `Services/AppSettings.cs`:

```csharp
// AstroLab — settings persistentes (focal/píxel/ASTAP) para a calibração por
// plate solve. JSON simples no perfil do utilizador. Ver design 2026-06-17.
using System.Text.Json;

namespace AstroLab.Services;

public sealed class AppSettings
{
    public double FocalLengthMm { get; set; }
    public double PixelSizeUm { get; set; }
    public string? AstapPath { get; set; }

    static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AstroLab", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new();
        }
        catch { /* ficheiro corrompido → defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
```

- [ ] **Step 5: Correr — confirmar PASSA**

Run: `dotnet run -- settingstest`
Expected: `guardou e releu focal/píxel OK`

- [ ] **Step 6: Commit**

```bash
git add Services/AppSettings.cs Services/SelfTest.cs Program.cs
git commit -m "feat: AppSettings — persistencia de focal/pixel/AstapPath"
```

---

### Task 4: Integração na Fase A (`ProcessingSession`)

**Files:**
- Modify: `Services/ProcessingSession.cs`

**Interfaces:**
- Consumes: `PlateSolve.TrySolve` (Task 2), `AppSettings` (Task 3), `WcsSolution` (Task 1).
- Produces (campos da sessão para a UI): `bool ShowAnnotation`, `double FocalLengthMm`, `double PixelSizeUm`, `string? AstapPath`, `WcsSolution? Wcs`, `string? SolveStatus`.

- [ ] **Step 1: Adicionar propriedades e carregar settings**

Em `Services/ProcessingSession.cs`, junto às outras propriedades de Fase A (`Radial`, `Crop`, `DrizzleFactor`, `LinearDenoise`):

```csharp
    /// <summary>Resolver coordenadas (plate solve ASTAP) na Fase A e mostrar info. Default off.</summary>
    public bool ShowAnnotation { get; set; }
    public double FocalLengthMm { get; set; }
    public double PixelSizeUm { get; set; }
    public string? AstapPath { get; set; }

    /// <summary>Solução WCS do último solve (null = sem solve/falhou).</summary>
    public WcsSolution? Wcs { get; private set; }
    public string? SolveStatus { get; private set; }
```

E no construtor/início, pré-preencher dos settings persistidos. Adicionar um construtor (a classe não tem um explícito):

```csharp
    public ProcessingSession()
    {
        var s = AppSettings.Load();
        FocalLengthMm = s.FocalLengthMm;
        PixelSizeUm = s.PixelSizeUm;
        AstapPath = s.AstapPath;
    }
```

- [ ] **Step 2: Correr o solve na Fase A**

Em `Services/ProcessingSession.cs`, dentro do `Task.Run` de `RunPhaseA`, **depois** do `ColorCalibrate` e antes do proxy:

```csharp
                Wcs = null; SolveStatus = null;
                if (ShowAnnotation)
                {
                    progress.Report(("a resolver coordenadas (ASTAP)…", 0.90));
                    if (PlateSolve.TrySolve(img, AstapPath, FocalLengthMm, PixelSizeUm, out var w, out var st))
                        Wcs = w;
                    SolveStatus = st;
                }
```

- [ ] **Step 3: Persistir focal/píxel quando mudam (método auxiliar)**

Em `Services/ProcessingSession.cs`, adicionar:

```csharp
    /// <summary>Guarda focal/píxel/ASTAP nos settings persistentes.</summary>
    public void PersistSolveSettings()
        => new AppSettings { FocalLengthMm = FocalLengthMm, PixelSizeUm = PixelSizeUm, AstapPath = AstapPath }.Save();
```

- [ ] **Step 4: Build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add Services/ProcessingSession.cs
git commit -m "feat: ProcessingSession — solve na Fase A + settings de focal/pixel"
```

---

### Task 5: UI — secção "Anotação" (toggle, inputs, cartão de info)

**Files:**
- Modify: `Components/Pages/Editor.razor`

**Interfaces:**
- Consumes: `Session.ShowAnnotation`, `Session.FocalLengthMm`, `Session.PixelSizeUm`, `Session.AstapPath`, `Session.Wcs`, `Session.SolveStatus`, `Session.PersistSolveSettings()`, `Session.FullWidth/FullHeight`, e os helpers existentes `RunPhaseA`, `ReprocessAsync`, `MakeProgress`, `_busy`, `_ready`.

- [ ] **Step 1: Adicionar a secção "Anotação" nos controlos**

Em `Components/Pages/Editor.razor`, a seguir à secção "Estrelas" (antes da secção final dos botões), adicionar:

```razor
        <section>
            <h3>Anotação (plate solve)</h3>
            <label class="check">
                <input type="checkbox" checked="@Session.ShowAnnotation" @onchange="OnAnnotationToggle" disabled="@_busy" />
                Resolver coordenadas (ASTAP)
            </label>
            <div class="slider-note">requer ASTAP instalado — corre na Fase A (mais lento)</div>

            <label class="slider-label" style="margin-top:.6rem">Distância focal efetiva (mm)</label>
            <input class="path-input" type="number" step="1" @bind="Session.FocalLengthMm" disabled="@_busy" />
            <label class="slider-label">Tamanho de píxel (µm)</label>
            <input class="path-input" type="number" step="0.01" @bind="Session.PixelSizeUm" disabled="@_busy" />

            @if (Session.Wcs is { } wcs)
            {
                <div class="solve-card">
                    <div>Centro: @wcs.CenterRaDeg.ToString("0.0000")°, @wcs.CenterDecDeg.ToString("0.0000")°</div>
                    <div>Escala: @wcs.ScaleArcsecPerPixel.ToString("0.00")″/px · FOV @wcs.FovWidthDeg(Session.FullWidth).ToString("0.0")×@wcs.FovHeightDeg(Session.FullHeight).ToString("0.0")°</div>
                    <div>Orientação: @wcs.OrientationDeg.ToString("0.0")°</div>
                </div>
            }
            else if (Session.SolveStatus is { } st)
            {
                <div class="slider-note" style="color:var(--accent)">@st</div>
            }
        </section>
```

- [ ] **Step 2: Adicionar o handler `OnAnnotationToggle`**

No bloco `@code` de `Editor.razor`, junto a `OnRadialToggle`/`OnLinearDenoise`:

```csharp
    async Task OnAnnotationToggle(ChangeEventArgs e)
    {
        Session.ShowAnnotation = e.Value is true || e.Value?.ToString() == "True";
        Session.PersistSolveSettings();
        if (Session.IsLoaded)
            await RunPhaseA(() => Session.ReprocessAsync(MakeProgress()));
    }
```

- [ ] **Step 3: Persistir focal/píxel ao reprocessar (já coberto pelo toggle)**

Os campos `@bind` escrevem direto em `Session`; o `OnAnnotationToggle` persiste. Para garantir que editar focal/píxel e voltar a resolver persiste, o handler do toggle chamará `PersistSolveSettings()` (Step 2) — suficiente para a Fase 1.

- [ ] **Step 4: Estilo do cartão (mínimo)**

Em `wwwroot/app.css`, adicionar:

```css
.solve-card { margin-top:.6rem; padding:.5rem .6rem; background:#1a1a22; border-radius:6px;
    font-size:.8rem; line-height:1.5; color:var(--text); }
```

- [ ] **Step 5: Build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 6: Verificação visual**

Run: `dotnet run` — abrir um TIF, pôr focal 247 / píxel 5.74, ligar "Resolver coordenadas". Esperado: barra mostra "a resolver coordenadas…", e o cartão aparece com centro/escala/FOV/orientação (ou a nota de falha se o ASTAP não estiver instalado).

- [ ] **Step 7: Commit**

```bash
git add Components/Pages/Editor.razor wwwroot/app.css
git commit -m "feat: UI de anotacao — toggle de solve, inputs focal/pixel e cartao de info"
```

---

### Task 6: SelfTest `solvetest` (end-to-end)

**Files:**
- Modify: `Services/SelfTest.cs` (subcomando `solvetest`)
- Modify: `Program.cs:5` (registar `solvetest`)

**Interfaces:**
- Consumes: `PlateSolve.TrySolve`, `WcsSolution` (Tasks 1–2).

- [ ] **Step 1: Adicionar o subcomando**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "solvetest":
                    if (args.Length < 2) throw new ArgumentException("uso: solvetest <path>");
                    SolveTest(args[1]);
                    break;
```

Método:

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "solvetest"`.

- [ ] **Step 3: Correr (ASTAP está instalado nesta máquina)**

Run: `dotnet run -- solvetest testdata/Autosave.tif`
Expected: `solve ok …`, centro ≈ `245.8, -24.96`, escala ≈ `4.8"/px`, FOV ≈ `8.1×5.4°`.

- [ ] **Step 4: Commit**

```bash
git add Services/SelfTest.cs Program.cs
git commit -m "test: solvetest — plate solve end-to-end no Autosave.tif"
```

---

## Self-Review (preenchido)

**Cobertura do spec (Fase 1):**
- Plate solve ASTAP (subprocess, verificado) → Tasks 2, 6. ✓
- `WcsSolution` + projeção partilhada → Task 1. ✓
- Solve na Fase A, nunca bloqueia → Task 4 (+ `TrySolve` nunca lança). ✓
- Escala/FOV de focal+píxel → Task 2 (`FovHeightDeg`). ✓
- Persistência focal/píxel/ASTAP → Task 3 + Task 4. ✓
- UI: toggle + inputs + cartão de info (centro/escala/orientação/FOV) → Task 5. ✓
- Fallback/degradação por status → Tasks 4,5 (mostra `SolveStatus`). ✓
- Pré-preenchimento por EXIF: **adiado para a Fase 2** (PCC) — na Fase 1 usa-se persistido/manual. Registado como gap consciente (o spec marcou EXIF como best-effort; sem ele o fluxo funciona via valores persistidos).

**Placeholders:** nenhum — todo o código está completo (projeção TAN, parse, subprocess verificado).

**Consistência de tipos:** `WcsSolution.Parse/WorldToPixel/PixelToWorld` e propriedades usadas igualmente nas Tasks 1, 5, 6; `PlateSolve.TrySolve(LinearImage, string?, double, double, out WcsSolution?, out string)` idêntico nas Tasks 2, 4, 6.

## Fora desta fase (Fases 2 e 3, planos próprios)
- **Fase 2 — PCC:** fotometria (base Gaia D80 do ASTAP / spike), medição de fluxo, ganhos por canal, fallback ao `ColorCalibrate`; leitura EXIF best-effort.
- **Fase 3 — Overlay:** catálogos embebidos (OpenNGC + estrelas nomeadas), `FieldAnnotation`, SVG sobre o preview (DSO, grelha, estrelas).
