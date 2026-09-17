namespace MCPHub.Core.Routing;

/// <summary>Shared validation for every router configuration source.</summary>
public static class RouterConfigurationRules
{
    public static void Validate(RouterConfiguration c)
    {
        if (c.SchemaVersion != 1) throw new ArgumentException("Unsupported router configuration version.");
        if (c.Port is < 1024 or > 65535) throw new ArgumentException("Choose a router port between 1024 and 65535.");
        if (c.Inputs is null || c.Outputs is null || c.Inputs.Length > 256 || c.Outputs.Length > 256)
            throw new ArgumentException("Router supports up to 256 inputs and outputs.");
        if (c.Inputs.Any(i => i is null) || c.Outputs.Any(o => o is null)) throw new ArgumentException("Invalid router entries.");
        var ids = c.Outputs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != c.Outputs.Length || c.Inputs.Select(i => i.Id).Distinct().Count() != c.Inputs.Length)
            throw new ArgumentException("Router IDs must be unique.");
        if (c.DefaultOutputId is not null && !ids.Contains(c.DefaultOutputId)) throw new ArgumentException("Choose an existing default output.");
        foreach (var o in c.Outputs)
        {
            ValidateName(o.Name);
            if (string.IsNullOrWhiteSpace(o.Id)) throw new ArgumentException("Missing output ID.");
            NormalizeBaseUrl(o.BaseUrl);
            if (o.Model?.Length > 256) throw new ArgumentException("Model name is too long.");
        }
        foreach (var i in c.Inputs)
        {
            ValidateName(i.Name);
            if (string.IsNullOrWhiteSpace(i.Id) || i.KeyHash is null || i.KeyHash.Length != 64 || !i.KeyHash.All(Uri.IsHexDigit))
                throw new ArgumentException("Invalid input key hash.");
            if (i.OutputId is not null && !ids.Contains(i.OutputId)) throw new ArgumentException("Choose an existing output or the global default.");
        }
        if (c.Inputs.Select(i => i.KeyHash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != c.Inputs.Length)
            throw new ArgumentException("Each input must have a unique key.");
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTP(S) API base URL without credentials, query, or fragment, e.g. http://localhost:8000/v1.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Enter a name of 1–100 characters.");
    }
}
