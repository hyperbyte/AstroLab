# PCC (calibração fotométrica de cor) — investigação e decisão de NÃO adotar

Data: 2026-06-17

## Decisão

A PCC artesanal (Fase 2) **não foi adotada**. O método atual de cor — white-balance
por halos de estrela (`AstroPipeline.ColorCalibrate`) + SCNR — fica-se **perto da
verdade fotométrica** e é mais tolerante a sinal baixo. O código da PCC não foi
mergeado; ficam este documento, o [spec](2026-06-17-pcc-phase2-design.md) e o
[plano](../plans/2026-06-17-pcc-phase2.md) como registo.

## O que se construiu e testou

Pipeline completo (subagent-driven): `GaiaCatalog` (cone search Vizier Gaia DR3),
`PhotometricCalibration` (fotometria de abertura por canal nas estrelas projetadas
via `WcsSolution` da Fase 1 + ajuste cor↔BP-RP), integração na Fase A com fallback,
UI e `pcctest`. Mecanicamente funcional.

## Porque não presta (causa raiz, com evidência)

Comparação no campo de Rho Ophiuchi (e Arcturus), medindo R/G e B/G na nebulosa:

| Método | R/G | B/G | Veredicto |
|---|---|---|---|
| Siril SPCC (referência, espectros Gaia xp_sampled) | ~1,01 | ~1,00 | neutro = correto |
| Halos (`ColorCalibrate`) | 1,08 | 1,05 | **perto da verdade** ✓ |
| PCC artesanal | ~0,82 | ~1,1 | errada (fria/cyan) ✗ |

1. **Sinal de cor fraco.** A fotometria de abertura sobre o stack mediu R/G
   praticamente **constante** com a cor de catálogo (azul vs vermelho ~iguais):
   discriminação de cor quase nula. O ajuste tinha slope ~0,2 e sigma ~0,14.
2. **Sem "sweet spot" de parâmetros.** Abertura grande → dominada pelo fundo (sem
   cor); pequena → sinal aparece mas ganhos instáveis (gR≈5); janela de cor larga →
   viés de gigantes vermelhas (branco extrapolado); estreita → poucas estrelas.
3. **O Siril usa exatamente a mesma abordagem** (fotometria + ajuste linear
   cor↔catálogo) e na MESMA imagem também marca *"imprecise solution"* — mas acerta
   porque usa **espectros xp_sampled** (não só BP-RP), aberturas grandes (14/24 px),
   ~2000 estrelas e tratamento próprio.
4. **Gradiente NÃO é a causa.** O nosso `ExtractBackground` corre antes da cor; o
   Siril mantém o aviso de gradiente mesmo sobre a nossa imagem já sem gradiente, e
   o ajuste é igual → o aviso é genérico para um fit ruidoso.
5. **Limitação de dados.** O alvo de teste tinha **~1h40 de integração numa DSLR**
   (Bortle 3): SNR baixo → cor por estrela ruidosa. PCC fiável precisa de muito
   mais integração ou da via espectral. Os halos toleram melhor sinal baixo.

## O verde

A dominante verde dos OSC é tratada pelo **SCNR** (Fase B, `AstroPipeline.Scnr`,
default 0.7 — equivalente ao "Remove Green Noise" do Siril). No caminho dos halos
o verde já é o canal mais baixo (controlado). O "verde" só aparecia na PCC partida
e no output do Siril a que não apliquei SCNR.

## Recomendação para o futuro

Se a PCC voltar à mesa, **delegar ao Siril** (`siril-cli` SPCC) por subprocess —
como já se faz com o ASTAP para o plate solve — em vez de reimplementar
fotometria espectral. A Fase 1 (plate solve + `WcsSolution`) fica sólida e
reutilizável para essa delegação e para o overlay (Fase 3).
