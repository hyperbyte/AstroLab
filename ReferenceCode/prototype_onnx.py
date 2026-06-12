#!/usr/bin/env python3
# Protótipo: sharpening estelar (coma) via deep_sharp_stellar_cnn ONNX.
# Replica o contrato do Cosmic Clarity: Y (BT.601) -> tiles 256 (Y em 3 canais,
# saída canal 0) -> stitch com blend -> merge YCbCr -> blend de strength.
import sys
import numpy as np
import cv2
import onnxruntime as ort

src = sys.argv[1] if len(sys.argv) > 1 else "testdata/corner_off.jpg"
strength = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
TILE, OV = 256, 64

sess = ort.InferenceSession("Models/deep_sharp_stellar_cnn.onnx",
                            providers=["CPUExecutionProvider"])
nin, nout = sess.get_inputs()[0].name, sess.get_outputs()[0].name

bgr = cv2.imread(src).astype(np.float32) / 255.0
rgb = bgr[:, :, ::-1]
H, W, _ = rgb.shape

# RGB -> YCbCr (BT.601), Y em [0,1]
M = np.array([[0.299, 0.587, 0.114],
              [-0.168736, -0.331264, 0.5],
              [0.5, -0.418688, -0.081312]], np.float32)
ycc = rgb @ M.T
Y, Cb, Cr = ycc[:, :, 0], ycc[:, :, 1], ycc[:, :, 2]

# janela de blend 2D (rampa nas bordas de overlap)
ramp = np.linspace(0, 1, OV, dtype=np.float32)
win1d = np.concatenate([ramp, np.ones(TILE - 2 * OV, np.float32), ramp[::-1]])
win = np.outer(win1d, win1d)

def positions(n):
    step = TILE - OV
    ps = list(range(0, max(1, n - TILE + 1), step))
    if ps[-1] != n - TILE:
        ps.append(n - TILE)
    return ps

acc = np.zeros((H, W), np.float32)
wsum = np.zeros((H, W), np.float32)
for yi in positions(H):
    for xj in positions(W):
        tile = Y[yi:yi + TILE, xj:xj + TILE]
        inp = np.stack([tile] * 3)[None].astype(np.float32)   # (1,3,256,256)
        out = sess.run([nout], {nin: inp})[0][0, 0]           # canal 0
        acc[yi:yi + TILE, xj:xj + TILE] += out * win
        wsum[yi:yi + TILE, xj:xj + TILE] += win

Ysharp = acc / np.maximum(wsum, 1e-6)
Ymix = Y * (1 - strength) + Ysharp * strength

# merge YCbCr -> RGB
ycc2 = np.stack([Ymix, Cb, Cr], -1)
Minv = np.linalg.inv(M).astype(np.float32)
rgb2 = np.clip(ycc2 @ Minv.T, 0, 1)
cv2.imwrite(src.replace(".jpg", "_onnx.jpg"), (rgb2[:, :, ::-1] * 255).astype(np.uint8),
            [cv2.IMWRITE_JPEG_QUALITY, 95])
print("ok ->", src.replace(".jpg", "_onnx.jpg"), "strength", strength)
