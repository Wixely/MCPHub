using MCPHub.Core.Process;
using Xunit;

namespace MCPHub.Tests;

public class ServerConfigReaderTests
{
    [Fact]
    public void Reads_numeric_port_and_host()
    {
        var path = WriteTemp("""{ "Server": { "Host": "localhost", "Port": 5710 } }""");
        try
        {
            Assert.Equal(5710, ServerConfigReader.ReadPort(path));
            Assert.Equal("localhost", ServerConfigReader.ReadHost(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reads_string_port()
    {
        var path = WriteTemp("""{ "Server": { "Port": "5712" } }""");
        try { Assert.Equal(5712, ServerConfigReader.ReadPort(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        var path = Path.Combine(Path.GetTempPath(), "mcphub-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Null(ServerConfigReader.ReadPort(path));
    }

    [Fact]
    public void Missing_server_section_returns_null()
    {
        var path = WriteTemp("""{ "Noteworthy": { "ReadOnly": true } }""");
        try { Assert.Null(ServerConfigReader.ReadPort(path)); }
        finally { File.Delete(path); }
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcphub-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
