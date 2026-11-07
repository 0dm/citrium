namespace Citrium.Models;

/// <summary>
/// Desktop viewer preferences. Stored separately from <see cref="AppSettings"/> (viewer.json) because viewer
/// processes and the main window run independently and must not overwrite each other's settings.
/// Settings written to the ICA file apply to the next connection.
/// </summary>
public sealed class ViewerSettings
{
    public bool PinToolbar { get; set; }

    /// <summary>"Top", "Bottom", "Left", "Right" or "Floating".</summary>
    public string ToolbarDock { get; set; } = "Top";

    /// <summary>The toolbar's centre across the screen, from 0 to 1. Fractions, so it survives a resolution change.</summary>
    public double ToolbarX { get; set; } = 0.5;

    public double ToolbarY { get; set; } = 0.5;

    public bool StartFullScreen { get; set; }

    /// <summary>
    /// Where Windows key combinations go: "Remote", "FullScreenOnly" (Citrix's default) or "Local". Written as
    /// TransparentKeyPassthrough. Citrium's Alt+Tab forwarding follows it straight away.
    /// </summary>
    public string KeyPassthrough { get; set; } = "Remote";

    public bool HideCitrixToasts { get; set; } = true;

    /// <summary>
    /// "Yes" scales the session to match this screen, "Native" uses the remote desktop's native resolution,
    /// and "System" lets Windows scale it. Written as Enable_DPI, or DpiAware for system scaling.
    /// </summary>
    public string DpiScaling { get; set; } = "Yes";

    /// <summary>Off by default: on a GPU that can't decode H.265 the session has no video path left.</summary>
    public bool EnableH265 { get; set; }

    /// <summary>Thinwire cache size as a power of two (19-25, so 512 KB to 32 MB), or 0 to let Citrix choose.</summary>
    public int Tw2CachePower { get; set; }

    /// <summary>"Default" leaves the store's launch file as it is; otherwise "High", "Medium", "Low" or "Off".</summary>
    public string AudioQuality { get; set; } = "Default";

    /// <summary>On leaves the microphone to the store and Citrix's policy; off is written as EnableAudioInput.</summary>
    public bool AllowMicrophone { get; set; } = true;

    /// <summary>When a desktop shrinks to picture in picture by itself: "Off", "Minimize" or "SwitchAway".</summary>
    public string AutoPictureInPicture { get; set; } = "Off";

    /// <summary>How solid the picture-in-picture window is, from 0.3 to 1.</summary>
    public double PictureInPictureOpacity { get; set; } = 1;
}
