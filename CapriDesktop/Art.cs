using System.IO;
using System.Windows.Media.Imaging;

namespace CapriDesktop;

/// <summary>One picture she can stand in: idle_N (with breathing frames; frame 0 is the pose itself) or talk_Nx, a
/// talking gesture of idle_N's family. Mouth is the same picture with her mouth open; Blink her eyes-off overlay.</summary>
public sealed record Pose(string Name, BitmapSource[] Frames, BitmapSource? Mouth, BitmapSource? Blink)
{
    public bool IsIdle => Name.StartsWith("idle_");
    /// <summary>The idle number she belongs with: idle_3 and talk_3a are both family 3, idle_12 is 12.</summary>
    public int Family
    {
        get
        {
            var at = Name.IndexOf('_') + 1;
            var n = 0;
            while (at > 0 && at < Name.Length && char.IsDigit(Name[at])) n = n * 10 + (Name[at++] - '0');
            return n;
        }
    }

    /// <summary>Her kind of picture: idle, talk, cast, angryidle...</summary>
    public string Kind => Name.Split('_')[0];
}

/// <summary>Her desktop art, made by tools/prep_art.py into E:\VoxtaArt\CapriDesktop (one folder per picture).</summary>
public static class Art
{
    /// <summary>Her art beside the app (the release zip's art folder). Made by tools/prep_art.py and
    /// tools/prep_face.py, which write to E:\VoxtaArt\CapriDesktop; the release build copies it here.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "art");

    public static List<Pose> Load(string folder)
    {
        var poses = new List<Pose>();
        foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d))
        {
            var name = Path.GetFileName(dir);
            // every picture of hers (<kind>_<n>: idle, talk, back, angry, wave...), not the face HUD's or sounds' folders
            if (!name.Contains('_')) continue;
            var frames = new List<BitmapSource>();
            for (var k = 0; File.Exists(Path.Combine(dir, $"f{k}.png")); k++)
                frames.Add(Png(Path.Combine(dir, $"f{k}.png")));
            if (frames.Count == 0) continue;
            poses.Add(new Pose(name, frames.ToArray(), Optional(dir, "m.png"), Optional(dir, "blink.png")));
        }
        return poses;
    }

    static BitmapSource? Optional(string dir, string file) =>
        File.Exists(Path.Combine(dir, file)) ? Png(Path.Combine(dir, file)) : null;

    static BitmapSource Png(string path)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;   // read now, don't keep the file open
        b.UriSource = new Uri(path);
        b.EndInit();
        b.Freeze();
        return b;
    }
}
