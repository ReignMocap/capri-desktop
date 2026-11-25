using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Repositories;
using Voxta.Abstractions.Services.Launchers;
using Voxta.Shared.ExternalProcesses;

namespace CapriModule;

/// <summary>
/// Capri in the Play library. Her app lives in %LocalAppData%\Programs\CapriDesktop, where her installer (Setup.exe)
/// puts it too. Play: if it's not there, or older than this module, her app zip is downloaded from this repository's
/// release of the same version and unpacked there; then she's started. Started while she's already up, she zaps where
/// she stands to show she's there (her app lets only one of her run).
/// </summary>
public class CapriLauncher(IUnitOfWorkFactory unitOfWorkFactory, ILogger<CapriLauncher> logger) : ILauncher
{
    /// <summary>Where her releases are: the app zip is Capri-Desktop-App-{version}.zip on the v{version} release.</summary>
    public const string Repository = "ReignMocap/capri-desktop";

    const string Exe = "CapriDesktop.exe";
    const string VersionFile = "version.txt";   // written beside her app by this module and by her installer

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    static readonly SemaphoreSlim Installing = new(1, 1);

    static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CapriDesktop");

    static string ExePath => Path.Combine(Folder, Exe);

    public string ServiceName => VoxtaModule.ServiceName;

    public ModuleLogo? Cover { get; } = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.Cover.jpg");

    // She talks to Voxta by herself, as an app.
    public string? ChatBridgeServiceName => null;

    public async Task<IReadOnlyList<LauncherStatus>> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var modules = await unitOfWorkFactory.ReadAsync(userId, x => x.Modules.GetManyByServiceNameAsync(ServiceName, cancellationToken));
        return [.. modules.Select(m => Status(m.Id))];
    }

    static LauncherStatus Status(Guid moduleId)
    {
        var running = ExternalProgram.IsRunning(Exe, Folder);
        string? detail;
        if (running) detail = "On your desktop.";
        else if (!File.Exists(ExePath)) detail = "Play downloads her app (about 100 MB) and starts her.";
        else if (InstalledVersion() is { } v && v != ModuleInfo.Version) detail = $"Play updates her to {ModuleInfo.Version} and starts her.";
        else detail = null;
        return new LauncherStatus { ModuleId = moduleId, Running = running, CanLaunch = true, Detail = detail };
    }

    public async Task LaunchAsync(Guid userId, Guid moduleId, CancellationToken cancellationToken)
    {
        var current = File.Exists(ExePath) && InstalledVersion() == ModuleInfo.Version;
        // (running: started again only to show herself; an update waits till she's closed)
        if (!current && !ExternalProgram.IsRunning(Exe, Folder)) await InstallAsync(cancellationToken);
        if (!File.Exists(ExePath))
            throw new InvalidOperationException("Capri's app isn't installed. Try Play again, or install her with her Setup.exe from " +
                                                $"https://github.com/{Repository}/releases.");
        ExternalProgram.Start(ExePath);
    }

    /// <summary>Her app zip from this module's release, unpacked into her folder (her settings, in %AppData%, stay).</summary>
    async Task InstallAsync(CancellationToken cancellationToken)
    {
        await Installing.WaitAsync(cancellationToken);
        try
        {
            var version = ModuleInfo.Version;
            var url = $"https://github.com/{Repository}/releases/download/v{version}/Capri-Desktop-App-{version}.zip";
            var zip = Path.Combine(Path.GetTempPath(), $"CapriDesktop-{version}-{Guid.NewGuid():N}.zip");
            logger.LogInformation("Capri: downloading her app {Version} from {Url}", version, url);
            try
            {
                using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Couldn't download Capri's app {version} ({(int)response.StatusCode}). " +
                                                            $"Check your connection, or get her from https://github.com/{Repository}/releases.");
                    await using var file = File.Create(zip);
                    await response.Content.CopyToAsync(file, cancellationToken);
                }

                // The zip holds a CapriDesktop folder: its files are copied over her old app's (anything else there, such
                // as her installer's uninstaller, stays).
                var staging = Folder + ".new";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                ZipFile.ExtractToDirectory(zip, staging);
                var inner = Path.Combine(staging, "CapriDesktop");
                CopyOver(Directory.Exists(inner) ? inner : staging, Folder);
                Directory.Delete(staging, true);
                await File.WriteAllTextAsync(Path.Combine(Folder, VersionFile), version, cancellationToken);
                logger.LogInformation("Capri: her app {Version} is in {Folder}", version, Folder);
            }
            finally
            {
                if (File.Exists(zip)) File.Delete(zip);
            }
        }
        finally
        {
            Installing.Release();
        }
    }

    static void CopyOver(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyOver(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    static string? InstalledVersion()
    {
        var file = Path.Combine(Folder, VersionFile);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }
}
