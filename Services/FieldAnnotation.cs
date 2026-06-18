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
}
