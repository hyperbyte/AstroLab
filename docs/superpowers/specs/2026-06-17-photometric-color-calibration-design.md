# Design — Plate solve (ASTAP): calibração fotométrica de cor (PCC) + anotação de campo

Data: 2026-06-17

## Contexto e objetivo

O `ColorCalibrate` atual ([AstroPipeline.cs](../../../Services/AstroPipeline.cs)) faz
white-balance por halos de estrela — funciona, mas não é fotométrico (sem
coordenadas, sem catálogo) e deriva em campos pobres em estrelas.

Este projeto adiciona **plate solve** (resolver as coordenadas da imagem com o
ASTAP) como capacidade base, e **dois consumidores** independentes da solução:

1. **PCC** — calibração fotométrica de cor: mede estrelas de catálogo (Gaia
   BP-RP) na imagem e ajusta os canais para a cor física bater certo.
2. **Anotação de campo** — overlay no preview: objetos de céu profundo, grelha
   de coordenadas, estrelas nomeadas e um cartão com a info do solve.

Tudo **opcional**; desligado ou em falha, o pipeline mantém o comportamento atual.

## Decisões (fechadas no brainstorm)

1. **Solver:** ASTAP local, por subprocess (padrão do `NativeFileDialog`). Offline.
2. **Plate solve desacoplado da PCC:** o solve corre uma vez e guarda uma
   `WcsSolution` na sessão; PCC e anotação consomem-na independentemente.
3. **Fotometria (PCC):** base Gaia do ASTAP (offline); mecanismo de extração a
   confirmar num **spike**; fallback = query online Gaia/Vizier.
4. **Dependência:** assumir ASTAP+DB instalados. Auto-detetar; se faltarem,
   apontar o utilizador. Não descarrega nem empacota o ASTAP.
5. **Falha:** PCC → fallback ao `ColorCalibrate` + nota; anotação → não mostra
   overlay + nota. Nunca bloqueia a Fase A.
6. **Escala/FOV:** de **distância focal efetiva (mm)** + **tamanho de píxel (µm)**
   + dimensões da imagem, passada ao ASTAP como hint (`-fov`).
7. **Campos focal/píxel:** dois inputs persistidos, pré-preenchidos por metadata
   (best-effort) → últimos valores → vazio.
8. **Overlay mostra:** DSO (Messier/NGC/IC), grelha RA/Dec, estrelas nomeadas e
   cartão de info (centro/escala/orientação/FOV).
9. **Catálogos de anotação:** CSVs compactos embebidos (públicos: OpenNGC para
   DSO + lista de estrelas nomeadas IAU). Offline, sem licença problemática.

## Arquitetura

### Capacidade base — `Services/PlateSolve.cs`
```csharp
public static bool TrySolve(LinearImage img, SolveSettings s,
                            out WcsSolution? wcs, out string status);
```
- Render temporário **auto-esticado** (luminância) porque o ASTAP precisa de
  estrelas visíveis, não dados lineares.
- Subprocess `astap.exe -f <tmp> -fov <graus> -wcs` (FOV do hint; sem hint → auto).
  Timeout. Parse do `.wcs`/`.ini` (CRVAL1/2 + matriz CD).
- **Nunca lança**; devolve `false` + `status` em falha.
- `SolveSettings`: `AstapPath`, `FocalLengthMm`, `PixelSizeUm`.

### `WcsSolution` (objeto partilhado)
Centro RA/Dec, matriz CD/escala, rotação (ângulo de posição), dimensões. Métodos:
```csharp
(double x, double y) WorldToPixel(double ra, double dec);   // anotação + PCC
(double ra, double dec) PixelToWorld(double x, double y);   // grelha/cartão
```
Projeção partilhada entre os dois consumidores.

### Consumidor 1 — `Services/PhotometricCalibration.cs`
```csharp
public static bool TryCalibrate(LinearImage img, WcsSolution wcs,
                                PhotSettings s, out string status);
```
Catálogo (estrelas+BP-RP no FOV) → projetar via `WorldToPixel` → medir fluxo por
canal na imagem **linear** (abertura, rejeitar saturadas/coladas/bordo) → ganhos
por canal (cor medida → esperada, normalizada ao G) → aplicar in-place.
Catálogo primário = base Gaia do ASTAP (spike); fallback = online Gaia/Vizier.

### Consumidor 2 — `Services/FieldAnnotation.cs`
```csharp
public static AnnotationOverlay Build(WcsSolution wcs, AnnotationCatalogs cat);
```
Dado o WCS + catálogos embebidos, devolve itens de overlay com coordenadas em
**fração 0–1** da imagem (escalam com o tamanho mostrado):
- **DSO** no campo: marcador + etiqueta (nome, tipo).
- **Grelha RA/Dec:** polilinhas projetadas + rótulos.
- **Estrelas nomeadas** no campo: marca + nome.
- **Cartão de info:** RA/Dec do centro, escala (arcsec/px), orientação, FOV.

Não toca nos píxeis — é dados para a UI desenhar.

### Escala / FOV
```
escala_arcsec_px = 206.265 × PixelSizeUm ÷ FocalLengthMm
FOV_altura_graus  = escala_arcsec_px × altura_px ÷ 3600
```
O FOV angular não muda com o resample de drizzle; usamos as dimensões da imagem
apresentada. Sem focal/píxel válidos → solve em modo auto.

## Integração na Fase A (`ProcessingSession`)

Novas propriedades: `Pcc` (bool), `ShowAnnotation` (bool), `Wcs` (WcsSolution?),
`SolveStatus`/`PccStatus` (strings), `FocalLengthMm`, `PixelSizeUm`, `AstapPath`.

Na `RunPhaseA`, após a geometria estar fixa (pós crop/resample):
```csharp
Wcs = null;
if (Pcc || ShowAnnotation)
{
    if (PlateSolve.TrySolve(img, solveSettings, out var wcs, out var st)) Wcs = wcs;
    SolveStatus = st;
}
// cor:
if (Pcc && Wcs != null && PhotometricCalibration.TryCalibrate(img, Wcs, photSettings, out var ps))
    PccStatus = ps;
else { if (Pcc) PccStatus = /* motivo */; AstroPipeline.ColorCalibrate(img); }
```
O solve é **caro mas corre 1× na Fase A** (reprocess). O overlay de anotação é
construído do `Wcs` cacheado e **liga/desliga sem reprocess** (puro UI).

## Persistência
Focal/píxel e `AstapPath` num ficheiro de settings (padrão do `RecentFiles`).
Pré-preenchimento: metadata (EXIF `FocalLength`/`FocalPlaneXResolution`,
best-effort) → últimos valores → vazio.

> Nota honesta: o `Autosave.tif` do DSS raramente preserva EXIF; o caso comum é
> virem dos últimos valores e o utilizador confirmar.

## UI

Secção "Cor":
- Checkbox **"Calibração fotométrica (PCC — ASTAP)"**.
- **Distância focal efetiva (mm)** e **Tamanho de píxel (µm)** (persistidos).
- Campo opcional **caminho do `astap.exe`**.
- Nota de estado (`SolveStatus`/`PccStatus`).

Secção "Anotação":
- Checkbox **"Mostrar anotação de campo"** + sub-toggles (DSO, grelha, estrelas,
  cartão) para ligar cada camada.
- Overlay desenhado **por cima do `<img>` do preview** (SVG com coordenadas em %,
  escala com o tamanho mostrado). Não interfere com os sliders de tone.

## Erros (todos → degradam, nunca lançam)
- Sem `astap.exe`/DB → "ASTAP não encontrado"; PCC usa halos, anotação não mostra.
- Solve falha/timeout → "solve falhou".
- PCC com < `MinStars` casadas → "poucas estrelas" + fallback halos.
- Sem fotometria offline e sem internet → "sem catálogo".

## Testes

- **Spike (primeiro):** confirmar extração offline de estrelas+BP-RP do ASTAP
  (CLI vs ler a DB); senão, ativar fallback online. Decide o resto.
- **`WcsSolution`:** round-trip `WorldToPixel`/`PixelToWorld` com WCS determinístico
  (sem ASTAP); objeto conhecido no FOV projeta na fração esperada.
- **`SelfTest solvetest <path>`:** corre solve no `Autosave.tif`, imprime RA/Dec/
  escala/FOV, lista DSO no campo, e grava uma imagem com o overlay "queimado" para
  inspeção visual.
- **`SelfTest pcctest <path>`:** PCC vs halos (A/B) + nº estrelas e ganhos.
- Nota honesta: o end-to-end exige ASTAP instalado; o spike valida logo.

## Decomposição sugerida (para o plano)
Três entregáveis independentes, todos sobre o `PlateSolve`:
1. **Plate solve + WcsSolution + cartão de info** (a anotação mais barata).
2. **PCC** (cor) — consome o WCS.
3. **Overlay completo** (DSO, grelha, estrelas) — consome o WCS.

## Fora de scope (YAGNI)
- Catálogo online como caminho primário (só fallback de PCC).
- Solver online (astrometry.net) ou outros solvers além do ASTAP.
- Edição/medição interativa sobre o overlay (é só visualização).

## Critérios de sucesso
1. Tudo off → resultado idêntico ao atual.
2. PCC on, ASTAP ausente → fallback + nota; processamento conclui.
3. Solve com ASTAP+DB e focal/píxel corretos → sucesso; PCC dá cor mais neutra
   que halos (A/B no `pcctest`); overlay coloca DSO conhecidos na posição certa.
4. Toggle de anotação liga/desliga sem reprocess.
5. Focal/píxel/AstapPath persistem entre sessões.

## Ficheiros afetados (previsão)
- Criar: `Services/PlateSolve.cs`, `Services/WcsSolution.cs`,
  `Services/PhotometricCalibration.cs`, `Services/FieldAnnotation.cs`.
- Criar: recursos embebidos de catálogo (DSO + estrelas nomeadas) + settings.
- Modificar: `Services/ProcessingSession.cs`, `Services/TiffIO.cs` (EXIF best-effort).
- Modificar: `Components/Pages/Editor.razor` (secções Cor + Anotação, overlay SVG)
  e JS de apoio ao posicionamento do overlay.
- Modificar: `Services/SelfTest.cs` + `Program.cs` (`solvetest`, `pcctest`).
