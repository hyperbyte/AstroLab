// AstroLab — plate solve via ASTAP (subprocess). Render de uma imagem esticada
// em alta resolução, invoca astap.exe, lê PLTSOLVD do .ini e parseia o .wcs.
// Nunca lança. Ver design 2026-06-17. Spike: full-res resolve, proxy falha.
using System.Diagnostics;

namespace AstroLab.Services;

public static class PlateSolve
{
    /// <summary>FOV (altura, graus) a partir de focal/píxel. 0 se inválido (→ auto).</summary>
    public static double FovHeightDeg(double focalMm, double pixelUm, int heightPx)
    {
        if (focalMm <= 0 || pixelUm <= 0 || heightPx <= 0) return 0;
        double arcsecPerPx = 206.265 * pixelUm / focalMm;
        return arcsecPerPx * heightPx / 3600.0;
    }

    /// <summary>Localiza o astap.exe: caminho configurado → dirs comuns → PATH.</summary>
    public static string? FindAstap(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string[] common =
        {
            @"C:\Program Files\astap\astap.exe",
            @"C:\Program Files (x86)\astap\astap.exe",
        };
        foreach (var p in common) if (File.Exists(p)) return p;
        return null;   // (deixar o caller reportar "não encontrado")
    }

    /// <summary>Resolve a imagem. Devolve true + wcs em sucesso; false + status caso
    /// contrário. Nunca lança.</summary>
    public static bool TrySolve(LinearImage img, string? astapPath, double focalMm,
                                double pixelUm, out WcsSolution? wcs, out string status)
    {
        wcs = null;
        string? exe = FindAstap(astapPath);
        if (exe is null) { status = "ASTAP não encontrado"; return false; }

        string tmpBase = Path.Combine(Path.GetTempPath(), "astrolab_solve_" + Guid.NewGuid().ToString("N"));
        string tmpImg = tmpBase + ".jpg";
        string wcsFile = tmpBase + ".wcs", iniFile = tmpBase + ".ini";
        try
        {
            // imagem de solve: Fase B (esticada) em alta resolução, qualidade alta
            var toned = PreviewRenderer.ToneToImage(img, ToneParams.Defaults, withNr: false);
            File.WriteAllBytes(tmpImg, PreviewRenderer.EncodeImage(toned, 95));

            double fov = FovHeightDeg(focalMm, pixelUm, img.Height);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(tmpImg);
            psi.ArgumentList.Add("-r"); psi.ArgumentList.Add("180");
            psi.ArgumentList.Add("-fov"); psi.ArgumentList.Add(fov.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-wcs");

            using (var proc = Process.Start(psi)!)
            {
                _ = proc.StandardOutput.ReadToEndAsync();
                _ = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } status = "solve excedeu o tempo"; return false; }
            }

            // exit code é 0 mesmo em falha → confiar no PLTSOLVD/.wcs
            if (!File.Exists(wcsFile))
            {
                string why = File.Exists(iniFile) && File.ReadAllText(iniFile).Contains("PLTSOLVD=F")
                    ? "solve falhou (sem solução)" : "solve falhou";
                status = why; return false;
            }

            wcs = WcsSolution.Parse(File.ReadAllText(wcsFile));
            if (wcs is null) { status = "solve falhou (WCS ilegível)"; return false; }
            status = "solve ok";
            return true;
        }
        catch (Exception ex) { status = $"solve erro: {ex.Message}"; return false; }
        finally
        {
            foreach (var f in new[] { tmpImg, wcsFile, iniFile }) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        }
    }
}
