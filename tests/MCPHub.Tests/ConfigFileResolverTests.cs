using MCPHub.Core.Catalog;
using Xunit;

namespace MCPHub.Tests;

public class ConfigFileResolverTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "mcphub-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigFileResolverTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string name, string content = "{}")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("remote_admin_windows_servers.json", "remote_admin_windows_servers.example.json")]
    [InlineData("RemoteAdminMCPSharp.json", "RemoteAdminMCPSharp.example.json")]
    public void Example_name_inserts_the_marker_before_the_extension(string file, string expected)
    {
        Assert.Equal(expected, ConfigFileResolver.ExampleFileNameFor(file));
    }

    [Fact]
    public void Existing_file_is_returned_untouched()
    {
        Write("remote_admin_linux_servers.json", "{\"real\":true}");

        var result = ConfigFileResolver.Resolve(_folder, "remote_admin_linux_servers.json");

        Assert.Equal(ConfigFileOutcome.Existing, result.Outcome);
        Assert.True(result.Exists);
        Assert.Equal("{\"real\":true}", File.ReadAllText(result.Path!));
    }

    [Fact]
    public void Missing_file_is_created_by_renaming_the_example()
    {
        Write("remote_admin_windows_servers.example.json", "{\"template\":true}");

        var result = ConfigFileResolver.Resolve(_folder, "remote_admin_windows_servers.json");

        Assert.Equal(ConfigFileOutcome.CreatedFromExample, result.Outcome);
        Assert.Equal(Path.Combine(_folder, "remote_admin_windows_servers.json"), result.Path);

        // Renamed, not copied: the template is consumed and its content carried over.
        Assert.True(File.Exists(result.Path!));
        Assert.False(File.Exists(Path.Combine(_folder, "remote_admin_windows_servers.example.json")));
        Assert.Equal("{\"template\":true}", File.ReadAllText(result.Path!));
    }

    [Fact]
    public void A_real_file_wins_over_a_template_and_the_template_survives()
    {
        // Both present: the user's file must be opened and the template left alone, or a reinstall
        // that re-ships the example would silently eat real inventory.
        Write("remote_admin_windows_servers.json", "{\"real\":true}");
        Write("remote_admin_windows_servers.example.json", "{\"template\":true}");

        var result = ConfigFileResolver.Resolve(_folder, "remote_admin_windows_servers.json");

        Assert.Equal(ConfigFileOutcome.Existing, result.Outcome);
        Assert.Equal("{\"real\":true}", File.ReadAllText(result.Path!));
        Assert.True(File.Exists(Path.Combine(_folder, "remote_admin_windows_servers.example.json")));
    }

    [Fact]
    public void Neither_file_nor_template_resolves_to_missing()
    {
        var result = ConfigFileResolver.Resolve(_folder, "nothing_here.json");

        Assert.Equal(ConfigFileOutcome.Missing, result.Outcome);
        Assert.False(result.Exists);
        Assert.Null(result.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_names_resolve_to_missing_rather_than_throwing(string fileName)
    {
        Assert.Equal(ConfigFileOutcome.Missing, ConfigFileResolver.Resolve(_folder, fileName).Outcome);
    }

    [Fact]
    public void Resolve_does_not_create_the_folder_or_throw_when_it_is_absent()
    {
        var absent = Path.Combine(_folder, "does-not-exist");

        var result = ConfigFileResolver.Resolve(absent, "whatever.json");

        Assert.Equal(ConfigFileOutcome.Missing, result.Outcome);
        Assert.False(Directory.Exists(absent));
    }

    [Theory]
    [InlineData("remote_admin_windows_servers.example.json", true)]
    [InlineData("remote_admin_windows_servers.json", false)]
    public void Example_names_are_recognised(string fileName, bool expected)
    {
        Assert.Equal(expected, ConfigFileResolver.IsExampleFileName(fileName));
    }

    [Theory]
    [InlineData("RemoteAdminMCPSharp.json", "RemoteAdminMCPSharp", "Main configuration")]
    [InlineData("remote_admin_windows_servers.json", "RemoteAdminMCPSharp", "Windows servers")]
    [InlineData("remote_admin_linux_servers.json", "RemoteAdminMCPSharp", "Linux servers")]
    public void Menu_labels_drop_the_product_prefix(string fileName, string serviceName, string expected)
    {
        Assert.Equal(expected, ConfigFileResolver.DescribeFileName(fileName, serviceName));
    }

    [Fact]
    public void Remote_admin_is_the_catalogs_multi_config_product()
    {
        var entry = ServiceCatalog.FindByName("RemoteAdminMCPSharp")!;

        Assert.True(entry.HasExtraConfigFiles);
        Assert.Equal(
            ["RemoteAdminMCPSharp.json", "remote_admin_windows_servers.json", "remote_admin_linux_servers.json"],
            entry.AllConfigFileNames);
    }

    [Fact]
    public void Every_other_product_has_a_single_config_file()
    {
        var multi = ServiceCatalog.All.Where(e => e.HasExtraConfigFiles).Select(e => e.Name).ToList();
        Assert.Equal(["RemoteAdminMCPSharp"], multi);
    }
}
