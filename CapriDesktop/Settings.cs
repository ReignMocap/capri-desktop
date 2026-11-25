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
    // where her face HUD is (its bottom middle): it stays where you put it; she only zips about in her full body
    public double? FaceX { get; set; }
    public double? FaceY { get; set; }

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

    // Timings, as the game's Capri tab has them: { at least, at most } seconds, a random time in between each time.
    public double ZipMin { get; set; } = 15;          // her zaps on her own (the game's wanderEvery)
    public double ZipMax { get; set; } = 35;
    public double FormMin { get; set; } = 40;         // each form for, when she switches on her own (formEvery)
    public double FormMax { get; set; } = 120;
    public double IdleMin { get; set; } = 20;         // quiet, before she settles into another idle
    public double IdleMax { get; set; } = 40;
    public double SfxVolume { get; set; } = 0.5;      // her zap and glitch sounds (0: off, 1: as made)
    public double Opacity { get; set; } = 1.0;        // how solid she is (her body and face; not her speech bubble)
    // she lands from a zip at a random size: { at least, at most } px tall
    public bool RandomSize { get; set; } = true;
    public double SizeMin { get; set; } = 80;
    public double SizeMax { get; set; } = 520;
    public bool Mic { get; set; } = true;             // Voxta listens to the mic: talk to her out loud

    /// <summary>Where her settings and log live: %AppData%\CapriDesktop (a --fresh test run: a new empty folder).</summary>
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CapriDesktop");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(FilePath))
                s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? s;
        }
        catch (Exception) { /* a broken file: start fresh */ }
        // (an art folder that isn't there any more, or never was on this PC: the one beside the app)
        if (string.IsNullOrWhiteSpace(s.ArtFolder) || !Directory.Exists(s.ArtFolder)) s.ArtFolder = Art.DefaultFolder;
        return s;
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
