// AstroLab — overlay de anotação de campo (Fase 3). Parseia o deep_sky.csv do ASTAP
// e projeta DSO / estrelas nomeadas / grelha RA-Dec via WcsSolution. Ver design 2026-06-18.
using System.Globalization;

namespace AstroLab.Services;

public sealed record CatalogObject(double RaDeg, double DecDeg, string Name,
                                   double LengthArcmin, double WidthArcmin, double AngleDeg, bool IsStar);

public sealed record DsoMark(double X, double Y, double WidthPx, double HeightPx,
                             double AngleDeg, string Name, bool IsStar);
public sealed record GridLine(IReadOnlyList<(double X, double Y)> Points, string Label);
public sealed record FieldOverlay(int Width, int Height,
                                  IReadOnlyList<DsoMark> Objects, IReadOnlyList<GridLine> Grid);

public static class FieldAnnotation
{
    /// <summary>Parseia o deep_sky.csv do ASTAP. Ignora linhas cujo 1º campo não seja
    /// número (cabeçalho). RA°=RA/2400, DEC°=DEC/3600. Sem length → estrela nomeada;
    /// só length → circular; length+width(+orient) → elipse.</summary>
    public static List<CatalogObject> ParseDeepSky(string text)
    {
        var inv = CultureInfo.InvariantCulture;
        var outp = new List<CatalogObject>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0) continue;
            var f = raw.Split(',');
            if (f.Length < 3) continue;
            if (!double.TryParse(f[0], NumberStyles.Float, inv, out var raU)) continue;   // cabeçalho
            if (!double.TryParse(f[1], NumberStyles.Float, inv, out var decU)) continue;
            string name = f[2].Trim();
            if (name.Length == 0) continue;

            double ra = raU / 2400.0, dec = decU / 3600.0;
            bool hasLen = f.Length >= 4 && double.TryParse(f[3], NumberStyles.Float, inv, out _);
            if (!hasLen)
            {
                outp.Add(new CatalogObject(ra, dec, name, 0, 0, 0, IsStar: true));
                continue;
            }
            double len = double.Parse(f[3], inv) / 10.0;     // 0.1' → '
            double wid = f.Length >= 5 && double.TryParse(f[4], NumberStyles.Float, inv, out var w) ? w / 10.0 : len;
            double ang = f.Length >= 6 && double.TryParse(f[5], NumberStyles.Float, inv, out var a) ? a : 0;
            outp.Add(new CatalogObject(ra, dec, name, len, wid, ang, IsStar: false));
        }
        return outp;
    }

    const double MinDsoArcmin = 3.0;   // não poluir o campo largo com objetos minúsculos

    public static FieldOverlay Build(WcsSolution wcs, int width, int height, string? astapDir)
    {
        var objects = new List<DsoMark>();
        string? csv = astapDir is null ? null : System.IO.Path.Combine(astapDir, "deep_sky.csv");
        if (csv != null && File.Exists(csv))
        {
            double arcsecPerPx = wcs.ScaleArcsecPerPixel;
            foreach (var o in ParseDeepSky(SafeReadAll(csv)))
            {
                if (!o.IsStar && o.LengthArcmin < MinDsoArcmin) continue;
                var (fx, fy) = wcs.WorldToPixel(o.RaDeg, o.DecDeg);
                double x = fx - 1, y = height - fy;   // ASTAP/WCS é FITS bottom-up → flip p/ array top-down
                if (x < 0 || y < 0 || x >= width || y >= height) continue;
                double wpx = o.WidthArcmin * 60.0 / arcsecPerPx;     // arcmin→arcsec→px
                double hpx = o.LengthArcmin * 60.0 / arcsecPerPx;
                string label = o.Name.Split('/')[0];                  // nome primário
                objects.Add(new DsoMark(x, y, wpx, hpx, o.AngleDeg, label, o.IsStar));
            }
        }
        return new FieldOverlay(width, height, objects, BuildGrid(wcs, width, height));
    }

    static string SafeReadAll(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    /// <summary>Grelha RA/Dec: linhas de Dec constante e RA constante, projetadas
    /// ponto-a-ponto (curvam-se via WCS), recortadas à imagem.</summary>
    static List<GridLine> BuildGrid(WcsSolution wcs, int width, int height)
    {
        // limites RA/Dec a partir dos 4 cantos + centro
        double raMin = 1e9, raMax = -1e9, decMin = 1e9, decMax = -1e9;
        foreach (var (px, py) in new[] { (0.0, 0.0), (width - 1.0, 0.0), (0.0, height - 1.0),
                                         (width - 1.0, height - 1.0), (width / 2.0, height / 2.0) })
        {
            var (ra, dec) = wcs.PixelToWorld(px + 1, py + 1);
            raMin = Math.Min(raMin, ra); raMax = Math.Max(raMax, ra);
            decMin = Math.Min(decMin, dec); decMax = Math.Max(decMax, dec);
        }
        double stepDec = NiceStep(decMax - decMin);
        double stepRa = NiceStep((raMax - raMin) * Math.Cos(((decMin + decMax) / 2) * Math.PI / 180));

        var lines = new List<GridLine>();
        // linhas de Dec constante (varia RA)
        for (double dec = Math.Ceiling(decMin / stepDec) * stepDec; dec <= decMax; dec += stepDec)
            AddLine(lines, wcs, width, height, $"{dec:F1}°",
                    n => (raMin + (raMax - raMin) * n, dec));
        // linhas de RA constante (varia Dec)
        for (double ra = Math.Ceiling(raMin / stepRa) * stepRa; ra <= raMax; ra += stepRa)
            AddLine(lines, wcs, width, height, $"{ra / 15:F2}h",
                    n => (ra, decMin + (decMax - decMin) * n));
        return lines;
    }

    static void AddLine(List<GridLine> lines, WcsSolution wcs, int width, int height,
                        string label, Func<double, (double ra, double dec)> at)
    {
        var pts = new List<(double X, double Y)>();
        for (int s = 0; s <= 40; s++)
        {
            var (ra, dec) = at(s / 40.0);
            var (fx, fy) = wcs.WorldToPixel(ra, dec);
            double x = fx - 1, y = fy - 1;
            if (x >= -50 && y >= -50 && x <= width + 50 && y <= height + 50) pts.Add((x, y));
        }
        if (pts.Count >= 2) lines.Add(new GridLine(pts, label));
    }

    /// <summary>Passo "redondo" para ~5 divisões num intervalo (graus).</summary>
    static double NiceStep(double range)
    {
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10 };
        double target = Math.Max(range, 1e-6) / 5;
        foreach (var s in steps) if (s >= target) return s;
        return 10;
    }
}
