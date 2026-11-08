using System.IO;
using System.Text.Json;

namespace CapriDesktop;

/// <summary>What she remembers between runs: %AppData%\CapriDesktop\settings.json.</summary>
public sealed class Settings
{
    public string ArtFolder { get; set; } = Art.DefaultFolder;
    public string Pose { get; set; } = "idle_3";
    public double Height { get; set; } = 420;
    // where she stands: the middle of her feet, in screen pixels (so it holds when the window's size changes)
    public double? FeetX { get; set; }
    public double? FeetY { get; set; }

    // Voxta
    public string VoxtaUrl { get; set; } = "http://localhost:5384";
    public string? VoxtaApiKey { get; set; }          // only needed if Voxta's root account has a password
    public string CharacterName { get; set; } = "Capri";
    public Guid? ChatId { get; set; }                 // her desktop chat, picked up again next time
    public bool Voice { get; set; } = true;           // her voice on (off: she only types)
    public double Volume { get; set; } = 1.0;         // 0..1

    // how she looks: "body" (her full self), "face" (her face HUD), or "auto" (switching between them now and then)
    public string Form { get; set; } = "body";
    public double FaceWidth { get; set; } = 340;
    public bool FacePanel { get; set; } = true;       // the face HUD's green panel behind her eyes
    public bool Glitches { get; set; } = true;
    public bool Effects { get; set; } = true;         // zap and glitch sounds
    public bool ZipOnHerOwn { get; set; } = true;     // zips somewhere else now and then while quiet
    public bool IdlesOnHerOwn { get; set; } = true;   // settles into another idle (now and then turned away) while quiet
    public bool Mic { get; set; } = true;             // Voxta listens to the mic: talk to her out loud

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapriDesktop", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception) { /* a broken file: start fresh */ }
        return new Settings();
    }

    /// <summary>A test run (--demo): nothing saved.</summary>
    public static bool ReadOnly { get; set; }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* not worth crashing over */ }
    }
}
