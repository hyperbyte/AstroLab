# AstroLab — Arquitetura

## Projeto

Um único projeto Blazor Server:

```
AstroLab.csproj                (net10.0, <Nullable>enable</Nullable>)
Program.cs
Components/
  App.razor, Routes.razor
  Pages/
    Editor.razor               ← página única da app
  Shared/
    SliderControl.razor        ← slider + valor numérico + reset
Services/
  ProcessingSession.cs         ← estado: LinearFull, LinearProxy, parâmetros
  TiffIO.cs                    ← load/save TIFF (LibTiff.Net)
  AstroPipeline.cs             ← núcleo matemático (ver ReferenceCode/)
  PreviewRenderer.cs           ← Fase B sobre proxy → JPEG bytes
  ExportService.cs             ← Fase B+NR sobre full-res, com IProgress<double>
wwwroot/
```

## Modelo de dados em memória

Representação interna das imagens: `float[]` planar por canal
(`float[3][H*W]`) ou intercalado `float[H*W*3]` — **escolher intercalado**
para mapear diretamente para `Mat` do OpenCvSharp (CV_32FC3) sem cópias.

```csharp
public sealed class LinearImage
{
    public int Width, Height;
    public float[] Data;          // RGB intercalado, 0–1
    public Mat AsMat() => Mat.FromPixelData(Height, Width, MatType.CV_32FC3, Data);
}
```

`ProcessingSession` (singleton DI — app single-user):

```csharp
public sealed class ProcessingSession
{
    public LinearImage? LinearFull;     // pós Fase A
    public LinearImage? LinearProxy;    // 1536 px
    public ToneParams Params = ToneParams.Defaults;
    public string? SourcePath;
    public SemaphoreSlim Gate = new(1, 1);  // serializa render/export
}

public record ToneParams(
    double Stretch = 600, double Sky = 0.12, double Saturation = 0.45,
    double Scnr = 0.7, double NoiseReduction = 1.0, double BlackPoint = 0.0)
{ public static ToneParams Defaults => new(); }
```

## Fluxo de abertura de ficheiro

O servidor corre na máquina do utilizador → **não usar upload via browser**
para um TIF de 250 MB. A UI tem um campo de caminho + botão "Abrir", e o
servidor lê diretamente do disco. Conveniência: dropdown com os `.tif`
encontrados em pastas recentes (persistir últimos caminhos em
`%LOCALAPPDATA%/AstroLab/recent.json`).

Sequência (Fase A, em `Task.Run` com progresso via callback → re-render):

1. `TiffIO.LoadFloat(path)` → normalizar min-max para 0–1
2. Crop bordas (fração configurável, default 0.012)
3. `AstroPipeline.ExtractBackground(img, radial: true)` (in-place)
4. `AstroPipeline.ColorCalibrate(img)` (in-place)
5. Gerar proxy: `Cv2.Resize(..., InterpolationFlags.Area)` para lado maior 1536
6. Render inicial do preview

## Fluxo de slider (Fase B)

```
oninput do slider → atualiza Params → debounce 80 ms →
PreviewRenderer.Render(LinearProxy, Params) → byte[] JPEG (q85) →
imgSrc = "data:image/jpeg;base64," + ... → StateHasChanged()
```

- `Render` clona o proxy (são ~10 MB, é barato), aplica stretch/SCNR/
  saturação/curva, codifica com `Cv2.ImEncode(".jpg", ...)`.
- Debounce + `SemaphoreSlim` + descarte de pedidos obsoletos (guardar um
  `version` incremental; se ao terminar o render `version` já avançou,
  deita fora o resultado e renderiza o mais recente).
- **NR não corre aqui** (caro demais mesmo no proxy para 60 fps de slider).

## Preview de NR (modo separado)

Botão "Pré-visualizar NR": aplica Fase B + NR a um **crop 100% de
600×400 px** centrado num ponto que o utilizador escolhe clicando no
preview. Mostra lado-a-lado com/sem. Custo ~1 s, aceitável on-demand.

## Export (Fase C)

`ExportService.Export(LinearFull, Params, outPrefix, IProgress<double>)`:
1. Clonar `LinearFull` (pico de memória: 2× full-res ≈ 600 MB — ok)
2. Fase B em full-res
3. NR (se `NoiseReduction > 0`)
4. Escrever `{prefix}_16bit.tif` (LibTiff, deflate) e `{prefix}.jpg` (q93)
5. UI: barra de progresso + abrir pasta no fim
   (`Process.Start("explorer.exe", folder)`)

Correr em `Task.Run`; bloquear novo export/abertura enquanto decorre
(usar `Gate`).

## Gestão de memória

- Reutilizar buffers onde possível; `LinearFull` antigo → libertar
  referência ao abrir novo ficheiro e `GC.Collect()` explícito (justificado:
  arrays no LOH).
- `Mat.FromPixelData` NÃO copia — atenção ao tempo de vida do array.
- Server GC: `<ServerGarbageCollection>true</ServerGarbageCollection>`.

## Notas Blazor

- `@rendermode InteractiveServer` na página.
- SignalR: aumentar `MaximumReceiveMessageSize` não é necessário (imagens
  vão servidor→cliente como base64 no DOM; ~300 KB por preview, ok).
  Alternativa se houver latência: endpoint minimal API `GET /preview?v=N`
  que devolve o JPEG e `<img src>` aponta lá — evita base64 no diff do DOM.
- Sliders com `@bind:event="oninput"` para atualização contínua.
