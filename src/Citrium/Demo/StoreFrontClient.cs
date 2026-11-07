using System.Net;
using Citrium.Demo;
using Citrium.Models;

namespace Citrium.Services;

public sealed class StoreAuthRequiredException(string message) : Exception(message);

/// <summary>Takes the place of the real client in a demo build: the same calls, answered by <see cref="DemoWorld"/>.</summary>
public sealed class StoreFrontClient(Account account, IEnumerable<Cookie> cookies) : IDisposable
{
    public Account Account { get; } = account;
    public IEnumerable<Cookie> Cookies { get; } = cookies;

    public async Task<List<StoreResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        await Task.Delay(250, ct);
        return DemoWorld.ResourcesFor(Account);
    }

    public async Task<string> GetIcaFileAsync(StoreResource resource, CancellationToken ct = default)
    {
        await Task.Delay(300, ct);
        return DemoWorld.LaunchFileFor(resource);
    }

    public Task<byte[]?> GetIconAsync(string iconUrl, CancellationToken ct = default) => DemoWorld.IconAsync(iconUrl, ct);

    public Task LogoffAsync() => Task.CompletedTask;

    public void Dispose() { }
}
