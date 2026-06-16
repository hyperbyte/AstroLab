#!/usr/bin/env python3
# Protótipo: remoção de estrelas (darkstar ONNX) — separa starless + estrelas.
# Replica o contrato do Cosmic Clarity: stretch (mediana 0.25) -> tiles 256 ->
# modelo -> unstretch; estrelas = clip(original - starless). RGB.
import sys
import numpy as np
import cv2
import onnxruntime as ort

src = sys.argv[1] if len(sys.argv) > 1 else "testdata/cs_full.jpg"
# região central (para ver rápido): cx,cy,w,h
img_full = cv2.imread(src)[:, :, ::-1].astype(np.float32) / 255.0
H0, W0, _ = img_full.shape
w, h = 1280, 960
x0, y0 = (W0 - w) // 2, (H0 - h) // 2
img = np.ascontiguousarray(img_full[y0:y0 + h, x0:x0 + w])

TILE, OV, TM = 256, 32, 0.25
sess = ort.InferenceSession("Models/darkstar.onnx", providers=["CPUExecutionProvider"])
nin, nout = sess.get_inputs()[0].name, sess.get_outputs()[0].name


def stretch(im, tm=TM):
    mn = im.reshape(-1, 3).min(0)
    r = (im - mn) / (1 - mn + 1e-12)
    med = np.median(r, axis=(0, 1))
    mb = med.reshape(1, 1, 3)
    num = (mb - 1) * tm * r
    den = mb * (tm + r - 1) - tm * r
    den[np.abs(den) < 1e-12] = 1e-12
    return np.clip(num / den, 0, 1).astype(np.float32), mn, med


def unstretch(im, omed, omin):
    out = np.empty_like(im)
    for c in range(3):
        cm = np.median(im[..., c])
        num = (cm - 1) * omed[c] * im[..., c]
        den = cm * (omed[c] + im[..., c] - 1) - omed[c] * im[..., c]
        den[np.abs(den) < 1e-12] = 1e-12
        out[..., c] = num / den + omin[c]
    return np.clip(out, 0, 1).astype(np.float32)


s, mn, med = stretch(img)

# janela de blend 2D
ramp = np.linspace(0, 1, OV, dtype=np.float32)
win1d = np.concatenate([ramp, np.ones(TILE - 2 * OV, np.float32), ramp[::-1]])
win = np.outer(win1d, win1d)[:, :, None]


def positions(n):
    step = TILE - OV
    ps = list(range(0, max(1, n - TILE + 1), step))
    if ps[-1] != n - TILE:
        ps.append(n - TILE)
    return ps


acc = np.zeros_like(s)
wsum = np.zeros((h, w, 1), np.float32)
for yi in positions(h):
    for xj in positions(w):
        tile = s[yi:yi + TILE, xj:xj + TILE]                       # HWC
        inp = np.transpose(tile, (2, 0, 1))[None]                  # 1,3,256,256
        out = sess.run([nout], {nin: inp})[0][0]                   # 3,256,256
        out = np.transpose(out, (1, 2, 0))                         # HWC
        acc[yi:yi + TILE, xj:xj + TILE] += out * win
        wsum[yi:yi + TILE, xj:xj + TILE] += win

starless_s = acc / np.maximum(wsum, 1e-6)
starless = unstretch(starless_s, med, mn)
stars = np.clip(img - starless, 0, 1)

cv2.imwrite("testdata/starless.jpg", (starless[:, :, ::-1] * 255).astype(np.uint8), [cv2.IMWRITE_JPEG_QUALITY, 95])
cv2.imwrite("testdata/stars.jpg", (stars[:, :, ::-1] * 255).astype(np.uint8), [cv2.IMWRITE_JPEG_QUALITY, 95])
cv2.imwrite("testdata/orig_crop.jpg", (img[:, :, ::-1] * 255).astype(np.uint8), [cv2.IMWRITE_JPEG_QUALITY, 95])
print("ok -> testdata/orig_crop.jpg, starless.jpg, stars.jpg")
