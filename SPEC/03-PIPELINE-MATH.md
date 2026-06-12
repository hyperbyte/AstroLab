# AstroLab — Matemática do Pipeline

> A verdade absoluta é `ReferenceCode/astro_process_groundtruth.py` (testado
> em dados reais). Este documento descreve cada etapa; em caso de dúvida ou
> ambiguidade, **o Python ganha**. `ReferenceCode/AstroPipeline.cs` é um port
> C# de referência, não testado em runtime — validar contra o Python.

Convenções: imagens RGB float 0–1. `H`,`W` em píxeis. Todas as operações
in-place salvo indicação. Mediana = mediana exata (não aproximação).

## 1. Normalização e crop

```
img = (img - min(img)) / (max(img) - min(img))      # min/max globais, 3 canais
crop: remover floor(H*f) em cima/baixo e floor(W*f) à esq/dir, f default 0.012
```

## 2. Extração de background (por canal)

Modelo de fundo: polinómio 2ª ordem + termos radiais (vinhetagem):

```
B(x,y) = c0 + c1·x + c2·y + c3·x² + c4·y² + c5·xy + c6·r² + c7·r⁴
x = coluna/W, y = linha/H               (normalizados 0–1)
rx = x − 0.5;  ry = (y − 0.5)·(H/W)     (correção de aspeto!)
r² = rx² + ry²
```

Sem `radial` (flag), usar só c0–c5.

**Amostragem:** grelha 32×32; por célula, mediana do canal e centro da
célula como ponto. → 1024 amostras (pts, vals).

**Fit robusto iterativo (6 iterações):**
1. Least-squares com as amostras `keep` (inicialmente todas).
2. Resíduo = vals − fit, para TODAS as amostras.
3. σ = desvio-padrão dos resíduos das amostras `keep`.
4. `keep = (resid < 0.8σ) AND (resid > −2.5σ)`

A assimetria é deliberada: amostras acima do fit são provavelmente
nebulosidade (rejeitar cedo); abaixo, céu escuro legítimo (tolerar).

**Subtração:** `pixel = pixel − B(x,y) + 0.001` (pedestal evita clipping a
zero). Depois clamp 0–1. Avaliar B linha-a-linha (memória).

**Least-squares em C#:** sistema 8×8 (ou 6×6) via equações normais
`(AᵀA + λI)c = Aᵀv` com **ridge λ=1e-9 obrigatório** + eliminação de Gauss
com pivotagem parcial. O ridge é necessário porque a base é rank-deficiente:
r² é combinação linear exata de {1, x, y, x², y²}. O Python usa SVD via
`lstsq`, que tolera isso; equações normais puras não. Validado: as
predições da superfície são idênticas com e sem ridge (diferença < 1e-10).
Está implementado em `AstroPipeline.cs::FitLeastSquares`.

## 3. Calibração de cor (estrelas como branco)

```
lum = média RGB por pixel
limiar = percentil 99.7 de lum
mask = (lum > limiar) AND (max(R,G,B) < 0.7)        # halos de estrela, não saturados
ref_c = mediana de canal c sobre mask                # c ∈ {R,G,B}
bg_c  = mediana global de canal c
canal_c = (canal_c − bg_c) · (ref_G / ref_c) + bg_G
clamp 0–1
```

Percentil: usar interpolação linear (equivalente a `np.percentile` default).

## 4. Stretch

### 4a. Arcsinh ratiométrico (preserva cor)

```
lum    = média RGB por pixel
lum_s  = arcsinh(lum · S) / arcsinh(S)        # S = parâmetro stretch, default 600
ratio  = lum_s / lum   (0 onde lum ≤ 1e−9)
pixel *= ratio          # os 3 canais pelo mesmo fator
clamp 0–1
```

### 4b. MTF para fixar o fundo

```
mtf(x, m) = ((m−1)·x) / ((2m−1)·x − m)
med = mediana GLOBAL da imagem (3 canais juntos)
mid = (med·(sky−1)) / (sky·(2·med−1) − med)    # sky default 0.12
pixel = mtf(pixel, mid);  clamp 0–1
```

### 4c. Black point opcional (slider extra da app, não está no Python)

```
pixel = clamp((pixel − bp) / (1 − bp), 0, 1)      # bp ∈ [0, 0.05], default 0
```
Aplicar DEPOIS do MTF.

## 5. SCNR (verde)

```
neutral = (R + B) / 2
G = G − a·(G − neutral)   apenas onde G > neutral      # a default 0.7
```

## 6. Redução de ruído (só no export / preview NR)

```
luma   = 0.2126·R + 0.7152·G + 0.0722·B
chroma = pixel − luma                                  # por canal

# máscara de proteção
p50, p98 = percentis 50 e 98 de luma
prot = clamp((luma − p50)/(p98 − p50), 0, 1)
prot = GaussianBlur(prot, sigma=3)
wbg  = (1 − prot) · strength                           # strength = slider NR

# cromático
para cada canal de chroma:
    cs = GaussianBlur(canal, sigma=6)
    canal = canal·(1 − 0.85·wbg) + cs·(0.85·wbg)

# luminância
ls = BilateralFilter(luma, d=9, sigmaColor=0.045, sigmaSpace=5)
ls = BilateralFilter(ls,   d=7, sigmaColor=0.03,  sigmaSpace=3)
luma = luma·(1 − 0.75·wbg) + ls·(0.75·wbg)

pixel = clamp(luma + chroma, 0, 1)
```

OpenCvSharp: `Cv2.GaussianBlur(src, dst, Size.Zero, sigma)` e
`Cv2.BilateralFilter(src, dst, d, sigmaColor, sigmaSpace)` — bilateral
exige Mat de 1 canal CV_32F para luma; chroma processar canal a canal
(`Cv2.Split`/`Merge` ou Mats individuais).

## 7. Saturação seletiva + curva final

```
lum  = média RGB
w    = clamp((lum − 0.13)/0.25, 0, 1) · clamp((0.9 − lum)/0.3, 0, 1)
mean = média RGB por pixel (broadcast)
pixel = clamp(mean + (pixel − mean)·(1 + sat·w), 0, 1)     # sat default 0.45

# curva: aprofundar ligeiramente o fundo
pixel = clamp(pixel − 0.025·exp(−((pixel − 0.10)/0.09)²), 0, 1)
```

## 8. Export

- TIF: 16-bit uint (`round(pixel·65535)`), RGB, compressão deflate (zlib)
- JPEG: 8-bit, qualidade 93

## Validação obrigatória (Tarefa 7)

Correr o Python e a app sobre o MESMO Autosave.tif com parâmetros default e
comparar os JPEGs: diferença média absoluta por pixel < 2/255. Pontos
prováveis de divergência a verificar: implementação de percentil/mediana,
ordem do clamp, e o bilateral do OpenCV (deve ser idêntico — é a mesma lib).
