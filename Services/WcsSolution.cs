// AstroLab — solução WCS (plate solve). Parse do header FITS do ASTAP + projeção
// gnomónica (TAN). Coordenadas de píxel em convenção FITS 1-based. Ver design
// 2026-06-17 (plate solve / PCC / anotação).
using System.Globalization;

namespace AstroLab.Services;

public sealed class WcsSolution
{
    public required double Crpix1, Crpix2, Crval1, Crval2;
    public required double Cd11, Cd12, Cd21, Cd22;

    const double D2R = Math.PI / 180.0, R2D = 180.0 / Math.PI;

    public double CenterRaDeg => Crval1;
    public double CenterDecDeg => Crval2;

    /// <summary>Escala média (arcsec/px) = sqrt(|det(CD)|)·3600.</summary>
    public double ScaleArcsecPerPixel
        => Math.Sqrt(Math.Abs(Cd11 * Cd22 - Cd12 * Cd21)) * 3600.0;

    /// <summary>Ângulo de posição do eixo Y (graus), E de N.</summary>
    public double OrientationDeg => Math.Atan2(Cd12, Cd11) * R2D;

    public double FovWidthDeg(int width) => ScaleArcsecPerPixel * width / 3600.0;
    public double FovHeightDeg(int height) => ScaleArcsecPerPixel * height / 3600.0;

    /// <summary>Parse de um header FITS (cards "KEY = VALUE"). Devolve null se não
    /// solucionado (PLTSOLVD≠T) ou sem os campos essenciais.</summary>
    public static WcsSolution? Parse(string fitsHeader)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // O ASTAP escreve cards de 80 chars concatenados; aceitar também \n.
        var text = fitsHeader.Replace("\r", "");
        // partir em cards de 80 se não houver newlines suficientes
        IEnumerable<string> cards = text.Contains('\n')
            ? text.Split('\n')
            : Enumerable.Range(0, text.Length / 80).Select(i => text.Substring(i * 80, 80));
        foreach (var card in cards)
        {
            int eq = card.IndexOf('=');
            if (eq < 1) continue;
            string key = card[..eq].Trim();
            string val = card[(eq + 1)..];
            int slash = val.IndexOf('/');           // remover comentário
            if (slash >= 0) val = val[..slash];
            keys[key] = val.Trim().Trim('\'').Trim();
        }

        if (keys.TryGetValue("PLTSOLVD", out var s) && s.StartsWith("F", StringComparison.OrdinalIgnoreCase))
            return null;

        double? G(string k) => keys.TryGetValue(k, out var v)
            && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        if (G("CRPIX1") is not { } crpix1 || G("CRPIX2") is not { } crpix2 ||
            G("CRVAL1") is not { } crval1 || G("CRVAL2") is not { } crval2 ||
            G("CD1_1") is not { } cd11 || G("CD1_2") is not { } cd12 ||
            G("CD2_1") is not { } cd21 || G("CD2_2") is not { } cd22)
            return null;

        return new WcsSolution
        {
            Crpix1 = crpix1, Crpix2 = crpix2, Crval1 = crval1, Crval2 = crval2,
            Cd11 = cd11, Cd12 = cd12, Cd21 = cd21, Cd22 = cd22,
        };
    }

    /// <summary>Píxel (FITS 1-based) → (RA,Dec) graus. Projeção TAN inversa.</summary>
    public (double raDeg, double decDeg) PixelToWorld(double x, double y)
    {
        double dx = x - Crpix1, dy = y - Crpix2;
        double xi = (Cd11 * dx + Cd12 * dy) * D2R;     // standard coords (rad)
        double eta = (Cd21 * dx + Cd22 * dy) * D2R;

        double ra0 = Crval1 * D2R, dec0 = Crval2 * D2R;
        double r = Math.Sqrt(xi * xi + eta * eta);
        double c = Math.Atan(r);
        double sinc = Math.Sin(c), cosc = Math.Cos(c);
        double dec, ra;
        if (r < 1e-12)
        {
            ra = ra0; dec = dec0;
        }
        else
        {
            dec = Math.Asin(cosc * Math.Sin(dec0) + eta * sinc * Math.Cos(dec0) / r);
            ra = ra0 + Math.Atan2(xi * sinc, r * Math.Cos(dec0) * cosc - eta * Math.Sin(dec0) * sinc);
        }
        ra *= R2D; dec *= R2D;
        ra = (ra % 360.0 + 360.0) % 360.0;
        return (ra, dec);
    }

    /// <summary>(RA,Dec) graus → píxel (FITS 1-based). Projeção TAN direta.</summary>
    public (double x, double y) WorldToPixel(double raDeg, double decDeg)
    {
        double ra = raDeg * D2R, dec = decDeg * D2R;
        double ra0 = Crval1 * D2R, dec0 = Crval2 * D2R;
        double cosc = Math.Sin(dec0) * Math.Sin(dec)
                    + Math.Cos(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0);
        double xi = Math.Cos(dec) * Math.Sin(ra - ra0) / cosc * R2D;          // deg
        double eta = (Math.Cos(dec0) * Math.Sin(dec)
                    - Math.Sin(dec0) * Math.Cos(dec) * Math.Cos(ra - ra0)) / cosc * R2D;

        double det = Cd11 * Cd22 - Cd12 * Cd21;
        double dx = (Cd22 * xi - Cd12 * eta) / det;
        double dy = (-Cd21 * xi + Cd11 * eta) / det;
        return (Crpix1 + dx, Crpix2 + dy);
    }
}
