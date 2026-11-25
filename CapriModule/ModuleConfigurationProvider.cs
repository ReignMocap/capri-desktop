using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;

namespace CapriModule;

/// <summary>Nothing to set here: her own settings are in her app (right-click her, Settings).</summary>
public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public Task<FormField[]> GetModuleConfigurationFieldsAsync(IAuthenticationContext auth, ISettingsSource settings,
        CancellationToken cancellationToken) =>
        Task.FromResult(FormBuilder.Build(
            FormTitleField.Create("Capri on your desktop"),
            FormDocumentationField.Create(
                "Nothing to set here. Start her from **Play**; her own settings (how often she zips about, her size, her " +
                "look, Voxta's address and API key) are in her app: right-click her, **Settings**.")));
}
