using System.Text.Json;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Config;

public static class ConfigSecretRedactor
{
    public const string RedactedSecret = "__NZBDAV_REDACTED__";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool IsRedactedSecret(string? value)
    {
        return string.Equals(value, RedactedSecret, StringComparison.Ordinal);
    }

    public static ConfigItem RedactForDisplay(ConfigItem item)
    {
        return new ConfigItem
        {
            ConfigName = item.ConfigName,
            ConfigValue = RedactValue(item.ConfigName, item.ConfigValue)
        };
    }

    public static string RedactValue(string configName, string value)
    {
        return configName switch
        {
            "api.key" or "webdav.pass" or "rclone.pass" => RedactScalarSecret(value),
            "usenet.providers" => RedactUsenetProviders(value),
            "arr.instances" => RedactArrInstances(value),
            _ => value
        };
    }

    public static ConfigItem PreserveExistingSecrets(ConfigItem submitted, string? existingValue)
    {
        return new ConfigItem
        {
            ConfigName = submitted.ConfigName,
            ConfigValue = PreserveExistingSecretValue(submitted.ConfigName, submitted.ConfigValue, existingValue)
        };
    }

    public static string PreserveExistingSecretValue(string configName, string submittedValue, string? existingValue)
    {
        if (existingValue is null) return IsRedactedSecret(submittedValue) ? "" : submittedValue;

        return configName switch
        {
            "api.key" or "webdav.pass" or "rclone.pass" when IsRedactedSecret(submittedValue) => existingValue,
            "usenet.providers" => PreserveUsenetProviderSecrets(submittedValue, existingValue),
            "arr.instances" => PreserveArrInstanceSecrets(submittedValue, existingValue),
            _ => submittedValue
        };
    }

    private static string RedactScalarSecret(string value)
    {
        return string.IsNullOrEmpty(value) ? value : RedactedSecret;
    }

    private static string RedactUsenetProviders(string value)
    {
        var config = TryDeserialize<UsenetProviderConfig>(value);
        if (config is null) return """{"Providers":[]}""";

        foreach (var provider in config.Providers)
        {
            if (!string.IsNullOrEmpty(provider.Pass))
                provider.Pass = RedactedSecret;
        }

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static string PreserveUsenetProviderSecrets(string submittedValue, string existingValue)
    {
        var submitted = TryDeserialize<UsenetProviderConfig>(submittedValue);
        var existing = TryDeserialize<UsenetProviderConfig>(existingValue);
        if (submitted is null || existing is null) return submittedValue;

        for (var i = 0; i < submitted.Providers.Count; i++)
        {
            var provider = submitted.Providers[i];
            if (!IsRedactedSecret(provider.Pass)) continue;

            var existingProvider = FindMatchingProvider(existing.Providers, provider, i);
            provider.Pass = existingProvider?.Pass ?? "";
        }

        return JsonSerializer.Serialize(submitted, JsonOptions);
    }

    private static UsenetProviderConfig.ConnectionDetails? FindMatchingProvider
    (
        IReadOnlyList<UsenetProviderConfig.ConnectionDetails> existingProviders,
        UsenetProviderConfig.ConnectionDetails submitted,
        int submittedIndex
    )
    {
        var match = existingProviders.FirstOrDefault(existing =>
            string.Equals(existing.Host.Trim(), submitted.Host.Trim(), StringComparison.OrdinalIgnoreCase)
            && existing.Port == submitted.Port
            && string.Equals(existing.User.Trim(), submitted.User.Trim(), StringComparison.Ordinal));
        if (match is not null) return match;

        return submittedIndex >= 0 && submittedIndex < existingProviders.Count
            ? existingProviders[submittedIndex]
            : null;
    }

    private static string RedactArrInstances(string value)
    {
        var config = TryDeserialize<ArrConfig>(value);
        if (config is null) return EmptyArrConfigJson();

        RedactArrApiKeys(config.RadarrInstances);
        RedactArrApiKeys(config.SonarrInstances);
        RedactArrApiKeys(config.LidarrInstances);
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static void RedactArrApiKeys(List<ArrConfig.ConnectionDetails> instances)
    {
        foreach (var instance in instances)
        {
            if (!string.IsNullOrEmpty(instance.ApiKey))
                instance.ApiKey = RedactedSecret;
        }
    }

    private static string PreserveArrInstanceSecrets(string submittedValue, string existingValue)
    {
        var submitted = TryDeserialize<ArrConfig>(submittedValue);
        var existing = TryDeserialize<ArrConfig>(existingValue);
        if (submitted is null || existing is null) return submittedValue;

        PreserveArrApiKeys(submitted.RadarrInstances, existing.RadarrInstances);
        PreserveArrApiKeys(submitted.SonarrInstances, existing.SonarrInstances);
        PreserveArrApiKeys(submitted.LidarrInstances, existing.LidarrInstances);
        return JsonSerializer.Serialize(submitted, JsonOptions);
    }

    private static void PreserveArrApiKeys
    (
        List<ArrConfig.ConnectionDetails> submittedInstances,
        IReadOnlyList<ArrConfig.ConnectionDetails> existingInstances
    )
    {
        for (var i = 0; i < submittedInstances.Count; i++)
        {
            var instance = submittedInstances[i];
            var existing = FindMatchingArrInstance(existingInstances, instance, i);
            if (existing is not null && NormalizeEndpoint(existing.Host) == NormalizeEndpoint(instance.Host))
                instance.Host = existing.Host;
            if (IsRedactedSecret(instance.ApiKey))
                instance.ApiKey = existing?.ApiKey ?? "";
        }
    }

    private static ArrConfig.ConnectionDetails? FindMatchingArrInstance
    (
        IReadOnlyList<ArrConfig.ConnectionDetails> existingInstances,
        ArrConfig.ConnectionDetails submitted,
        int submittedIndex
    )
    {
        var normalizedHost = NormalizeEndpoint(submitted.Host);
        var match = existingInstances.FirstOrDefault(existing => NormalizeEndpoint(existing.Host) == normalizedHost);
        if (match is not null) return match;

        return submittedIndex >= 0 && submittedIndex < existingInstances.Count
            ? existingInstances[submittedIndex]
            : null;
    }

    private static string NormalizeEndpoint(string value)
    {
        return value.Trim().TrimEnd('/').ToLowerInvariant();
    }

    private static T? TryDeserialize<T>(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private static string EmptyArrConfigJson()
    {
        return JsonSerializer.Serialize(new ArrConfig(), JsonOptions);
    }
}
