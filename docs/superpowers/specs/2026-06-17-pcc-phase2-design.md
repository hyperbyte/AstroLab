# Design — Fase 2: Calibração fotométrica de cor (PCC)

Data: 2026-06-17

## Contexto

A Fase 1 (mergeada em `main`) deu o **plate solve** (ASTAP) e uma `WcsSolution`
partilhada. Esta fase usa-a para **calibração fotométrica de cor**: medir
estrelas de catálogo na imagem e ajustar os canais para a cor física bater certo,
substituindo o white-balance por halos (`AstroPipeline.ColorCalibrate`) quando
disponível.

## Achado do spike (JÁ FEITO)

ASTAP `astap_cli.exe`:
- `-extract2 snr` exporta estrelas **detetadas** com `x,y,hfd,snr,flux,ra,dec` —
  **fluxo único (luminância), sem magnitude/cor de catálogo**. Logo não serve para
  cor, e o ASTAP **não expõe fotometria de catálogo** por CLI.
- A base D80 é binária (`.1476`, formato proprietário) — ler offline seria
  desproporcionado.

→ A cor de catálogo (BP-RP) vem de **query online ao Gaia/Vizier**. Tudo o resto é
offline (temos a `WcsSolution` e medimos o fluxo na nossa imagem linear).

## Decisões (fechadas no brainstorm)

1. **Catálogo:** cone search online Gaia/Vizier (RA/Dec + BP/RP). Sem auth.
2. **Medição:** fotometria de abertura **por canal (R,G,B)** na imagem linear, nas
   posições das estrelas Gaia projetadas via `WcsSolution.WorldToPixel`.
3. **Modelo de ajuste:** regressão empírica de `log(R/G)` e `log(B/G)` vs `BP-RP`;
   correção para o ponto branco **G2V (BP-RP ≈ 0,82)**. Sem curvas de resposta.
4. **Falha → fallback** ao `ColorCalibrate` + nota. Internet é a única dependência
   nova. Nunca bloqueia.

## Arquitetura

### `Services/GaiaCatalog.cs`
```csharp
public sealed record GaiaStar(double Ra, double Dec, double BpRp, double GMag);
public static class GaiaCatalog
{
    // Cone search por FOV. Devolve [] em qualquer falha (timeout/rede/parse). Nunca lança.
    public static IReadOnlyList<GaiaStar> Query(double raDeg, double decDeg,
                                                double radiusDeg, out string status);
}
```
- HTTP a um endpoint Vizier/Gaia que aceita cone search e devolve CSV/TSV com
  `ra, dec, phot_bp_mean_mag, phot_rp_mean_mag, phot_g_mean_mag`.
- Filtra à partida linhas sem BP nem RP. Timeout (ex.: 20s).

### `Services/PhotometricCalibration.cs`
```csharp
public sealed record PccSettings(int MinStars = 30, double ApertureRadiusPx = 6,
                                 double WhitePointBpRp = 0.82);
public static bool TryCalibrate(LinearImage img, WcsSolution wcs, PccSettings s,
                                out string status);   // aplica in-place; nunca lança
```
Etapas privadas (cada uma testável):
1. `GaiaCatalog.Query(wcs.CenterRaDeg, wcs.CenterDecDeg, raio)` — raio ≈ metade da
   diagonal do FOV.
2. Projetar cada estrela: `wcs.WorldToPixel(ra,dec)` → píxel (converter 1-based→0).
3. **Fotometria de abertura por canal**: soma R/G/B numa abertura de raio
   `ApertureRadiusPx`, menos a mediana de um anel (fundo local) → fluxo estelar.
4. **Seleção**: dentro da imagem (com margem), pico não saturado, isolada (sem
   outra estrela Gaia a < N px), `GMag` numa gama (ex.: 7–15), fluxo G > 0.
5. **Regressão**: ajustar `log(R/G) = a_R + b_R·BP_RP` e `log(B/G) = a_B + b_B·BP_RP`
   (mínimos quadrados) sobre as estrelas selecionadas.
6. **Ganhos**: avaliar as retas no ponto branco → `ratioR*, ratioB*`;
   `gR = 1/ratioR*`, `gB = 1/ratioB*`, `gG = 1`.
7. **Aplicar** à imagem linear com a mesma estrutura do `ColorCalibrate`
   (subtrai mediana de fundo por canal, multiplica pelo ganho, repõe fundo do G),
   clamp 0–1.
8. Se `nSelecionadas < MinStars` → `false` + status.

## Integração na Fase A (`ProcessingSession`)

- Nova propriedade `Pcc` (bool, default off).
- O solve passa a correr quando **`Pcc || ShowAnnotation`** (a PCC precisa do WCS).
- Passo de cor (após o solve, substitui o atual):
```csharp
if (Pcc && Wcs != null && PhotometricCalibration.TryCalibrate(img, Wcs, pcc, out var st))
    PccStatus = st;                       // "PCC ok — 137 estrelas, gR=1.08 gB=0.94"
else { if (Pcc) PccStatus = st;           // motivo da falha
       AstroPipeline.ColorCalibrate(img); }  // fallback
```
- `PccStatus` exposto à UI. Toggle dispara reprocess; nunca bloqueia.

## UI
Secção "Anotação"/"Cor": checkbox **"Calibração fotométrica (PCC)"** (reusa
focal/píxel já existentes; disabled durante busy). Nota de estado (`PccStatus`):
sucesso (nº estrelas + ganhos) ou motivo da falha (sem internet / poucas estrelas /
sem solve). Reusa o cartão de info da Fase 1.

## Erros (todos → fallback + status, nunca lançam)
- Sem `Wcs` (solve falhou/ASTAP ausente) → "sem solve" → halos.
- Query Gaia falha/timeout/sem rede → "sem catálogo (rede?)" → halos.
- `< MinStars` casadas/selecionadas → "poucas estrelas" → halos.

## Testes
- **`SelfTest pcctest <path>`**: Fase A + solve + PCC no `testdata/Autosave.tif`;
  imprime nº de estrelas Gaia, nº casadas/selecionadas, e `gR/gB`; escreve **A/B**
  (`testdata/pcc_a_halos.jpg` vs `testdata/pcc_b_pcc.jpg`) para comparação visual.
- **Regressão offline** (`pccfit`): estrelas sintéticas com cor conhecida e ganhos
  conhecidos → confirmar que a regressão recupera os ganhos (sem rede).
- Validação final visual (Playwright/screenshot), como na Fase 1.

## Critérios de sucesso
1. PCC off → idêntico ao atual.
2. PCC on sem internet / sem solve → fallback + nota; processamento conclui.
3. PCC on com solve + internet → ≥ `MinStars`, cor visivelmente mais neutra que os
   halos no A/B do `pcctest`.
4. A regressão recupera ganhos sintéticos conhecidos (`pccfit`).

## Fora de scope (YAGNI)
- SPCC com curvas de resposta do sensor/filtro.
- Catálogo offline / leitura da base D80.
- Cache de queries Gaia; correção de extinção atmosférica.
- A Fase 3 (overlay DSO/grelha/estrelas) continua em plano próprio.

## Ficheiros afetados (previsão)
- Criar: `Services/GaiaCatalog.cs`, `Services/PhotometricCalibration.cs`.
- Modificar: `Services/ProcessingSession.cs` (prop `Pcc`, gate do solve, passo de cor).
- Modificar: `Components/Pages/Editor.razor` (checkbox PCC + estado).
- Modificar: `Services/SelfTest.cs` + `Program.cs` (`pccfit`, `pcctest`).
