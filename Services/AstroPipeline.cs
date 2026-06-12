// AstroLab — Núcleo matemático de referência (port do astro_process_groundtruth.py)
// ⚠ NÃO TESTADO EM RUNTIME — validar contra o Python (SPEC/05-TASKS.md, Tarefa 7).
// Verdade matemática: ReferenceCode/astro_process_groundtruth.py
//
// Dependências: OpenCvSharp4 (filtros + resize + encode)

using OpenCvSharp;

namespace AstroLab.Services;

public sealed class LinearImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public required float[] Data { get; init; }   // RGB intercalado, 0–1

    public LinearImage Clone() => new()
    { Width = Width, Height = Height, Data = (float[])Data.Clone() };

    // ⚠ Não copia: manter Data vivo enquanto o Mat existir.
    public Mat AsMat() => Mat.FromPixelData(Height, Width, MatType.CV_32FC3, Data);
}

public record ToneParams(
    double Stretch = 600, double Sky = 0.12, double Saturation = 0.45,
    double Scnr = 0.7, double NoiseReduction = 1.0, double BlackPoint = 0.0,
    bool ComaCorrect = false,   // mitigação cosmética de coma (unsharp, live)
    bool AiSharpen = false)      // deconvolução estelar por IA (ONNX, on-demand)
{
    public static ToneParams Defaults => new();
}

public static class AstroPipeline
{
    // ============================================================ Fase A ==

    public static void Normalize(LinearImage img)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in img.Data) { if (v < min) min = v; if (v > max) max = v; }
        float range = Math.Max(max - min, 1e-12f);
        var d = img.Data;
        for (int i = 0; i < d.Length; i++) d[i] = (d[i] - min) / range;
    }

    public static LinearImage Crop(LinearImage img, double frac = 0.012)
    {
        int cy = (int)(img.Height * frac), cx = (int)(img.Width * frac);
        int h = img.Height - 2 * cy, w = img.Width - 2 * cx;
        var outData = new float[h * w * 3];
        for (int y = 0; y < h; y++)
            Array.Copy(img.Data, ((y + cy) * img.Width + cx) * 3,
                       outData, y * w * 3, w * 3);
        return new LinearImage { Width = w, Height = h, Data = outData };
    }

    /// <summary>
    /// Extração de background por canal: polinómio 2ª ordem (+ r², r⁴ se
    /// radial) com fit robusto assimétrico. In-place. Ver SPEC/03 §2.
    /// </summary>
    public static void ExtractBackground(LinearImage img, bool radial = true,
                                         int grid = 32, float pedestal = 0.001f)
    {
        int H = img.Height, W = img.Width;
        double aspect = (double)H / W;
        int nTerms = radial ? 8 : 6;

        // limites da grelha
        var ys = LinSpaceInt(0, H, grid + 1);
        var xs = LinSpaceInt(0, W, grid + 1);
        int n = grid * grid;
        var py = new double[n]; var px = new double[n];

        for (int c = 0; c < 3; c++)
        {
            var vals = new double[n];
            int k = 0;
            for (int i = 0; i < grid; i++)
                for (int j = 0; j < grid; j++, k++)
                {
                    vals[k] = BoxMedian(img, c, ys[i], ys[i + 1], xs[j], xs[j + 1]);
                    py[k] = (ys[i] + ys[i + 1]) / 2.0;
                    px[k] = (xs[j] + xs[j + 1]) / 2.0;
                }

            var keep = new bool[n];
            Array.Fill(keep, true);
            double[] coef = new double[nTerms];

            for (int it = 0; it < 6; it++)
            {
                coef = FitLeastSquares(px, py, vals, keep, W, H, aspect, radial);

                // resíduos de TODAS as amostras; σ só das keep
                var resid = new double[n];
                double sum = 0, sum2 = 0; int m = 0;
                for (int s = 0; s < n; s++)
                {
                    resid[s] = vals[s] - EvalBasis(coef, px[s] / W, py[s] / H, aspect, radial);
                    if (keep[s]) { sum += resid[s]; sum2 += resid[s] * resid[s]; m++; }
                }
                double mean = sum / m;
                double sigma = Math.Sqrt(Math.Max(sum2 / m - mean * mean, 1e-30));
                for (int s = 0; s < n; s++)
                    keep[s] = resid[s] < 0.8 * sigma && resid[s] > -2.5 * sigma;
            }

            // subtração linha-a-linha
            var d = img.Data;
            for (int row = 0; row < H; row++)
            {
                double yv = (double)row / H;
                int rowBase = row * W * 3 + c;
                for (int col = 0; col < W; col++)
                {
                    double bg = EvalBasis(coef, (double)col / W, yv, aspect, radial);
                    int idx = rowBase + col * 3;
                    d[idx] = (float)(d[idx] - bg) + pedestal;
                }
            }
        }
        ClampInPlace(img.Data);
    }

    public static void ColorCalibrate(LinearImage img)
    {
        int N = img.Width * img.Height;
        var d = img.Data;

        var lum = new float[N];
        for (int i = 0; i < N; i++)
            lum[i] = (d[i * 3] + d[i * 3 + 1] + d[i * 3 + 2]) / 3f;

        float thr = Percentile(lum, 99.7);

        // amostras: halos de estrela não saturados
        var samples = new List<float>[3] { new(), new(), new() };
        for (int i = 0; i < N; i++)
        {
            float mx = Math.Max(d[i * 3], Math.Max(d[i * 3 + 1], d[i * 3 + 2]));
            if (lum[i] > thr && mx < 0.7f)
                for (int c = 0; c < 3; c++) samples[c].Add(d[i * 3 + c]);
        }
        var refC = new float[3];
        for (int c = 0; c < 3; c++) refC[c] = MedianOf(samples[c].ToArray());

        var bg = new float[3];
        for (int c = 0; c < 3; c++)
        {
            var ch = new float[N];
            for (int i = 0; i < N; i++) ch[i] = d[i * 3 + c];
            bg[c] = MedianOf(ch);
        }

        for (int c = 0; c < 3; c++)
        {
            float scale = refC[1] / Math.Max(refC[c], 1e-9f);
            for (int i = 0; i < N; i++)
                d[i * 3 + c] = (d[i * 3 + c] - bg[c]) * scale + bg[1];
        }
        ClampInPlace(d);
    }

    // ============================================================ Fase B ==

    /// <summary>Stretch arcsinh ratiométrico + MTF + black point. In-place.
    /// Se <paramref name="fixedMid"/> for dado, usa esse midpoint de MTF em vez de o
    /// calcular da mediana local — para tone consistente entre crops (inspetor 1:1).</summary>
    public static void Stretch(LinearImage img, ToneParams p, double? fixedMid = null)
    {
        var d = img.Data;
        ArcsinhRatio(d, img.Width * img.Height, p.Stretch);

        double mid = fixedMid ?? MtfMidFromMedian(MedianOf((float[])d.Clone()), p.Sky);
        Parallel.For(0, d.Length, i =>
            d[i] = (float)Math.Clamp(Mtf(d[i], mid), 0, 1));

        if (p.BlackPoint > 0)
        {
            double bp = p.BlackPoint;
            Parallel.For(0, d.Length, i =>
                d[i] = (float)Math.Clamp((d[i] - bp) / (1 - bp), 0, 1));
        }
    }

    /// <summary>Arcsinh ratiométrico (preserva cor) in-place sobre o buffer RGB.</summary>
    static void ArcsinhRatio(float[] d, int N, double S)
    {
        double asinhS = Math.Asinh(S);
        Parallel.For(0, N, i =>
        {
            double lum = (d[i * 3] + d[i * 3 + 1] + d[i * 3 + 2]) / 3.0;
            double ratio = lum > 1e-9 ? Math.Asinh(lum * S) / asinhS / lum : 0.0;
            for (int c = 0; c < 3; c++)
                d[i * 3 + c] = (float)Math.Clamp(d[i * 3 + c] * ratio, 0, 1);
        });
    }

    static double MtfMidFromMedian(double med, double sky)
        => (med * (sky - 1)) / (sky * (2 * med - 1) - med);

    /// <summary>Midpoint de MTF que a Fase B usaria para <paramref name="src"/>
    /// (clona, aplica só o arcsinh, tira a mediana). Para tone global consistente.</summary>
    public static double ComputeMtfMid(LinearImage src, ToneParams p)
    {
        var d = (float[])src.Data.Clone();
        ArcsinhRatio(d, src.Width * src.Height, p.Stretch);
        return MtfMidFromMedian(MedianOf(d), p.Sky);
    }

    public static void Scnr(LinearImage img, double amount)
    {
        var d = img.Data;
        int N = img.Width * img.Height;
        Parallel.For(0, N, k =>
        {
            int i = k * 3;
            float neutral = (d[i] + d[i + 2]) / 2f;
            if (d[i + 1] > neutral)
                d[i + 1] -= (float)(amount * (d[i + 1] - neutral));
        });
    }

    public static void SaturationAndCurve(LinearImage img, double sat)
    {
        var d = img.Data;
        int N = img.Width * img.Height;
        Parallel.For(0, N, k =>
        {
            int i = k * 3;
            float mean = (d[i] + d[i + 1] + d[i + 2]) / 3f;
            double w = Math.Clamp((mean - 0.13) / 0.25, 0, 1)
                     * Math.Clamp((0.9 - mean) / 0.3, 0, 1);
            double f = 1 + sat * w;
            for (int c = 0; c < 3; c++)
            {
                double v = Math.Clamp(mean + (d[i + c] - mean) * f, 0, 1);
                double t = (v - 0.10) / 0.09;
                d[i + c] = (float)Math.Clamp(v - 0.025 * Math.Exp(-t * t), 0, 1);
            }
        });
    }

    // ================================================== NR (export only) ==

    /// <summary>NR cromático + luminância, mascarado. Usa OpenCvSharp.</summary>
    public static void Denoise(LinearImage img, double strength)
    {
        if (strength <= 0) return;
        int H = img.Height, W = img.Width, N = H * W;
        var d = img.Data;

        var luma = new float[N];
        var chroma = new float[N * 3];
        for (int i = 0; i < N; i++)
        {
            luma[i] = 0.2126f * d[i * 3] + 0.7152f * d[i * 3 + 1] + 0.0722f * d[i * 3 + 2];
            for (int c = 0; c < 3; c++) chroma[i * 3 + c] = d[i * 3 + c] - luma[i];
        }

        // máscara
        float p50 = Percentile(luma, 50), p98 = Percentile(luma, 98);
        var wbg = new float[N];
        for (int i = 0; i < N; i++)
        {
            float prot = Math.Clamp((luma[i] - p50) / Math.Max(p98 - p50, 1e-9f), 0f, 1f);
            wbg[i] = prot; // blur a seguir, depois inverte
        }
        using (var mProt = Mat.FromPixelData(H, W, MatType.CV_32FC1, wbg))
            Cv2.GaussianBlur(mProt, mProt, new Size(), 3);
        for (int i = 0; i < N; i++) wbg[i] = (1f - wbg[i]) * (float)strength;

        // cromático: gaussian σ=6 por canal, blend 0.85·wbg
        var chPlane = new float[N];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < N; i++) chPlane[i] = chroma[i * 3 + c];
            using var m = Mat.FromPixelData(H, W, MatType.CV_32FC1, chPlane);
            using var ms = new Mat();
            Cv2.GaussianBlur(m, ms, new Size(), 6);
            ms.GetArray(out float[] smooth);
            for (int i = 0; i < N; i++)
            {
                float w = 0.85f * wbg[i];
                chroma[i * 3 + c] = chroma[i * 3 + c] * (1 - w) + smooth[i] * w;
            }
        }

        // luminância: bilateral ×2, blend 0.75·wbg
        float[] ls;
        using (var mL = Mat.FromPixelData(H, W, MatType.CV_32FC1, luma))
        using (var m1 = new Mat())
        using (var m2 = new Mat())
        {
            Cv2.BilateralFilter(mL, m1, 9, 0.045, 5);
            Cv2.BilateralFilter(m1, m2, 7, 0.03, 3);
            m2.GetArray(out ls);
        }
        for (int i = 0; i < N; i++)
        {
            float w = 0.75f * wbg[i];
            float l = luma[i] * (1 - w) + ls[i] * w;
            for (int c = 0; c < 3; c++)
                d[i * 3 + c] = Math.Clamp(l + chroma[i * 3 + c], 0f, 1f);
        }
    }

    // ================================== coma (cosmético, opcional) ==

    /// <summary>
    /// Mitigação cosmética de coma: unsharp da luminância nas estrelas, com peso a
    /// crescer radialmente para os cantos (onde a coma do RedCat em full-frame é
    /// maior). Preserva cor (ratiométrico). NÃO é correção ótica real e está fora
    /// do pipeline congelado — opcional, off por defeito. Usa OpenCvSharp.
    /// <para>Os parâmetros de campo permitem aplicar a um crop sabendo a sua posição
    /// no campo inteiro (inspetor 1:1): o peso radial e o sigma usam as dimensões do
    /// CAMPO, não as do crop. Por omissão, a imagem É o campo inteiro.</para>
    /// </summary>
    public static void ComaCorrect(LinearImage img, double amount = 2.0,
        int fieldX0 = 0, int fieldY0 = 0, int fieldW = 0, int fieldH = 0)
    {
        if (amount <= 0) return;
        int W = img.Width, H = img.Height, N = W * H;
        if (fieldW == 0) { fieldW = W; fieldH = H; }
        var d = img.Data;
        double aspect = (double)fieldH / fieldW;

        var luma = new float[N];
        for (int i = 0; i < N; i++)
            luma[i] = 0.2126f * d[i * 3] + 0.7152f * d[i * 3 + 1] + 0.0722f * d[i * 3 + 2];

        // sigma na escala do CAMPO (full-res ~2.4 px; proxy ~0.6 px)
        float sigma = (float)Math.Max(0.6, Math.Max(fieldW, fieldH) * 0.0004);
        using (var m = Mat.FromPixelData(H, W, MatType.CV_32FC1, luma))
        using (var mb = new Mat())
        {
            Cv2.GaussianBlur(m, mb, new Size(), sigma);
            mb.GetArray(out float[] blur);

            double rmax = Math.Sqrt(0.25 + 0.25 * aspect * aspect);
            Parallel.For(0, H, y =>
            {
                double ry = ((fieldY0 + y + 0.5) / fieldH - 0.5) * aspect;
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    double rx = (fieldX0 + x + 0.5) / fieldW - 0.5;
                    double wr = Math.Sqrt(rx * rx + ry * ry) / rmax;   // 0 centro → 1 canto
                    wr *= wr;                                          // concentra nos cantos
                    float l = luma[i];
                    double wStar = Math.Clamp((l - 0.15) / 0.35, 0, 1); // só estrelas/brilhante
                    double gain = amount * wr * wStar;
                    if (gain <= 0) continue;
                    double ln = Math.Clamp(l + gain * (l - blur[i]), 0, 1);
                    double ratio = l > 1e-6 ? ln / l : 1.0;
                    for (int c = 0; c < 3; c++)
                        d[i * 3 + c] = (float)Math.Clamp(d[i * 3 + c] * ratio, 0, 1);
                }
            });
        }
    }

    // ============================================================ helpers ==

    public static double Mtf(double x, double m)
        => ((m - 1) * x) / ((2 * m - 1) * x - m);

    static double EvalBasis(double[] c, double x, double y, double aspect, bool radial)
    {
        double v = c[0] + c[1] * x + c[2] * y + c[3] * x * x + c[4] * y * y + c[5] * x * y;
        if (radial)
        {
            double rx = x - 0.5, ry = (y - 0.5) * aspect;
            double r2 = rx * rx + ry * ry;
            v += c[6] * r2 + c[7] * r2 * r2;
        }
        return v;
    }

    static double[] FitLeastSquares(double[] px, double[] py, double[] vals,
        bool[] keep, int W, int H, double aspect, bool radial)
    {
        int nT = radial ? 8 : 6;
        var AtA = new double[nT, nT];
        var Atv = new double[nT];
        var row = new double[nT];

        for (int s = 0; s < vals.Length; s++)
        {
            if (!keep[s]) continue;
            double x = px[s] / W, y = py[s] / H;
            row[0] = 1; row[1] = x; row[2] = y;
            row[3] = x * x; row[4] = y * y; row[5] = x * y;
            if (radial)
            {
                double rx = x - 0.5, ry = (y - 0.5) * aspect;
                double r2 = rx * rx + ry * ry;
                row[6] = r2; row[7] = r2 * r2;
            }
            for (int a = 0; a < nT; a++)
            {
                Atv[a] += row[a] * vals[s];
                for (int b = 0; b < nT; b++) AtA[a, b] += row[a] * row[b];
            }
        }
        // Ridge λ=1e-9: a base radial é colinear com a polinomial (r² é
        // combinação linear de {1,x,y,x²,y²}) → AtA é singular. O ridge
        // estabiliza o solve sem alterar as predições (validado vs numpy).
        for (int a = 0; a < nT; a++) AtA[a, a] += 1e-9;
        return SolveLeastSquares(AtA, Atv, nT);
    }

    /// <summary>Gauss com pivotagem parcial sobre as equações normais.</summary>
    static double[] SolveLeastSquares(double[,] A, double[] b, int n)
    {
        var M = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) M[i, j] = A[i, j];
            M[i, n] = b[i];
        }
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(M[r, col]) > Math.Abs(M[piv, col])) piv = r;
            if (piv != col)
                for (int j = col; j <= n; j++)
                    (M[col, j], M[piv, j]) = (M[piv, j], M[col, j]);
            double p = M[col, col];
            if (Math.Abs(p) < 1e-14) continue;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = M[r, col] / p;
                for (int j = col; j <= n; j++) M[r, j] -= f * M[col, j];
            }
        }
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = Math.Abs(M[i, i]) > 1e-14 ? M[i, n] / M[i, i] : 0;
        return x;
    }

    static double BoxMedian(LinearImage img, int c, int y0, int y1, int x0, int x1)
    {
        int h = y1 - y0, w = x1 - x0;
        var buf = new float[h * w];
        int k = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                buf[k++] = img.Data[(y * img.Width + x) * 3 + c];
        return MedianOf(buf);
    }

    /// <summary>Mediana exata via quickselect O(n) (destrói/reordena o array).
    /// Par: média dos dois centrais. Idêntica a Array.Sort+índice, mais rápida.</summary>
    public static float MedianOf(float[] a)
    {
        int n = a.Length;
        if (n == 0) return 0;
        int m = n / 2;
        float hi = QuickSelect(a, 0, n - 1, m);   // m-ésima ordem; [0..m-1] ficam ≤ a[m]
        if ((n & 1) == 1) return hi;
        float lo = float.NegativeInfinity;        // (m-1)-ésima = máx de [0..m-1]
        for (int i = 0; i < m; i++) if (a[i] > lo) lo = a[i];
        return (lo + hi) / 2f;
    }

    /// <summary>Quickselect (Lomuto + mediana-de-três). Reordena a[left..right]
    /// tal que a[k] = k-ésimo menor; [left..k-1] ≤ a[k] ≤ [k+1..right].</summary>
    static float QuickSelect(float[] a, int left, int right, int k)
    {
        while (left < right)
        {
            int mid = left + ((right - left) >> 1);
            if (a[mid] < a[left]) (a[left], a[mid]) = (a[mid], a[left]);
            if (a[right] < a[left]) (a[left], a[right]) = (a[right], a[left]);
            if (a[right] < a[mid]) (a[mid], a[right]) = (a[right], a[mid]);
            float pivot = a[mid];
            (a[mid], a[right]) = (a[right], a[mid]);   // pivot → fim
            int store = left;
            for (int i = left; i < right; i++)
                if (a[i] < pivot) { (a[i], a[store]) = (a[store], a[i]); store++; }
            (a[store], a[right]) = (a[right], a[store]);
            if (k == store) return a[k];
            if (k < store) right = store - 1; else left = store + 1;
        }
        return a[left];
    }

    /// <summary>Percentil com interpolação linear (= np.percentile default).
    /// Destrói o array.</summary>
    public static float Percentile(float[] a, double p)
    {
        var copy = (float[])a.Clone();
        Array.Sort(copy);
        double rank = p / 100.0 * (copy.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, copy.Length - 1);
        double frac = rank - lo;
        return (float)(copy[lo] * (1 - frac) + copy[hi] * frac);
    }

    static int[] LinSpaceInt(int a, int b, int n)
    {
        var r = new int[n];
        for (int i = 0; i < n; i++)
            r[i] = (int)Math.Round(a + (double)(b - a) * i / (n - 1));
        return r;
    }

    static void ClampInPlace(float[] d)
    {
        for (int i = 0; i < d.Length; i++)
            d[i] = Math.Clamp(d[i], 0f, 1f);
    }
}
