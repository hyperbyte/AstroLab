#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Valida a Fase A: corre load->crop->extract_background->color_calibrate do
groundtruth e imprime medianas por canal nos pontos de comparação com o C#.
Uso: python validate_phasea.py <Autosave.tif> [--no-radial]
"""
import os
import sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import astro_process_groundtruth as gt

path = sys.argv[1]
radial = "--no-radial" not in sys.argv
crop = 0.012

img = gt.load_linear(path)
H, W, _ = img.shape
cy, cx = int(H * crop), int(W * crop)
img = img[cy:H - cy, cx:W - cx].copy()
print(f"  crop -> {img.shape}")

gt.extract_background(img, radial=radial)
med_bg = [float(np.median(img[:, :, c])) for c in range(3)]
print("MEDIAN_POST_BG " + " ".join(f"{m:.6f}" for m in med_bg))

gt.color_calibrate(img)
med_cc = [float(np.median(img[:, :, c])) for c in range(3)]
print("MEDIAN_POST_CC " + " ".join(f"{m:.6f}" for m in med_cc))
