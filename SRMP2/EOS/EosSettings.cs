using System;
using System.Collections.Generic;
using System.IO;

namespace SRMP2.EOS;

internal sealed class EosSettings
{
    // Non-secret EOS identifiers for the SRMP2 development product.
    internal const string DefaultProductId = "b141bdbdbd934a1ea6b5cb531b13f8ba";
    internal const string DefaultSandboxId = "80c2297da3994ee0a1dd7b9db96da322";
    internal const string DefaultDeploymentId = "251ae107900e40e9953d2e0b26c60b6e";
    internal const string DefaultClientId = "xyza7891tt1CUTkIN3ufDVnLAZ9zkWol";
    internal const string DefaultClientSecret = "sMUT+3As7VJo/Vk25jgZ3uIVL2Gd0QbEMqYLiMjKLP0";

    internal string ProductId { get; private set; } = DefaultProductId;
    internal string SandboxId { get; private set; } = DefaultSandboxId;
    internal string DeploymentId { get; private set; } = DefaultDeploymentId;
    internal string ClientId { get; private set; } = DefaultClientId;
    internal string ClientSecret { get; private set; } = DefaultClientSecret;

    internal static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRMP2");

    internal bool IsComplete =>
        !string.IsNullOrWhiteSpace(ProductId) &&
        !string.IsNullOrWhiteSpace(SandboxId) &&
        !string.IsNullOrWhiteSpace(DeploymentId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);

    internal static EosSettings Load()
    {
        var settings = new EosSettings();

        if (!File.Exists(ConfigPath))
            return settings;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(ConfigPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                continue;

            var split = line.IndexOf('=');
            if (split <= 0)
                continue;

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            values[key] = value;
        }

        settings.ProductId = Read(values, "ProductId", DefaultProductId);
        settings.SandboxId = Read(values, "SandboxId", DefaultSandboxId);
        settings.DeploymentId = Read(values, "DeploymentId", DefaultDeploymentId);
        settings.ClientId = Read(values, "ClientId", DefaultClientId);
        settings.ClientSecret = Read(values, "ClientSecret", DefaultClientSecret);
        return settings;
    }



    private static string Read(IReadOnlyDictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;
}
