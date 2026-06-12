# AstroLab — Especificação de UI

Página única (`/`), layout em duas colunas. Tema escuro obrigatório —
é uma app de astrofotografia, será usada de noite (fundo `#15171c`,
texto `#c9ccd4`, acento `#4f8ff7`).

```
┌────────────────────────────────────────────────┬──────────────────┐
│                                                │  ABRIR           │
│                                                │  [caminho..] [📂]│
│                PREVIEW                         │  recentes ▾      │
│        (imagem, max 100% da coluna,            │──────────────────│
│         object-fit: contain)                   │  TONE            │
│                                                │  Stretch   ──●── │
│                                                │  Céu       ──●── │
│   [estado: "a extrair background… 43%"]        │  Black pt  ●──── │
│                                                │  SCNR      ───●─ │
│                                                │  Saturação ──●── │
│                                                │──────────────────│
│                                                │  RUÍDO           │
│                                                │  Força NR  ───●─ │
│                                                │  [Pré-visualizar │
│                                                │   NR (crop 100%)]│
│                                                │──────────────────│
│                                                │  [Repor defaults]│
│                                                │  [EXPORTAR]      │
│                                                │  ▓▓▓▓░░░░ 52%    │
└────────────────────────────────────────────────┴──────────────────┘
```

## Sliders

| Slider | Param | Min | Max | Step | Default |
|---|---|---|---|---|---|
| Stretch | `Stretch` | 100 | 1200 | 10 | 600 |
| Céu (fundo) | `Sky` | 0.06 | 0.20 | 0.005 | 0.12 |
| Black point | `BlackPoint` | 0 | 0.05 | 0.001 | 0 |
| SCNR | `Scnr` | 0 | 1 | 0.05 | 0.7 |
| Saturação | `Saturation` | 0 | 1 | 0.05 | 0.45 |
| Força NR | `NoiseReduction` | 0 | 1 | 0.05 | 1.0 |

- Cada slider: label, `<input type=range>` com `@bind:event="oninput"`,
  valor numérico editável ao lado, ícone ↺ de reset individual.
- NR **não** atualiza o preview principal (mostrar nota subtil
  "aplica-se no export — usar pré-visualização").

## Comportamentos

- **Abrir:** textbox de caminho + botão. Durante a Fase A, overlay no
  preview com etapa atual e percentagem; sliders desativados.
- **Preview NR:** abre painel modal com crop 600×400 a 100%, lado-a-lado
  antes/depois. O centro do crop define-se clicando no preview principal
  (default: centro da imagem). Botão "Atualizar" re-renderiza com os
  parâmetros atuais.
- **Exportar:** dialog simples com prefixo de saída (default: pasta do
  ficheiro de origem + nome + `_processed`). Barra de progresso; no fim,
  link "Abrir pasta".
- **Repor defaults:** restaura `ToneParams.Defaults`, re-renderiza.
- Mostrar discretamente no rodapé: dimensões da imagem, RAM aproximada
  em uso, tempo do último render do preview (ms) — útil para diagnosticar.

## Acessórios (nice-to-have, só se sobrar tempo)

- Comparação antes/depois no preview (manter pressionado = mostra
  autostretch neutro do linear).
- Persistir últimos parâmetros usados por ficheiro em
  `%LOCALAPPDATA%/AstroLab/params/{hash}.json`.
- Histograma RGB pequeno por baixo do preview (calculado no proxy).
