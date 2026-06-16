// AstroLab — remoção de estrelas (starless) por IA. Port da receita validada do
// Cosmic Clarity "darkstar": stretch por canal (mediana 0.25) → tiles 256×256 RGB →
// modelo → unstretch. estrelas = clip(original − starless). ONNX Runtime + DirectML.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AstroLab.Services;

public static class StarRemoval
{
    const int Tile = 256, Overlap = 32;
    const float TargetMedian = 0.25f;

    static InferenceSession? _session;
    static string? _in, _out;
    static readonly object _lock = new();

    static InferenceSession Session()
    {
        if (_session != null) return _session;
        lock (_lock)
        {
            if (_session != null) return _session;
            string path = Path.Combine(AppContext.BaseDirectory, "Models", "darkstar.onnx");
            InferenceSession s;
            try
            {
                var o = new Microsoft.ML.OnnxRuntime.SessionOptions();
                o.AppendExecutionProvider_DML(0);
                s = new InferenceSession(path, o);
                Console.WriteLine("[StarRemoval] darkstar em DirectML (GPU).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[StarRemoval] DirectML indisponível ({ex.Message}); CPU.");
                s = new InferenceSession(path);
            }
            _in = s.InputMetadata.Keys.First();
            _out = s.OutputMetadata.Keys.First();
            _session = s;
            return s;
        }
    }

    /// <summary>Devolve a imagem starless (estrelas removidas). Não altera o input.</summary>
    public static LinearImage Starless(LinearImage img)
    {
        var sess = Session();
        int W = img.Width, H = img.Height, N = W * H;
        var d = img.Data;

        // stretch por canal para mediana alvo (mantém min/median para unstretch)
        var min = new float[3]; var med = new float[3];
        var s = new float[N * 3];
        for (int c = 0; c < 3; c++)
        {
            float mn = float.MaxValue;
            for (int i = 0; i < N; i++) { float v = d[i * 3 + c]; if (v < mn) mn = v; }
            min[c] = mn;
            float inv = 1f / (1f - mn + 1e-12f);
            var r = new float[N];
            for (int i = 0; i < N; i++) r[i] = (d[i * 3 + c] - mn) * inv;
            float m = AstroPipeline.MedianOf((float[])r.Clone());
            med[c] = m;
            for (int i = 0; i < N; i++) s[i * 3 + c] = Mtf(r[i], m, TargetMedian);
        }

        // tiles 256 RGB com reflect-pad + blend
        int Hp = Math.Max(Tile, H), Wp = Math.Max(Tile, W);
        var sp = ReflectRGB(s, W, H, Wp, Hp);
        var acc = new float[Wp * Hp * 3];
        var wsum = new float[Wp * Hp];
        var win = Window();

        var input = new DenseTensor<float>(new[] { 1, 3, Tile, Tile });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_in!, input) };

        foreach (int yi in Positions(Hp))
            foreach (int xj in Positions(Wp))
            {
                for (int ty = 0; ty < Tile; ty++)
                {
                    int row = (yi + ty) * Wp + xj;
                    for (int tx = 0; tx < Tile; tx++)
                    {
                        int p = (row + tx) * 3;
                        input[0, 0, ty, tx] = sp[p];
                        input[0, 1, ty, tx] = sp[p + 1];
                        input[0, 2, ty, tx] = sp[p + 2];
                    }
                }
                using var res = sess.Run(inputs);
                var o = res[0].AsTensor<float>();
                for (int ty = 0; ty < Tile; ty++)
                {
                    int row = (yi + ty) * Wp + xj;
                    for (int tx = 0; tx < Tile; tx++)
                    {
                        float w = win[ty * Tile + tx];
                        int p = (row + tx) * 3;
                        acc[p] += o[0, 0, ty, tx] * w;
                        acc[p + 1] += o[0, 1, ty, tx] * w;
                        acc[p + 2] += o[0, 2, ty, tx] * w;
                        wsum[row + tx] += w;
                    }
                }
            }

        // normalizar, recortar ao tamanho original, unstretch por canal
        var outData = new float[N * 3];
        for (int c = 0; c < 3; c++)
        {
            // mediana do starless esticado (no recorte) para o unstretch
            var ch = new float[N];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int ip = y * Wp + x;
                    ch[y * W + x] = wsum[ip] > 1e-6f ? acc[ip * 3 + c] / wsum[ip] : 0f;
                }
            float cmed = AstroPipeline.MedianOf((float[])ch.Clone());
            for (int i = 0; i < N; i++)
                outData[i * 3 + c] = Unmtf(ch[i], cmed, med[c], min[c]);
        }

        return new LinearImage { Width = W, Height = H, Data = outData };
    }

    /// <summary>estrelas = clip(original − starless), por pixel.</summary>
    public static LinearImage StarsLayer(LinearImage original, LinearImage starless)
    {
        int n = original.Data.Length;
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = Math.Clamp(original.Data[i] - starless.Data[i], 0f, 1f);
        return new LinearImage { Width = original.Width, Height = original.Height, Data = s };
    }

    /// <summary>Recombina fundo + estrelas por screen: 1−(1−a)(1−b).</summary>
    public static LinearImage Screen(LinearImage bg, LinearImage stars)
    {
        int n = bg.Data.Length;
        var o = new float[n];
        for (int i = 0; i < n; i++)
            o[i] = Math.Clamp(1f - (1f - bg.Data[i]) * (1f - stars.Data[i]), 0f, 1f);
        return new LinearImage { Width = bg.Width, Height = bg.Height, Data = o };
    }

    // MTF do stretch (mediana → target) e o seu inverso, idênticos ao Cosmic Clarity.
    static float Mtf(float r, float med, float tm)
    {
        float num = (med - 1f) * tm * r;
        float den = med * (tm + r - 1f) - tm * r;
        if (Math.Abs(den) < 1e-12f) den = 1e-12f;
        return Math.Clamp(num / den, 0f, 1f);
    }

    static float Unmtf(float v, float cmed, float omed, float omin)
    {
        float num = (cmed - 1f) * omed * v;
        float den = cmed * (omed + v - 1f) - omed * v;
        if (Math.Abs(den) < 1e-12f) den = 1e-12f;
        return Math.Clamp(num / den + omin, 0f, 1f);
    }

    static int[] Positions(int n)
    {
        int step = Tile - Overlap;
        var ps = new List<int>();
        for (int p = 0; p <= n - Tile; p += step) ps.Add(p);
        if (ps.Count == 0 || ps[^1] != n - Tile) ps.Add(n - Tile);
        return ps.ToArray();
    }

    static float[] Window()
    {
        var ramp = new float[Tile];
        for (int i = 0; i < Overlap; i++) ramp[i] = (float)i / (Overlap - 1);
        for (int i = Overlap; i < Tile - Overlap; i++) ramp[i] = 1f;
        for (int i = Tile - Overlap; i < Tile; i++) ramp[i] = (float)(Tile - 1 - i) / (Overlap - 1);
        var w = new float[Tile * Tile];
        for (int y = 0; y < Tile; y++)
            for (int x = 0; x < Tile; x++) w[y * Tile + x] = ramp[y] * ramp[x];
        return w;
    }

    static float[] ReflectRGB(float[] src, int W, int H, int Wp, int Hp)
    {
        if (Wp == W && Hp == H) return src;
        var dst = new float[Wp * Hp * 3];
        for (int y = 0; y < Hp; y++)
        {
            int sy = Mirror(y, H);
            for (int x = 0; x < Wp; x++)
            {
                int sx = Mirror(x, W), dp = (y * Wp + x) * 3, spi = (sy * W + sx) * 3;
                dst[dp] = src[spi]; dst[dp + 1] = src[spi + 1]; dst[dp + 2] = src[spi + 2];
            }
        }
        return dst;
    }

    static int Mirror(int i, int n)
    {
        if (n == 1) return 0;
        i %= 2 * n - 2; if (i < 0) i += 2 * n - 2;
        return i < n ? i : 2 * n - 2 - i;
    }
}
