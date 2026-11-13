using System.Globalization;
using System.Linq;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Core;

public static class ManualSaPlanFileSerializer
{
    public const string KeyLocal = "local";
    public const string KeyRemote = "remote";
    public const string KeySelectorProtocol = "selector_protocol";
    public const string KeyInboundSpi = "in_spi";
    public const string KeyOutboundSpi = "out_spi";
    public const string KeyInboundCipher = "in_cipher_key";
    public const string KeyInboundAuth = "in_auth_key";
    public const string KeyOutboundCipher = "out_cipher_key";
    public const string KeyOutboundAuth = "out_auth_key";

    public static readonly string[] RequiredKeys =
    [
        KeyLocal,
        KeyRemote,
        KeySelectorProtocol,
        KeyInboundSpi,
        KeyOutboundSpi,
        KeyInboundCipher,
        KeyInboundAuth,
        KeyOutboundCipher,
        KeyOutboundAuth
    ];

    public sealed record ManualSaPlanFileValidationResult(
        bool IsValid,
        string[] Errors)
    {
        public string? Error => Errors.Length > 0 ? Errors[0] : null;

        public static ManualSaPlanFileValidationResult Valid()
            => new(IsValid: true, Errors: []);

        public static ManualSaPlanFileValidationResult Invalid(params string[] errors)
            => new(
                IsValid: false,
                Errors: errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
    }

    public static string ToKeyValuePlan(ManualSaInstallPlan plan)
    {
        var validation = ManualSaInstallPlanValidator.Validate(plan);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"manual SA install plan is invalid: {string.Join("; ", validation.Errors)}");
        }

        return string.Join(
            '\n',
            $"{KeyLocal}={plan.LocalIp}",
            $"{KeyRemote}={plan.RemoteIp}",
            $"{KeySelectorProtocol}={plan.TrafficSelectorProtocol}",
            $"{KeyInboundSpi}={plan.InboundSpi}",
            $"{KeyOutboundSpi}={plan.OutboundSpi}",
            $"{KeyInboundCipher}={ToHex(plan.InboundCipherKeyPlaceholder)}",
            $"{KeyInboundAuth}={ToHex(plan.InboundAuthKeyPlaceholder)}",
            $"{KeyOutboundCipher}={ToHex(plan.OutboundCipherKeyPlaceholder)}",
            $"{KeyOutboundAuth}={ToHex(plan.OutboundAuthKeyPlaceholder)}");
    }

    public static string ToRedactedKeyValuePlan(ManualSaInstallPlan plan)
    {
        var validation = ManualSaInstallPlanValidator.Validate(plan);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"manual SA install plan is invalid: {string.Join("; ", validation.Errors)}");
        }

        return string.Join(
            '\n',
            $"{KeyLocal}={plan.LocalIp}",
            $"{KeyRemote}={plan.RemoteIp}",
            $"{KeySelectorProtocol}={plan.TrafficSelectorProtocol}",
            $"{KeyInboundSpi}={plan.InboundSpi}",
            $"{KeyOutboundSpi}={plan.OutboundSpi}",
            $"{KeyInboundCipher}_len={plan.InboundCipherKeyPlaceholder.Length}",
            $"{KeyInboundAuth}_len={plan.InboundAuthKeyPlaceholder.Length}",
            $"{KeyOutboundCipher}_len={plan.OutboundCipherKeyPlaceholder.Length}",
            $"{KeyOutboundAuth}_len={plan.OutboundAuthKeyPlaceholder.Length}");
    }

    public static ManualSaPlanFileValidationResult ValidateRoundTrip(ManualSaInstallPlan expectedPlan, string serializedPlan)
    {
        var errors = new List<string>();

        if (expectedPlan is null)
        {
            return ManualSaPlanFileValidationResult.Invalid("expected manual SA install plan is required.");
        }

        if (string.IsNullOrWhiteSpace(serializedPlan))
        {
            return ManualSaPlanFileValidationResult.Invalid("serialized plan file is empty.");
        }

        var values = ParseKeyValuePairs(serializedPlan, errors);
        if (errors.Count > 0)
        {
            return ManualSaPlanFileValidationResult.Invalid(errors.ToArray());
        }

        foreach (var key in RequiredKeys)
        {
            if (!values.ContainsKey(key))
            {
                errors.Add($"missing field '{key}' in serialized plan.");
            }
        }

        if (errors.Count > 0)
        {
            return ManualSaPlanFileValidationResult.Invalid(errors.ToArray());
        }

        if (!string.Equals(values[KeyLocal], expectedPlan.LocalIp, StringComparison.Ordinal))
        {
            errors.Add($"serialized local_ip does not match expected '{expectedPlan.LocalIp}'.");
        }

        if (!string.Equals(values[KeyRemote], expectedPlan.RemoteIp, StringComparison.Ordinal))
        {
            errors.Add($"serialized remote_ip does not match expected '{expectedPlan.RemoteIp}'.");
        }

        if (!string.Equals(values[KeySelectorProtocol], expectedPlan.TrafficSelectorProtocol, StringComparison.Ordinal))
        {
            errors.Add("serialized selector_protocol does not match expected value.");
        }

        if (!uint.TryParse(values[KeyInboundSpi], NumberStyles.None, CultureInfo.InvariantCulture, out var inSpi))
        {
            errors.Add("in_spi must be an unsigned integer.");
        }
        else if (inSpi != expectedPlan.InboundSpi)
        {
            errors.Add($"in_spi mismatch: expected {expectedPlan.InboundSpi} got {inSpi}.");
        }

        if (!uint.TryParse(values[KeyOutboundSpi], NumberStyles.None, CultureInfo.InvariantCulture, out var outSpi))
        {
            errors.Add("out_spi must be an unsigned integer.");
        }
        else if (outSpi != expectedPlan.OutboundSpi)
        {
            errors.Add($"out_spi mismatch: expected {expectedPlan.OutboundSpi} got {outSpi}.");
        }

        ValidateHexField(values, KeyInboundCipher, expectedPlan.InboundCipherKeyPlaceholder, errors);
        ValidateHexField(values, KeyInboundAuth, expectedPlan.InboundAuthKeyPlaceholder, errors);
        ValidateHexField(values, KeyOutboundCipher, expectedPlan.OutboundCipherKeyPlaceholder, errors);
        ValidateHexField(values, KeyOutboundAuth, expectedPlan.OutboundAuthKeyPlaceholder, errors);

        return errors.Count == 0
            ? ManualSaPlanFileValidationResult.Valid()
            : ManualSaPlanFileValidationResult.Invalid(errors.ToArray());
    }

    private static Dictionary<string, string> ParseKeyValuePairs(string text, List<string> errors)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                errors.Add($"invalid line '{line}'. expected key=value.");
                continue;
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add($"invalid line '{line}'. key cannot be empty.");
                continue;
            }

            if (!values.TryAdd(key, value))
            {
                errors.Add($"duplicate field '{key}' in serialized plan.");
            }
        }

        return values;
    }

    private static void ValidateHexField(
        IReadOnlyDictionary<string, string> values,
        string key,
        byte[] expectedBytes,
        List<string> errors)
    {
        if (!values.TryGetValue(key, out var hex))
        {
            errors.Add($"missing field '{key}' in serialized plan.");
            return;
        }

        byte[]? parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(hex)
                ? throw new FormatException("empty hex string")
                : Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            errors.Add($"field '{key}' has invalid hex encoding.");
            return;
        }

        if (parsed.Length != expectedBytes.Length)
        {
            errors.Add(
                $"field '{key}' length mismatch: expected {expectedBytes.Length} bytes, got {parsed.Length} bytes.");
        }
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
