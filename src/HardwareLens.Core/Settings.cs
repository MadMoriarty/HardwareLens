using System.Text.Json;
namespace HardwareLens.Core;

public sealed class AppSettings
{
    public int RefreshSeconds { get; set; } = 2;
    public int HistoryMinutes { get; set; } = 20;
    public int ViewMinutes { get; set; } = 5;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public string Section { get; set; } = "Live";
    public bool ShowEmptyReaders { get; set; }
    public int RecordingMaxMB { get; set; } = 50;
    public string ProcessSort { get; set; } = "cpu";
    public bool ProcessSortDescending { get; set; } = true;
    public string? LastExportFolder { get; set; }
}

/// <summary>Settings and recordings live beside the executable when that folder is writable (portable use), otherwise in LocalAppData.</summary>
public sealed class SettingsStore
{
    public string Folder { get; }
    public string Path { get; private set; }
    public string Warning { get; private set; } = "";
    public string RecordingsFolder => System.IO.Path.Combine(Folder, "recordings");

    public SettingsStore(string? baseDirectory = null)
    {
        string exe = baseDirectory ?? AppContext.BaseDirectory;
        string fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareLens");
        Folder = exe;
        try
        {
            string probe = System.IO.Path.Combine(exe, ".hardwarelens-write-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Folder = fallback; Warning = "The application folder is read-only; settings and recordings use " + fallback + ".";
        }
        Path = System.IO.Path.Combine(Folder, "hardwarelens.settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new();
            if (new FileInfo(Path).Length > 1_000_000) throw new IOException("Settings file is larger than 1 MB.");
            return Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new());
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Warning = "Could not read settings (using defaults; the file was left untouched): " + e.Message;
            return new();
        }
    }

    public static AppSettings Validate(AppSettings s)
    {
        if (!new[] { 1, 2, 5, 10 }.Contains(s.RefreshSeconds)) s.RefreshSeconds = 2;
        s.HistoryMinutes = Math.Clamp(s.HistoryMinutes, 15, 60);
        s.ViewMinutes = Math.Clamp(s.ViewMinutes, 1, s.HistoryMinutes);
        s.RecordingMaxMB = Math.Clamp(s.RecordingMaxMB, 1, 500);
        if (s.WindowWidth is < 400 or > 20000) s.WindowWidth = null;
        if (s.WindowHeight is < 300 or > 20000) s.WindowHeight = null;
        return s;
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(Folder);
        string tmp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); File.Move(tmp, Path, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
