# AstroLab — Plano de Implementação (para Claude Code)

Ordem pensada para ter validação matemática cedo e UI no fim.
Cada tarefa termina com critério verificável.

## Tarefa 1 — Esqueleto + I/O TIFF
- `dotnet new blazor -n AstroLab` (net10.0, interactive server)
- NuGet: `OpenCvSharp4`, `OpenCvSharp4.runtime.win`, `BitMiracle.LibTiff.NET`
- Implementar `TiffIO.LoadFloat(path)` → `LinearImage` (suportar TIFF RGB
  float32 E uint16; o DSS produz float32). Implementar
  `TiffIO.Save16Bit(img, path)` com deflate.
- ✔ Teste de consola: load do Autosave.tif real, imprimir dimensões,
  min/max/mediana por canal; round-trip save/load de um sintético.
- ⚠ Se o LibTiff não ler o float TIFF do DSS à primeira, tentar
  `Cv2.ImRead(path, ImreadModes.AnyDepth | ImreadModes.AnyColor)`.

## Tarefa 2 — Núcleo matemático (Fase A)
- Portar de `ReferenceCode/AstroPipeline.cs` (validar contra o Python!):
  `Normalize`, `Crop`, `ExtractBackground` (com `SolveLeastSquares`),
  `ColorCalibrate`, percentil e mediana exatos.
- ✔ Programa de teste: correr Fase A sobre o Autosave real; medianas por
  canal pós-background devem ficar ≈ 0.001–0.0015 (comparar com o Python).

## Tarefa 3 — Fase B + proxy
- `Stretch` (arcsinh+MTF), `Scnr`, `SaturationAndCurve`, `BlackPoint`.
- Geração do proxy (Cv2.Resize Area, lado maior 1536).
- `PreviewRenderer.Render(proxy, params)` → JPEG bytes.
- ✔ Guardar o JPEG do proxy com defaults e comparar visualmente com o
  output do Python (mesma aparência: fundo ~0.12, cores corretas).

## Tarefa 4 — UI base
- Página Editor: abrir ficheiro (caminho + recentes), preview, sliders
  com debounce 80 ms e descarte de renders obsoletos, overlay de progresso
  da Fase A. Tema escuro conforme 04-UI-SPEC.
- ✔ Mover sliders é fluido (< 150 ms/render no proxy, mostrar ms no rodapé).

## Tarefa 5 — NR + preview de crop
- `Denoise` (port do AstroPipeline.cs, usa OpenCvSharp bilateral/gaussian).
- Modal de preview NR com crop 100% antes/depois, centro por clique.
- ✔ Crop 600×400 com NR renderiza em ≤ ~2 s.

## Tarefa 6 — Export
- `ExportService` com `IProgress<double>`, ficheiros TIF 16-bit + JPEG q93,
  bloqueio de ações concorrentes, link "Abrir pasta".
- ✔ Export full-res termina sem OOM e com progresso visível.

## Tarefa 7 — Validação final contra o Python
- Correr `python astro_process_groundtruth.py Autosave.tif --out py_ref`
  e exportar da app com defaults.
- ✔ Diferença média absoluta entre os JPEGs < 2/255 por pixel.
  Investigar divergências na ordem: percentil → mediana → clamps → MTF.

## Armadilhas conhecidas
- `Mat.FromPixelData` não copia memória — manter o `float[]` vivo enquanto
  o Mat existir; nunca devolver Mats que apontem para buffers temporários.
- Mediana de 25M floats: usar seleção (e.g. `Array.Sort` de uma cópia é
  aceitável ~1s, ou nth_element via `Span`); não fazer sort por slider —
  medianas globais só acontecem na Fase A e no export.
- O bilateral do OpenCV em CV_32F espera sigmaColor na escala dos dados
  (0–1 aqui) — os valores 0.045/0.03 da spec JÁ estão nessa escala.
- Cuidado com a ordem RGB vs BGR do OpenCV: os dados do TIFF estão RGB;
  ao usar `ImEncode` para JPEG converter com `Cv2.CvtColor(..., RGB2BGR)`.
- `H/W` no termo radial usa as dimensões PÓS-crop.
