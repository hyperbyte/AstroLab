#!/usr/bin/env python3
# Compara dois JPEGs; redimensiona o 2º para o tamanho do 1º (área).
# Uso: python compare_jpg.py <a.jpg> <b.jpg>
import sys
import numpy as np
import cv2

a = cv2.imread(sys.argv[1])
b = cv2.imread(sys.argv[2])
if a.shape != b.shape:
    b = cv2.resize(b, (a.shape[1], a.shape[0]), interpolation=cv2.INTER_AREA)
diff = np.abs(a.astype(np.float32) - b.astype(np.float32))
print(f"  shapes: {a.shape} vs {b.shape}")
print(f"  mean abs diff /255 = {diff.mean():.4f}")
print(f"  p99  abs diff /255 = {np.percentile(diff, 99):.4f}")
print(f"  max  abs diff /255 = {diff.max():.4f}")
