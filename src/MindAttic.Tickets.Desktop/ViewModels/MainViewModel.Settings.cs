using System.Windows.Input;
using MindAttic.Components.Wpf.Mvvm;
using MindAttic.Tickets.Desktop.Data;
using MindAttic.Vault.Launch;

namespace MindAttic.Tickets.Desktop.ViewModels;

/// <summary>
/// The header's Settings button (M.TIX-T270): Tickets Desktop's settings are a MindAttic Vault settings app (M.TIX-T266),
/// edited only in MindAttic.Vault.Desktop, so Settings opens Vault at the Tickets Desktop tab. There is no settings
/// editor in this app.
/// </summary>
public sealed partial class MainViewModel
{
    private ICommand? openSettings;

    public ICommand OpenSettingsCommand => openSettings ??= new RelayCommand(OpenSettings);

    public void OpenSettings() =>
        StatusMessage = VaultSettings.Open(DesktopSettings.AppId)
            ? "Opened Tickets Desktop's settings in MindAttic Vault. Restart Tickets Desktop for a new start view or zoom."
            : $"MindAttic Vault isn't installed; edit {settings.Path ?? "settings.json"} instead.";
}
