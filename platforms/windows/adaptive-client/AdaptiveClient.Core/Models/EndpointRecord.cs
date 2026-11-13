using System.Net;

namespace AdaptiveClient.Core.Models;

public sealed record EndpointRecord(
    string NodeId,
    string Ip,
    int? Port,
    string SelectorProtocol,
    string Source,
    ulong Sequence,
    DateTimeOffset ValidFrom,
    DateTimeOffset ExpiresAt,
    string? Signature)
{
    public string ToLogSafeSummary() =>
        $"{nameof(EndpointRecord)}(node_id={NodeId}, ip={Ip}, port={Port?.ToString() ?? "<none>"}, " +
        $"selector_protocol={SelectorProtocol}, source={Source}, sequence={Sequence}, " +
        $"valid_from={ValidFrom:o}, expires_at={ExpiresAt:o}, signature_present={!string.IsNullOrWhiteSpace(Signature)})";

    public EndpointRecordLog ToLogSafe() => new(
        NodeId: NodeId,
        Ip: Ip,
        Port: Port,
        SelectorProtocol: SelectorProtocol,
        Source: Source,
        Sequence: Sequence,
        ValidFrom: ValidFrom,
        ExpiresAt: ExpiresAt,
        SignaturePresent: !string.IsNullOrWhiteSpace(Signature));

    public override string ToString() => ToLogSafeSummary();
}

public sealed record EndpointRecordLog(
    string NodeId,
    string Ip,
    int? Port,
    string SelectorProtocol,
    string Source,
    ulong Sequence,
    DateTimeOffset ValidFrom,
    DateTimeOffset ExpiresAt,
    bool SignaturePresent);

public sealed record EndpointRecordValidationResult(
    bool IsValid,
    string[] Errors)
{
    public string? Error => Errors.Length > 0 ? Errors[0] : null;

    public static EndpointRecordValidationResult Valid()
        => new(IsValid: true, Errors: []);

    public static EndpointRecordValidationResult Invalid(params string[] errors)
        => new(IsValid: false, Errors: errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
}

public static class EndpointRecordValidator
{
    public static EndpointRecordValidationResult Validate(
        EndpointRecord? record,
        DateTimeOffset now,
        ulong? previousSeq = null)
    {
        if (record is null)
        {
            return EndpointRecordValidationResult.Invalid("endpoint record is null.");
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(record.NodeId))
        {
            errors.Add("endpoint record node_id is required.");
        }

        if (string.IsNullOrWhiteSpace(record.Ip))
        {
            errors.Add("endpoint record ip is required.");
        }
        else if (!IPAddress.TryParse(record.Ip, out _))
        {
            errors.Add("endpoint record ip must be a valid IP address.");
        }

        if (record.Port is <= 0 or > 65535)
        {
            errors.Add("endpoint record port must be in range 1..65535 when present.");
        }

        if (!ManualSaInstallPlanValidator.IsValidTrafficSelectorProtocol(record.SelectorProtocol))
        {
            errors.Add("endpoint record selector_protocol must be 'icmp', 'any', or a numeric L4 protocol value.");
        }

        if (string.IsNullOrWhiteSpace(record.Source))
        {
            errors.Add("endpoint record source is required.");
        }

        if (record.Sequence == 0)
        {
            errors.Add("endpoint record sequence must be non-zero.");
        }

        if (previousSeq.HasValue && record.Sequence <= previousSeq.Value)
        {
            errors.Add($"endpoint record sequence must advance previous sequence {previousSeq.Value}.");
        }

        if (record.ExpiresAt <= record.ValidFrom)
        {
            errors.Add("endpoint record expires_at must be after valid_from.");
        }

        if (now < record.ValidFrom)
        {
            errors.Add($"endpoint record is not valid yet (valid_from={record.ValidFrom:o}).");
        }

        if (now >= record.ExpiresAt)
        {
            errors.Add($"endpoint record is expired (expires_at={record.ExpiresAt:o}).");
        }

        if (string.IsNullOrWhiteSpace(record.Signature))
        {
            errors.Add("endpoint record signature placeholder is required.");
        }

        return errors.Count == 0
            ? EndpointRecordValidationResult.Valid()
            : EndpointRecordValidationResult.Invalid(errors.ToArray());
    }
}

public sealed record EndpointDirectoryResolveResult(
    bool Success,
    EndpointRecord? Endpoint,
    EndpointRecordValidationResult Validation,
    string? Error)
{
    public EndpointRecordLog? EndpointLog => Endpoint?.ToLogSafe();

    public static EndpointDirectoryResolveResult Ok(EndpointRecord endpoint)
        => new(
            Success: true,
            Endpoint: endpoint,
            Validation: EndpointRecordValidationResult.Valid(),
            Error: null);

    public static EndpointDirectoryResolveResult Fail(string error)
        => new(
            Success: false,
            Endpoint: null,
            Validation: EndpointRecordValidationResult.Invalid(error),
            Error: error);
}

public sealed class EndpointDirectorySnapshot
{
    public EndpointDirectorySnapshot(IEnumerable<EndpointRecord> records)
    {
        Records = records?.ToArray() ?? [];
    }

    public IReadOnlyList<EndpointRecord> Records { get; }

    public EndpointDirectoryResolveResult ResolveLatest(string nodeId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return EndpointDirectoryResolveResult.Fail("node_id is required for endpoint resolution.");
        }

        var candidates = Records
            .Where(record => string.Equals(record.NodeId, nodeId, StringComparison.Ordinal))
            .OrderByDescending(record => record.Sequence)
            .ThenByDescending(record => record.ExpiresAt)
            .ToArray();

        if (candidates.Length == 0)
        {
            return EndpointDirectoryResolveResult.Fail($"no endpoint records found for node_id '{nodeId}'.");
        }

        foreach (var candidate in candidates)
        {
            var validation = EndpointRecordValidator.Validate(candidate, now);
            if (validation.IsValid)
            {
                return EndpointDirectoryResolveResult.Ok(candidate);
            }
        }

        return EndpointDirectoryResolveResult.Fail($"no valid non-expired endpoint records found for node_id '{nodeId}'.");
    }
}
