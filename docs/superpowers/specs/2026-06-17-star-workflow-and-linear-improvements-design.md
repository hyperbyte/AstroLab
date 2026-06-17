# Design — Melhorias do workflow de estrelas, contraste local, NR linear e drizzle/resample

Data: 2026-06-17

## Contexto e decisão estruturante

Comparação do pipeline atual contra uma checklist de fluxo de astrofotografia
revelou passos saltados. O utilizador decidiu que o
`ReferenceCode/astro_process_groundtruth.py` **deixa de ser regra** e a Fase A
**deixa de estar congelada** — o critério de equivalência numérica ao Python
(SPEC/01 #3) já não se aplica. O foco passa a ser o melhor resultado visual.

Junção `starless + stars` mantém-se sempre por **screen** (preferência confirmada).

## Âmbito desta ronda

1. **NR linear** (checklist #8) — redução de ruído antes do stretch, na Fase A.
2. **Contraste local** no fundo starless (checklist #12).
3. **Redução + cor das estrelas** na camada Stars (checklist #13).
4. **Drizzle/Resample** (checklist #2) — prompt ao abrir + resample por fator.

Fora de scope: stacking, plate solve, PCC fotométrico, finishing tipo Photoshop.

## Arquitetura

### Refactor do `StarWorkflow` (elimina duplicação proxy ↔ full)

Hoje a recombinação está duplicada e diverge facilmente: `Compose()` (proxy) e
`ApplyToFull()` + `ExportService` (full-res) montam o screen à mão. Extrair dois
métodos partilhados, usados pelas **duas** vias:

- `ProcessBackground(img)` → correção de clone (já existe) + ganho + saturação
  + **contraste local**.
- `ProcessStars(img)` → **erosão (redução)** + **saturação de estrelas**.

Recombinação final = `Screen(ProcessBackground(bg), ProcessStars(stars))` nos
dois caminhos. Garante preview e export sempre iguais.

Kernels/sigmas dependentes de escala (erosão, blur do contraste local) **escalam
com a resolução** (proxy vs full) para o efeito ser visualmente idêntico.

### Novos parâmetros

`StarWorkflow` (live no proxy via `ComposeStarPreview`):
- `LocalContrast` : double 0–1, default 0
- `StarReduction` : double 0–1, default 0
- `StarSaturation` : double 0–2.5, default 1.0

`ProcessingSession` (Fase A, dispara reprocess como `Radial`/`Crop`):
- `LinearDenoise` : double 0–1, default 0
- `DrizzleFactor` : int {1,2,3}, default 1

## Algoritmos

### NR linear (Fase A, full-res, corre 1×)
Após `ColorCalibrate`, antes do proxy. Cromático: blur gaussiano σ≈2 por canal.
Luminância: bilateral suave. Sem máscara forte de estrelas (pré-stretch os
núcleos dominam). Bem mais gentil que o `Denoise` de export porque em linear o
sinal está comprimido nas sombras. Default 0 (off); é destrutivo (cozido no
`LinearFull`), por isso opt-in.

### Contraste local (fundo)
Unsharp mask de raio grande na luminância, ratiométrico (preserva cor):
`L' = L + amount·(L − blur_grande(L))`, depois aplica o rácio `L'/L` aos 3 canais.
Sigma escala com o lado maior. Default 0.

### Redução de estrelas (erosão)
`Cv2.Erode` com kernel elíptico na camada Stars (encolhe núcleos brilhantes
sobre fundo preto). Blend: `stars·(1−amt) + eroded·amt`. Tamanho do kernel escala
com a resolução. Default 0.

### Cor das estrelas
Saturação seletiva na camada Stars (mesma fórmula de `AdjustBackground`, ganho=1).
Default 1.0.

### Resample por drizzle
Se `DrizzleFactor` N>1: reduz dimensões por N com interpolação de área
(`InterpolationFlags.Area`), logo após o `load`, antes do `Normalize`:
```
load → [resample ÷N] → normalize → crop → background → color → proxy
```
N=1 → no-op (comportamento atual intacto).

## Fluxo de UI

### Prompt de drizzle
Todas as vias de abertura convergem em `OpenFromPath`. Intercetar aí, antes da
Fase A: modal curto com lista fixa **Nenhum / 2× / 3×**. Ao confirmar, define
`Session.DrizzleFactor` e corre a Fase A.

### Controlos novos
- Secção "Fundo" (sempre visível): slider **NR linear** 0–1, dispara reprocess,
  com nota a avisar que recorre a Fase A.
- Secção "Estrelas", só em modo fundo (`Session.Stars != null`), juntar aos
  sliders atuais (Ganho/Saturação fundo):
  - **Contraste local (fundo)** 0–1, default 0
  - **Redução de estrelas** 0–1, default 0
  - **Saturação estrelas** 0–2.5, default 1.0

Todos live exceto NR linear (reprocess). "Repor defaults" repõe tudo.

## Critérios de sucesso (validação visual — não há testes automáticos)

1. App compila e arranca.
2. Cada slider novo a 0 / default e drizzle "Nenhum" → resultado idêntico ao
   atual (no-op verificável).
3. Subir cada controlo → efeito visível e **coerente entre preview e export**
   (mesmo crop, comparação visual).
4. Drizzle 2×/3× → imagem carregada com dimensões ÷N; RAM proporcionalmente menor.

## Ficheiros afetados (previsão)

- `Services/ProcessingSession.cs` — `LinearDenoise`, `DrizzleFactor`, resample +
  NR linear na Fase A.
- `Services/AstroPipeline.cs` — `DenoiseLinear`, helper de contraste local.
- `Services/StarWorkflow.cs` — `ProcessBackground`/`ProcessStars`, novos params.
- `Services/StarRemoval.cs` ou `PreviewRenderer.cs` — erosão de estrelas (OpenCV).
- `Services/ExportService.cs` — usar os métodos partilhados de `StarWorkflow`.
- `Components/Pages/Editor.razor` — modal de drizzle + sliders novos.
