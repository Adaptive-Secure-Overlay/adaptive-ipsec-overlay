namespace AdaptiveClient.Core.Models;

public sealed record EspMaterial(
    string LocalIp,
    string RemoteIp,
    string Protocol,
    EspProfile Profile,
    EspMode Mode,
    uint InboundSpi,
    uint OutboundSpi,
    string InboundCipherAlgorithm,
    byte[] InboundCipherKey,
    string InboundAuthAlgorithm,
    byte[] InboundAuthKey,
    string OutboundCipherAlgorithm,
    byte[] OutboundCipherKey,
    string OutboundAuthAlgorithm,
    byte[] OutboundAuthKey,
    int LifetimeSeconds,
    ulong MaxBytes)
{
    public string ToLogSafeSummary() =>
        $"{nameof(EspMaterial)}(protocol={Protocol}, profile={Profile.Name}, mode={Mode}, local_ip={LocalIp}, remote_ip={RemoteIp}, " +
        $"inbound_spi={InboundSpi}, outbound_spi={OutboundSpi}, lifetime_seconds={LifetimeSeconds}, max_bytes={MaxBytes}, " +
        $"inbound_cipher={InboundCipherAlgorithm}, inbound_auth={InboundAuthAlgorithm}, " +
        $"outbound_cipher={OutboundCipherAlgorithm}, outbound_auth={OutboundAuthAlgorithm}, " +
        $"inbound_cipher_len={InboundCipherKey.Length}, inbound_auth_len={InboundAuthKey.Length}, " +
        $"outbound_cipher_len={OutboundCipherKey.Length}, outbound_auth_len={OutboundAuthKey.Length})";

    public string ToLogSafeJson() => System.Text.Json.JsonSerializer.Serialize(ToLogSafe());

    public EspMaterialLog ToLogSafe() => new EspMaterialLog(
        LocalIp: LocalIp,
        RemoteIp: RemoteIp,
        Protocol: Protocol,
        Profile: Profile.Name,
        Mode: Mode,
        InboundSpi: InboundSpi,
        OutboundSpi: OutboundSpi,
        InboundCipherAlgorithm: InboundCipherAlgorithm,
        InboundAuthAlgorithm: InboundAuthAlgorithm,
        OutboundCipherAlgorithm: OutboundCipherAlgorithm,
        OutboundAuthAlgorithm: OutboundAuthAlgorithm,
        LifetimeSeconds: LifetimeSeconds,
        MaxBytes: MaxBytes);

    public ManualSaInstallPlan ToManualSaInstallPlan() => new ManualSaInstallPlan(
        LocalIp: LocalIp,
        RemoteIp: RemoteIp,
        IpsecProtocol: ResolveIpsecProtocol(),
        TrafficSelectorProtocol: ManualSaInstallPlan.DefaultTrafficSelectorProtocol,
        Profile: ResolveProfile(),
        InboundSpi: InboundSpi,
        OutboundSpi: OutboundSpi,
        InboundCipherKeyPlaceholder: InboundCipherKey,
        InboundAuthKeyPlaceholder: InboundAuthKey,
        OutboundCipherKeyPlaceholder: OutboundCipherKey,
        OutboundAuthKeyPlaceholder: OutboundAuthKey);

    public ManualSaInstallPlan ToManualSaInstallPlan(
        EndpointRecord localEndpoint,
        EndpointRecord remoteEndpoint)
    {
        if (localEndpoint is null)
        {
            throw new InvalidOperationException("local endpoint record is required.");
        }

        if (remoteEndpoint is null)
        {
            throw new InvalidOperationException("remote endpoint record is required.");
        }

        return new ManualSaInstallPlan(
            LocalIp: localEndpoint.Ip,
            RemoteIp: remoteEndpoint.Ip,
            IpsecProtocol: ResolveIpsecProtocol(),
            TrafficSelectorProtocol: ResolveEndpointSelectorProtocol(localEndpoint, remoteEndpoint),
            Profile: ResolveProfile(),
            InboundSpi: InboundSpi,
            OutboundSpi: OutboundSpi,
            InboundCipherKeyPlaceholder: InboundCipherKey,
            InboundAuthKeyPlaceholder: InboundAuthKey,
            OutboundCipherKeyPlaceholder: OutboundCipherKey,
            OutboundAuthKeyPlaceholder: OutboundAuthKey);
    }

    public override string ToString() => ToLogSafeSummary();

    private EspProfile ResolveProfile()
    {
        if (Profile is null)
        {
            return EspProfiles.ResolveFromParameters(InboundCipherAlgorithm, InboundAuthAlgorithm);
        }

        if (string.Equals(Profile.Name, EspProfiles.Unknown.Name, StringComparison.OrdinalIgnoreCase))
        {
            return EspProfiles.ResolveFromParameters(InboundCipherAlgorithm, InboundAuthAlgorithm);
        }

        return Profile;
    }

    private string ResolveIpsecProtocol()
    {
        return string.IsNullOrWhiteSpace(Protocol)
            ? ManualSaInstallPlan.IpsecProtocolEsp
            : Protocol;
    }

    private static string ResolveEndpointSelectorProtocol(
        EndpointRecord localEndpoint,
        EndpointRecord remoteEndpoint)
    {
        var localSelector = localEndpoint.SelectorProtocol?.Trim() ?? string.Empty;
        var remoteSelector = remoteEndpoint.SelectorProtocol?.Trim() ?? string.Empty;

        if (!ManualSaInstallPlanValidator.IsValidTrafficSelectorProtocol(localSelector))
        {
            throw new InvalidOperationException("local endpoint selector_protocol is invalid.");
        }

        if (!ManualSaInstallPlanValidator.IsValidTrafficSelectorProtocol(remoteSelector))
        {
            throw new InvalidOperationException("remote endpoint selector_protocol is invalid.");
        }

        if (string.Equals(localSelector, remoteSelector, StringComparison.OrdinalIgnoreCase))
        {
            return remoteSelector;
        }

        if (string.Equals(localSelector, "any", StringComparison.OrdinalIgnoreCase))
        {
            return remoteSelector;
        }

        if (string.Equals(remoteSelector, "any", StringComparison.OrdinalIgnoreCase))
        {
            return localSelector;
        }

        throw new InvalidOperationException("endpoint selector_protocol values must match or include 'any'.");
    }
}

public sealed record EspMaterialLog(
    string LocalIp,
    string RemoteIp,
    string Protocol,
    string Profile,
    EspMode Mode,
    uint InboundSpi,
    uint OutboundSpi,
    string InboundCipherAlgorithm,
    string InboundAuthAlgorithm,
    string OutboundCipherAlgorithm,
    string OutboundAuthAlgorithm,
    int LifetimeSeconds,
    ulong MaxBytes);

public sealed record ManualSaInstallPlan(
    string LocalIp,
    string RemoteIp,
    string IpsecProtocol,
    string TrafficSelectorProtocol,
    EspProfile Profile,
    uint InboundSpi,
    uint OutboundSpi,
    byte[] InboundCipherKeyPlaceholder,
    byte[] InboundAuthKeyPlaceholder,
    byte[] OutboundCipherKeyPlaceholder,
    byte[] OutboundAuthKeyPlaceholder)
{
    public const string IpsecProtocolEsp = "ESP";
    public const string IpsecProtocolEspNumeric = "50";
    public const string DefaultTrafficSelectorProtocol = "icmp";

    public string ToLogSafeSummary() =>
        $"{nameof(ManualSaInstallPlan)}(ipsec_protocol={IpsecProtocol}, traffic_selector_protocol={TrafficSelectorProtocol}, " +
        $"profile={Profile.Name}, local_ip={LocalIp}, remote_ip={RemoteIp}, " +
        $"inbound_spi={InboundSpi}, outbound_spi={OutboundSpi}, " +
        $"inbound_cipher_placeholder_len={InboundCipherKeyPlaceholder.Length}, " +
        $"inbound_auth_placeholder_len={InboundAuthKeyPlaceholder.Length}, " +
        $"outbound_cipher_placeholder_len={OutboundCipherKeyPlaceholder.Length}, " +
        $"outbound_auth_placeholder_len={OutboundAuthKeyPlaceholder.Length}, " +
        $"profile_inbound_cipher_len={Profile.KeyLengths.InboundCipherKeyLength}, " +
        $"profile_inbound_auth_len={Profile.KeyLengths.InboundAuthKeyLength}, " +
        $"profile_outbound_cipher_len={Profile.KeyLengths.OutboundCipherKeyLength}, " +
        $"profile_outbound_auth_len={Profile.KeyLengths.OutboundAuthKeyLength})";

    public ManualSaInstallPlanLog ToLogSafe() => new ManualSaInstallPlanLog(
        LocalIp: LocalIp,
        RemoteIp: RemoteIp,
        IpsecProtocol: IpsecProtocol,
        TrafficSelectorProtocol: TrafficSelectorProtocol,
        Profile: Profile.Name,
        InboundSpi: InboundSpi,
        OutboundSpi: OutboundSpi,
        InboundCipherKeyPlaceholderLength: InboundCipherKeyPlaceholder.Length,
        InboundAuthKeyPlaceholderLength: InboundAuthKeyPlaceholder.Length,
        OutboundCipherKeyPlaceholderLength: OutboundCipherKeyPlaceholder.Length,
        OutboundAuthKeyPlaceholderLength: OutboundAuthKeyPlaceholder.Length,
        ProfileInboundCipherKeyLength: Profile.KeyLengths.InboundCipherKeyLength,
        ProfileInboundAuthKeyLength: Profile.KeyLengths.InboundAuthKeyLength,
        ProfileOutboundCipherKeyLength: Profile.KeyLengths.OutboundCipherKeyLength,
        ProfileOutboundAuthKeyLength: Profile.KeyLengths.OutboundAuthKeyLength);

    public override string ToString() => ToLogSafeSummary();
}

public sealed record ManualSaInstallPlanLog(
    string LocalIp,
    string RemoteIp,
    string IpsecProtocol,
    string TrafficSelectorProtocol,
    string Profile,
    uint InboundSpi,
    uint OutboundSpi,
    int InboundCipherKeyPlaceholderLength,
    int InboundAuthKeyPlaceholderLength,
    int OutboundCipherKeyPlaceholderLength,
    int OutboundAuthKeyPlaceholderLength,
    int ProfileInboundCipherKeyLength,
    int ProfileInboundAuthKeyLength,
    int ProfileOutboundCipherKeyLength,
    int ProfileOutboundAuthKeyLength);

public sealed record ManualSaInstallPlanValidationResult(
    bool IsValid,
    string[] Errors)
{
    public string? Error => Errors.Length > 0 ? Errors[0] : null;

    public static ManualSaInstallPlanValidationResult Valid()
        => new(IsValid: true, Errors: []);

    public static ManualSaInstallPlanValidationResult Invalid(params string[] errors)
        => new(IsValid: false, Errors: errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray());
}

public static class ManualSaInstallPlanValidator
{
    public static ManualSaInstallPlanValidationResult Validate(ManualSaInstallPlan plan)
    {
        var errors = new List<string>();

        if (plan is null)
        {
            return ManualSaInstallPlanValidationResult.Invalid("manual SA install plan is null.");
        }

        var localIp = plan.LocalIp?.Trim();
        var remoteIp = plan.RemoteIp?.Trim();
        if (string.IsNullOrWhiteSpace(localIp))
        {
            errors.Add("manual SA plan local_ip is required.");
        }

        if (string.IsNullOrWhiteSpace(remoteIp))
        {
            errors.Add("manual SA plan remote_ip is required.");
        }

        if (!string.IsNullOrWhiteSpace(localIp)
            && !string.IsNullOrWhiteSpace(remoteIp)
            && string.Equals(localIp, remoteIp, StringComparison.Ordinal))
        {
            errors.Add("manual SA plan local_ip and remote_ip must be different.");
        }

        if (!IsIpsecProtocolEsp(plan.IpsecProtocol))
        {
            errors.Add($"manual SA plan ipsec_protocol must be {ManualSaInstallPlan.IpsecProtocolEsp} or {ManualSaInstallPlan.IpsecProtocolEspNumeric}.");
        }

        if (!IsValidTrafficSelectorProtocol(plan.TrafficSelectorProtocol))
        {
            errors.Add("manual SA plan traffic_selector_protocol must be 'icmp', 'any', or a numeric L4 protocol value.");
        }

        if (plan.Profile is null)
        {
            errors.Add("manual SA plan profile is required.");
        }

        if (plan.Profile?.KeyLengths is null)
        {
            errors.Add("manual SA plan profile key lengths are required.");
        }

        if (plan.InboundSpi == 0)
        {
            errors.Add("manual SA plan inbound_spi must be non-zero.");
        }

        if (plan.OutboundSpi == 0)
        {
            errors.Add("manual SA plan outbound_spi must be non-zero.");
        }

        if (plan.InboundSpi != 0 && plan.OutboundSpi != 0 && plan.InboundSpi == plan.OutboundSpi)
        {
            errors.Add("manual SA plan inbound_spi and outbound_spi must be different.");
        }

        var lengths = plan.Profile?.KeyLengths ?? new EspKeyLengths(0, 0, 0, 0);
        ValidateKeyArray(plan.InboundCipherKeyPlaceholder, lengths.InboundCipherKeyLength, "manual SA plan inbound_cipher_key_placeholder", errors);
        ValidateKeyArray(plan.InboundAuthKeyPlaceholder, lengths.InboundAuthKeyLength, "manual SA plan inbound_auth_key_placeholder", errors);
        ValidateKeyArray(plan.OutboundCipherKeyPlaceholder, lengths.OutboundCipherKeyLength, "manual SA plan outbound_cipher_key_placeholder", errors);
        ValidateKeyArray(plan.OutboundAuthKeyPlaceholder, lengths.OutboundAuthKeyLength, "manual SA plan outbound_auth_key_placeholder", errors);

        return errors.Count == 0
            ? ManualSaInstallPlanValidationResult.Valid()
            : ManualSaInstallPlanValidationResult.Invalid(errors.ToArray());
    }

    private static void ValidateKeyArray(byte[]? keyBytes, int expectedLength, string name, List<string> errors)
    {
        if (keyBytes is null || expectedLength <= 0)
        {
            errors.Add($"manual SA plan has invalid expected length for {name}.");
            return;
        }

        if (keyBytes.Length != expectedLength)
        {
            errors.Add($"{name} must be {expectedLength} bytes.");
        }
    }

    private static bool IsIpsecProtocolEsp(string? ipsecProtocol)
    {
        if (string.IsNullOrWhiteSpace(ipsecProtocol))
        {
            return false;
        }

        return string.Equals(ipsecProtocol.Trim(), ManualSaInstallPlan.IpsecProtocolEsp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(ipsecProtocol.Trim(), ManualSaInstallPlan.IpsecProtocolEspNumeric, StringComparison.Ordinal);
    }

    public static bool IsValidTrafficSelectorProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return false;
        }

        var trimmed = protocol.Trim();
        if (string.Equals(trimmed, "icmp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return int.TryParse(trimmed, out var numeric)
               && numeric is >= 0 and <= 255;
    }
}

public sealed record EspKeyLengths(
    int InboundCipherKeyLength,
    int InboundAuthKeyLength,
    int OutboundCipherKeyLength,
    int OutboundAuthKeyLength)
{
    public static EspKeyLengths Uniform(int cipherLength, int authLength)
        => new(
            InboundCipherKeyLength: cipherLength,
            InboundAuthKeyLength: authLength,
            OutboundCipherKeyLength: cipherLength,
            OutboundAuthKeyLength: authLength);
}

public sealed record EspProfile(string Name, EspKeyLengths KeyLengths);

public static class EspProfiles
{
    public static readonly EspProfile ManualSa = new(
        Name: "ESP_AES_128_CBC_HMAC_SHA256_128",
        KeyLengths: EspKeyLengths.Uniform(cipherLength: 16, authLength: 32));

    public static readonly EspProfile LegacyAes256 = new(
        Name: "ESP_AES_256_CBC_HMAC_SHA256_128",
        KeyLengths: EspKeyLengths.Uniform(cipherLength: 32, authLength: 32));

    public static readonly EspProfile Unknown = new(
        Name: "ESP_UNKNOWN",
        KeyLengths: EspKeyLengths.Uniform(cipherLength: 32, authLength: 32));

    public static EspProfile Resolve(string? profile)
    {
        return string.Equals(profile, ManualSa.Name, StringComparison.OrdinalIgnoreCase)
            ? ManualSa
            : string.Equals(profile, LegacyAes256.Name, StringComparison.OrdinalIgnoreCase)
                ? LegacyAes256
                : ManualSa;
    }

    public static EspProfile ResolveFromParameters(string? inboundCipherAlgorithm, string? inboundAuthAlgorithm)
    {
        // Small deterministic defaulting for smoke only:
        if (string.Equals(inboundCipherAlgorithm, "AES-128-CBC", StringComparison.OrdinalIgnoreCase))
        {
            return ManualSa;
        }

        if (string.Equals(inboundCipherAlgorithm, "AES-256-CBC", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyAes256;
        }

        if (string.Equals(inboundAuthAlgorithm, "HMAC-SHA256-128", StringComparison.OrdinalIgnoreCase))
        {
            return ManualSa;
        }

        return LegacyAes256;
    }
}

public sealed record EspInstallParameters(
    string LocalIp,
    string RemoteIp,
    string Protocol,
    string EspProfile,
    uint InboundSpi,
    uint OutboundSpi,
    string InboundCipherAlgorithm,
    string InboundAuthAlgorithm,
    string OutboundCipherAlgorithm,
    string OutboundAuthAlgorithm,
    int LifetimeSeconds,
    ulong MaxBytes);

public sealed record EspKeyMaterial(
    byte[] InboundCipherKey,
    byte[] InboundAuthKey,
    byte[] OutboundCipherKey,
    byte[] OutboundAuthKey);

public enum EspMode
{
    Transport,
    Tunnel
}
