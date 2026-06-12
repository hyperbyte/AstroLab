// AstroLab — estado da sessão (singleton DI, app single-user). SPEC/02.
// Detém LinearFull (pós Fase A), LinearProxy (1536), parâmetros e o Gate que
// serializa render/export. Orquestra a Fase A com progresso.

namespace AstroLab.Services;

public sealed class ProcessingSession
{
    public LinearImage? LinearFull { get; private set; }     // pós Fase A (full-res)
    public LinearImage? LinearProxy { get; private set; }    // 1536 px
    public ToneParams Params { get; set; } = ToneParams.Defaults;
    public string? SourcePath { get; private set; }
    public bool IsLoaded => LinearProxy != null;

    /// <summary>Termos radiais r²/r⁴ na extração de background (vinhetagem). Fase A.</summary>
    public bool Radial { get; set; } = true;
    public double Crop { get; set; } = 0.012;

    /// <summary>Serializa render do preview e export (um de cada vez).</summary>
    public readonly SemaphoreSlim Gate = new(1, 1);

    public int FullWidth => LinearFull?.Width ?? 0;
    public int FullHeight => LinearFull?.Height ?? 0;

    /// <summary>RAM aproximada dos buffers de imagem (LinearFull + Proxy).</summary>
    public long ApproxImageBytes =>
        ((long)(LinearFull?.Data.Length ?? 0) + (LinearProxy?.Data.Length ?? 0)) * sizeof(float);

    /// <summary>Abre um novo ficheiro: corre a Fase A e repõe os parâmetros de tone.</summary>
    public Task OpenAsync(string path, IProgress<(string stage, double pct)> progress)
        => RunPhaseA(path, progress, resetParams: true);

    /// <summary>Re-corre a Fase A no ficheiro atual (ex.: toggle radial), mantendo
    /// os parâmetros de tone do utilizador.</summary>
    public Task ReprocessAsync(IProgress<(string stage, double pct)> progress)
        => SourcePath is null ? Task.CompletedTask
                              : RunPhaseA(SourcePath, progress, resetParams: false);

    /// <summary>
    /// Fase A: load → normalize → crop → background(radial) → color calibrate → proxy.
    /// Corre em Task.Run; reporta etapa+percentagem. Liberta a imagem anterior.
    /// </summary>
    async Task RunPhaseA(string path, IProgress<(string stage, double pct)> progress, bool resetParams)
    {
        await Gate.WaitAsync();
        try
        {
            LinearFull = null;
            LinearProxy = null;
            GC.Collect();   // arrays no LOH — libertação explícita justificada (SPEC/02)

            await Task.Run(() =>
            {
                progress.Report(("a carregar TIF…", 0.05));
                var img = TiffIO.LoadFloat(path);

                progress.Report(("a normalizar…", 0.22));
                AstroPipeline.Normalize(img);

                progress.Report(("a cortar bordas…", 0.28));
                img = AstroPipeline.Crop(img, Crop);

                progress.Report((Radial ? "a extrair background (radial)…" : "a extrair background…", 0.35));
                AstroPipeline.ExtractBackground(img, radial: Radial);

                progress.Report(("a calibrar cor…", 0.80));
                AstroPipeline.ColorCalibrate(img);

                progress.Report(("a gerar proxy…", 0.95));
                LinearFull = img;
                LinearProxy = PreviewRenderer.MakeProxy(img);

                progress.Report(("pronto", 1.0));
            });

            SourcePath = path;
            if (resetParams) Params = ToneParams.Defaults;
        }
        finally
        {
            Gate.Release();
        }
    }
}
