using System.IO;

namespace CapriDesktop;

/// <summary>A small log of what she's doing: %AppData%\CapriDesktop\log.txt (started fresh each run).</summary>
public static class Log
{
    static string FilePath => Path.Combine(Settings.Folder,
        Environment.GetCommandLineArgs().Contains("--demo") ? "log-demo.txt" : "log.txt");
    static readonly object Gate = new();
    static bool _started;

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var line = $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";
                if (!_started) { File.WriteAllText(FilePath, line); _started = true; }
                else File.AppendAllText(FilePath, line);
            }
        }
        catch (Exception) { /* logging never breaks her */ }
    }
}
