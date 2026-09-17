using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPHub.Core.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public sealed class RouterDeploymentTests
{
    [Fact]
    public void Portable_read_only_config_resolves_files_without_desktop_storage()
    {
        using var f = new Fixture();
        File.SetAttributes(f.Path, FileAttributes.ReadOnly);
        var source = new RouterDeploymentSource(f.Path, _ => null);
        var route = source.Resolve(Fixture.InputKey)!;
        Assert.Equal("local", route.Output!.Id);
        Assert.Equal("provider-key-one", route.ReadApiKey!());
        Assert.DoesNotContain("provider-key-one", File.ReadAllText(f.Path));
        Assert.DoesNotContain(Fixture.InputKey, File.ReadAllText(f.Path));
        Assert.Equal(3, Directory.GetFiles(f.DirectoryPath).Length);
        Assert.Null(source.LoadError);
    }

    [Fact]
    public void Environment_credentials_and_port_override_work_without_files()
    {
        using var f = new Fixture();
        f.Config = f.Config with
        {
            Inputs = [f.Config.Inputs[0] with { KeyFile = null, KeyEnvironmentVariable = "INPUT_TOKEN" }],
            Outputs = [f.Config.Outputs[0] with { ApiKeyFile = null, ApiKeyEnvironmentVariable = "OUTPUT_TOKEN" }],
        };
        f.Save();
        var env = new Dictionary<string, string> { ["INPUT_TOKEN"] = Fixture.InputKey, ["OUTPUT_TOKEN"] = "provider-env-key", ["MCPHUB_ROUTER_PORT"] = "15801" };
        var source = new RouterDeploymentSource(f.Path, name => env.GetValueOrDefault(name));
        Assert.Equal(15801, source.Snapshot.Port);
        Assert.Equal("provider-env-key", source.Resolve(Fixture.InputKey)!.ReadApiKey!());
    }

    [Fact]
    public void Reload_captures_routes_and_credentials_atomically()
    {
        using var f = new Fixture();
        var source = new RouterDeploymentSource(f.Path, _ => null);
        var oldRoute = source.Resolve(Fixture.InputKey)!;
        File.WriteAllText(System.IO.Path.Combine(f.DirectoryPath, "output.key"), "provider-key-two\n");
        f.Config = f.Config with { Outputs = [f.Config.Outputs[0] with { Model = "new-model" }] };
        f.Save();
        Assert.True(source.Reload());
        Assert.Equal("provider-key-one", oldRoute.ReadApiKey!());
        Assert.Equal("old-model", oldRoute.Output!.Model);
        var newRoute = source.Resolve(Fixture.InputKey)!;
        Assert.Equal("provider-key-two", newRoute.ReadApiKey!());
        Assert.Equal("new-model", newRoute.Output!.Model);
        var rotated = new string('b', 64);
        File.WriteAllText(System.IO.Path.Combine(f.DirectoryPath, "input.key"), rotated);
        Assert.True(source.Reload());
        Assert.Null(source.Resolve(Fixture.InputKey));
        Assert.NotNull(source.Resolve(rotated));
    }

    [Fact]
    public void Invalid_reload_retains_last_valid_routes_and_recovers()
    {
        using var f = new Fixture();
        var source = new RouterDeploymentSource(f.Path, _ => null);
        File.WriteAllText(f.Path, "not-json-private-content");
        Assert.False(source.Reload());
        Assert.NotNull(source.ReloadError);
        Assert.DoesNotContain("private-content", source.ReloadError);
        Assert.NotNull(source.Resolve(Fixture.InputKey));
        f.Save();
        Assert.True(source.Reload());
        Assert.Null(source.ReloadError);
        f.Config = f.Config with { Port = 15802 };
        f.Save();
        Assert.False(source.Reload());
        Assert.Equal(5801, source.Snapshot.Port);
    }

    [Fact]
    public void Missing_secret_never_silently_turns_output_authentication_off()
    {
        using var f = new Fixture();
        var source = new RouterDeploymentSource(f.Path, _ => null);
        File.Delete(System.IO.Path.Combine(f.DirectoryPath, "output.key"));
        Assert.False(source.Reload());
        Assert.Equal("provider-key-one", source.Resolve(Fixture.InputKey)!.ReadApiKey!());
        var ex = Assert.Throws<InvalidOperationException>(() => new RouterDeploymentSource(f.Path, _ => null));
        Assert.DoesNotContain(f.DirectoryPath, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Input_hashes_are_supported_and_ambiguous_secret_sources_are_rejected()
    {
        using var f = new Fixture();
        f.Config = f.Config with { Inputs = [f.Config.Inputs[0] with { KeyFile = null, KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Fixture.InputKey))) }] };
        f.Save();
        Assert.NotNull(new RouterDeploymentSource(f.Path, _ => null).Resolve(Fixture.InputKey));
        f.Config = f.Config with { Inputs = [f.Config.Inputs[0] with { KeyFile = "input.key" }] };
        f.Save();
        Assert.Throws<InvalidOperationException>(() => new RouterDeploymentSource(f.Path, _ => null));
    }

    [Fact]
    public void Duplicate_input_keys_and_desktop_config_fields_are_rejected()
    {
        using var f = new Fixture();
        f.Config = f.Config with { Inputs = [f.Config.Inputs[0], f.Config.Inputs[0] with { Id = "second", Name = "Second" }] };
        f.Save();
        Assert.Throws<InvalidOperationException>(() => new RouterDeploymentSource(f.Path, _ => null));
        File.WriteAllText(f.Path, "{\"StartOnLaunch\":true,\"Outputs\":[],\"Inputs\":[]}");
        Assert.Throws<InvalidOperationException>(() => new RouterDeploymentSource(f.Path, _ => null));
    }

    [Fact]
    public async Task Wildcard_listener_uses_deployment_source_and_requires_authentication()
    {
        using var f = new Fixture();
        var source = new RouterDeploymentSource(f.Path, _ => null);
        await using var router = new RouterHost(source, NullLogger<RouterHost>.Instance, new() { BindAddress = "0.0.0.0" });
        await router.StartAsync(0);
        Assert.Equal("0.0.0.0", router.BindAddress);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{router.Port}/v1/"), Timeout = TimeSpan.FromSeconds(10) };
        using var anonymous = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Fixture.InputKey);
        f.Config = f.Config with { DefaultOutputId = null };
        f.Save();
        Assert.True(source.Reload());
        using var authenticated = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, authenticated.StatusCode);
        f.Config = f.Config with { Inputs = [f.Config.Inputs[0] with { Enabled = false }] };
        f.Save();
        Assert.True(source.Reload());
        using var revoked = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        public const string InputKey = "test-input-key-with-at-least-32-characters";
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcphub-deployment-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "router.json");
        public RouterDeploymentConfiguration Config { get; set; } = new()
        {
            DefaultOutputId = "local",
            Inputs = [new() { Id = "agent", Name = "Agent", KeyFile = "input.key" }],
            Outputs = [new() { Id = "local", Name = "Local", BaseUrl = "http://localhost:1/v1", Model = "old-model", ApiKeyFile = "output.key" }],
        };
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(System.IO.Path.Combine(DirectoryPath, "input.key"), InputKey);
            File.WriteAllText(System.IO.Path.Combine(DirectoryPath, "output.key"), "provider-key-one");
            Save();
        }
        public void Save() => File.WriteAllText(Path, JsonSerializer.Serialize(Config));
        public void Dispose()
        {
            if (File.Exists(Path)) File.SetAttributes(Path, FileAttributes.Normal);
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
