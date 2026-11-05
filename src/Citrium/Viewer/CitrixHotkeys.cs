using Citrium.Services;

namespace Citrium.Viewer;

internal readonly record struct CitrixHotkey(int ModifierVk, int KeyVk);

/// <summary>
/// The session hotkeys Citrium sends (Ctrl+Alt+Del, Alt+Tab), read from the user's Citrix client settings
/// (%APPDATA%\ICAClient\APPSRV.INI, [WFClient]) so remapped hotkeys keep working. Citrix's defaults apply
/// when a value is missing. "plus" and "minus" are the numeric keypad keys (scan codes 78 and 74 in
/// Citrix's module.ini).
/// </summary>
internal static class CitrixHotkeys
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_F1 = 0x70, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D;

    public static CitrixHotkey CtrlAltDel { get; } = Read(4, "Ctrl", "F1");

    public static CitrixHotkey AltTab { get; } = Read(7, "Alt", "plus");

    public static CitrixHotkey AltShiftTab { get; } = Read(8, "Alt", "minus");

    private static CitrixHotkey Read(int number, string defaultShift, string defaultChar)
    {
        var values = LoadIni();
        var shift = values.GetValueOrDefault($"Hotkey{number}Shift", defaultShift);
        var key = values.GetValueOrDefault($"Hotkey{number}Char", defaultChar);
        // A value turned off or not understood falls back to Citrix's default rather than sending nothing.
        return new CitrixHotkey(ModifierVk(shift) ?? ModifierVk(defaultShift)!.Value, KeyVk(key) ?? KeyVk(defaultChar)!.Value);
    }

    private static int? ModifierVk(string name) => name.ToLowerInvariant() switch
    {
        "shift" => VK_SHIFT,
        "ctrl" => VK_CONTROL,
        "alt" => VK_MENU,
        _ => null,
    };

    private static int? KeyVk(string name)
    {
        var n = name.ToLowerInvariant();
        if (n == "plus") return VK_ADD;
        if (n == "minus") return VK_SUBTRACT;
        if (n.Length is 2 or 3 && n[0] == 'f' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 12) return VK_F1 + f - 1;
        return null;
    }

    private static Dictionary<string, string> LoadIni()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ICAClient", "APPSRV.INI");
            if (!File.Exists(path)) return values;
            var inSection = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inSection = line.Equals("[WFClient]", StringComparison.OrdinalIgnoreCase); continue; }
                var eq = line.IndexOf('=');
                if (inSection && eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read Citrix hotkeys", ex);
        }
        return values;
    }
}
