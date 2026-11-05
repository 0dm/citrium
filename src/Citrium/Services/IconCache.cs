using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Citrium.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Citrium.Services;

internal static class IconCache
{
    private static readonly SemaphoreSlim Throttle = new(6);

    private static string FileFor(string accountId, string iconUrl)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(accountId + "|" + iconUrl)));
        return Path.Combine(AppPaths.Icons, hash + ".img");
    }

    /// <summary>Loads the icon for <paramref name="resource"/>. Must be called on the UI thread.</summary>
    public static async Task LoadAsync(StoreResource resource, StoreFrontClient? client)
    {
        if (resource.IconUrl is null) return;
        var file = FileFor(resource.AccountId, resource.IconUrl);

        byte[]? bytes = null;
        if (File.Exists(file))
        {
            try { bytes = await File.ReadAllBytesAsync(file); } catch { }
        }
        else if (client is not null)
        {
            await Throttle.WaitAsync();
            try { bytes = await client.GetIconAsync(AppendSize(resource.IconUrl)); }
            finally { Throttle.Release(); }
            if (bytes is { Length: > 0 })
            {
                try { await File.WriteAllBytesAsync(file, bytes); } catch { }
            }
        }

        if (bytes is not { Length: > 0 }) return;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bmp = new BitmapImage { DecodePixelWidth = 96 };
            await bmp.SetSourceAsync(stream);
            resource.Icon = bmp;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not decode icon for {resource.Name}", ex);
        }
    }

    // StoreFront serves icons at several sizes; ask for a crisp one when the URL allows it.
    private static string AppendSize(string url) =>
        url.Contains("size=", StringComparison.OrdinalIgnoreCase) ? url : url + (url.Contains('?') ? "&" : "?") + "size=128";

    public static void Clear()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Icons)) File.Delete(f);
        }
        catch { }
    }
}
