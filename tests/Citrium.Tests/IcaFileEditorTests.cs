using Citrium.Viewer;
using Xunit;

namespace Citrium.Tests;

public sealed class IcaFileEditorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"citrium-test-{Guid.NewGuid():N}.ica");

    private const string Launch = """
        [WFClient]
        Version=2
        ClientAudio=Off

        [ApplicationServers]
        Finance Desktop=

        [Finance Desktop]
        Address=abc
        ClientAudio=Off

        [Thinwire3.0]
        Tw2CachePower=22
        """;

    public IcaFileEditorTests() => File.WriteAllText(_path, Launch);

    public void Dispose() => File.Delete(_path);

    /// <summary>The value of a key in one section, the way the engine reads it.</summary>
    private string? Read(string section, string key)
    {
        var current = "";
        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { current = line.Trim('[', ']'); continue; }
            if (current == section && line.StartsWith(key + "=")) return line[(key.Length + 1)..];
        }
        return null;
    }

    [Fact]
    public void ReplacesAValueWhereItIs()
    {
        IcaFileEditor.SetValue(_path, "WFClient", "ClientAudio", "On");

        Assert.Equal("On", Read("WFClient", "ClientAudio"));
        Assert.Equal("Off", Read("Finance Desktop", "ClientAudio"));
    }

    [Fact]
    public void AddsAValueToAnExistingSection()
    {
        IcaFileEditor.SetValue(_path, "WFClient", "EnableH265", "1");

        Assert.Equal("1", Read("WFClient", "EnableH265"));
        Assert.Null(Read("Finance Desktop", "EnableH265"));
    }

    [Fact]
    public void AddsAMissingSection()
    {
        IcaFileEditor.SetValue(_path, "Brand New", "Key", "Value");
        Assert.Equal("Value", Read("Brand New", "Key"));
    }

    [Fact]
    public void DoesNotMistakeAKeyForOneThatStartsTheSame()
    {
        IcaFileEditor.SetValue(_path, "WFClient", "Version", "3");
        IcaFileEditor.SetValue(_path, "WFClient", "Ver", "9");

        Assert.Equal("3", Read("WFClient", "Version"));
        Assert.Equal("9", Read("WFClient", "Ver"));
    }

    [Fact]
    public void SessionValuesAlsoBeatTheStoresPerResourceValue()
    {
        IcaFileEditor.SetSessionValue(_path, "ClientAudio", "On");

        Assert.Equal("On", Read("WFClient", "ClientAudio"));
        Assert.Equal("On", Read("Finance Desktop", "ClientAudio"));
    }

    [Fact]
    public void SessionValuesLeaveOtherSectionsAlone()
    {
        IcaFileEditor.SetSessionValue(_path, "ClientAudio", "On");
        Assert.Null(Read("Thinwire3.0", "ClientAudio"));
    }

    [Fact]
    public void RemovesAValue()
    {
        IcaFileEditor.RemoveValue(_path, "Thinwire3.0", "Tw2CachePower");
        Assert.Null(Read("Thinwire3.0", "Tw2CachePower"));
    }

    [Fact]
    public void RemovingWhatIsNotThereChangesNothing()
    {
        var before = File.ReadAllText(_path);
        IcaFileEditor.RemoveValue(_path, "Nowhere", "Nothing");
        Assert.Equal(before, File.ReadAllText(_path));
    }
}
