# Design — Fase 3: Overlay de anotação de campo

Data: 2026-06-18

## Contexto

A Fase 1 (em `main`) deu o **plate solve** (ASTAP) + `WcsSolution` (projeção
gnomónica `WorldToPixel`/`PixelToWorld`) e um **cartão de info** (centro/escala/
FOV/orientação) na secção "Anotação" do `Editor`. Esta fase desenha um **overlay**
sobre o preview: objetos de céu profundo, estrelas nomeadas e grelha RA/Dec. O
cartão de info já existe.

## Decisões (fechadas no brainstorm)

1. **Catálogo:** ler `<pasta-ASTAP>/deep_sky.csv` em runtime (DSO + estrelas
   nomeadas IAU + tamanhos, num só ficheiro; já dependemos do ASTAP para o solve).
   Se faltar → overlay mostra só a grelha.
2. **Render:** SVG sobre o `<img>` do preview, alinhado **sem JS** via
   `viewBox` = dimensões da imagem + `preserveAspectRatio="xMidYMid meet"` (espelha
   o `object-fit:contain` do preview). `pointer-events:none`.
3. **Camadas com sub-toggles:** DSO, grelha, estrelas nomeadas — ligar/desligar é
   **instantâneo** (o overlay está em cache; sem reprocess).

## Arquitetura

### `Services/FieldAnnotation.cs` (puro, testável)
```csharp
public sealed record DsoMark(double X, double Y, double WidthPx, double HeightPx,
                             double AngleDeg, string Name, bool IsStar);
public sealed record GridLine(IReadOnlyList<(double X, double Y)> Points, string? Label);
public sealed record FieldOverlay(int Width, int Height,
                                  IReadOnlyList<DsoMark> Objects, IReadOnlyList<GridLine> Grid);

public static FieldOverlay Build(WcsSolution wcs, int width, int height, string? astapDir);
```
- Projeta catálogo + grelha para **píxeis da imagem** (coords no espaço da imagem
  resolvida, `width`×`height` = `FullWidth`×`FullHeight`).
- `astapDir` null/sem `deep_sky.csv` → `Objects` vazio (só grelha).

### Integração
- `ProcessingSession`: guarda `FieldOverlay? Overlay` (construído quando o solve
  corre, junto ao `Wcs`). Já existe `Wcs`; basta `Overlay = FieldAnnotation.Build(Wcs, FullWidth, FullHeight, astapDir)`.
- `Editor`: desenha o SVG a partir de `Session.Overlay`, com 3 checkboxes de camada
  (estado local na página, default todas on quando há overlay).

## Catálogo `deep_sky.csv`

Formato ASTAP: `RA, DEC, nome(s), length[0.1′], width[0.1′], orient[°]`.
Conversões: **RA° = RA/2400**, **DEC° = DEC/3600**, tamanho′ = length/10.
- Projeta cada objeto via `wcs.WorldToPixel`; mantém os que caem dentro da imagem
  (com margem). 
- **DSO:** filtra por tamanho mínimo (≥ 3′) para não poluir; desenha elipse com
  `WidthPx`/`HeightPx` (do tamanho real) + `AngleDeg`.
- **Estrelas nomeadas:** entradas com `width == 0` (pontuais) → `IsStar=true`,
  marcador pequeno; entram sem filtro de tamanho.
- Tamanho em píxeis = (tamanho em arcsec) ÷ (escala ″/px do `wcs`).

## Grelha RA/Dec
Linhas de RA constante e Dec constante a passos "redondos" (escolhidos pela escala
do FOV), projetadas ponto-a-ponto via `WorldToPixel` (curvam-se corretamente),
recortadas à imagem, com rótulo numa extremidade.

## UI
Na secção "Anotação", abaixo do cartão de info, 3 checkboxes:
**Objetos (DSO)**, **Grelha**, **Estrelas nomeadas** (default on). O SVG sobre o
preview mostra as camadas ativas. Sem overlay (sem solve) → secção inalterada.

## Render (SVG, sem JS)
```html
<svg viewBox="0 0 {W} {H}" preserveAspectRatio="xMidYMid meet"
     style="position:absolute; inset:0; width:100%; height:100%; pointer-events:none">
  <!-- elipses DSO, marcadores de estrela, polilinhas da grelha, textos -->
</svg>
```
Sobre o `.preview-pane` (já `position:relative`), por cima do `<img>`. Coordenadas
em píxeis da imagem; o `viewBox` + `meet` alinham com o `object-fit:contain` do img.

## Testes
- **`FieldAnnotation` puro:** com um `WcsSolution` fixo (fixture da Fase 1) e dims
  conhecidas, um objeto de RA/Dec conhecidas projeta no píxel esperado; conversão de
  unidades do CSV verificada; objeto fora do FOV é excluído.
- **`SelfTest annotatetest <path>`:** Fase A + solve + `Build`; imprime nº de DSO /
  estrelas / linhas de grelha no campo e grava um JPEG com o overlay "queimado"
  (elipses/markers/grelha) para inspeção visual.
- **Visual** na app (Playwright) no fim.

## Fora de scope (YAGNI)
- Pan/zoom do overlay (o preview é fixo, ajustado à janela).
- Clicar/selecionar objetos; tooltips interativos.
- Magnitudes/filtros avançados além do tamanho mínimo.

## Critérios de sucesso
1. Sem solve → secção e preview inalterados.
2. Com solve + `deep_sky.csv` → DSO conhecidos (ex.: M-objects no campo) aparecem na
   posição certa e do tamanho certo; estrelas nomeadas (ex.: Antares) no sítio certo;
   grelha alinhada.
3. Toggles de camada ligam/desligam sem reprocessar.
4. Sem `deep_sky.csv` → só grelha, sem erro.

## Ficheiros afetados (previsão)
- Criar: `Services/FieldAnnotation.cs`.
- Modificar: `Services/ProcessingSession.cs` (campo `Overlay`, build no solve),
  `Services/PlateSolve.cs` ou sessão (expor a pasta do ASTAP detetada).
- Modificar: `Components/Pages/Editor.razor` (SVG + 3 checkboxes) e `wwwroot/app.css`.
- Modificar: `Services/SelfTest.cs` + `Program.cs` (`annotatetest`).
