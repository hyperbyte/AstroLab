// AstroLab — settings persistentes (focal/píxel/ASTAP) para a calibração por
// plate solve. JSON simples no perfil do utilizador. Ver design 2026-06-17.
using System.Text.Json;

namespace AstroLab.Services;

public sealed class AppSettings
{
    public double FocalLengthMm { get; set; }
    public double PixelSizeUm { get; set; }
    public string? AstapPath { get; set; }

    static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AstroLab", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new();
        }
        catch { /* ficheiro corrompido → defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
