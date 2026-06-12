# AstroLab — Visão Geral

## O que é

Aplicação local **.NET 10 / Blazor Server** para processamento interativo de
stacks lineares de astrofotografia (Autosave.tif do DeepSkyStacker).
O utilizador abre um TIF, ajusta o processamento com sliders em tempo real
sobre um preview, e exporta o resultado em full-res (TIF 16-bit + JPEG).

## Contexto do utilizador

- Equipamento: Canon RP (não modificada, full frame) + RedCat 51, ASIAIR Plus
- Stacker: DeepSkyStacker → Autosave.tif (32-bit float RGB, linear, ~6264×4180)
- O pipeline matemático está **validado e congelado** em
  `ReferenceCode/astro_process_groundtruth.py` — o resultado da app deve ser
  numericamente equivalente (tolerância: diferenças de arredondamento float).
- Dados típicos: alvos baixos com gradiente forte de poluição luminosa
  (Lisboa) e vinhetagem residual de flats sem bias → daí o modelo de
  background com termos radiais.

## Princípio central de desempenho (LER PRIMEIRO)

O pipeline divide-se em duas fases com custos muito diferentes:

| Fase | Etapas | Custo (25 MP) | Quando corre |
|---|---|---|---|
| **A — Preparação** | load TIF, normalização, crop, extração de background, calibração de cor | ~10–30 s | 1× ao abrir o ficheiro |
| **B — Tone** | stretch arcsinh, MTF, SCNR, saturação, curva | ms num proxy | a cada movimento de slider |
| **C — Export** | Fase B em full-res + redução de ruído + encode | ~20–60 s | quando o utilizador exporta |

Ao terminar a Fase A, guardar em memória:
1. `LinearFull` — float32 RGB full-res pós-fase-A (~300 MB; aceitável, app local)
2. `LinearProxy` — downscale do anterior para **lado maior = 1536 px** (área-média)

Os sliders operam SEMPRE sobre `LinearProxy`. O export aplica os mesmos
parâmetros a `LinearFull` num background task com progresso.

A redução de ruído (bilateral + gaussiano mascarados) é a operação mais cara
e **não entra no preview principal** — tem um modo próprio (ver 04-UI-SPEC,
"Preview NR" com crop 100%).

## Stack técnica

- .NET 10, Blazor Server (interactive server rendering)
- **OpenCvSharp4** + `OpenCvSharp4.runtime.win` — filtros (GaussianBlur,
  BilateralFilter) e encode JPEG/PNG
- **LibTiff.Net** — leitura do TIF 32-bit float do DSS e escrita do TIF
  16-bit final com compressão deflate. (Validar na Tarefa 1 que lê o
  Autosave real; fallback: ler via OpenCvSharp `Cv2.ImRead` com
  `ImreadModes.AnyDepth | AnyColor`, que suporta TIFF float.)
- Sem base de dados, sem autenticação — app local, single user
  (`app.Urls.Add("http://localhost:5151")`).

## Estrutura de entrega

```
AstroLab/
  SPEC/                      ← estes documentos
  ReferenceCode/
    astro_process_groundtruth.py   ← VERDADE matemática (Python, testado)
    AstroPipeline.cs               ← port C# de referência do núcleo
```

## Critério de aceitação global

1. Abrir o Autosave.tif de teste (~250 MB float32) sem exceder ~2 GB de RAM.
2. Mover qualquer slider atualiza o preview em < 150 ms (proxy 1536 px).
3. `Exportar` produz TIF 16-bit + JPEG full-res visualmente equivalentes ao
   output do script Python com os mesmos parâmetros.
4. Parâmetros por defeito = defaults do script Python (stretch 600, sky 0.12,
   sat 0.45, nr 1.0, scnr 0.7, crop 0.012, radial ON).
