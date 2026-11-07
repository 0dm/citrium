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

    /// <summary>A link to the resource to open when Citrium starts, if any. See <see cref="Services.CitriumLink"/>.</summary>
    public string? AutoOpen { get; set; }

    public bool DisconnectOnLock { get; set; }

    public bool CheckForUpdates { get; set; } = true;

    public DateTimeOffset? LastUpdateCheck { get; set; }
}
