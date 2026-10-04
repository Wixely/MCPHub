using System.Net;
using System.Text.Json;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The Router as a headless container reads it: two mounted documents, one saying who holds which key
/// and one saying where each user's requests go. They were one document until users existed, which is
/// why a caller needed a Router key of its own and a second one for the proxy.
/// </summary>
public sealed class RouterDeploymentTests
{
    [Fact]
    public void Portable_read_only_config_resolves_files_without_desktop_storage()
    {
        using var f = new Fixture();
        File.SetAttributes(f.Path, FileAttributes.ReadOnly);
        var source = f.Source();
        var route = source.Resolve(Fixture.UserKey)!;
        Assert.Equal("local", route.Output!.Id);
        Assert.Equal("agent", route.UserId);
        Assert.Equal("provider-key-one", route.ReadApiKey!());
        Assert.DoesNotContain("provider-key-one", File.ReadAllText(f.Path));
        Assert.DoesNotContain(Fixture.UserKey, File.ReadAllText(f.Path));
        Assert.DoesNotContain(Fixture.UserKey, File.ReadAllText(f.UsersPath));
        Assert.Null(source.LoadError);
    }

    [Fact]
    public void Environment_credentials_and_port_override_work_without_files()
    {
        using var f = new Fixture();
        f.Users = f.Users with
        {
            Users = [f.Users.Users[0] with { KeyFile = null, KeyEnvironmentVariable = "INPUT_TOKEN" }],
        };
        f.Config = f.Config with
        {
            Outputs = [f.Config.Outputs[0] with { ApiKeyFile = null, ApiKeyEnvironmentVariable = "OUTPUT_TOKEN" }],
        };
        f.Save();

        var env = new Dictionary<string, string>
        {
            ["INPUT_TOKEN"] = Fixture.UserKey,
            ["OUTPUT_TOKEN"] = "provider-env-key",
            ["MCPHUB_ROUTER_PORT"] = "15801",
        };
        var source = f.Source(name => env.GetValueOrDefault(name));

        Assert.Equal(15801, source.Snapshot.Port);
        Assert.Equal("provider-env-key", source.Resolve(Fixture.UserKey)!.ReadApiKey!());
    }

    [Fact]
    public void Reload_captures_routes_and_credentials_atomically()
    {
        using var f = new Fixture();
        var source = f.Source();
        var oldRoute = source.Resolve(Fixture.UserKey)!;

        File.WriteAllText(Path.Combine(f.DirectoryPath, "output.key"), "provider-key-two\n");
        f.Config = f.Config with { Outputs = [f.Config.Outputs[0] with { Model = "new-model" }] };
        f.Save();

        Assert.True(source.Reload());
        Assert.Equal("provider-key-one", oldRoute.ReadApiKey!());
        Assert.Equal("old-model", oldRoute.Output!.Model);
        var newRoute = source.Resolve(Fixture.UserKey)!;
        Assert.Equal("provider-key-two", newRoute.ReadApiKey!());
        Assert.Equal("new-model", newRoute.Output!.Model);
    }

    /// <summary>
    /// A key rotated in the users document takes effect without the routing table being touched: the
    /// route names the user, so rotation is no longer the Router's business.
    /// </summary>
    [Fact]
    public void A_key_rotated_in_the_users_document_keeps_the_route_it_had()
    {
        using var f = new Fixture();
        var source = f.Source();
        var users = f.UserDirectory!;

        var rotated = new string('b', 64);
        File.WriteAllText(Path.Combine(f.DirectoryPath, "user.key"), rotated);

        Assert.True(users.Reload());
        Assert.Null(source.Resolve(Fixture.UserKey));
        Assert.Equal("local", source.Resolve(rotated)!.Output!.Id);
    }

    /// <summary>One switch, in one file, stops a caller everywhere — including a Router route left
    /// exactly as it was.</summary>
    [Fact]
    public void A_user_suspended_in_the_users_document_loses_its_route()
    {
        using var f = new Fixture();
        var source = f.Source();
        var users = f.UserDirectory!;

        f.Users = f.Users with { Users = [f.Users.Users[0] with { Enabled = false }] };
        f.Save();

        Assert.True(users.Reload());
        Assert.Null(source.Resolve(Fixture.UserKey));
        Assert.Single(source.Snapshot.Inputs);
    }

    /// <summary>A user with a key but no route gets nothing: reaching a tool and reaching a model are
    /// separate grants, so identity alone is not Router access.</summary>
    [Fact]
    public void A_user_with_no_route_is_refused_rather_than_sent_to_the_default_output()
    {
        using var f = new Fixture();
        f.Config = f.Config with { Inputs = [] };
        f.Save();

        Assert.Null(f.Source().Resolve(Fixture.UserKey));
    }

    [Fact]
    public void Invalid_reload_retains_last_valid_routes_and_recovers()
    {
        using var f = new Fixture();
        var source = f.Source();
        File.WriteAllText(f.Path, "not-json-private-content");
        Assert.False(source.Reload());
        Assert.NotNull(source.ReloadError);
        Assert.DoesNotContain("private-content", source.ReloadError);
        Assert.NotNull(source.Resolve(Fixture.UserKey));
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
        var source = f.Source();
        File.Delete(Path.Combine(f.DirectoryPath, "output.key"));
        Assert.False(source.Reload());
        Assert.Equal("provider-key-one", source.Resolve(Fixture.UserKey)!.ReadApiKey!());
        var ex = Assert.Throws<InvalidOperationException>(() => f.Source());
        Assert.DoesNotContain(f.DirectoryPath, ex.Message);
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// The one startup failure where the file is intact and the fix is in a different file. A deployment
    /// written against the old shape named a key per input; ignoring those fields would start a hub that
    /// let nobody in and said nothing about why, so it is refused by name instead.
    /// </summary>
    [Fact]
    public void An_input_that_still_carries_its_own_key_is_refused_with_the_migration_named()
    {
        using var f = new Fixture();
        f.Config = f.Config with
        {
            Inputs = [f.Config.Inputs[0] with { Name = "Agent", KeyFile = "user.key" }],
        };
        f.Save();

        var ex = Assert.Throws<InvalidOperationException>(() => f.Source());

        Assert.Contains("users document", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UserId", ex.Message, StringComparison.Ordinal);
        // The message is read from a log: it names the fields to change and quotes no value of any of them.
        Assert.DoesNotContain("user.key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_routes_for_one_user_and_desktop_config_fields_are_rejected()
    {
        using var f = new Fixture();
        f.Config = f.Config with { Inputs = [f.Config.Inputs[0], f.Config.Inputs[0]] };
        f.Save();
        Assert.Throws<InvalidOperationException>(() => f.Source());

        File.WriteAllText(f.Path, "{\"StartOnLaunch\":true,\"Outputs\":[],\"Inputs\":[]}");
        Assert.Throws<InvalidOperationException>(() => f.Source());
    }

    [Fact]
    public async Task Wildcard_listener_uses_deployment_source_and_requires_authentication()
    {
        using var f = new Fixture();
        var source = f.Source();
        await using var router = new RouterHost(source, NullLogger<RouterHost>.Instance, new() { BindAddress = "0.0.0.0" });
        await router.StartAsync(0);
        Assert.Equal("0.0.0.0", router.BindAddress);

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{router.Port}/v1/"), Timeout = TimeSpan.FromSeconds(10),
        };
        using var anonymous = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", Fixture.UserKey);
        f.Config = f.Config with { DefaultOutputId = null };
        f.Save();
        Assert.True(source.Reload());
        using var authenticated = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, authenticated.StatusCode);

        // Suspended in the other document, and the live listener stops accepting it.
        f.Users = f.Users with { Users = [f.Users.Users[0] with { Enabled = false }] };
        f.Save();
        Assert.True(f.UserDirectory!.Reload());
        using var revoked = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        public const string UserKey = "test-user-key-with-at-least-32-characters";

        public string DirectoryPath { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "mcphub-deployment-tests", Guid.NewGuid().ToString("N"));

        public string Path => System.IO.Path.Combine(DirectoryPath, "router.json");

        public string UsersPath => System.IO.Path.Combine(DirectoryPath, "users.json");

        /// <summary>The users source behind the last <see cref="Source"/>, for reloading it on its own.</summary>
        public UsersDeploymentSource? UserDirectory { get; private set; }

        public UsersDeploymentConfiguration Users { get; set; } = new()
        {
            Users = [new() { Id = "agent", Name = "Agent", KeyFile = "user.key" }],
        };

        public RouterDeploymentConfiguration Config { get; set; } = new()
        {
            DefaultOutputId = "local",
            Inputs = [new() { UserId = "agent" }],
            Outputs =
            [
                new()
                {
                    Id = "local", Name = "Local", BaseUrl = "http://localhost:1/v1", Model = "old-model",
                    ApiKeyFile = "output.key",
                },
            ],
        };

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(System.IO.Path.Combine(DirectoryPath, "user.key"), UserKey);
            File.WriteAllText(System.IO.Path.Combine(DirectoryPath, "output.key"), "provider-key-one");
            Save();
        }

        /// <summary>Both documents, read as a container would: identity first, then the routes naming it.</summary>
        public RouterDeploymentSource Source(Func<string, string?>? environment = null)
        {
            UserDirectory = new UsersDeploymentSource(UsersPath, environment ?? (_ => null));
            return new RouterDeploymentSource(Path, UserDirectory, environment ?? (_ => null));
        }

        public void Save()
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(Config));
            File.WriteAllText(UsersPath, JsonSerializer.Serialize(Users));
        }

        public void Dispose()
        {
            if (File.Exists(Path)) File.SetAttributes(Path, FileAttributes.Normal);
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
