using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DotLight;

/// <summary>A saved prompt (or any text) to paste with one click.</summary>
public class Snippet : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    string text = "";

    public string Text
    {
        get => text;
        set { text = value; Raise(nameof(Text)); Raise(nameof(Title)); Raise(nameof(Preview)); }
    }

    public string Title => Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Empty snippet";
    public string Preview => string.Join("  ", Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Snippets at %APPDATA%\.light\snippets.json. Starts with a few prompts for AI coding tools.</summary>
static class SnippetStore
{
    static string FilePath => Path.Combine(Settings.Dir, "snippets.json");

    static readonly string[] Starters =
    {
        "Fix the build errors\nRun the build, read the errors, fix them, and run it again until it passes.",
        "Explain this code\nExplain what this file does in plain words, then list anything that looks risky.",
        "Write tests\nWrite tests for the code you just changed. Cover the normal case and the edge cases, then run them.",
        "Review my changes\nReview the current diff for bugs and anything I forgot. Keep it short.",
        "Smallest fix\nMake the smallest change that fixes this. Don't refactor anything else.",
    };

    // declared after Starters on purpose: static fields start in order
    public static ObservableCollection<Snippet> All { get; } = Load();

    static ObservableCollection<Snippet> Load() =>
        JsonFile.Load<List<Snippet>>(FilePath) is { } list
            ? new(list)
            : new(Starters.Select(s => new Snippet { Text = s }));

    public static void Save() => JsonFile.Save(FilePath, All.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList());
}

/// <summary>A file held on the shelf: just a link to where it is, nothing copied.</summary>
public class ShelfItem
{
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);
}

/// <summary>Shelf at %APPDATA%\.light\shelf.json.</summary>
static class ShelfStore
{
    static string FilePath => Path.Combine(Settings.Dir, "shelf.json");
    public static ObservableCollection<ShelfItem> All { get; } = new(JsonFile.Load<List<ShelfItem>>(FilePath) ?? new());

    public static int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (All.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            All.Add(new ShelfItem { Path = p });
            added++;
        }
        Save();
        return added;
    }

    public static void Save() => JsonFile.Save(FilePath, All.ToList());
}

/// <summary>Reads and writes small JSON files safely (temp file, then swap, keeping a .bak).</summary>
static class JsonFile
{
    public static T? Load<T>(string path) where T : class
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try { if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p)); }
            catch { }
        }
        return null;
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }
        catch { }
    }
}
