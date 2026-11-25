using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.Launchers;

namespace CapriModule;

/// <summary>
/// Capri on your desktop, in Voxta's Games: Play downloads the Capri desktop app the first time (this repository's
/// GitHub release, the same version as this module) and starts it. The app connects to Voxta by itself and installs
/// her character; the module only gets it onto the PC and starts it.
/// </summary>
public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "CapriDesktop";

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "Capri on your desktop",
            Version = ModuleInfo.Version,
            Flags = [ModuleFlag.Experimental, ModuleFlag.Single, ModuleFlag.InstallsWithoutSettings],
            Supports = new() { Launcher = new(ServiceDefinitionCategoryScore.Low) },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            Notes = "Capri, the hologram from Voxta for Cyberpunk, on your Windows desktop: she stands on your taskbar and talks " +
                    "with you through Voxta. Play downloads her app (about 100 MB) the first time and starts her.",
            Pros = "Talk to her out loud or type; her own voice, lip sync, moods and poses, glitches, zips about the screen.",
            Cons = "Work in progress. Windows only. Needs a text generation and a text-to-speech service; to talk out loud, " +
                   "speech-to-text and a microphone set up in Voxta.",
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.logo.png"),
            Onboarding = ModuleOnboarding.EmbeddedResource(typeof(VoxtaModule), "Onboarding.md"),
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
        });

        builder.Services.AddSingleton<CapriLauncher>();
        builder.Contribute<ILauncher>(sp => sp.GetRequiredService<CapriLauncher>());
    }
}
