# Design — Remoção de halos magenta nas estrelas

Data: 2026-06-18

## Objetivo

Remover dominante magenta/roxo nos halos das estrelas (cromatismo), via a técnica
clássica do PixInsight: **inverter → SCNR (remover verde) → inverter**. Magenta é o
complementar do verde; ao inverter, o magenta vira verde, o SCNR remove-o, e ao
inverter de volta o magenta desaparece. Passo **interativo e opcional** (nem sempre
há roxo). Aplica-se só à **camada de estrelas** (os halos vivem nas estrelas).

## Decisões (fechadas no brainstorm)

1. Scope: **só a camada de estrelas** (`StarWorkflow`, já separada).
2. Técnica: `inverter → Scnr(amount) → inverter`, reusando o `AstroPipeline.Scnr` existente.
3. Interativo: slider 0–1, **default 0 (no-op)**. A amount=1 sobre-corrige (validado:
   fundo B/G 1,33→0,86), por isso é tunável.

## Implementação

- **`AstroPipeline.RemoveMagenta(LinearImage img, double amount)`** — in-place;
  `amount≤0` = no-op; senão `d=1-d` → `Scnr(img, amount)` → `d=1-d`.
- **`StarWorkflow.StarMagenta`** (double, default 0). No `ProcessStars` (já faz
  redução + saturação), aplica `RemoveMagenta` no fim. Partilhado preview/export.
- **UI:** slider "Remover halos magenta" na secção de estrelas (modo fundo), junto a
  Redução/Saturação. Live via `ComposeStarPreview` (sem reprocess).

## Verificação

- **`SelfTest magentatest`**: campo sintético de estrelas com halos magenta;
  `RemoveMagenta(0)` = no-op (dados idênticos); `RemoveMagenta(0.8)` reduz o excesso
  R+B sobre G (R/G e B/G dos halos aproximam-se de 1). Registado em `Program.cs:5`.
- Build limpo; validação visual na app (slider nas estrelas separadas).

## Fora de scope
- Magenta na imagem inteira (decidido: só estrelas).
- Deteção automática de quando aplicar (é manual/interativo por design).
