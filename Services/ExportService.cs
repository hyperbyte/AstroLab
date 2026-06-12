// AstroLab — Export (Fase C). SPEC/02 "Export", SPEC/03 §8.
// Fase B + NR em full-res sobre uma CÓPIA de LinearFull, escreve TIF 16-bit
// (deflate) + JPEG q93, com progresso. Ordem = Python: stretch→scnr→denoise→sat.

namespace AstroLab.Services;

public sealed record ExportResult(string TifPath, string JpgPath, string Folder);

public static class ExportService
{
    /// <summary>
    /// Exporta para {prefix}_16bit.tif e {prefix}.jpg. Corre em Task.Run; o
    /// chamador deve serializar via Gate. Pico de memória ~2× full-res.
    /// </summary>
    public static async Task<ExportResult> ExportAsync(
        LinearImage full, ToneParams p, string prefix, IProgress<double> progress)
    {
        string tif = prefix + "_16bit.tif";
        string jpg = prefix + ".jpg";

        await Task.Run(() =>
        {
            var img = full.Clone();                 // não tocar no LinearFull da sessão
            progress.Report(0.08);

            AstroPipeline.Stretch(img, p);
            progress.Report(0.30);

            AstroPipeline.Scnr(img, p.Scnr);
            progress.Report(0.38);

            if (p.NoiseReduction > 0)
            {
                AstroPipeline.Denoise(img, p.NoiseReduction);
                progress.Report(0.70);
            }

            if (p.ComaCorrect) AstroPipeline.ComaCorrect(img);

            AstroPipeline.SaturationAndCurve(img, p.Saturation);
            progress.Report(0.78);

            if (p.AiSharpen)
            {
                AiSharpen.Sharpen(img, 1.0);   // deconvolução estelar IA (full-res, GPU)
                progress.Report(0.88);
            }

            TiffIO.Save16Bit(img, tif);
            progress.Report(0.92);

            File.WriteAllBytes(jpg, PreviewRenderer.EncodeJpeg(img, 93));
            progress.Report(1.0);
        });

        return new ExportResult(tif, jpg, Path.GetDirectoryName(Path.GetFullPath(tif)) ?? ".");
    }
}
