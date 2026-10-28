using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileAction.App.Input;
using FileAction.Core;
using static FileAction.Core.Loc;

namespace FileAction.App;

public enum HotkeyPreset
{
    None,
    CtrlAltSpace,
    CtrlShiftSpace,
    CtrlAltF,
    CtrlAltW,
}

/// <summary>User preferences, stored as JSON in %APPDATA%\FileAction\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string? StorageOverride { get; set; }
    public static string Folder => StorageOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Peel");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public ModifierKey ConvertKey { get; set; } = ModifierKey.Shift;
    public ModifierKey ToolsKey { get; set; } = ModifierKey.Ctrl;
    public ModifierKey ActionsKey { get; set; } = ModifierKey.Alt;
    public HotkeyPreset Hotkey { get; set; } = HotkeyPreset.CtrlShiftSpace;

    /// <summary>Null = follow the Windows display language.</summary>
    public Language? Language { get; set; } = Core.Language.English;

    public List<SavedRecipe> Recipes { get; set; } = [];
    public List<JobRecord> History { get; set; } = [];
    public bool KeepHistory { get; set; } = true;

    public void RememberRecipe(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || LastOperationId is null) return;
        Recipes.RemoveAll(recipe => string.Equals(recipe.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        Recipes.Insert(0, new SavedRecipe(name.Trim(), LastOperationId, LastParameters is null ? [] : new(LastParameters)));
        Recipes = Recipes.Take(24).ToList();
        Save();
    }

    /// <summary>Empty = save results next to the original file.</summary>
    public string? OutputDirectory { get; set; }

    public bool RevealResult { get; set; }
    public bool Paused { get; set; }

    /// <summary>Offer an MP4/MP3 download when a YouTube, Instagram, TikTok or X video link is copied.</summary>
    public bool DetectVideoLinks { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string? LastCopyFolder { get; set; }
    public string? LastMoveFolder { get; set; }

    public Settings Quality { get; set; } = new();

    /// <summary>Operation ids the user removed from the wheels.</summary>
    public List<string> HiddenOperations { get; set; } = [];

    /// <summary>Operation ids that always come first on their wheel, in this order.</summary>
    public List<string> FavoriteOperations { get; set; } =
    [
        "image.compress", "image.crop", "image.adjust", "image.redact", "image.annotate", "image.background", "image.collage", "image.strip",
        "video.compress", "video.trim", "video.crop", "video.join", "video.speed", "video.split", "video.redact", "video.snapshot",
        "audio.compress", "audio.trim", "audio.normalize", "audio.bleep", "audio.channels", "audio.concat", "audio.visualize", "audio.silence",
        "pdf.organize", "pdf.merge", "pdf.split", "pdf.optimize", "pdf.metadata", "pdf.strip",
    ];

    /// <summary>The last operation run, for "repeat last" (with the values it asked for, e.g. trim times).</summary>
    public string? LastOperationId { get; set; }

    public Dictionary<string, string>? LastParameters { get; set; }

    public bool CheckForUpdates { get; set; } = false;

    /// <summary>Look for new versions on Peel's GitHub releases and offer them. (The inherited
    /// CheckForUpdates pointed at the upstream project and stays unused.)</summary>
    public bool AutoUpdate { get; set; } = true;

    public WheelKind? WheelFor(ModifierKey key)
    {
        if (key == ModifierKey.None)
            return null;
        if (key == ConvertKey)
            return WheelKind.Convert;
        if (key == ToolsKey)
            return WheelKind.Tools;
        if (key == ActionsKey)
            return WheelKind.Actions;
        return null;
    }

    public ModifierKey KeyFor(WheelKind wheel) => wheel switch
    {
        WheelKind.Convert => ConvertKey,
        WheelKind.Tools => ToolsKey,
        _ => ActionsKey,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Broken settings file: start with defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal; settings simply won't persist.
        }
    }

    public static string KeyName(ModifierKey key) => key switch
    {
        ModifierKey.Shift => "Shift",
        ModifierKey.Ctrl => "Ctrl",
        ModifierKey.Alt => "Alt",
        _ => "—",
    };

    public static string WheelName(WheelKind wheel) => wheel switch
    {
        WheelKind.Convert => T("Převést", "Convert"),
        WheelKind.Tools => T("Nástroje", "Tools"),
        _ => T("Akce", "Actions"),
    };

    public static string HotkeyName(HotkeyPreset preset) => preset switch
    {
        HotkeyPreset.CtrlAltSpace => T("Ctrl + Alt + Mezerník", "Ctrl + Alt + Space"),
        HotkeyPreset.CtrlShiftSpace => T("Ctrl + Shift + Mezerník", "Ctrl + Shift + Space"),
        HotkeyPreset.CtrlAltF => "Ctrl + Alt + F",
        HotkeyPreset.CtrlAltW => "Ctrl + Alt + W",
        _ => T("Vypnuto", "Off"),
    };
}
