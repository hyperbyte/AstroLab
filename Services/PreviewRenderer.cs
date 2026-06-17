// AstroLab — Fase B sobre o proxy → JPEG (Tarefa 3, SPEC/02 "Fluxo de slider").
// Proxy = downscale área para lado maior 1536. Render = clone + stretch/SCNR/
// saturação + encode JPEG (RGB→BGR). NR NÃO corre aqui (é caro; só no export).

using System.Runtime.InteropServices;
using OpenCvSharp;

namespace AstroLab.Services;

public static class PreviewRenderer
{
    /// <summary>Downscale (área) para lado maior = maxSide. Não faz upscale.</summary>
    public static LinearImage MakeProxy(LinearImage full, int maxSide = 1536)
    {
        int w = full.Width, h = full.Height;
        double scale = (double)maxSide / Math.Max(w, h);
        if (scale >= 1.0) return full.Clone();

        int nw = (int)Math.Round(w * scale), nh = (int)Math.Round(h * scale);
        using var src = full.AsMat();              // mantém full.Data vivo neste escopo
        using var dst = new Mat();
        Cv2.Resize(src, dst, new Size(nw, nh), 0, 0, InterpolationFlags.Area);

        var data = new float[(long)nw * nh * 3];
        Marshal.Copy(dst.Data, data, 0, data.Length);   // CV_32FC3 contíguo, RGB intercalado
        return new LinearImage { Width = nw, Height = nh, Data = data };
    }

    /// <summary>Reamostra por um fator de drizzle inteiro (≥2 reduz por área; ≤1 = no-op).
    /// Corre na Fase A, antes do resto. Ver design 2026-06-17.</summary>
    public static LinearImage Resample(LinearImage img, int factor)
    {
        if (factor <= 1) return img;
        int maxSide = (int)Math.Round(Math.Max(img.Width, img.Height) / (double)factor);
        return MakeProxy(img, maxSide);
    }

    /// <summary>
    /// Fase B (stretch arcsinh+MTF+blackpoint, SCNR, saturação+curva) sobre uma
    /// CÓPIA do proxy, encode JPEG. Ordem = Python (stretch→scnr→saturação), sem NR.
    /// </summary>
    /// <summary>Redimensiona RGB float (interp. linear) — para escalar a correção
    /// de clone do proxy para o full-res no export.</summary>
    public static float[] ResizeRGB(LinearImage src, int w, int h)
    {
        using var m = src.AsMat();
        using var dst = new Mat();
        Cv2.Resize(m, dst, new Size(w, h), 0, 0, InterpolationFlags.Linear);
        var data = new float[(long)w * h * 3];
        System.Runtime.InteropServices.Marshal.Copy(dst.Data, data, 0, data.Length);
        return data;
    }

    public static byte[] Render(LinearImage proxy, ToneParams p, int jpegQuality = 85)
    {
        var work = proxy.Clone();
        ApplyTone(work, p, withNr: false);
        return EncodeJpeg(work, jpegQuality);
    }

    /// <summary>
    /// Preview NR: crop 100% de cw×ch centrado em (cx,cy) do full-res, devolve
    /// (antes, depois) — Fase B sem NR vs com NR. Custo ~ms (SPEC/02 "Preview NR").
    /// </summary>
    public static (byte[] before, byte[] after) RenderNrCrop(
        LinearImage full, ToneParams p, int cx, int cy, int cw = 600, int ch = 400)
    {
        int w = Math.Min(cw, full.Width), h = Math.Min(ch, full.Height);
        int x0 = Math.Clamp(cx - w / 2, 0, full.Width - w);
        int y0 = Math.Clamp(cy - h / 2, 0, full.Height - h);

        var crop = new LinearImage { Width = w, Height = h, Data = new float[w * h * 3] };
        for (int y = 0; y < h; y++)
            Array.Copy(full.Data, ((y0 + y) * full.Width + x0) * 3, crop.Data, y * w * 3, w * 3);

        var before = crop.Clone();
        ApplyTone(before, p, withNr: false);

        var after = crop.Clone();
        ApplyTone(after, p, withNr: true);

        return (EncodeJpeg(before, 90), EncodeJpeg(after, 90));
    }

    /// <summary>
    /// Inspetor de campo (estilo PixInsight): mosaico 3×3 a 1:1 das 9 áreas —
    /// 4 cantos, 4 centros de borda e centro — para avaliar foco/estrelas/coma.
    /// Tone consistente entre células (midpoint MTF global, = preview principal).
    /// </summary>
    public static byte[] RenderInspector(LinearImage full, LinearImage proxy, ToneParams p,
                                         int cell = 512, int gutter = 8, bool withNr = true)
    {
        int cw = Math.Min(cell, full.Width), ch = Math.Min(cell, full.Height);
        double mid = AstroPipeline.ComputeMtfMid(proxy, p);   // tone global (= preview)

        int[] xs = { 0, (full.Width - cw) / 2, full.Width - cw };
        int[] ys = { 0, (full.Height - ch) / 2, full.Height - ch };

        int mosW = 3 * cw + 2 * gutter, mosH = 3 * ch + 2 * gutter;
        var mosaic = new float[(long)mosW * mosH * 3];        // fundo preto (0)

        for (int gy = 0; gy < 3; gy++)
            for (int gx = 0; gx < 3; gx++)
            {
                int x0 = xs[gx], y0 = ys[gy];
                var crop = new LinearImage { Width = cw, Height = ch, Data = new float[cw * ch * 3] };
                for (int y = 0; y < ch; y++)
                    Array.Copy(full.Data, ((long)(y0 + y) * full.Width + x0) * 3,
                               crop.Data, (long)y * cw * 3, cw * 3);

                AstroPipeline.Stretch(crop, p, fixedMid: mid);
                AstroPipeline.Scnr(crop, p.Scnr);
                if (withNr) AstroPipeline.Denoise(crop, p.NoiseReduction);
                if (p.ComaCorrect)   // peso radial pela posição do crop no campo inteiro
                    AstroPipeline.ComaCorrect(crop, 2.0, x0, y0, full.Width, full.Height);
                AstroPipeline.SaturationAndCurve(crop, p.Saturation);
                if (p.AiSharpen) AiSharpen.Sharpen(crop, 1.0);   // deconvolução IA (estrelas)

                int dx = gx * (cw + gutter), dy = gy * (ch + gutter);
                for (int y = 0; y < ch; y++)
                    Array.Copy(crop.Data, (long)y * cw * 3,
                               mosaic, ((long)(dy + y) * mosW + dx) * 3, cw * 3);
            }

        var img = new LinearImage { Width = mosW, Height = mosH, Data = mosaic };
        return EncodeJpeg(img, 90);
    }

    /// <summary>Aplica a Fase B a uma cópia e devolve a imagem (não codifica).
    /// Usado para alimentar a separação de estrelas com a imagem esticada.</summary>
    public static LinearImage ToneToImage(LinearImage src, ToneParams p, bool withNr)
    {
        var work = src.Clone();
        ApplyTone(work, p, withNr);
        return work;
    }

    /// <summary>Codifica uma LinearImage já processada (RGB 0–1) em JPEG.</summary>
    public static byte[] EncodeImage(LinearImage img, int quality = 85) => EncodeJpeg(img, quality);

    /// <summary>Fase B in-place. withNr insere o Denoise entre SCNR e saturação
    /// (ordem do Python: stretch→scnr→denoise→saturação).</summary>
    static void ApplyTone(LinearImage work, ToneParams p, bool withNr)
    {
        AstroPipeline.Stretch(work, p);
        AstroPipeline.Scnr(work, p.Scnr);
        if (withNr) AstroPipeline.Denoise(work, p.NoiseReduction);
        if (p.ComaCorrect) AstroPipeline.ComaCorrect(work);
        AstroPipeline.SaturationAndCurve(work, p.Saturation);
        if (p.AiSharpen) AiSharpen.Sharpen(work, 1.0);   // deconvolução IA (subtil no proxy)
    }

    /// <summary>Descodifica um JPEG/PNG (bytes) para LinearImage RGB 0–1.
    /// Usado para reler o resultado do clone stamp vindo do canvas.</summary>
    public static LinearImage DecodeImage(byte[] data)
    {
        using var src = Cv2.ImDecode(data, ImreadModes.Color);   // BGR 8U
        int w = src.Width, h = src.Height, n = w * h;
        using var f = new Mat();
        src.ConvertTo(f, MatType.CV_32F, 1.0 / 255);
        Mat[] ch = Cv2.Split(f);                                  // BGR
        try
        {
            ch[2].GetArray(out float[] r);
            ch[1].GetArray(out float[] g);
            ch[0].GetArray(out float[] b);
            var d = new float[(long)n * 3];
            for (int i = 0; i < n; i++) { d[i * 3] = r[i]; d[i * 3 + 1] = g[i]; d[i * 3 + 2] = b[i]; }
            return new LinearImage { Width = w, Height = h, Data = d };
        }
        finally { foreach (var m in ch) m.Dispose(); }
    }

    public static byte[] EncodeJpeg(LinearImage img, int quality)
    {
        using var mat = img.AsMat();               // RGB float 0–1; img.Data vivo até ImEncode
        using var bgr = new Mat();
        Cv2.CvtColor(mat, bgr, ColorConversionCodes.RGB2BGR);
        using var u8 = new Mat();
        bgr.ConvertTo(u8, MatType.CV_8UC3, 255.0);
        Cv2.ImEncode(".jpg", u8, out byte[] buf,
            new[] { (int)ImwriteFlags.JpegQuality, quality });
        return buf;
    }
}
