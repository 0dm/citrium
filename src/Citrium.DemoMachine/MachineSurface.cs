using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Citrium.DemoMachine;

/// <summary>
/// The fake machine's screen: a wallpaper, a taskbar with a clock, and a few busy windows that can be dragged around.
/// Its windows are laid out on a 1600 by 900 canvas that's scaled to fit, and the wallpaper and taskbar stretch past
/// the canvas to fill the screen, so nothing is cut off at any shape of window.
/// </summary>
internal sealed class MachineSurface : Control
{
    private const float CanvasWidth = 1600, CanvasHeight = 900, TaskbarHeight = 52, TitleHeight = 36;
    private static readonly Color Paper = Color.FromArgb(0xF7, 0xF6, 0xF3), Ink = Color.FromArgb(0x24, 0x28, 0x30), Muted = Color.FromArgb(0x7B, 0x80, 0x8A);

    private readonly string _title;
    private readonly Color _from, _to, _accent;
    private readonly List<AppWindow> _windows;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 66 };
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly bool _isDesktop;
    private AppWindow? _dragging;
    private PointF _grab;

    private sealed class AppWindow(string title, RectangleF bounds, Action<Graphics, RectangleF, float> draw)
    {
        public string Title { get; } = title;
        public RectangleF Bounds { get; set; } = bounds;
        public Action<Graphics, RectangleF, float> Draw { get; } = draw;
    }

    public MachineSurface(string title, string scene, bool isDesktop)
    {
        _title = title;
        _isDesktop = isDesktop;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        (_from, _to, _accent) = scene switch
        {
            "design" => (Color.FromArgb(0xF2, 0x6B, 0x5B), Color.FromArgb(0x5B, 0x2A, 0x86), Color.FromArgb(0xF2, 0x6B, 0x5B)),
            "finance" => (Color.FromArgb(0x1D, 0x8A, 0x7A), Color.FromArgb(0x0E, 0x2F, 0x4F), Color.FromArgb(0x1D, 0x8A, 0x7A)),
            "lab" => (Color.FromArgb(0x4B, 0x55, 0x63), Color.FromArgb(0x11, 0x18, 0x27), Color.FromArgb(0xE8, 0xA3, 0x17)),
            _ => (Color.FromArgb(0x2F, 0x80, 0xED), Color.FromArgb(0x10, 0x20, 0x5A), Color.FromArgb(0x2F, 0x80, 0xED)),
        };

        _windows = !isDesktop
            ? [new AppWindow(title, new RectangleF(0, 0, CanvasWidth, CanvasHeight), DrawAppPage)]
            : scene switch
            {
                "design" =>
                [
                    new AppWindow("Layers", new RectangleF(1130, 90, 370, 520), DrawLayers),
                    new AppWindow("Spring campaign.canvas", new RectangleF(110, 60, 960, 700), DrawCanvas),
                ],
                "finance" =>
                [
                    new AppWindow("Runway", new RectangleF(980, 110, 540, 400), DrawChart),
                    new AppWindow("Q3 close.sheet", new RectangleF(90, 70, 840, 690), DrawSheet),
                ],
                _ =>
                [
                    new AppWindow("Release notes", new RectangleF(1040, 440, 480, 340), DrawNotes),
                    new AppWindow("Build health", new RectangleF(900, 70, 620, 330), DrawChart),
                    new AppWindow("Terminal", new RectangleF(80, 80, 780, 660), DrawTerminal),
                ],
            };

        _tick.Tick += (_, _) => Invalidate();
        _tick.Start();
    }

    private float Seconds => (float)(DateTime.UtcNow - _started).TotalSeconds;
    private float Zoom => Math.Min(Width / CanvasWidth, Height / CanvasHeight);
    private RectangleF Screen => new((CanvasWidth - Width / Zoom) / 2, (CanvasHeight - Height / Zoom) / 2, Width / Zoom, Height / Zoom);
    private PointF ToCanvas(Point p) => new((p.X - (Width - CanvasWidth * Zoom) / 2) / Zoom, (p.Y - (Height - CanvasHeight * Zoom) / 2) / Zoom);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var p = ToCanvas(e.Location);
        var hit = _windows.LastOrDefault(w => w.Bounds.Contains(p));
        if (hit is null) return;

        _windows.Remove(hit);
        _windows.Add(hit);
        if (_isDesktop && p.Y < hit.Bounds.Top + TitleHeight)
        {
            _dragging = hit;
            _grab = new PointF(p.X - hit.Bounds.X, p.Y - hit.Bounds.Y);
            Capture = true;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging is null) return;
        var p = ToCanvas(e.Location);
        var size = _dragging.Bounds.Size;
        var x = Math.Clamp(p.X - _grab.X, -size.Width + 80, CanvasWidth - 80);
        var y = Math.Clamp(p.Y - _grab.Y, 0, CanvasHeight - TaskbarHeight - TitleHeight);
        _dragging.Bounds = new RectangleF(new PointF(x, y), size);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = null;
        Capture = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(_to);
        if (Width <= 0 || Height <= 0) return;

        var scale = Zoom;
        g.TranslateTransform((Width - CanvasWidth * scale) / 2, (Height - CanvasHeight * scale) / 2);
        g.ScaleTransform(scale, scale);

        if (_isDesktop)
        {
            DrawWallpaper(g);
            foreach (var window in _windows) DrawWindow(g, window);
            DrawTaskbar(g);
        }
        else
        {
            var page = new RectangleF(0, 0, CanvasWidth, CanvasHeight);
            using var fill = new SolidBrush(Paper);
            g.FillRectangle(fill, page);
            DrawAppPage(g, page, 1);
        }
    }

    private void DrawWallpaper(Graphics g)
    {
        var all = Screen;
        using (var sky = new LinearGradientBrush(all, _from, _to, 62f)) g.FillRectangle(sky, all);

        // Slow hills, drifting just enough to show that the picture is live.
        var drift = MathF.Sin(Seconds * 0.15f) * 30;
        for (var layer = 0; layer < 3; layer++)
        {
            using var path = new GraphicsPath();
            var points = new List<PointF> { new(all.Left, all.Bottom) };
            for (var x = all.Left; x <= all.Right + 40; x += 40)
                points.Add(new PointF(x, all.Bottom - 280 + layer * 90 + MathF.Sin(x / (260 - layer * 50) + layer * 2 + drift * 0.01f) * (70 - layer * 15)));
            points.Add(new PointF(all.Right, all.Bottom));
            path.AddPolygon(points.ToArray());
            using var hill = new SolidBrush(Color.FromArgb(46 + layer * 16, Color.Black));
            g.FillPath(hill, path);
        }
    }

    private void DrawWindow(Graphics g, AppWindow window)
    {
        var r = window.Bounds;
        for (var i = 3; i > 0; i--)
        {
            using var shadow = new SolidBrush(Color.FromArgb(16, Color.Black));
            using var s = Rounded(RectangleF.Inflate(new RectangleF(r.X, r.Y + 6, r.Width, r.Height), i * 6, i * 6), 16 + i * 4);
            g.FillPath(shadow, s);
        }

        using var outline = Rounded(r, 10);
        var state = g.Save();
        g.SetClip(outline);
        using (var paper = new SolidBrush(Paper)) g.FillRectangle(paper, r);
        using (var bar = new SolidBrush(Color.FromArgb(0xE6, 0xE5, 0xE1))) g.FillRectangle(bar, r.X, r.Y, r.Width, TitleHeight);
        using (var font = new Font("Segoe UI Semibold", 13, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(Ink))
            g.DrawString(window.Title, font, ink, r.X + 14, r.Y + 9);
        foreach (var (i, color) in new[] { (0, Muted), (1, Muted), (2, Color.FromArgb(0xD9, 0x5B, 0x4F)) })
        {
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, r.Right - 72 + i * 22, r.Y + 13, 10, 10);
        }
        window.Draw(g, new RectangleF(r.X, r.Y + TitleHeight, r.Width, r.Height - TitleHeight), Seconds);
        g.Restore(state);
    }

    private void DrawTaskbar(Graphics g)
    {
        var screen = Screen;
        var bar = new RectangleF(screen.X, screen.Bottom - TaskbarHeight, screen.Width, TaskbarHeight);
        using (var fill = new SolidBrush(Color.FromArgb(214, 0x15, 0x18, 0x20))) g.FillRectangle(fill, bar);

        using (var mark = new SolidBrush(_accent)) g.FillPath(mark, Mountain(new RectangleF(bar.X + 22, bar.Y + 13, 30, 26)));
        var x = bar.X + 76;
        foreach (var window in _windows)
        {
            var pill = new RectangleF(x, bar.Y + 9, 148, 34);
            using var shape = Rounded(pill, 8);
            using (var fill = new SolidBrush(Color.FromArgb(window == _windows[^1] ? 60 : 26, Color.White))) g.FillPath(fill, shape);
            using var font = new Font("Segoe UI", 13, GraphicsUnit.Pixel);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(window.Title, font, Brushes.White, RectangleF.Inflate(pill, -10, 0), format);
            x += 156;
        }

        using var clock = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        using var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(DateTime.Now.ToString("t"), clock, Brushes.White, new RectangleF(bar.Right - 200, bar.Y, 180, bar.Height), right);
    }

    private static readonly string[] BuildLog =
    [
        "$ acme build --all", "restoring packages... done", "compiling anvil-core        ok  1.2s", "compiling rocket-skates     ok  3.8s",
        "compiling tiny-parachute    ok  0.9s", "compiling giant-magnet      ok  2.1s", "linking portable-hole       ok  0.4s",
        "running 1,204 tests", "  gravity          412 passed", "  cliffs           390 passed", "  birds            402 passed",
        "packaging acme-2.14.0.zip   ok", "uploading to staging        ok", "deployed in 41s. nothing exploded.", "",
    ];

    private void DrawTerminal(Graphics g, RectangleF r, float t)
    {
        using (var background = new SolidBrush(Color.FromArgb(0x14, 0x17, 0x1F))) g.FillRectangle(background, r);
        using var font = new Font("Consolas", 17, GraphicsUnit.Pixel);
        var rows = (int)((r.Height - 28) / 26);
        var shown = (int)(t * 1.6f);
        for (var row = 0; row < rows; row++)
        {
            var index = shown - rows + 1 + row;
            if (index < 0) continue;
            var line = BuildLog[index % BuildLog.Length];
            var color = line.StartsWith('$') ? Color.FromArgb(0x7D, 0xD3, 0xFC) : line.EndsWith("ok") || line.Contains("passed") || line.Contains(" ok ") ? Color.FromArgb(0x86, 0xEF, 0xAC) : Color.FromArgb(0xD8, 0xDC, 0xE4);
            using var brush = new SolidBrush(color);
            g.DrawString(line, font, brush, r.X + 18, r.Y + 14 + row * 26);
        }
    }

    private void DrawChart(Graphics g, RectangleF r, float t)
    {
        using var label = new Font("Segoe UI", 13, GraphicsUnit.Pixel);
        using var big = new Font("Segoe UI Semibold", 30, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        var stats = new[] { ("99.2%", "passing"), ("41s", "build time"), ("3", "open incidents") };
        for (var i = 0; i < stats.Length; i++)
        {
            var x = r.X + 22 + i * (r.Width - 30) / 3;
            g.DrawString(stats[i].Item1, big, ink, x, r.Y + 12);
            g.DrawString(stats[i].Item2, label, muted, x + 2, r.Y + 54);
        }

        var plot = new RectangleF(r.X + 22, r.Y + 92, r.Width - 44, r.Height - 112);
        var points = new List<PointF>();
        for (var i = 0; i <= 48; i++)
        {
            var x = i / 48f;
            var y = 0.5f + 0.24f * MathF.Sin(x * 7 + t * 0.7f) + 0.12f * MathF.Sin(x * 17 - t * 0.4f);
            points.Add(new PointF(plot.X + x * plot.Width, plot.Bottom - y * plot.Height));
        }
        using var area = new GraphicsPath();
        area.AddLines(points.ToArray());
        area.AddLine(plot.Right, plot.Bottom, plot.X, plot.Bottom);
        area.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(46, _accent))) g.FillPath(fill, area);
        using var line = new Pen(_accent, 3);
        g.DrawLines(line, points.ToArray());
    }

    private void DrawNotes(Graphics g, RectangleF r, float t)
    {
        using var head = new Font("Segoe UI Semibold", 22, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        g.DrawString("Acme 2.14", head, ink, r.X + 22, r.Y + 16);
        var lines = new[] { "Anvils now fall 12% more accurately.", "Rocket skates ship with brakes.", "Portable holes no longer leak.", "Fixed: gravity only applied after looking down." };
        for (var i = 0; i < lines.Length; i++) g.DrawString("•  " + lines[i], body, muted, r.X + 24, r.Y + 62 + i * 30);
    }

    private void DrawSheet(Graphics g, RectangleF r, float t)
    {
        string[] columns = ["Division", "Budget", "Actual", "Variance"];
        (string, int, int)[] rows =
        [
            ("Anvils", 412, 398), ("Rocket skates", 288, 301), ("Portable holes", 96, 92), ("Giant magnets", 174, 181),
            ("Tiny parachutes", 58, 57), ("Dynamite (decorative)", 31, 29), ("Bird seed", 22, 26), ("Disguises", 64, 61),
            ("Tunnel paint", 18, 18), ("Springs", 47, 44), ("Catapults", 133, 140),
        ];
        var widths = new[] { 0.46f, 0.18f, 0.18f, 0.18f };
        var rowHeight = (r.Height - 8) / (rows.Length + 2);
        using var font = new Font("Segoe UI", 16, GraphicsUnit.Pixel);
        using var bold = new Font("Segoe UI Semibold", 16, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink);
        using var line = new Pen(Color.FromArgb(0xDD, 0xDC, 0xD8));
        using var head = new SolidBrush(Color.FromArgb(36, _accent));
        g.FillRectangle(head, r.X, r.Y, r.Width, rowHeight);

        void Row(int index, string[] cells, Font f, Color last)
        {
            var y = r.Y + index * rowHeight;
            var x = r.X + 16;
            for (var c = 0; c < cells.Length; c++)
            {
                using var brush = new SolidBrush(c == 3 ? last : Ink);
                g.DrawString(cells[c], f, brush, x, y + (rowHeight - 22) / 2);
                x += widths[c] * (r.Width - 24);
            }
            g.DrawLine(line, r.X, y + rowHeight, r.Right, y + rowHeight);
        }

        Row(0, columns, bold, Ink);
        for (var i = 0; i < rows.Length; i++)
        {
            var (name, budget, actual) = rows[i];
            var variance = budget - actual;
            Row(i + 1, [name, $"${budget}k", $"${actual}k", $"{variance:+0;-0;0}k"], font, variance >= 0 ? Color.FromArgb(0x15, 0x80, 0x3D) : Color.FromArgb(0xC0, 0x39, 0x2B));
        }
        var b = rows.Sum(x => x.Item2);
        var a = rows.Sum(x => x.Item3);
        Row(rows.Length + 1, ["Total", $"${b}k", $"${a}k", $"{b - a:+0;-0;0}k"], bold, Ink);
    }

    private void DrawCanvas(Graphics g, RectangleF r, float t)
    {
        using (var background = new SolidBrush(Color.FromArgb(0xEC, 0xEB, 0xE7))) g.FillRectangle(background, r);
        var board = RectangleF.Inflate(r, -70, -60);
        using (var sky = new LinearGradientBrush(board, Color.FromArgb(0xFF, 0xB0, 0x5C), _from, 90f)) g.FillRectangle(sky, board);
        var outside = g.Clip;
        g.IntersectClip(board);
        using (var sun = new SolidBrush(Color.FromArgb(0xFF, 0xF1, 0xC9)))
            g.FillEllipse(sun, board.X + board.Width * 0.58f, board.Y + 60 + MathF.Sin(t * 0.5f) * 8, 150, 150);
        using (var ground = new SolidBrush(_to)) g.FillPath(ground, Mountain(new RectangleF(board.X - 40, board.Y + board.Height * 0.42f, board.Width * 0.9f, board.Height * 0.58f)));
        using (var ground = new SolidBrush(Color.FromArgb(200, _to))) g.FillPath(ground, Mountain(new RectangleF(board.X + board.Width * 0.42f, board.Y + board.Height * 0.55f, board.Width * 0.6f, board.Height * 0.45f)));
        using var head = new Font("Segoe UI Semibold", 44, GraphicsUnit.Pixel);
        g.DrawString("Spring at Acme", head, Brushes.White, board.X + 34, board.Y + 30);
        g.Clip = outside;
    }

    private void DrawLayers(Graphics g, RectangleF r, float t)
    {
        using var font = new Font("Segoe UI", 15, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Ink);
        string[] layers = ["Headline", "Sun", "Mountains, near", "Mountains, far", "Sky", "Background"];
        for (var i = 0; i < layers.Length; i++)
        {
            var y = r.Y + 14 + i * 50;
            if (i == 1)
            {
                using var selected = Rounded(new RectangleF(r.X + 10, y - 6, r.Width - 20, 44), 8);
                using var fill = new SolidBrush(Color.FromArgb(40, _accent));
                g.FillPath(fill, selected);
            }
            using (var swatch = new SolidBrush(Color.FromArgb(0xD8 - i * 22, _accent))) g.FillRectangle(swatch, r.X + 24, y + 2, 28, 28);
            g.DrawString(layers[i], font, ink, r.X + 66, y + 6);
        }
    }

    private void DrawAppPage(Graphics g, RectangleF r, float t)
    {
        using (var band = new SolidBrush(_accent)) g.FillRectangle(band, r.X, r.Y, r.Width, 96);
        using var head = new Font("Segoe UI Semibold", 38, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 24, GraphicsUnit.Pixel);
        using var muted = new SolidBrush(Muted);
        g.DrawString(_title, head, Brushes.White, r.X + 48, r.Y + 22);
        g.DrawString("A pretend app, on a pretend server.", body, muted, r.X + 48, r.Y + 140);
    }

    private static GraphicsPath Mountain(RectangleF r)
    {
        var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(r.X, r.Bottom), new PointF(r.X + r.Width / 2, r.Y), new PointF(r.Right, r.Bottom) });
        return path;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
