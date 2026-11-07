using Citrium.DemoMachine;

// Stands in for Citrix's engine in a demo build. Like the engine, it's started with a launch file, and it shows a
// frame titled "<name> - Desktop Viewer" around a child window, the session surface, which Citrium adopts. So a
// demo goes through the same code as a real session.
var settings = File.ReadAllLines(args[0])
    .Select(line => line.Split('=', 2))
    .Where(parts => parts.Length == 2)
    .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

var title = settings.GetValueOrDefault("Title", "Demo");
var isDesktop = settings.GetValueOrDefault("Desktop") == "On";
var scene = settings.GetValueOrDefault("Scene", "");

ApplicationConfiguration.Initialize();
using var frame = new Form
{
    Text = isDesktop ? $"{title} - Desktop Viewer" : title,
    ClientSize = isDesktop ? new Size(1280, 720) : new Size(760, 480),
    StartPosition = FormStartPosition.CenterScreen,
};
frame.Controls.Add(new MachineSurface(title, scene, isDesktop) { Dock = DockStyle.Fill });
Application.Run(frame);
