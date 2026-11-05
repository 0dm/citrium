namespace Citrium.Models;

public sealed class AppSettings
{
    public List<Account> Accounts { get; set; } = [];

    /// <summary>"Default", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>"Mica", "MicaAlt" or "Acrylic".</summary>
    public string Backdrop { get; set; } = "Mica";

    public bool ShowOrganizationLogo { get; set; } = true;

    public string? HdxEnginePath { get; set; }

    /// <summary>Where desktops open: "Citrium" (built-in viewer, when available) or "Citrix" (Desktop Viewer).</summary>
    public string DesktopViewer { get; set; } = "Citrium";

    public bool KeepIcaFiles { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool TrayHintShown { get; set; }

    public bool RefreshOnStartup { get; set; } = true;
}
