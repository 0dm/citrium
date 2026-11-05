using System.Runtime.InteropServices;
using Citrium.Models;
using Citrium.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Citrium.Views;

/// <summary>
/// The viewer's settings, shown as a light-dismiss panel over the session. It's a small borderless window
/// (like the full-screen toolbar) because nothing drawn by XAML can appear above the native session surface.
/// </summary>
internal sealed class ViewerSettingsPanel : Window
{
    private const double WidthDip = 380;
    private readonly ViewerSettings _settings;
    private readonly StackPanel _root;
    private readonly ComboBox _keys;
    private readonly ComboBox _dpi;
    private readonly ComboBox _cache;
    private readonly ComboBox _audio;
    private readonly ToggleSwitch _pin;
    private PanelAnchor _anchor;
    private bool _sized;

    public event Action<ViewerSettings>? Changed;
    public event Action? OpenCitrixPreferences;

    public bool IsShown => AppWindow.IsVisible;

    private bool AnyDropDownOpen => _keys.IsDropDownOpen || _dpi.IsDropDownOpen || _cache.IsDropDownOpen || _audio.IsDropDownOpen;

    public ViewerSettingsPanel(ViewerSettings settings, bool citrixPreferencesAvailable)
    {
        _settings = settings;

        // Secondary text uses opacity on the normal foreground rather than app-level brushes, so it follows
        // this window's theme (app resources resolve against the app theme, not the window's).
        TextBlock Section(string text) => new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.75,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 20, 0, 6),
        };

        Grid Row(string label, FrameworkElement control, string? hint = null)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
            text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            if (hint is not null)
                text.Children.Add(new TextBlock
                {
                    Text = hint, TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Opacity = 0.7,
                });
            var g = new Grid { ColumnSpacing = 16, MinHeight = 44, Padding = new Thickness(0, 6, 0, 6), ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
            control.VerticalAlignment = VerticalAlignment.Center;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label);
            Grid.SetColumn(control, 1);
            g.Children.Add(text);
            g.Children.Add(control);
            return g;
        }

        ToggleSwitch Toggle(bool value, Action<bool> set)
        {
            var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, 0, -12, 0) };
            t.Toggled += (_, _) => { set(t.IsOn); Save(); };
            return t;
        }

        ComboBox Choice((string Tag, string Text)[] items, string selected, Action<string> set)
        {
            var c = new ComboBox { Width = 190 };
            foreach (var (tag, text) in items) c.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? c.Items[0];
            c.SelectionChanged += (_, _) =>
            {
                if (c.SelectedItem is ComboBoxItem { Tag: string tag }) { set(tag); Save(); }
            };
            return c;
        }

        _pin = Toggle(settings.PinToolbar, v => settings.PinToolbar = v);

        _dpi = Choice(
            new[] { ("Yes", "Yes"), ("Native", "No, native resolution"), ("System", "Let Windows scale it") },
            settings.DpiScaling, v => settings.DpiScaling = v);

        _cache = Choice(
            new[] { ("0", "Automatic") }
                .Concat(Enumerable.Range(19, 7).Select(p => (p.ToString(), (p < 20 ? $"{1 << (p - 10)} KB" : $"{1 << (p - 20)} MB"))))
                .ToArray(),
            settings.Tw2CachePower.ToString(), v => settings.Tw2CachePower = int.Parse(v));

        _audio = Choice(
            new[] { ("Default", "Default"), ("High", "High"), ("Medium", "Medium"), ("Low", "Low"), ("Off", "Off") },
            settings.AudioQuality, v => settings.AudioQuality = v);

        _keys = Choice(
            new[] { ("Remote", "Remote desktop"), ("FullScreenOnly", "Full screen only"), ("Local", "This PC") },
            settings.KeyPassthrough, v => settings.KeyPassthrough = v);

        var prefs = new HyperlinkButton
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = "Citrix Workspace preferences" }, new FontIcon { Glyph = "", FontSize = 12 } },
            },
            Padding = new Thickness(0, 4, 0, 4),
            IsEnabled = citrixPreferencesAvailable,
        };
        ToolTipService.SetToolTip(prefs, "Devices, display and other Citrix options");
        prefs.Click += (_, _) => { Hide(); OpenCitrixPreferences?.Invoke(); };

        _root = new StackPanel
        {
            Padding = new Thickness(24, 20, 24, 16),
            Children =
            {
                new TextBlock { Text = "Viewer settings", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                Section("Full screen"),
                Row("Keep toolbar visible", _pin),
                Row("Open desktops in full screen", Toggle(settings.StartFullScreen, v => settings.StartFullScreen = v)),
                Section("Display"),
                Row("Scale the session for high resolution?", _dpi, "Yes matches this screen's scaling. Applies the next time you open a desktop."),
                Row("Thinwire bitmap cache", _cache, "Automatic sizes the cache for the session. A larger cache can smooth scrolling on busy desktops."),
                Section("Graphics"),
                Row("H.265 decoding", Toggle(settings.EnableH265, v => settings.EnableH265 = v), "Off by default. Turn it on only if this GPU can decode H.265; otherwise the session has nothing to draw. Applies the next time you open a desktop."),
                Section("Audio"),
                Row("Sound quality", _audio, "Default uses your store's setting. Your organization can limit this. Applies the next time you open a desktop."),
                Row("Allow the microphone", Toggle(settings.AllowMicrophone, v => settings.AllowMicrophone = v), "Applies the next time you open a desktop."),
                Section("Keyboard"),
                Row("Windows shortcuts go to", _keys, "Win, Alt+Tab and similar keys. Applies the next time you open a desktop."),
                Section("Notifications"),
                Row("Hide Citrix connection pop-ups", Toggle(settings.HideCitrixToasts, v => settings.HideCitrixToasts = v)),
                new Border { Height = 1, Margin = new Thickness(0, 16, 0, 8), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80)) },
                prefs,
            },
        };
        Content = new ScrollViewer { Content = _root };

        WindowHelper.ApplyTheme(this, _root);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !AnyDropDownOpen) Hide();
        };
        _root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) Hide(); };
    }

    public void SetPinned(bool pinned)
    {
        if (_pin.IsOn != pinned) _pin.IsOn = pinned;
    }

    private void Save()
    {
        SettingsService.SaveViewer(_settings);
        Changed?.Invoke(_settings);
    }

    /// <summary>Shows the panel with the given corner at a screen point (physical pixels), kept on that monitor.</summary>
    public void ShowAt(PanelAnchor anchor)
    {
        _anchor = anchor;
        Place();
        AppWindow.Show(true);
        Activate();
        if (!_sized) _root.LayoutUpdated += OnFirstLayout;
    }

    public void Hide() => AppWindow.Hide();

    private void OnFirstLayout(object? sender, object e)
    {
        if (_root.ActualHeight <= 0) return;
        _root.LayoutUpdated -= OnFirstLayout;
        _sized = true;
        Place();
    }

    private void Place()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        _root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var heightDip = _sized && _root.DesiredSize.Height > 0 ? _root.DesiredSize.Height + 2 : 420;
        var w = (int)(WidthDip * scale);
        var screen = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(_anchor.X, _anchor.Y), DisplayAreaFallback.Nearest).OuterBounds;
        var h = Math.Min((int)(heightDip * scale), screen.Height);
        var x = _anchor.AlignRight ? _anchor.X - w : _anchor.X;
        var y = _anchor.AlignBottom ? _anchor.Y - h : _anchor.Y;
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - w));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - h));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
