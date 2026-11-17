using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace DotLight;

public class Note : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    string text = "";
    bool pinned;
    public DateTime Updated { get; set; } = DateTime.Now;

    public string Text
    {
        get => text;
        set { text = value; Updated = DateTime.Now; Raise(nameof(Text)); Raise(nameof(Title)); Raise(nameof(Preview)); Raise(nameof(TimeLabel)); }
    }

    public bool Pinned
    {
        get => pinned;
        set { pinned = value; Raise(nameof(Pinned)); Raise(nameof(PinVisibility)); }
    }

    public string Title
    {
        get
        {
            var first = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return first ?? "Empty note";
        }
    }

    public string Preview
    {
        get
        {
            var rest = Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1);
            return string.Join("  ", rest);
        }
    }

    public string TimeLabel => Updated.Date == DateTime.Today ? Updated.ToString("HH:mm") : Updated.ToString("d MMM");
    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>Notes saved at %APPDATA%\.light\notes.json (written safely, with a backup).</summary>
static class NoteStore
{
    static string FilePath => Path.Combine(Settings.Dir, "notes.json");
    public static ObservableCollection<Note> All { get; } = Load();

    /// <summary>The note shown in the resting island, if any.</summary>
    public static Note? PinnedNote => All.FirstOrDefault(n => n.Pinned && n.Title != "Empty note");

    static ObservableCollection<Note> Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                    return new(JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path)) ?? new());
            }
            catch { }
        }
        return new();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var keep = All.Where(n => !string.IsNullOrWhiteSpace(n.Text)).ToList();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keep, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
            else File.Move(tmp, FilePath);
        }
        catch { }
    }
}
