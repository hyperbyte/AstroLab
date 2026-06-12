// AstroLab — deconvolução estelar por IA (estilo BXT). Port da receita validada
// do Cosmic Clarity (deep_sharp_stellar_cnn): opera na luminância (Y, BT.601),
// em tiles 256×256 com overlap+blend; Y entra replicado em 3 canais, saída = canal 0;
// merge YCbCr e blend de intensidade. ONNX Runtime + DirectML (GPU), fallback CPU.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AstroLab.Services;

public static class AiSharpen
{
    const int Tile = 256, Overlap = 64;

    static InferenceSession? _session;
    static string? _inName, _outName;
    static readonly object _lock = new();

    static InferenceSession Session()
    {
        if (_session != null) return _session;
        lock (_lock)
        {
            if (_session != null) return _session;
            string path = Path.Combine(AppContext.BaseDirectory, "Models", "deep_sharp_stellar_cnn.onnx");
            InferenceSession s;
            try
            {
                var opts = new Microsoft.ML.OnnxRuntime.SessionOptions();
                opts.AppendExecutionProvider_DML(0);   // GPU (DirectML, qualquer DX12)
                s = new InferenceSession(path, opts);
                Console.WriteLine("[AiSharpen] ONNX em DirectML (GPU).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AiSharpen] DirectML indisponível ({ex.Message}); a usar CPU (lento).");
                s = new InferenceSession(path);        // fallback CPU
            }
            _inName = s.InputMetadata.Keys.First();
            _outName = s.OutputMetadata.Keys.First();
            _session = s;
            return s;
        }
    }

    /// <summary>Aperta/deconvolui estrelas na luminância. In-place. strength 0–1.</summary>
    public static void Sharpen(LinearImage img, double strength = 1.0)
    {
        if (strength <= 0) return;
        var sess = Session();
        int W = img.Width, H = img.Height, N = W * H;
        var d = img.Data;

        // RGB -> YCbCr (BT.601); Y em [0,1]
        var Y = new float[N];
        var Cb = new float[N];
        var Cr = new float[N];
        for (int i = 0; i < N; i++)
        {
            float r = d[i * 3], g = d[i * 3 + 1], b = d[i * 3 + 2];
            Y[i] = 0.299f * r + 0.587f * g + 0.114f * b;
            Cb[i] = -0.168736f * r - 0.331264f * g + 0.5f * b;
            Cr[i] = 0.5f * r - 0.418688f * g - 0.081312f * b;
        }

        // canvas com reflect-pad para garantir ≥ Tile e cobertura inteira
        int Hp = Math.Max(Tile, H), Wp = Math.Max(Tile, W);
        var Yp = Reflect(Y, W, H, Wp, Hp);
        var acc = new float[Wp * Hp];
        var wsum = new float[Wp * Hp];
        var win = BlendWindow();

        var input = new DenseTensor<float>(new[] { 1, 3, Tile, Tile });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inName!, input) };

        foreach (int yi in Positions(Hp))
            foreach (int xj in Positions(Wp))
            {
                // preencher tile (Y replicado nos 3 canais)
                for (int ty = 0; ty < Tile; ty++)
                {
                    int row = (yi + ty) * Wp + xj;
                    for (int tx = 0; tx < Tile; tx++)
                    {
                        float v = Yp[row + tx];
                        input[0, 0, ty, tx] = v;
                        input[0, 1, ty, tx] = v;
                        input[0, 2, ty, tx] = v;
                    }
                }

                using var res = sess.Run(inputs);
                var outT = res[0].AsTensor<float>();   // [1,3,256,256] → canal 0

                for (int ty = 0; ty < Tile; ty++)
                {
                    int row = (yi + ty) * Wp + xj;
                    for (int tx = 0; tx < Tile; tx++)
                    {
                        float w = win[ty * Tile + tx];
                        acc[row + tx] += outT[0, 0, ty, tx] * w;
                        wsum[row + tx] += w;
                    }
                }
            }

        // normalizar, recortar ao tamanho original, merge YCbCr + blend de strength
        float s = (float)strength;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int ip = y * Wp + x, i = y * W + x;
                float ysh = wsum[ip] > 1e-6f ? acc[ip] / wsum[ip] : Y[i];
                float ym = Y[i] * (1 - s) + ysh * s;
                float cb = Cb[i], cr = Cr[i];
                d[i * 3] = Clamp(ym + 1.402f * cr);
                d[i * 3 + 1] = Clamp(ym - 0.344136f * cb - 0.714136f * cr);
                d[i * 3 + 2] = Clamp(ym + 1.772f * cb);
            }
    }

    static int[] Positions(int n)
    {
        int step = Tile - Overlap;
        var ps = new List<int>();
        for (int p = 0; p <= n - Tile; p += step) ps.Add(p);
        if (ps.Count == 0 || ps[^1] != n - Tile) ps.Add(n - Tile);
        return ps.ToArray();
    }

    static float[] BlendWindow()
    {
        var ramp = new float[Tile];
        for (int i = 0; i < Overlap; i++) ramp[i] = (float)i / (Overlap - 1);
        for (int i = Overlap; i < Tile - Overlap; i++) ramp[i] = 1f;
        for (int i = Tile - Overlap; i < Tile; i++) ramp[i] = (float)(Tile - 1 - i) / (Overlap - 1);
        var win = new float[Tile * Tile];
        for (int y = 0; y < Tile; y++)
            for (int x = 0; x < Tile; x++)
                win[y * Tile + x] = ramp[y] * ramp[x];
        return win;
    }

    static float[] Reflect(float[] src, int W, int H, int Wp, int Hp)
    {
        if (Wp == W && Hp == H) return src;
        var dst = new float[Wp * Hp];
        for (int y = 0; y < Hp; y++)
        {
            int sy = Mirror(y, H);
            for (int x = 0; x < Wp; x++)
                dst[y * Wp + x] = src[sy * W + Mirror(x, W)];
        }
        return dst;
    }

    static int Mirror(int i, int n)
    {
        if (n == 1) return 0;
        i %= 2 * n - 2; if (i < 0) i += 2 * n - 2;
        return i < n ? i : 2 * n - 2 - i;
    }

    static float Clamp(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
