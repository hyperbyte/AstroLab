// AstroLab — estado e operações do workflow de separação de estrelas.
// Camadas no espaço esticado (Fase B). A edição (clone stamp + ganho/saturação) é
// feita sobre o FUNDO (starless); as estrelas voltam por screen no fim.

namespace AstroLab.Services;

public sealed class StarWorkflow
{
    public required LinearImage Starless;   // fundo sem estrelas (proxy, esticado) — original
    public required LinearImage Stars;      // camada de estrelas (proxy)

    /// <summary>Fundo após edição com clone stamp (substitui Starless no preview).
    /// Null = ainda sem edição de clone.</summary>
    public LinearImage? Edited;

    public double Gain = 1.0;        // ganho do fundo (multiplicativo)
    public double Saturation = 1.0;  // saturação do fundo
    public double LocalContrast = 0;  // contraste local do fundo (unsharp raio grande)
    public double StarReduction = 0;     // redução de estrelas (erosão), 0–1
    public double StarSaturation = 1.0;  // saturação da camada de estrelas
    public double StarMagenta = 0;       // remoção de halos magenta (invert→SCNR→invert), 0–1

    /// <summary>Fundo a usar (editado se existir, senão o original).</summary>
    public LinearImage BackgroundBase => Edited ?? Starless;

    /// <summary>Aplica ganho/saturação ao fundo (cópia) e recombina com as estrelas
    /// por screen. É o resultado visível.</summary>
    public LinearImage Compose()
    {
        var bg = BackgroundBase.Clone();
        ProcessBackground(bg);
        return StarRemoval.Screen(bg, ProcessStars(Stars));
    }

    /// <summary>Aplica a um starless FULL-RES a correção de clone (diferença do proxy,
    /// escalada) + ganho/saturação. Usado no export.</summary>
    public void ApplyToFull(LinearImage fullStarless)
    {
        if (Edited != null)
        {
            var corr = new float[Starless.Data.Length];
            for (int i = 0; i < corr.Length; i++) corr[i] = Edited.Data[i] - Starless.Data[i];
            var corrImg = new LinearImage { Width = Starless.Width, Height = Starless.Height, Data = corr };
            var up = PreviewRenderer.ResizeRGB(corrImg, fullStarless.Width, fullStarless.Height);
            var d = fullStarless.Data;
            for (int i = 0; i < d.Length; i++) d[i] = Math.Clamp(d[i] + up[i], 0f, 1f);
        }
        ProcessBackground(fullStarless);
    }

    /// <summary>Ganho (multiplica, mantém o preto) + saturação seletiva, in-place.</summary>
    public static void AdjustBackground(LinearImage img, double gain, double sat)
    {
        var d = img.Data;
        int N = img.Width * img.Height;
        Parallel.For(0, N, k =>
        {
            int i = k * 3;
            float r = (float)Math.Clamp(d[i] * gain, 0, 1);
            float g = (float)Math.Clamp(d[i + 1] * gain, 0, 1);
            float b = (float)Math.Clamp(d[i + 2] * gain, 0, 1);
            float mean = (r + g + b) / 3f;
            d[i] = (float)Math.Clamp(mean + (r - mean) * sat, 0, 1);
            d[i + 1] = (float)Math.Clamp(mean + (g - mean) * sat, 0, 1);
            d[i + 2] = (float)Math.Clamp(mean + (b - mean) * sat, 0, 1);
        });
    }

    /// <summary>Processa o FUNDO in-place: ganho + saturação + contraste local.
    /// Partilhado entre preview (proxy) e export (full-res).</summary>
    public void ProcessBackground(LinearImage img)
    {
        AdjustBackground(img, Gain, Saturation);
        AstroPipeline.LocalContrast(img, LocalContrast);
    }

    /// <summary>Processa as ESTRELAS: redução (erosão) + saturação. Devolve nova imagem
    /// (não muta o input). Partilhado entre preview e export.</summary>
    public LinearImage ProcessStars(LinearImage stars)
    {
        var s = StarRemoval.ReduceStars(stars, StarReduction);
        AdjustBackground(s, 1.0, StarSaturation);
        AstroPipeline.RemoveMagenta(s, StarMagenta);
        return s;
    }
}
