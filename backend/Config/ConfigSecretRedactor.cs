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
        if (existingValue is null)
        {
            if (ContainsRedactedSecret(submittedValue))
                throw CreateResolutionException(configName);
            return submittedValue;
        }

        return configName switch
        {
            "api.key" or "webdav.pass" or "rclone.pass"
                when IsRedactedSecret(submittedValue) => ResolveScalarSecret(configName, existingValue),
            "usenet.providers" => PreserveUsenetProviderSecrets(submittedValue, existingValue),
            "arr.instances" => PreserveArrInstanceSecrets(submittedValue, existingValue),
            _ => submittedValue
        };
    }

    private static string ResolveScalarSecret(string configName, string existingValue)
    {
        if (string.IsNullOrEmpty(existingValue) || IsRedactedSecret(existingValue))
            throw CreateResolutionException(configName);
        return existingValue;
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
        if (submitted?.Providers is null || existing?.Providers is null)
        {
            if (ContainsRedactedSecret(submittedValue))
                throw CreateResolutionException("usenet.providers");
            return submittedValue;
        }

        foreach (var provider in submitted.Providers)
        {
            if (!IsRedactedSecret(provider.Pass)) continue;

            var existingProvider = FindMatchingProvider(existing.Providers, provider);
            if (existingProvider is null
                || string.IsNullOrEmpty(existingProvider.Pass)
                || IsRedactedSecret(existingProvider.Pass))
            {
                throw CreateResolutionException("usenet.providers");
            }
            provider.Pass = existingProvider.Pass;
        }

        return JsonSerializer.Serialize(submitted, JsonOptions);
    }

    private static UsenetProviderConfig.ConnectionDetails? FindMatchingProvider
    (
        IReadOnlyList<UsenetProviderConfig.ConnectionDetails> existingProviders,
        UsenetProviderConfig.ConnectionDetails submitted
    )
    {
        var matches = existingProviders
            .Where(existing =>
                string.Equals(existing.Host.Trim(), submitted.Host.Trim(), StringComparison.OrdinalIgnoreCase)
                && existing.Port == submitted.Port
                && existing.GetEffectiveUseSsl() == submitted.GetEffectiveUseSsl()
                && string.Equals(existing.User, submitted.User, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
        if (submitted is null
            || existing is null
            || submitted.RadarrInstances is null
            || submitted.SonarrInstances is null
            || submitted.LidarrInstances is null
            || existing.RadarrInstances is null
            || existing.SonarrInstances is null
            || existing.LidarrInstances is null)
        {
            if (ContainsRedactedSecret(submittedValue))
                throw CreateResolutionException("arr.instances");
            return submittedValue;
        }

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
        foreach (var instance in submittedInstances)
        {
            var existing = FindMatchingArrInstance(existingInstances, instance);
            if (IsRedactedSecret(instance.ApiKey))
            {
                if (existing is null
                    || string.IsNullOrEmpty(existing.ApiKey)
                    || IsRedactedSecret(existing.ApiKey))
                {
                    throw CreateResolutionException("arr.instances");
                }
                instance.ApiKey = existing.ApiKey;
            }
            if (existing is not null)
                instance.Host = existing.Host;
        }
    }

    private static ArrConfig.ConnectionDetails? FindMatchingArrInstance
    (
        IReadOnlyList<ArrConfig.ConnectionDetails> existingInstances,
        ArrConfig.ConnectionDetails submitted
    )
    {
        var matches = existingInstances
            .Where(existing => EndpointIdentity.AreEquivalent(existing.Host, submitted.Host))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ContainsRedactedSecret(string value)
    {
        return value.Contains(RedactedSecret, StringComparison.Ordinal);
    }

    private static ConfigSecretResolutionException CreateResolutionException(string configName)
    {
        return new ConfigSecretResolutionException(
            $"Saved credentials for {configName} could not be matched uniquely; re-enter the credential.");
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
