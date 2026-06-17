# Design — Calibração fotométrica de cor (PCC) com plate solve (ASTAP)

Data: 2026-06-17

## Contexto e objetivo

O `ColorCalibrate` atual ([AstroPipeline.cs](../../../Services/AstroPipeline.cs)) faz white-balance
por halos de estrela não saturados — funciona, mas não é fotométrico (sem
coordenadas, sem catálogo) e deriva em campos pobres em estrelas. Este projeto
adiciona **calibração fotométrica de cor (PCC)**: resolve a placa (plate solve)
para obter coordenadas, obtém estrelas de catálogo com cor conhecida (Gaia
BP-RP), mede-as na imagem e ajusta os canais para a cor física bater certo.

É um **subsistema novo e opcional**; quando desligado ou em falha, o pipeline
mantém exatamente o comportamento atual.

## Decisões (fechadas no brainstorm)

1. **Solver:** ASTAP local, invocado por subprocess (mesmo padrão do
   `NativeFileDialog`). Offline e privado.
2. **Fotometria:** base Gaia do ASTAP (offline). Mecanismo exato de extração das
   estrelas+BP-RP **a confirmar num spike**; fallback = query online Gaia/Vizier.
3. **Dependência:** assumir ASTAP+DB instalados. Auto-detetar; se faltarem, a app
   aponta o utilizador (instruções/link). Não descarrega nem empacota nada.
4. **Falha:** fallback ao `ColorCalibrate` por halos + nota de estado a explicar.
   Nunca bloqueia a Fase A.
5. **Escala/FOV:** calculada de **distância focal efetiva (mm)** + **tamanho de
   píxel (µm)** + dimensões da imagem, passada ao ASTAP como hint (`-fov`).
6. **Campos focal/píxel:** dois inputs na secção "Cor", pré-preenchidos por
   metadata (best-effort) → últimos valores memorizados → vazio. Persistidos.

## Arquitetura

### Novo serviço: `Services/PhotometricCalibration.cs`

Fronteira única:
```csharp
public static bool TryCalibrate(LinearImage img, PccSettings s, out string status);
```
- Aplica a calibração **in-place** e devolve `true` em sucesso; `false` em
  qualquer falha (`status` explica). **Nunca lança** para fora — o
  `ProcessingSession` decide o fallback.
- `PccSettings`: `AstapPath` (auto-detetado ou configurado), `FocalLengthMm`,
  `PixelSizeUm`, `MinStars` (default 20), `ApertureRadiusPx`.

Etapas privadas, cada uma testável isoladamente:
1. **Localizar ASTAP+DB** — PATH, dirs comuns (`C:\Program Files\astap\astap.exe`),
   ou `AstapPath`. DB junto ao exe.
2. **Imagem de solve** — render temporário **auto-esticado** (luminância,
   PNG/TIFF 16-bit) porque o ASTAP precisa de estrelas visíveis, não dados
   lineares.
3. **Solve** — subprocess `astap.exe -f <tmp> -fov <graus> -wcs` (FOV do hint;
   sem hint válido → modo auto). Timeout. Parse do `.wcs`/`.ini` (CRVAL1/2 +
   matriz CD) → solução.
4. **Catálogo (estrelas + BP-RP no FOV)** — primário: base Gaia do ASTAP
   (mecanismo do spike); fallback: cone search online Gaia/Vizier.
5. **Medição** — projetar estrelas do catálogo para píxeis via WCS; somar fluxo
   por canal (R,G,B) na imagem **linear** (abertura `ApertureRadiusPx`),
   rejeitando saturadas/coladas/de bordo.
6. **Ajuste** — ganhos por canal para a cor medida bater com a esperada
   (catálogo BP-RP, normalizada ao G) → aplicar à imagem linear (mesma forma de
   saída do `ColorCalibrate`).

### Escala / FOV

```
escala_arcsec_px = 206.265 × PixelSizeUm ÷ FocalLengthMm
FOV_altura_graus  = escala_arcsec_px × altura_px ÷ 3600
```
O FOV angular não muda com o resample de drizzle (só muda a contagem de píxeis);
usamos as dimensões da imagem apresentada ao ASTAP. Sem focal/píxel válidos, o
solve cai em modo auto.

## Integração na Fase A (`ProcessingSession`)

Novas propriedades: `Pcc` (bool, default `false`), `PccStatus` (string p/ UI),
`FocalLengthMm`, `PixelSizeUm`. O passo de cor passa a:
```csharp
if (Pcc && PhotometricCalibration.TryCalibrate(img, settings, out var status))
    PccStatus = status;                       // ex.: "PCC ok — 137 estrelas"
else {
    if (Pcc) PccStatus = status;              // ex.: "ASTAP não encontrado — usei halos"
    AstroPipeline.ColorCalibrate(img);        // fallback (método atual)
}
```
O toggle dispara reprocess (como `Radial`/`LinearDenoise`). Nunca bloqueia.

## Persistência

Focal/píxel e o `AstapPath` guardados num pequeno ficheiro de settings (padrão do
`RecentFiles`). Pré-preenchimento: metadata da imagem (best-effort via EXIF —
`FocalLength`, `FocalPlaneXResolution`) → últimos valores → vazio.

> Nota honesta: o `Autosave.tif` do DSS raramente preserva EXIF, por isso o caso
> comum é virem dos últimos valores e o utilizador confirmar.

## UI

Secção "Cor" nos controlos:
- Checkbox **"Calibração fotométrica (PCC — ASTAP)"** (disabled durante busy).
- **Distância focal efetiva (mm)** e **Tamanho de píxel (µm)** — pré-preenchidos,
  editáveis, persistidos.
- Campo opcional **caminho do `astap.exe`** (se a auto-deteção falhar).
- Nota de estado (`PccStatus`): sucesso+nº estrelas, ou motivo da falha.

Sem overlay/anotação do campo resolvido (fora de scope).

## Erros (todos → fallback + status, nunca lançam)

- Sem `astap.exe`/DB → "ASTAP não encontrado".
- Solve falha/timeout → "solve falhou".
- < `MinStars` casadas → "poucas estrelas".
- Sem fotometria offline e sem internet → "sem catálogo".

## Testes

- **Spike (primeiro, de-risca a fotometria):** confirmar como obter estrelas +
  BP-RP offline do ASTAP (CLI vs ler a DB); se inviável, ativar o fallback online.
  Decide o resto da implementação.
- **`SelfTest pcctest <path>`:** corre a PCC no `Autosave.tif`, imprime RA/Dec/FOV,
  nº de estrelas e ganhos por canal, e escreve A/B (PCC vs halos).
- **Parse de WCS:** testado com um `.wcs` determinístico (sem ASTAP).
- **Medição de fluxo:** testada com estrelas sintéticas de cor conhecida.
- Nota honesta: o end-to-end exige ASTAP instalado na máquina de dev; o spike
  valida isso logo.

## Fora de scope (YAGNI)

- Anotação/overlay do campo resolvido.
- Catálogo online como caminho primário (só fallback).
- Solver online (astrometry.net).
- Suporte a outros solvers além do ASTAP.

## Critérios de sucesso

1. PCC off → resultado idêntico ao atual (fallback `ColorCalibrate`).
2. PCC on, ASTAP ausente → fallback + nota clara; processamento conclui.
3. PCC on, com ASTAP+DB e focal/píxel corretos → solve com sucesso, ≥20 estrelas,
   cor visivelmente mais neutra/física que o método por halos (A/B no `pcctest`).
4. Focal/píxel persistem entre sessões e pré-preenchem.

## Ficheiros afetados (previsão)

- Criar: `Services/PhotometricCalibration.cs` (+ parser WCS, settings).
- Modificar: `Services/ProcessingSession.cs` (props + passo de cor).
- Modificar: `Services/TiffIO.cs` (leitura best-effort de EXIF focal/píxel).
- Modificar: `Components/Pages/Editor.razor` (secção Cor: toggle + campos).
- Modificar: `Services/SelfTest.cs` + `Program.cs` (`pcctest`).
