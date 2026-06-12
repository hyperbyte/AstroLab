#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
astro_process.py — Pipeline de processamento para stacks lineares do DSS
=========================================================================
Pensado para: Canon RP (não modificada) + RedCat 51, Autosave.tif do DSS
(16 ou 32-bit, linear). Testado em Rho Ophiuchi a baixa altitude (Lisboa).

Etapas:
  1. Crop das bordas de empilhamento
  2. Extração de background: polinómio 2ª ordem + termos radiais r²/r⁴
     (apanha poluição luminosa E vinhetagem residual de flats sem bias),
     com rejeição iterativa assimétrica das amostras (protege nebulosidade)
  3. Calibração de cor com estrelas como referência de branco
  4. Stretch arcsinh (preserva cor) + MTF (fixa o nível do fundo)
  5. SCNR suave (remove cast verde)
  6. Redução de ruído cromático (gaussiano) e de luminância (bilateral),
     ambos mascarados — só atuam no fundo, protegem estrelas/nebulosa
  7. Saturação seletiva nos meios-tons + curva final
  8. Export: TIF 16-bit (zlib) + JPEG

Uso:
  pip install tifffile numpy opencv-python pillow
  python astro_process.py Autosave.tif
  python astro_process.py Autosave.tif --out RhoOph --stretch 600 --sky 0.12

Afinação rápida (ver argumentos no fim):
  --stretch   intensidade do arcsinh (400 suave … 900 agressivo)
  --sky       nível do fundo pós-stretch (0.10–0.14)
  --sat       boost de saturação (0.3 suave … 0.6 forte)
  --nr        força da redução de ruído 0–1
  --no-radial desliga os termos radiais (usar quando a calibração
              com bias/dark flats estiver correta)
"""

import argparse
import numpy as np
import tifffile
import cv2
from PIL import Image


# ---------------------------------------------------------------- utils ----

def mtf(x, m):
    """Midtone Transfer Function (igual à do PixInsight/Siril)."""
    return ((m - 1) * x) / ((2 * m - 1) * x - m)


def load_linear(path):
    """Lê o TIF do DSS (uint16 ou float32) e normaliza para 0-1 float32."""
    img = tifffile.imread(path).astype(np.float32)
    img = (img - img.min()) / (img.max() - img.min())
    print(f"  carregado: {img.shape}, normalizado 0-1")
    return img


# ------------------------------------------------- background extraction ----

def basis_pts(x, y, aspect, radial=True):
    """Base do modelo: 1, x, y, x², y², xy [, r², r⁴]."""
    cols = [np.ones_like(x), x, y, x * x, y * y, x * y]
    if radial:
        rx, ry = x - 0.5, (y - 0.5) * aspect
        r2 = rx * rx + ry * ry
        cols += [r2, r2 * r2]
    return np.column_stack(cols)


def extract_background(img, grid=32, radial=True, pedestal=0.001):
    """
    Ajusta e subtrai um modelo de background por canal.
    Rejeição assimétrica: amostras acima do fit (nebulosidade) são
    rejeitadas agressivamente (0.8σ); abaixo, suavemente (2.5σ).
    Avaliação linha-a-linha para não rebentar a memória em full-res.
    """
    H, W, _ = img.shape
    aspect = H / W
    ys = np.linspace(0, H, grid + 1).astype(int)
    xs = np.linspace(0, W, grid + 1).astype(int)

    for c in range(3):
        pts, vals = [], []
        for i in range(grid):
            for j in range(grid):
                box = img[ys[i]:ys[i + 1], xs[j]:xs[j + 1], c]
                pts.append(((ys[i] + ys[i + 1]) / 2, (xs[j] + xs[j + 1]) / 2))
                vals.append(np.median(box))
        pts = np.array(pts)
        vals = np.array(vals, dtype=np.float64)

        keep = np.ones(len(vals), bool)
        for _ in range(6):
            A = basis_pts(pts[keep, 1] / W, pts[keep, 0] / H, aspect, radial)
            coef, *_ = np.linalg.lstsq(A, vals[keep], rcond=None)
            Af = basis_pts(pts[:, 1] / W, pts[:, 0] / H, aspect, radial)
            resid = vals - Af @ coef
            s = np.std(resid[keep])
            keep = (resid < 0.8 * s) & (resid > -2.5 * s)
        print(f"  canal {c}: {keep.sum()}/{len(vals)} amostras de céu usadas")

        # subtração linha a linha (memória mínima)
        xv = np.arange(W, dtype=np.float32) / W
        for row in range(H):
            yv = np.float32(row / H)
            b = (coef[0] + coef[1] * xv + coef[2] * yv + coef[3] * xv * xv
                 + coef[4] * yv * yv + coef[5] * xv * yv)
            if radial:
                rx = xv - 0.5
                ry = (yv - 0.5) * aspect
                r2 = rx * rx + ry * ry
                b = b + coef[6] * r2 + coef[7] * r2 * r2
            img[row, :, c] = img[row, :, c] - b.astype(np.float32) + pedestal

    np.clip(img, 0, 1, out=img)
    return img


# ----------------------------------------------------------- color & tone ----

def color_calibrate(img):
    """Usa estrelas brilhantes não saturadas como referência de branco."""
    lum = img.mean(axis=2)
    mask = (lum > np.percentile(lum, 99.7)) & (img.max(axis=2) < 0.7)
    ref = [np.median(img[:, :, c][mask]) for c in range(3)]
    print(f"  cor das estrelas (R,G,B): {[f'{r:.4f}' for r in ref]}")
    bg = [np.median(img[:, :, c]) for c in range(3)]
    for c in range(3):
        img[:, :, c] = (img[:, :, c] - bg[c]) * (ref[1] / ref[c]) + bg[1]
    np.clip(img, 0, 1, out=img)
    return img


def stretch(img, factor=600, sky=0.12):
    """Arcsinh ratiométrico (preserva cor) + MTF para fixar o fundo."""
    lum = img.mean(axis=2)
    lum_s = np.arcsinh(lum * factor) / np.arcsinh(factor)
    ratio = np.divide(lum_s, lum, out=np.zeros_like(lum), where=lum > 1e-9)
    img *= ratio[:, :, None]
    np.clip(img, 0, 1, out=img)
    med = np.median(img)
    mid = (med * (sky - 1)) / (sky * (2 * med - 1) - med)
    img = mtf(img, np.float32(mid)).astype(np.float32)
    np.clip(img, 0, 1, out=img)
    return img


def scnr(img, amount=0.7):
    """Remove cast verde onde G > média(R,B)."""
    neutral = (img[:, :, 0] + img[:, :, 2]) / 2
    g = img[:, :, 1]
    img[:, :, 1] = np.where(g > neutral, g - amount * (g - neutral), g)
    return img


def denoise(img, strength=1.0):
    """NR cromático + luminância, mascarado para proteger detalhe."""
    luma = 0.2126 * img[:, :, 0] + 0.7152 * img[:, :, 1] + 0.0722 * img[:, :, 2]
    chroma = img - luma[:, :, None]

    p_lo, p_hi = np.percentile(luma, 50), np.percentile(luma, 98)
    prot = np.clip((luma - p_lo) / (p_hi - p_lo), 0, 1).astype(np.float32)
    prot = cv2.GaussianBlur(prot, (0, 0), 3)
    wbg = (1.0 - prot) * strength

    wc = 0.85 * wbg  # peso NR cromático
    for c in range(3):
        cs = cv2.GaussianBlur(chroma[:, :, c], (0, 0), 6)
        chroma[:, :, c] = chroma[:, :, c] * (1 - wc) + cs * wc

    wl = 0.75 * wbg  # peso NR luminância
    ls = cv2.bilateralFilter(luma, 9, 0.045, 5)
    ls = cv2.bilateralFilter(ls, 7, 0.03, 3)
    luma = luma * (1 - wl) + ls * wl

    return np.clip(luma[:, :, None] + chroma, 0, 1).astype(np.float32)


def saturation_and_curve(img, sat=0.45):
    """Saturação só nos meios-tons + leve aprofundamento do fundo."""
    lum = img.mean(axis=2)
    w = np.clip((lum - 0.13) / 0.25, 0, 1) * np.clip((0.9 - lum) / 0.3, 0, 1)
    mean = img.mean(axis=2, keepdims=True)
    img = np.clip(mean + (img - mean) * (1 + sat * w[:, :, None]), 0, 1)
    img = np.clip(img - 0.025 * np.exp(-((img - 0.10) / 0.09) ** 2), 0, 1)
    return img.astype(np.float32)


# ------------------------------------------------------------------ main ----

def main():
    ap = argparse.ArgumentParser(description="Processamento de stacks DSS")
    ap.add_argument("input", help="Autosave.tif do DSS (linear)")
    ap.add_argument("--out", default="processed", help="prefixo dos outputs")
    ap.add_argument("--crop", type=float, default=0.012,
                    help="fração a cortar em cada borda (default 0.012)")
    ap.add_argument("--stretch", type=float, default=600)
    ap.add_argument("--sky", type=float, default=0.12)
    ap.add_argument("--sat", type=float, default=0.45)
    ap.add_argument("--nr", type=float, default=1.0)
    ap.add_argument("--no-radial", action="store_true",
                    help="desliga termos radiais do background")
    args = ap.parse_args()

    print("1/7 a carregar…")
    img = load_linear(args.input)

    H, W, _ = img.shape
    cy, cx = int(H * args.crop), int(W * args.crop)
    img = img[cy:H - cy, cx:W - cx].copy()
    print(f"2/7 crop -> {img.shape}")

    print("3/7 extração de background…")
    img = extract_background(img, radial=not args.no_radial)

    print("4/7 calibração de cor…")
    img = color_calibrate(img)

    print("5/7 stretch…")
    img = stretch(img, args.stretch, args.sky)
    img = scnr(img)

    print("6/7 redução de ruído…")
    img = denoise(img, args.nr)
    img = saturation_and_curve(img, args.sat)

    print("7/7 export…")
    tifffile.imwrite(f"{args.out}_16bit.tif",
                     (img * 65535).astype(np.uint16), compression="zlib")
    Image.fromarray((img * 255).astype(np.uint8)).save(
        f"{args.out}.jpg", quality=93)
    print(f"  -> {args.out}_16bit.tif e {args.out}.jpg")


if __name__ == "__main__":
    main()
