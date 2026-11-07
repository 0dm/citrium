using System.Text.Json.Serialization;
using Citrium.Models;

namespace Citrium.Services;

/// <summary>Source-generated JSON metadata, so (de)serialization survives trimming in Release builds.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ViewerSettings))]
[JsonSerializable(typeof(Placements))]
[JsonSerializable(typeof(List<CookieDto>))]
internal sealed partial class JsonContext : JsonSerializerContext;

internal sealed record CookieDto(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, DateTime Expires);
