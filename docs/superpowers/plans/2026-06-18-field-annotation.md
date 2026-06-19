# Overlay de anotação de campo (Fase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Desenhar um overlay sobre o preview — objetos de céu profundo (elipses), estrelas nomeadas e grelha RA/Dec — projetados pela `WcsSolution` da Fase 1, a partir do `deep_sky.csv` do ASTAP.

**Architecture:** `FieldAnnotation` parseia o `deep_sky.csv`, projeta cada objeto/linha-de-grelha para píxeis da imagem via `WcsSolution.WorldToPixel`, e devolve um `FieldOverlay`. O `ProcessingSession` constrói-o quando o solve corre; o `Editor` desenha-o num `<svg>` sobre o `<img>` do preview (alinhado por `viewBox`+`preserveAspectRatio`, sem JS), com sub-toggles por camada.

**Tech Stack:** .NET 10, Blazor Server. Depende da Fase 1 (`WcsSolution`, `PlateSolve`) em `main`. Lê o `deep_sky.csv` da pasta do ASTAP em runtime.

## Global Constraints

- `WcsSolution.WorldToPixel(raDeg, decDeg)` devolve píxel **FITS 1-based** no espaço da imagem resolvida (`FullWidth`×`FullHeight`); para coords de desenho 0-based, subtrair 1.
- `deep_sky.csv` (ASTAP): campos `RA,DEC,nome(s)[,length,width,orient]`. **RA° = RA/2400**, **DEC° = DEC/3600**, tamanho′ = length/10 (e width/10); orient em graus. **Sem campos de tamanho → estrela nomeada**; com `length` → DSO.
- Tudo nunca-lança: `FieldAnnotation.Build` com catálogo ausente devolve só a grelha; ficheiro ilegível → `Objects` vazio.
- Sem framework de testes: subcomandos de consola em `Services/SelfTest.cs` (asserts que `throw`), via `dotnet run -- <cmd>`, registados em `Program.cs:5`. **Sem trailer `Co-Authored-By`** nos commits.
- Cultura invariante em todo o parse numérico.
- O overlay é cosmético: não altera os píxeis da imagem.

## Factos do spike (JÁ FEITO — usar como verdade)

Linhas reais do `deep_sky.csv`:
```
593645,-95155,Antares/α_Sco                  → 247.35°,-26.43°  (estrela: sem tamanho)
590155,-95489,M4/NGC6121,360                 → 245.90°,-26.52°  length 36.0′ (DSO)
658140,-67140,M24/IC4715,1200,450,45         → elipse 120′×45′ @45°
```
As 2 primeiras linhas do ficheiro são cabeçalho (texto), e há marcadores de polo (`NP_2000`,`SP_2000`); o parser ignora linhas cujo 1º campo não seja número. Ficheiro em UTF-8 (com BOM; nomes podem ter não-ASCII).

## File Structure
- `Services/FieldAnnotation.cs` — records + `ParseDeepSky` + `Build` (projeção, filtros, grelha).
- `Services/ProcessingSession.cs` — campo `Overlay`, build no solve.
- `Components/Pages/Editor.razor` + `wwwroot/app.css` — SVG + 3 checkboxes.
- `Services/SelfTest.cs` + `Program.cs` — `dsoparse`, `annotatetest`.

---

### Task 1: `FieldAnnotation` — records + parse do deep_sky.csv

**Files:**
- Create: `Services/FieldAnnotation.cs`
- Modify: `Services/SelfTest.cs` (subcomando `dsoparse`)
- Modify: `Program.cs:5` (registar `dsoparse`)

**Interfaces:**
- Produces: `record CatalogObject(double RaDeg, double DecDeg, string Name, double LengthArcmin, double WidthArcmin, double AngleDeg, bool IsStar)`.
- Produces: `FieldAnnotation.ParseDeepSky(string text) -> List<CatalogObject>` (ignora cabeçalho/polos; sem tamanho → IsStar; só length → circular).

- [ ] **Step 1: Escrever o teste de parse (fixture real)**

Em `Services/SelfTest.cs`, ao `switch` (antes de `default:`):

```csharp
                case "dsoparse":
                    DsoParse();
                    break;
```

Método:

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "dsoparse"` ao fim da condição `is "..."`.

- [ ] **Step 3: Correr — confirmar que FALHA na compilação**

Run: `dotnet run -- dsoparse`
Expected: erro — `FieldAnnotation` não existe.

- [ ] **Step 4: Implementar os records + `ParseDeepSky`**

Create `Services/FieldAnnotation.cs`:

```csharp
// AstroLab — overlay de anotação de campo (Fase 3). Parseia o deep_sky.csv do ASTAP
// e projeta DSO / estrelas nomeadas / grelha RA-Dec via WcsSolution. Ver design 2026-06-18.
using System.Globalization;

namespace AstroLab.Services;

public sealed record CatalogObject(double RaDeg, double DecDeg, string Name,
                                   double LengthArcmin, double WidthArcmin, double AngleDeg, bool IsStar);

public sealed record DsoMark(double X, double Y, double WidthPx, double HeightPx,
                             double AngleDeg, string Name, bool IsStar);
public sealed record GridLine(IReadOnlyList<(double X, double Y)> Points, string Label);
public sealed record FieldOverlay(int Width, int Height,
                                  IReadOnlyList<DsoMark> Objects, IReadOnlyList<GridLine> Grid);

public static class FieldAnnotation
{
    /// <summary>Parseia o deep_sky.csv do ASTAP. Ignora linhas cujo 1º campo não seja
    /// número (cabeçalho). RA°=RA/2400, DEC°=DEC/3600. Sem length → estrela nomeada;
    /// só length → circular; length+width(+orient) → elipse.</summary>
    public static List<CatalogObject> ParseDeepSky(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        var outp = new List<CatalogObject>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0) continue;
            var f = raw.Split(',');
            if (f.Length < 3) continue;
            if (!double.TryParse(f[0], NumberStyles.Float, inv, out var raU)) continue;   // cabeçalho
            if (!double.TryParse(f[1], NumberStyles.Float, inv, out var decU)) continue;
            string name = f[2].Trim();
            if (name.Length == 0) continue;

            double ra = raU / 2400.0, dec = decU / 3600.0;
            bool hasLen = f.Length >= 4 && double.TryParse(f[3], NumberStyles.Float, inv, out _);
            if (!hasLen)
            {
                outp.Add(new CatalogObject(ra, dec, name, 0, 0, 0, IsStar: true));
                continue;
            }
            double len = double.Parse(f[3], inv) / 10.0;     // 0.1' → '
            double wid = f.Length >= 5 && double.TryParse(f[4], NumberStyles.Float, inv, out var w) ? w / 10.0 : len;
            double ang = f.Length >= 6 && double.TryParse(f[5], NumberStyles.Float, inv, out var a) ? a : 0;
            outp.Add(new CatalogObject(ra, dec, name, len, wid, ang, IsStar: false));
        }
        return outp;
    }
}
```

- [ ] **Step 5: Correr — confirmar que PASSA**

Run: `dotnet run -- dsoparse`
Expected: `… objetos; Antares⭑ 247.35,-26.43; M4 36.0' -> OK`

- [ ] **Step 6: Commit**

```bash
git add Services/FieldAnnotation.cs Services/SelfTest.cs Program.cs
git commit -m "feat: FieldAnnotation — parse do deep_sky.csv (DSO + estrelas nomeadas)"
```

---

### Task 2: `FieldAnnotation.Build` — projeção, filtros e grelha

**Files:**
- Modify: `Services/FieldAnnotation.cs` (adicionar `Build` + helpers)
- Modify: `Services/SelfTest.cs` (subcomando `annotbuild`)
- Modify: `Program.cs:5` (registar `annotbuild`)

**Interfaces:**
- Consumes: `WcsSolution` (Fase 1: `WorldToPixel`, `PixelToWorld`, `ScaleArcsecPerPixel`), `ParseDeepSky` (Task 1).
- Produces: `FieldAnnotation.Build(WcsSolution wcs, int width, int height, string? astapDir) -> FieldOverlay`.

- [ ] **Step 1: Escrever o teste de Build (WCS fixo da Fase 1)**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "annotbuild":
                    AnnotBuild();
                    break;
```

Método (usa o `.wcs` real de Rho Oph; M4 está no campo → deve projetar dentro):

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "annotbuild"`.

- [ ] **Step 3: Correr — confirmar que FALHA na compilação**

Run: `dotnet run -- annotbuild`
Expected: erro — `FieldAnnotation` não contém `Build`.

- [ ] **Step 4: Implementar `Build` + helpers**

Em `Services/FieldAnnotation.cs`, adicionar (dentro da classe, a seguir a `ParseDeepSky`):

```csharp
    const double MinDsoArcmin = 3.0;   // não poluir o campo largo com objetos minúsculos

    public static FieldOverlay Build(WcsSolution wcs, int width, int height, string? astapDir)
    {
        var objects = new List<DsoMark>();
        string? csv = astapDir is null ? null : System.IO.Path.Combine(astapDir, "deep_sky.csv");
        if (csv != null && File.Exists(csv))
        {
            double arcsecPerPx = wcs.ScaleArcsecPerPixel;
            foreach (var o in ParseDeepSky(SafeReadAll(csv)))
            {
                if (!o.IsStar && o.LengthArcmin < MinDsoArcmin) continue;
                var (fx, fy) = wcs.WorldToPixel(o.RaDeg, o.DecDeg);
                double x = fx - 1, y = fy - 1;
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                double wpx = o.WidthArcmin * 60.0 / arcsecPerPx;     // arcmin→arcsec→px
                double hpx = o.LengthArcmin * 60.0 / arcsecPerPx;
                string label = o.Name.Split('/')[0];                  // nome primário
                objects.Add(new DsoMark(x, y, wpx, hpx, o.AngleDeg, label, o.IsStar));
            }
        }
        return new FieldOverlay(width, height, objects, BuildGrid(wcs, width, height));
    }

    static string SafeReadAll(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    /// <summary>Grelha RA/Dec: linhas de Dec constante e RA constante, projetadas
    /// ponto-a-ponto (curvam-se via WCS), recortadas à imagem.</summary>
    static List<GridLine> BuildGrid(WcsSolution wcs, int width, int height)
    {
        // limites RA/Dec a partir dos 4 cantos + centro
        double raMin = 1e9, raMax = -1e9, decMin = 1e9, decMax = -1e9;
        foreach (var (px, py) in new[] { (0.0, 0.0), (width - 1.0, 0.0), (0.0, height - 1.0),
                                         (width - 1.0, height - 1.0), (width / 2.0, height / 2.0) })
        {
            var (ra, dec) = wcs.PixelToWorld(px + 1, py + 1);
            raMin = Math.Min(raMin, ra); raMax = Math.Max(raMax, ra);
            decMin = Math.Min(decMin, dec); decMax = Math.Max(decMax, dec);
        }
        double stepDec = NiceStep(decMax - decMin);
        double stepRa = NiceStep((raMax - raMin) * Math.Cos(((decMin + decMax) / 2) * Math.PI / 180));

        var lines = new List<GridLine>();
        // linhas de Dec constante (varia RA)
        for (double dec = Math.Ceiling(decMin / stepDec) * stepDec; dec <= decMax; dec += stepDec)
            AddLine(lines, wcs, width, height, $"{dec:F1}°",
                    n => (raMin + (raMax - raMin) * n, dec));
        // linhas de RA constante (varia Dec)
        for (double ra = Math.Ceiling(raMin / stepRa) * stepRa; ra <= raMax; ra += stepRa)
            AddLine(lines, wcs, width, height, $"{ra / 15:F2}h",
                    n => (ra, decMin + (decMax - decMin) * n));
        return lines;
    }

    static void AddLine(List<GridLine> lines, WcsSolution wcs, int width, int height,
                        string label, Func<double, (double ra, double dec)> at)
    {
        var pts = new List<(double X, double Y)>();
        for (int s = 0; s <= 40; s++)
        {
            var (ra, dec) = at(s / 40.0);
            var (fx, fy) = wcs.WorldToPixel(ra, dec);
            double x = fx - 1, y = fy - 1;
            if (x >= -50 && y >= -50 && x <= width + 50 && y <= height + 50) pts.Add((x, y));
        }
        if (pts.Count >= 2) lines.Add(new GridLine(pts, label));
    }

    /// <summary>Passo "redondo" para ~5 divisões num intervalo (graus).</summary>
    static double NiceStep(double range)
    {
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10 };
        double target = Math.Max(range, 1e-6) / 5;
        foreach (var s in steps) if (s >= target) return s;
        return 10;
    }
```

- [ ] **Step 5: Correr — confirmar que PASSA**

Run: `dotnet run -- annotbuild`
Expected: `só grelha: N linhas | com catálogo: M objetos no campo` e `-> OK` (M ≥ 1).

- [ ] **Step 6: Commit**

```bash
git add Services/FieldAnnotation.cs Services/SelfTest.cs Program.cs
git commit -m "feat: FieldAnnotation.Build — projecao de DSO/estrelas + grelha RA/Dec"
```

---

### Task 3: Integração na sessão (`ProcessingSession`)

**Files:**
- Modify: `Services/ProcessingSession.cs`

**Interfaces:**
- Consumes: `FieldAnnotation.Build` (Task 2), `PlateSolve.FindAstap` (Fase 1), `Wcs`/`ShowAnnotation`/`AstapPath` (Fase 1, já existem).
- Produces: `FieldOverlay? Overlay { get; private set; }`.

- [ ] **Step 1: Adicionar o campo `Overlay`**

Em `Services/ProcessingSession.cs`, junto a `Wcs`/`SolveStatus`:

```csharp
    /// <summary>Overlay de anotação (DSO/grelha/estrelas) do último solve. Null se sem solve.</summary>
    public FieldOverlay? Overlay { get; private set; }
```

- [ ] **Step 2: Construir o overlay quando há solve**

Em `Services/ProcessingSession.cs`, no `RunPhaseA`, dentro do bloco do solve (onde `Wcs` é atribuído após `PlateSolve.TrySolve` ter sucesso), construir o overlay. Localizar a atribuição `Wcs = w;` e logo a seguir adicionar a construção; e garantir `Overlay = null;` no reset junto a `Wcs = null;`.

Reset (junto ao `Wcs = null; SolveStatus = null;`):
```csharp
                Wcs = null; SolveStatus = null; Overlay = null;
```
Após o solve com sucesso (a seguir a `Wcs = w;`):
```csharp
                        string? astapDir = Path.GetDirectoryName(PlateSolve.FindAstap(AstapPath) ?? "");
                        Overlay = FieldAnnotation.Build(w, img.Width, img.Height, astapDir);
```
(`img.Width/Height` aqui são as dimensões da imagem resolvida — as mesmas do `WcsSolution`.)

- [ ] **Step 3: Build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add Services/ProcessingSession.cs
git commit -m "feat: ProcessingSession — construir FieldOverlay no solve"
```

---

### Task 4: UI — SVG do overlay + toggles de camada

**Files:**
- Modify: `Components/Pages/Editor.razor`
- Modify: `wwwroot/app.css`

**Interfaces:**
- Consumes: `Session.Overlay` (Task 3), `DsoMark`/`GridLine`/`FieldOverlay` (Task 1).

- [ ] **Step 1: Adicionar o SVG sobre o preview**

Em `Components/Pages/Editor.razor`, dentro de `<div class="preview-pane">`, **a seguir** ao `<img class="preview-img" ... />` (e antes do overlay de progresso `_busy`), adicionar:

```razor
        @if (Session.Overlay is { } ov && _imgSrc is not null)
        {
            <svg class="annot-svg" viewBox="@($"0 0 {ov.Width} {ov.Height}")" preserveAspectRatio="xMidYMid meet">
                @if (_annotGrid)
                {
                    @foreach (var g in ov.Grid)
                    {
                        <polyline points="@string.Join(' ', g.Points.Select(p => $"{p.X:F0},{p.Y:F0}"))"
                                  fill="none" stroke="#3a6ea5" stroke-width="@(_annotStroke)" opacity="0.5" />
                    }
                }
                @if (_annotDso)
                {
                    @foreach (var o in ov.Objects.Where(o => !o.IsStar))
                    {
                        <ellipse cx="@o.X.ToString("F0")" cy="@o.Y.ToString("F0")"
                                 rx="@((o.WidthPx / 2).ToString("F0"))" ry="@((o.HeightPx / 2).ToString("F0"))"
                                 transform="@($"rotate({o.AngleDeg.ToString("F0")} {o.X:F0} {o.Y:F0})")"
                                 fill="none" stroke="#e0b341" stroke-width="@(_annotStroke)" opacity="0.8" />
                        <text x="@((o.X + o.WidthPx / 2 + 6).ToString("F0"))" y="@o.Y.ToString("F0")"
                              fill="#e0b341" font-size="@(_annotFont)">@o.Name</text>
                    }
                }
                @if (_annotStars)
                {
                    @foreach (var o in ov.Objects.Where(o => o.IsStar))
                    {
                        <circle cx="@o.X.ToString("F0")" cy="@o.Y.ToString("F0")" r="@(_annotStroke * 3)"
                                fill="none" stroke="#9fd0ff" stroke-width="@(_annotStroke)" />
                        <text x="@((o.X + 8).ToString("F0"))" y="@((o.Y - 8).ToString("F0"))"
                              fill="#9fd0ff" font-size="@(_annotFont)">@o.Name</text>
                    }
                }
            </svg>
        }
```

- [ ] **Step 2: Adicionar os toggles na secção "Anotação"**

Em `Components/Pages/Editor.razor`, na `<section>` da "Anotação (plate solve)", a seguir ao cartão de info (`@if (Session.Wcs is { } wcs) {...}` / bloco de estado), adicionar:

```razor
            @if (Session.Overlay is not null)
            {
                <div class="annot-layers">
                    <label class="check"><input type="checkbox" checked="@_annotDso" @onchange="@(e => _annotDso = e.Value is true)" /> Objetos (DSO)</label>
                    <label class="check"><input type="checkbox" checked="@_annotGrid" @onchange="@(e => _annotGrid = e.Value is true)" /> Grelha RA/Dec</label>
                    <label class="check"><input type="checkbox" checked="@_annotStars" @onchange="@(e => _annotStars = e.Value is true)" /> Estrelas nomeadas</label>
                </div>
            }
```

- [ ] **Step 3: Adicionar os campos de estado no `@code`**

Em `Components/Pages/Editor.razor`, no bloco `@code`, junto aos outros campos de estado da UI:

```csharp
    bool _annotDso = true, _annotGrid = true, _annotStars = true;
    // espessura/fonte do overlay em unidades da imagem (escalam com o viewBox)
    double _annotStroke => Math.Max(2, (Session.FullWidth) / 1200.0);
    double _annotFont => Math.Max(14, (Session.FullWidth) / 90.0);
```

- [ ] **Step 4: CSS do SVG**

Em `wwwroot/app.css`, adicionar:

```css
.annot-svg { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.annot-svg text { font-family: sans-serif; paint-order: stroke; }
.annot-layers { display: flex; flex-direction: column; gap: .25rem; margin-top: .6rem; }
```

- [ ] **Step 5: Build limpo**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 6: Verificação visual**

Run: `dotnet run` — abrir um TIF, pôr focal 247 / píxel 5,74, ligar "Resolver coordenadas (ASTAP)". Esperado: após o solve, aparecem elipses douradas nos DSO (ex.: M4), marcadores azuis nas estrelas nomeadas (ex.: Antares) e a grelha; os 3 checkboxes ligam/desligam cada camada sem reprocessar; o overlay acompanha a imagem (alinhado).

- [ ] **Step 7: Commit**

```bash
git add Components/Pages/Editor.razor wwwroot/app.css
git commit -m "feat: UI — overlay SVG de anotacao + toggles de camada"
```

---

### Task 5: SelfTest `annotatetest` (end-to-end + overlay queimado)

**Files:**
- Modify: `Services/SelfTest.cs` (subcomando `annotatetest`)
- Modify: `Program.cs:5` (registar `annotatetest`)

**Interfaces:**
- Consumes: `PlateSolve.TrySolve`, `FieldAnnotation.Build`, `PreviewRenderer`, `AstroPipeline` (Fases 1–3).

- [ ] **Step 1: Adicionar o subcomando**

Em `Services/SelfTest.cs`, ao `switch`:

```csharp
                case "annotatetest":
                    if (args.Length < 2) throw new ArgumentException("uso: annotatetest <path>");
                    AnnotateTest(args[1]);
                    break;
```

Método (Fase A + solve + Build; imprime contagens e grava um JPEG com a grelha+elipses desenhadas via OpenCV):

```csharp
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
```

- [ ] **Step 2: Registar em Program.cs**

Em `Program.cs:5`, acrescentar `or "annotatetest"`.

- [ ] **Step 3: Correr (ASTAP instalado)**

Run: `dotnet run -- annotatetest testdata/Autosave.tif`
Expected: `N DSO, M estrelas nomeadas, K linhas de grelha no campo` (N≥1) e grava `testdata/annotate_result.jpg`.

- [ ] **Step 4: Commit**

```bash
git add Services/SelfTest.cs Program.cs
git commit -m "test: annotatetest — overlay end-to-end (DSO/grelha/estrelas)"
```

---

## Self-Review (preenchido)

**Cobertura do spec:**
- Ler `deep_sky.csv` (conversões RA/2400, DEC/3600, tamanho) → Task 1. ✓
- Projeção via `WcsSolution` + filtro FOV + tamanho mínimo DSO → Task 2. ✓
- Estrelas nomeadas (sem tamanho) → Task 1/2 (`IsStar`). ✓
- Grelha RA/Dec projetada ponto-a-ponto → Task 2 (`BuildGrid`). ✓
- Build no solve, em cache → Task 3. ✓
- SVG `viewBox`+`preserveAspectRatio`, `pointer-events:none`, 3 toggles instantâneos → Task 4. ✓
- `deep_sky.csv` ausente → só grelha, sem erro → Task 2 (`File.Exists`/`SafeReadAll`) + teste Task 2. ✓
- `annotatetest` end-to-end → Task 5. ✓

**Placeholders:** nenhum — código completo (parse, projeção, grelha, SVG, burn).

**Consistência de tipos:** `CatalogObject`, `DsoMark(X,Y,WidthPx,HeightPx,AngleDeg,Name,IsStar)`, `GridLine(Points,Label)`, `FieldOverlay(Width,Height,Objects,Grid)`, `Build(WcsSolution,int,int,string?)` — usados igualmente nas Tasks 1–5. `WcsSolution.WorldToPixel/PixelToWorld/ScaleArcsecPerPixel` da Fase 1.

## Fora desta fase
- Pan/zoom, clique/seleção de objetos, tooltips.
- Tratamento de RA a cruzar 0°/360° na grelha (campos largos perto de RA=0 — raro; limitação aceite).
