// AstroLab — caminhos recentes persistidos em %LOCALAPPDATA%/AstroLab/recent.json.

using System.Text.Json;

namespace AstroLab.Services;

public static class RecentFiles
{
    static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstroLab");
    static readonly string FilePath = Path.Combine(Dir, "recent.json");
    const int Max = 8;

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
            return list.Where(File.Exists).ToList();
        }
        catch { return new(); }
    }

    public static void Add(string path)
    {
        try
        {
            var list = Load();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > Max) list = list.Take(Max).ToList();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch { /* recentes são conveniência; falha não é crítica */ }
    }
}
