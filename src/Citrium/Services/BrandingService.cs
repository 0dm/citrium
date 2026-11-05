using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Citrium.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Citrium.Services;

public sealed record AccountLogo(string LightPath, string DarkPath);

/// <summary>
/// Fetches a store's branding (the logo the Citrix web portal shows) and prepares it for Citrium:
/// a white background is turned into transparency, empty margins are trimmed, and single-colour logos get a
/// variant that stays visible on the other theme (e.g. a black wordmark becomes white in dark mode).
/// </summary>
internal static class BrandingService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string Dir => Directory.CreateDirectory(Path.Combine(AppPaths.Root, "Branding")).FullName;

    public static AccountLogo? Cached(Account account)
    {
        var light = Directory.EnumerateFiles(Dir, $"{account.Id}-*-light.png").FirstOrDefault();
        if (light is null) return null;
        var dark = light[..^"light.png".Length] + "dark.png";
        return File.Exists(dark) ? new AccountLogo(light, dark) : null;
    }

    public static async Task<AccountLogo?> RefreshAsync(Account account)
    {
        if (account.StoreWebUrl is null) return Cached(account);
        try
        {
            var logoUrl = await GetLogoUrlAsync(new Uri(account.StoreWebUrl));
            if (logoUrl is null) return Cached(account);

            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(logoUrl)))[..12];
            var light = Path.Combine(Dir, $"{account.Id}-{key}-light.png");
            var dark = Path.Combine(Dir, $"{account.Id}-{key}-dark.png");
            if (File.Exists(light) && File.Exists(dark)) return new AccountLogo(light, dark);

            var bytes = await Http.GetByteArrayAsync(logoUrl);
            var (lightPng, darkPng) = await PrepareAsync(bytes);
            foreach (var old in Directory.EnumerateFiles(Dir, $"{account.Id}-*.png")) File.Delete(old);
            await File.WriteAllBytesAsync(light, lightPng);
            await File.WriteAllBytesAsync(dark, darkPng);
            Log.Info($"Prepared organization logo for {account.Name}");
            return new AccountLogo(light, dark);
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't load branding for {account.Name}", ex);
            return Cached(account);
        }
    }

    public static void Delete(Account account)
    {
        try { foreach (var f in Directory.EnumerateFiles(Dir, $"{account.Id}-*.png")) File.Delete(f); } catch { }
    }

    /// <summary>The store's public client configuration (no sign-in needed) carries the branding logos.</summary>
    private static async Task<string?> GetLogoUrlAsync(Uri storeWeb)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(storeWeb, "Home/Configuration")) { Content = new StringContent("") };
        req.Headers.Add("X-Citrix-IsUsingHTTPS", storeWeb.Scheme == "https" ? "Yes" : "No");
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var branding = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "branding");
        string? Attr(string n) => branding?.Attribute(n)?.Value is { Length: > 0 } v ? v : null;
        var url = Attr("headerLogo") ?? Attr("logonLogo");
        return url is null ? null : new Uri(storeWeb, url).ToString(); // may be relative to the store
    }

    private static async Task<(byte[] Light, byte[] Dark)> PrepareAsync(byte[] source)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var w = (int)decoder.PixelWidth;
        var h = (int)decoder.PixelHeight;
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = data.DetachPixelData();

        if (HasWhiteBackground(px, w, h)) WhiteToAlpha(px);
        (px, w, h) = Trim(px, w, h);

        var (luma, saturation) = Analyze(px);
        var light = (byte[])px.Clone();
        var dark = (byte[])px.Clone();
        // Single-colour logos get recoloured for the theme they'd vanish on; colourful logos are left as they are.
        if (saturation < 0.25 && luma < 0.45) Recolor(dark, 255, 255, 255);
        else if (saturation < 0.25 && luma > 0.75) Recolor(light, 26, 26, 26);

        return (await EncodeAsync(light, w, h), await EncodeAsync(dark, w, h));
    }

    /// <summary>True when the image border is (almost all) opaque near-white: a logo pasted on a white canvas.</summary>
    private static bool HasWhiteBackground(byte[] px, int w, int h)
    {
        int white = 0, total = 0;
        void Sample(int x, int y)
        {
            var i = (y * w + x) * 4;
            total++;
            if (px[i + 3] > 200 && px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235) white++;
        }
        for (var x = 0; x < w; x++) { Sample(x, 0); Sample(x, h - 1); }
        for (var y = 0; y < h; y++) { Sample(0, y); Sample(w - 1, y); }
        return total > 0 && white >= total * 0.9;
    }

    /// <summary>
    /// "Colour to alpha" against white: every pixel becomes the most transparent colour that, composited over
    /// white, reproduces the original. Anti-aliased edges stay smooth instead of getting a white fringe.
    /// </summary>
    private static void WhiteToAlpha(byte[] px)
    {
        for (var i = 0; i < px.Length; i += 4)
        {
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0, a0 = px[i + 3] / 255.0;
            var a = Math.Max(1 - r, Math.Max(1 - g, 1 - b));
            if (a <= 0.004) { px[i + 3] = 0; continue; }
            px[i] = ToByte((b - (1 - a)) / a);
            px[i + 1] = ToByte((g - (1 - a)) / a);
            px[i + 2] = ToByte((r - (1 - a)) / a);
            px[i + 3] = ToByte(a * a0);
        }
    }

    private static (byte[] Px, int W, int H) Trim(byte[] px, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return (px, w, h);
        int nw = maxX - minX + 1, nh = maxY - minY + 1;
        var outPx = new byte[nw * nh * 4];
        for (var y = 0; y < nh; y++)
            System.Buffer.BlockCopy(px, ((minY + y) * w + minX) * 4, outPx, y * nw * 4, nw * 4);
        return (outPx, nw, nh);
    }

    private static (double Luma, double Saturation) Analyze(byte[] px)
    {
        double lum = 0, sat = 0, weight = 0;
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3] / 255.0;
            if (a < 0.1) continue;
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            lum += a * (0.2126 * r + 0.7152 * g + 0.0722 * b);
            sat += a * (max == 0 ? 0 : (max - min) / max);
            weight += a;
        }
        return weight == 0 ? (0.5, 0) : (lum / weight, sat / weight);
    }

    private static void Recolor(byte[] px, byte r, byte g, byte b)
    {
        for (var i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; }
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static async Task<byte[]> EncodeAsync(byte[] px, int w, int h)
    {
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)w, (uint)h, 96, 96, px);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}
