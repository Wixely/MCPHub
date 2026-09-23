using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MCPHub.Core.Models;
using MCPHub.Core.Recipes;
using MCPHub.Core.Routing;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Backup;

/// <summary>Anything that went wrong with an archive. The message is written to be shown to the user.</summary>
public sealed class SettingsArchiveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What an import changed, so the UI can say more than "done".</summary>
/// <param name="Applied">Categories actually written.</param>
/// <param name="Notes">Per-category detail, and anything deliberately skipped.</param>
/// <param name="RequiresRestartFor">Settings that will not take effect until MCPHub is restarted.</param>
public sealed record SettingsImportResult(
    IReadOnlyList<SettingsCategory> Applied,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> RequiresRestartFor);

/// <summary>Writes and reads MCPHub settings archives — a zip of selected configuration, optionally encrypted.</summary>
public interface ISettingsArchiveService
{
    /// <summary>Categories that can be exported from this installation, in a sensible display order.</summary>
    IReadOnlyList<SettingsCategory> Categories { get; }

    /// <summary>
    /// Writes <paramref name="categories"/> to a zip at <paramref name="path"/>. A non-empty
    /// <paramref name="password"/> encrypts every entry; <see cref="SettingsCategory.Secrets"/> may only be
    /// exported with one.
    /// </summary>
    Task ExportAsync(string path, IReadOnlyCollection<SettingsCategory> categories, string? password, CancellationToken cancellationToken = default);

    /// <summary>Reads the manifest without needing the password, so the UI can offer what the archive holds.</summary>
    Task<SettingsArchiveManifest> InspectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Applies the chosen subset of an archive. Categories the archive lacks are ignored.</summary>
    Task<SettingsImportResult> ImportAsync(string path, IReadOnlyCollection<SettingsCategory> categories, string? password, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SettingsArchiveService : ISettingsArchiveService
{
    /// <summary>Entry every archive carries, in the clear, so an archive can be inspected before decrypting.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>PBKDF2-HMAC-SHA256 iterations. Raised only alongside a schema bump, since readers must match.</summary>
    public const int KdfIterations = 600_000;

    /// <summary>Most iterations this reader will honour from an archive's manifest, to bound the work.</summary>
    public const int MaxKdfIterations = 10_000_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private const long MaxEntryBytes = 32 * 1024 * 1024;

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IRecipeStore _recipes;
    private readonly RouterStore _router;
    private readonly ILogger<SettingsArchiveService> _logger;

    public SettingsArchiveService(
        ISettingsStore settings,
        ISecretStore secrets,
        IRecipeStore recipes,
        RouterStore router,
        ILogger<SettingsArchiveService> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _recipes = recipes;
        _router = router;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<SettingsCategory> Categories { get; } = Enum.GetValues<SettingsCategory>();

    // ---- export ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task ExportAsync(string path, IReadOnlyCollection<SettingsCategory> categories, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(categories);

        var chosen = Categories.Where(categories.Contains).ToArray();
        if (chosen.Length == 0)
            throw new SettingsArchiveException("Choose at least one category to export.");

        var encrypted = !string.IsNullOrEmpty(password);
        if (chosen.Contains(SettingsCategory.Secrets) && !encrypted)
            throw new SettingsArchiveException("Exporting tokens and keys requires a password — they would otherwise be written in plaintext.");

        var salt = encrypted ? RandomNumberGenerator.GetBytes(SaltBytes) : null;
        var key = salt is null ? null : DeriveKey(password!, salt);

        try
        {
            // Build in memory, then write once: a half-written archive beside a real one invites restoring it.
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = new SettingsArchiveManifest
                {
                    CreatedUtc = DateTimeOffset.UtcNow,
                    AppVersion = typeof(SettingsArchiveService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    Categories = chosen,
                    Encrypted = encrypted,
                    KdfSalt = salt is null ? null : Convert.ToBase64String(salt),
                    KdfIterations = encrypted ? KdfIterations : 0,
                };
                WriteEntry(zip, ManifestEntryName, Serialize(manifest, SettingsArchiveJsonContext.Default.SettingsArchiveManifest), protectWith: null);

                foreach (var category in chosen)
                    WriteEntry(zip, EntryName(category, encrypted), BuildSection(category, chosen), key);
            }

            buffer.Position = 0;
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await buffer.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SettingsArchiveException("Could not write the archive. Check the folder, permissions, and free space.", ex);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] BuildSection(SettingsCategory category, IReadOnlyCollection<SettingsCategory> chosen)
    {
        var s = _settings.Current;
        return category switch
        {
            SettingsCategory.General => Serialize(new GeneralSection
            {
                SharedServersFolder = s.SharedServersFolder,
                Flavor = s.Flavor.ToString(),
                ProxyPort = s.ProxyPort,
                ProxyBindAddress = s.ProxyBindAddress,
                StartProxyOnLaunch = s.StartProxyOnLaunch,
                MinimizeToTray = s.MinimizeToTray,
                CloseToTray = s.CloseToTray,
                AutoStartServices = [.. s.AutoStartServices],
            }, SettingsArchiveJsonContext.Default.GeneralSection),

            SettingsCategory.UserServers => Serialize(new UserServersSection
            {
                Servers = [.. s.UserServers],
            }, SettingsArchiveJsonContext.Default.UserServersSection),

            SettingsCategory.AgentAndEngine => Serialize(new AgentAndEngineSection
            {
                AgentFolder = s.AgentFolder,
                SlopworksFolder = s.SlopworksFolder,
                AutoStartAgentCli = s.AutoStartAgentCli,
                AutoStartAgentWeb = s.AutoStartAgentWeb,
                AutoStartAgentJobs = s.AutoStartAgentJobs,
                AgentServeBindAllInterfaces = s.AgentServeBindAllInterfaces,
                AutoStartSlopworks = s.AutoStartSlopworks,
                RecipesEnabled = s.RecipesEnabled,
                RecipesAgentEditEnabled = s.RecipesAgentEditEnabled,
                AgentManagementEnabled = s.AgentManagementEnabled,
                AgentManagementControlEnabled = s.AgentManagementControlEnabled,
                AgentManagementInstallEnabled = s.AgentManagementInstallEnabled,
                AgentManagementUpdateChecksEnabled = s.AgentManagementUpdateChecksEnabled,
            }, SettingsArchiveJsonContext.Default.AgentAndEngineSection),

            SettingsCategory.Appearance => Serialize(new AppearanceSection
            {
                Theme = s.Theme,
                WindowWidth = s.WindowWidth,
                WindowHeight = s.WindowHeight,
            }, SettingsArchiveJsonContext.Default.AppearanceSection),

            SettingsCategory.Recipes => Serialize(new RecipesSection
            {
                Recipes = [.. _recipes.All],
            }, SettingsArchiveJsonContext.Default.RecipesSection),

            SettingsCategory.Router => Serialize(BuildRouterSection(), SettingsArchiveJsonContext.Default.RouterSection),

            SettingsCategory.Secrets => Serialize(BuildSecretsSection(chosen), SettingsArchiveJsonContext.Default.SecretsSection),

            _ => throw new SettingsArchiveException($"Unknown settings category '{category}'."),
        };
    }

    private RouterSection BuildRouterSection()
    {
        var config = _router.Snapshot;
        return new RouterSection
        {
            Port = config.Port,
            BindAddress = config.BindAddress,
            StartOnLaunch = config.StartOnLaunch,
            DefaultOutputId = config.DefaultOutputId,
            // Credentials are stripped here unconditionally; they travel only via the Secrets category.
            Outputs = [.. config.Outputs.Select(o => o with { ProtectedApiKey = null })],
            Inputs = [.. config.Inputs],
        };
    }

    private SecretsSection BuildSecretsSection(IReadOnlyCollection<SettingsCategory> chosen)
    {
        var section = new SecretsSection();

        if (_secrets.Get(SecretKeys.GithubPat) is { Length: > 0 } pat)
            section.Store[SecretKeys.GithubPat] = pat;

        // A user-server token without its server definition would be orphaned on the far side, so it rides
        // along only when the servers themselves are being exported.
        if (chosen.Contains(SettingsCategory.UserServers))
        {
            foreach (var server in _settings.Current.UserServers)
                if (_secrets.Get(server.SecretKey) is { Length: > 0 } token)
                    section.Store[server.SecretKey] = token;
        }

        if (chosen.Contains(SettingsCategory.Router))
        {
            foreach (var (outputId, key) in _router.ExportApiKeys())
                section.RouterOutputKeys[outputId] = key;
        }

        return section;
    }

    // ---- inspect / import -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<SettingsArchiveManifest> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        using var zip = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return ReadManifest(zip);
    }

    /// <inheritdoc />
    public async Task<SettingsImportResult> ImportAsync(string path, IReadOnlyCollection<SettingsCategory> categories, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categories);
        using var zip = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManifest(zip);

        var chosen = manifest.Categories.Where(categories.Contains).ToArray();
        if (chosen.Length == 0)
            throw new SettingsArchiveException("None of the chosen categories are in this archive.");

        byte[]? key = null;
        if (manifest.Encrypted)
        {
            if (string.IsNullOrEmpty(password))
                throw new SettingsArchiveException("This archive is encrypted. Enter the password used to export it.");
            // The iteration ceiling is a guard, not a policy: a manifest is attacker-controllable, and an
            // absurd count would make opening the file hang rather than fail.
            if (manifest.KdfSalt is null || manifest.KdfIterations is <= 0 or > MaxKdfIterations)
                throw new SettingsArchiveException("This archive is marked encrypted but its key parameters are missing or implausible, so it cannot be read.");

            byte[] salt;
            try { salt = Convert.FromBase64String(manifest.KdfSalt); }
            catch (FormatException ex) { throw new SettingsArchiveException("This archive's key parameters are malformed, so it cannot be read.", ex); }
            if (salt.Length is < 8 or > 64)
                throw new SettingsArchiveException("This archive's key parameters are malformed, so it cannot be read.");

            key = DeriveKey(password, salt, manifest.KdfIterations);
        }

        var context = new ImportContext();
        try
        {
            // Read (and decrypt) everything first: a wrong password must fail before anything is written.
            var sections = chosen.ToDictionary(
                category => category,
                category => ReadEntry(zip, EntryName(category, manifest.Encrypted), key));

            // Routes are the one section that can be structurally invalid (an output id nothing defines, a
            // duplicate key hash). Rejecting that here, rather than when it is written last, keeps a bad
            // archive from leaving half its categories applied.
            if (sections.TryGetValue(SettingsCategory.Router, out var routerJson))
                ValidateRouterSection(Deserialize(routerJson, SettingsArchiveJsonContext.Default.RouterSection));

            foreach (var category in chosen)
            {
                ApplySection(category, sections[category], context);
                context.Applied.Add(category);
            }

            // Last, so the routing table is written once both its routes and any keys for it are in hand.
            if (context.Router is { } router)
                FlushRouter(router, context);

            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }

        _logger.LogInformation("Imported settings categories: {Categories}.", string.Join(", ", context.Applied));
        return new(context.Applied, context.Notes, context.Restart);
    }

    /// <summary>One import's accumulating state. Local to the call, so concurrent imports cannot interleave.</summary>
    private sealed class ImportContext
    {
        public List<SettingsCategory> Applied { get; } = [];
        public List<string> Notes { get; } = [];
        public List<string> Restart { get; } = [];
        public RouterSection? Router { get; set; }
        public IReadOnlyDictionary<string, string>? RouterKeys { get; set; }
    }

    private void ApplySection(SettingsCategory category, byte[] json, ImportContext context)
    {
        var notes = context.Notes;
        var restart = context.Restart;
        var s = _settings.Current;
        switch (category)
        {
            case SettingsCategory.General:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.GeneralSection);
                if (!string.Equals(s.SharedServersFolder, section.SharedServersFolder, StringComparison.OrdinalIgnoreCase))
                    restart.Add("shared servers folder");
                s.SharedServersFolder = section.SharedServersFolder;
                s.Flavor = Enum.TryParse<PublishFlavor>(section.Flavor, ignoreCase: true, out var flavor) ? flavor : s.Flavor;
                s.ProxyPort = section.ProxyPort is > 0 and < 65536 ? section.ProxyPort : s.ProxyPort;
                s.ProxyBindAddress = string.IsNullOrWhiteSpace(section.ProxyBindAddress) ? s.ProxyBindAddress : section.ProxyBindAddress.Trim();
                s.StartProxyOnLaunch = section.StartProxyOnLaunch;
                s.MinimizeToTray = section.MinimizeToTray;
                s.CloseToTray = section.CloseToTray;
                s.AutoStartServices = [.. section.AutoStartServices];
                notes.Add($"General settings applied (proxy {s.ProxyBindAddress}:{s.ProxyPort}).");
                break;
            }

            case SettingsCategory.UserServers:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.UserServersSection);
                s.UserServers = [.. section.Servers.Where(server => server is not null)];
                notes.Add($"{s.UserServers.Count} user-added MCP server(s) replaced the previous list.");
                break;
            }

            case SettingsCategory.AgentAndEngine:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.AgentAndEngineSection);
                if (!string.Equals(s.AgentFolder, section.AgentFolder, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(s.SlopworksFolder, section.SlopworksFolder, StringComparison.OrdinalIgnoreCase))
                    restart.Add("agent and engine folders");
                s.AgentFolder = section.AgentFolder;
                s.SlopworksFolder = section.SlopworksFolder;
                s.AutoStartAgentCli = section.AutoStartAgentCli;
                s.AutoStartAgentWeb = section.AutoStartAgentWeb;
                s.AutoStartAgentJobs = section.AutoStartAgentJobs;
                s.AgentServeBindAllInterfaces = section.AgentServeBindAllInterfaces;
                s.AutoStartSlopworks = section.AutoStartSlopworks;
                s.RecipesEnabled = section.RecipesEnabled;
                s.RecipesAgentEditEnabled = section.RecipesAgentEditEnabled;
                s.AgentManagementEnabled = section.AgentManagementEnabled;
                s.AgentManagementControlEnabled = section.AgentManagementControlEnabled;
                s.AgentManagementInstallEnabled = section.AgentManagementInstallEnabled;
                s.AgentManagementUpdateChecksEnabled = section.AgentManagementUpdateChecksEnabled;
                notes.Add("Agent and engine settings applied.");
                break;
            }

            case SettingsCategory.Appearance:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.AppearanceSection);
                s.Theme = string.IsNullOrWhiteSpace(section.Theme) ? s.Theme : section.Theme;
                if (section.WindowWidth >= 400) s.WindowWidth = section.WindowWidth;
                if (section.WindowHeight >= 300) s.WindowHeight = section.WindowHeight;
                notes.Add($"Appearance applied (theme {s.Theme}).");
                break;
            }

            case SettingsCategory.Recipes:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.RecipesSection);
                var (added, replaced) = _recipes.Import(section.Recipes);
                notes.Add($"Recipes merged: {added} added, {replaced} updated. Local recipes not in the archive were kept.");
                break;
            }

            case SettingsCategory.Router:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.RouterSection);
                context.Router = section;
                notes.Add($"Router: {section.Outputs.Count} output(s) and {section.Inputs.Count} agent(s) staged.");
                break;
            }

            case SettingsCategory.Secrets:
            {
                var section = Deserialize(json, SettingsArchiveJsonContext.Default.SecretsSection);
                foreach (var (secretKey, value) in section.Store)
                    _secrets.Set(secretKey, value);
                context.RouterKeys = section.RouterOutputKeys;
                notes.Add($"{section.Store.Count} stored secret(s) restored.");
                break;
            }

            default:
                throw new SettingsArchiveException($"Unknown settings category '{category}'.");
        }
    }

    private static RouterConfiguration ToConfiguration(RouterSection section) => new()
    {
        Port = section.Port,
        BindAddress = section.BindAddress,
        StartOnLaunch = section.StartOnLaunch,
        DefaultOutputId = section.DefaultOutputId,
        Outputs = [.. section.Outputs],
        Inputs = [.. section.Inputs],
    };

    /// <summary>Applies the same rules the store would, so the rejection happens before anything is written.</summary>
    private static void ValidateRouterSection(RouterSection section)
    {
        try
        {
            // Base URLs are normalised on the way in, so validate the normalised form the store will hold.
            RouterConfigurationRules.Validate(ToConfiguration(section) with
            {
                Outputs = [.. section.Outputs.Select(o => o with { BaseUrl = TryNormalize(o.BaseUrl) })],
            });
        }
        catch (ArgumentException ex)
        {
            throw new SettingsArchiveException("The archive's router routes were rejected: " + ex.Message, ex);
        }

        static string TryNormalize(string baseUrl)
        {
            try { return RouterConfigurationRules.NormalizeBaseUrl(baseUrl); }
            catch (ArgumentException) { return baseUrl; }
        }
    }

    private void FlushRouter(RouterSection section, ImportContext context)
    {
        var (notes, restart, keys) = (context.Notes, context.Restart, context.RouterKeys);
        try
        {
            _router.Import(ToConfiguration(section), keys);

            var missing = section.Outputs.Count(o => keys?.ContainsKey(o.Id) != true);
            notes.Add(keys is null || missing > 0
                ? $"Router routes applied. {missing} output(s) arrived without an upstream key — re-enter those keys, or export again with the Secrets category and a password."
                : "Router routes and upstream keys applied.");
            restart.Add("router listener address and port (use Apply on the Router page to rebind now)");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new SettingsArchiveException("The archive's router routes were rejected: " + ex.Message, ex);
        }
    }

    // ---- zip + crypto ---------------------------------------------------------------------------

    private static string EntryName(SettingsCategory category, bool encrypted) =>
        $"settings/{category.ToString().ToLowerInvariant()}.json" + (encrypted ? ".enc" : string.Empty);

    private static async Task<ZipArchive> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            // Copied into memory so the file is not held open across the whole import.
            var buffer = new MemoryStream();
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaxEntryBytes)
                    throw new SettingsArchiveException("That file is too large to be an MCPHub settings archive.");
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            buffer.Position = 0;
            return new ZipArchive(buffer, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new SettingsArchiveException("That file is not a readable zip archive.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsArchiveException("Could not read that file. Check it still exists and is readable.", ex);
        }
    }

    private static SettingsArchiveManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntryName)
            ?? throw new SettingsArchiveException("That zip is not an MCPHub settings archive — it has no manifest.json.");
        var manifest = Deserialize(ReadRaw(entry), SettingsArchiveJsonContext.Default.SettingsArchiveManifest);
        if (manifest.SchemaVersion != 1)
            throw new SettingsArchiveException($"This archive was written by a newer MCPHub (format {manifest.SchemaVersion}). Update MCPHub to import it.");
        return manifest;
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content, byte[]? protectWith)
    {
        var payload = protectWith is null ? content : Encrypt(content, protectWith);
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(payload);
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, byte[]? key)
    {
        var entry = zip.GetEntry(name)
            ?? throw new SettingsArchiveException($"The archive is missing the '{name}' entry it says it contains.");
        var raw = ReadRaw(entry);
        return key is null ? raw : Decrypt(raw, key);
    }

    private static byte[] ReadRaw(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
            throw new SettingsArchiveException($"The '{entry.FullName}' entry is unreasonably large for a settings archive.");
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations = KdfIterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>AES-256-GCM, nonce and tag prefixed. Each entry gets its own nonce under the archive's key.</summary>
    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var output = new byte[NonceBytes + TagBytes + plaintext.Length];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceBytes + TagBytes), output.AsSpan(NonceBytes, TagBytes));
        return output;
    }

    private static byte[] Decrypt(byte[] payload, byte[] key)
    {
        if (payload.Length < NonceBytes + TagBytes)
            throw new SettingsArchiveException("An encrypted entry in the archive is truncated.");
        var plaintext = new byte[payload.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(payload.AsSpan(0, NonceBytes), payload.AsSpan(NonceBytes + TagBytes), payload.AsSpan(NonceBytes, TagBytes), plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new SettingsArchiveException("The password is wrong, or the archive has been altered since it was written.", ex);
        }
    }

    private static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T Deserialize<T>(byte[] json, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new SettingsArchiveException("An entry in the archive is empty.");
        }
        catch (JsonException ex)
        {
            throw new SettingsArchiveException("An entry in the archive is not valid JSON, so the archive cannot be trusted.", ex);
        }
    }
}
