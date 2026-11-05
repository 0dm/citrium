using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Security.Cryptography.DataProtection;

namespace Citrium.Services;

/// <summary>
/// Persists a store's session cookies between runs, encrypted with DPAPI for the current Windows user,
/// so that Citrium can reuse a still-valid StoreFront session without showing the sign-in window.
/// </summary>
internal static class CookieVault
{
    private static string FileFor(string accountId) => Path.Combine(AppPaths.Sessions, accountId + ".bin");

    public static async Task SaveAsync(string accountId, IEnumerable<Cookie> cookies)
    {
        try
        {
            var dtos = cookies.Select(c => new CookieDto(c.Name, c.Value, c.Domain, c.Path, c.Secure, c.HttpOnly, c.Expires)).ToList();
            var plain = JsonSerializer.SerializeToUtf8Bytes(dtos, JsonContext.Default.ListCookieDto);
            var provider = new DataProtectionProvider("LOCAL=user");
            var protectedBuffer = await provider.ProtectAsync(plain.AsBuffer());
            await File.WriteAllBytesAsync(FileFor(accountId), protectedBuffer.ToArray());
        }
        catch (Exception ex)
        {
            Log.Error("Failed to persist cookies", ex);
        }
    }

    public static async Task<List<Cookie>> LoadAsync(string accountId)
    {
        var file = FileFor(accountId);
        if (!File.Exists(file)) return [];
        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var provider = new DataProtectionProvider();
            var plain = await provider.UnprotectAsync(bytes.AsBuffer());
            var dtos = JsonSerializer.Deserialize(plain.ToArray(), JsonContext.Default.ListCookieDto) ?? [];
            return dtos
                .Where(d => d.Expires == DateTime.MinValue || d.Expires > DateTime.Now)
                .Select(d => new Cookie(d.Name, d.Value, d.Path, d.Domain) { Secure = d.Secure, HttpOnly = d.HttpOnly, Expires = d.Expires })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load cookies", ex);
            return [];
        }
    }

    public static void Delete(string accountId)
    {
        try { File.Delete(FileFor(accountId)); } catch { /* ignore */ }
    }
}
