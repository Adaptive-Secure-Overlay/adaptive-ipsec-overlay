using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Globalization;
using System.Diagnostics;
using AdaptiveClient.Core;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions IpcJsonOptions = new(JsonOptions)
    {
        WriteIndented = false
    };

    private static readonly string[] ForbiddenSecretMarkers =
    [
        "\"in_cipher_key\"",
        "\"in_auth_key\"",
        "\"out_cipher_key\"",
        "\"out_auth_key\"",
        "in_cipher_key=",
        "in_auth_key=",
        "out_cipher_key=",
        "out_auth_key="
    ];

    public static async Task<int> Main(string[] args)
    {
        var json = IsJson(args);
        if (args.Length == 0)
        {
            return WriteCliResult(false, "help", new CliError("cli.invalid_usage", "no command provided"), json);
        }

        if (args.Any(IsHelp))
        {
            PrintHelp();
            return 0;
        }

        if (!HasCommand(args))
        {
            return WriteCliResult(false, "help", new CliError("cli.invalid_usage", "no command provided"), json);
        }

        var configPath = Value(args, "--config");
        var commandIndex = GetCommandIndex(args);
        if (commandIndex < 0)
        {
            return WriteCliResult(false, "help", new CliError("cli.invalid_usage", "no command provided"), json);
        }

        var command = args[commandIndex].ToLowerInvariant();
        var positionals = GetPositionals(args, commandIndex);
        var peer = positionals.FirstOrDefault();

        if ((command is "connect" or "rekey" or "cleanup" or "manual-sa-plan" or "peer-install") && string.IsNullOrWhiteSpace(peer))
        {
            return WriteCliResult(false, command, new CliError("cli.missing_peer", $"{command} requires <peer>"), json);
        }

        if ((command == "peer-install" || command == "peer-install-envelope") && string.IsNullOrWhiteSpace(Value(args, "--payload-file")))
        {
            return WriteCliResult(
                false,
                command,
                new CliError("cli.missing_payload_file", $"{command} requires --payload-file <payload.json>"),
                json);
        }

        if (command == "endpoint-update" && positionals.Length < 2)
        {
            return WriteCliResult(
                false,
                command,
                new CliError("cli.missing_endpoint", "endpoint-update requires <node_id> <ip>"),
                json);
        }

        if (command is not ("status" or "connect" or "rekey" or "cleanup" or "maintenance" or "self-test" or "manual-sa-plan" or "peer-install" or "peer-install-envelope" or "endpoint-status" or "endpoint-update" or "aike-key-export" or "aike-live-proof"))
        {
            return WriteCliResult(
                false,
                command,
                new CliError("cli.unknown_command", $"Unknown command: {command}"),
                json);
        }

        if (command == "aike-key-export")
        {
            return WriteAikeKeyExport(args, json);
        }

        if (command == "peer-install-envelope")
        {
            return WritePeerInstallEnvelope(args, json);
        }

        if (command == "aike-live-proof")
        {
            return await WriteAikeLiveProofAsync(args, configPath, json).ConfigureAwait(false);
        }

        var port = await ResolveListenPortAsync(configPath).ConfigureAwait(false);
        if (int.TryParse(Value(args, "--port"), out var explicitPort))
        {
            port = explicitPort;
        }

        string request;
        try
        {
            request = BuildRequestPayload(command, peer, positionals, args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return WriteCliResult(
                false,
                command,
                new CliError("cli.invalid_payload", ex.Message),
                json);
        }

        try
        {
            var response = await SendAsync(port, request).ConfigureAwait(false);
            return WriteResponse(response, json);
        }
        catch (SocketException ex)
        {
            return WriteCliResult(
                false,
                command,
                new CliError("cli.service_unreachable", $"Service is not reachable on 127.0.0.1:{port}: {ex.Message}"),
                json);
        }
    }

    private static string BuildRequestPayload(
        string command,
        string? peer,
        string[] positionals,
        string[] args)
    {
        if (command is "status" or "self-test" or "endpoint-status" or "maintenance")
        {
            return command;
        }

        if (command == "endpoint-update")
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["node_id"] = positionals[0],
                ["ip"] = positionals[1],
                ["selector_protocol"] = Value(args, "--selector")
                    ?? Value(args, "--selector-protocol")
                    ?? "icmp"
            };

            AddOptional(parameters, "port", Value(args, "--endpoint-port"));
            AddOptional(parameters, "ttl_seconds", Value(args, "--ttl-seconds"));

            return JsonSerializer.Serialize(
                new ControlCommand(command, Peer: positionals[0], Parameters: parameters),
                IpcJsonOptions);
        }

        if (command == "peer-install")
        {
            var payloadFile = Value(args, "--payload-file")
                ?? throw new InvalidOperationException("peer-install requires --payload-file");
            var parameters = LoadPayloadParameters(payloadFile);
            parameters.TryAdd("peer_id", peer ?? string.Empty);
            return JsonSerializer.Serialize(
                new ControlCommand(
                    Command: command,
                    Peer: peer,
                    SessionId: parameters.TryGetValue("session_id", out var sessionId) ? sessionId : null,
                    Parameters: parameters),
                IpcJsonOptions);
        }

        return $"{command} {peer}";
    }

    private static Dictionary<string, string> LoadPayloadParameters(string payloadFile)
    {
        var fullPath = Path.GetFullPath(payloadFile);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("peer-install payload must be a flat JSON object");
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException($"peer-install payload field '{property.Name}' must be a scalar value")
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[property.Name] = value;
            }
        }

        return parameters;
    }

    private static int WritePeerInstallEnvelope(string[] args, bool json)
    {
        var payloadFile = Value(args, "--payload-file")
            ?? throw new InvalidOperationException("peer-install-envelope requires --payload-file");
        var output = Value(args, "--output")
            ?? throw new InvalidOperationException("peer-install-envelope requires --output <envelope.json>");
        var secret = Value(args, "--envelope-secret")
            ?? Environment.GetEnvironmentVariable("ADAPTIVE_PEER_INSTALL_ENVELOPE_SECRET")
            ?? "lab-peer-install-envelope-secret-change-me";
        var recipientX25519PublicKey = Value(args, "--recipient-x25519-public")
            ?? Value(args, "--recipient-public-key");
        var payload = LoadPayloadParameters(payloadFile);
        string Required(string key) => payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"peer-install-envelope payload missing '{key}'");

        var senderId = Value(args, "--sender")
            ?? payload.GetValueOrDefault("remote_peer_id")
            ?? payload.GetValueOrDefault("sender_id")
            ?? "unknown-sender";
        var recipientId = Value(args, "--recipient")
            ?? payload.GetValueOrDefault("peer_id")
            ?? payload.GetValueOrDefault("recipient_id")
            ?? "unknown-recipient";
        var envelope = string.IsNullOrWhiteSpace(recipientX25519PublicKey)
            ? PeerInstallEnvelope.Encrypt(
                payload,
                secret,
                senderId,
                recipientId,
                Required("session_id"),
                Required("route_id"))
            : PeerInstallEnvelope.EncryptForRecipient(
                payload,
                recipientX25519PublicKey,
                senderId,
                recipientId,
                Required("session_id"),
                Required("route_id"));

        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(outputPath, JsonSerializer.Serialize(envelope, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return WriteCliResult(
            true,
            "peer-install-envelope",
            null,
            new
            {
                output = outputPath,
                envelope_version = envelope.EnvelopeVersion,
                envelope_mode = envelope.EnvelopeMode,
                envelope_encryption = envelope.EnvelopeEncryption,
                envelope_kdf = envelope.EnvelopeKdf,
                sender_id = envelope.EnvelopeSenderId,
                recipient_id = envelope.EnvelopeRecipientId,
                session_id = envelope.SessionId,
                route_id = envelope.RouteId,
                encrypted = true,
                raw_keys_logged = false
            },
            json);
    }

    private static void AddOptional(Dictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value;
        }
    }

    private static int WriteAikeKeyExport(string[] args, bool json)
    {
        var local = Value(args, "--local") ?? "192.168.95.1";
        var remote = Value(args, "--remote") ?? "192.168.113.199";
        var localPeer = Value(args, "--local-peer") ?? "HostWindows";
        var remotePeer = Value(args, "--remote-peer") ?? "Windows11-U13";
        var routeId = Value(args, "--route-id") ?? $"live-aike:{localPeer}->{remotePeer}";
        var profile = Value(args, "--profile") ?? EspProfiles.ManualSa.Name;
        var hostInboundSpi = ParseUIntOption(args, "--host-in-spi", 0xA100);
        var peerInboundSpi = ParseUIntOption(args, "--peer-in-spi", 0xB200);

        var result = AikeManualSaKeyExporter.Export(new AikeManualSaKeyExportRequest(
            HostIp: local,
            PeerIp: remote,
            HostPeerId: localPeer,
            PeerPeerId: remotePeer,
            RouteId: routeId,
            EspProfile: profile,
            HostInboundSpi: hostInboundSpi,
            PeerInboundSpi: peerInboundSpi));

        if (!result.Success)
        {
            return WriteCliResult(
                false,
                "aike-key-export",
                new CliError("aike.key_export_failed", result.Error ?? "AIKE key export failed."),
                json);
        }

        if (json)
        {
            WriteJson(new CliResponse(true, "aike-key-export", null, result));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(CommandResponse.Ok("aike-key-export", result), JsonOptions));
        }

        return 0;
    }

    private static uint ParseUIntOption(string[] args, string name, uint fallback)
    {
        var raw = Value(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedHex)
                ? parsedHex
                : fallback;
        }

        return uint.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static async Task<int> WriteAikeLiveProofAsync(string[] args, string? configPath, bool json)
    {
        var configFile = configPath ?? FindDefaultConfigPath();
        var local = Value(args, "--local") ?? "192.168.95.1";
        var remote = Value(args, "--remote") ?? "192.168.113.199";
        var localPeer = Value(args, "--local-peer") ?? "HostWindows";
        var remotePeer = Value(args, "--remote-peer") ?? "Windows11-U13";
        var helperListenHost = Value(args, "--helper-listen-host") ?? local;
        var helperPort = ParseIntOption(args, "--helper-port", 18091);
        var agentNodeId = Value(args, "--agent-node-id") ?? remotePeer;
        var controlPlanePorts = Value(args, "--control-plane-ports") ?? "18091,9000";
        var tcpPort = ParseIntOption(args, "--tcp-port", 18101);
        var udpPort = ParseIntOption(args, "--udp-port", 18102);
        var waitSeconds = ParseIntOption(args, "--wait-seconds", 120);
        var timeoutSeconds = ParseIntOption(args, "--timeout-seconds", Math.Max(waitSeconds + 90, 180));
        var scriptPath = Value(args, "--script") ?? ResolveAikeProofScriptPath(configFile);

        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            return WriteCliResult(
                false,
                "aike-live-proof",
                new CliError("aike.live_proof_script_missing", $"AIKE live proof script not found: {scriptPath}"),
                json);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        AddNamedArgument(psi, "-Local", local);
        AddNamedArgument(psi, "-Remote", remote);
        AddNamedArgument(psi, "-LocalPeerId", localPeer);
        AddNamedArgument(psi, "-RemotePeerId", remotePeer);
        AddNamedArgument(psi, "-HelperListenHost", helperListenHost);
        AddNamedArgument(psi, "-HelperPort", helperPort.ToString(CultureInfo.InvariantCulture));
        AddNamedArgument(psi, "-AgentNodeId", agentNodeId);
        AddNamedArgument(psi, "-ControlPlanePorts", controlPlanePorts);
        AddNamedArgument(psi, "-TcpPort", tcpPort.ToString(CultureInfo.InvariantCulture));
        AddNamedArgument(psi, "-UdpPort", udpPort.ToString(CultureInfo.InvariantCulture));
        AddNamedArgument(psi, "-WaitSeconds", waitSeconds.ToString(CultureInfo.InvariantCulture));
        if (HasFlag(args, "--keep-sa"))
        {
            psi.ArgumentList.Add("-KeepSa");
        }

        if (HasFlag(args, "--keep-key-file"))
        {
            psi.ArgumentList.Add("-KeepKeyFile");
        }

        var startedAt = DateTimeOffset.UtcNow;
        using var process = Process.Start(psi);
        if (process is null)
        {
            return WriteCliResult(
                false,
                "aike-live-proof",
                new CliError("aike.live_proof_start_failed", "Failed to start powershell.exe."),
                json);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var completed = await WaitForExitWithTimeoutAsync(process, TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var durationMs = (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        var reportPath = MatchFirst(stdout, @"aike_protocol0_proof_report=(.+)");
        var markdownPath = MatchFirst(stdout, @"aike_protocol0_proof_markdown=(.+)");
        var reportText = TryReadText(reportPath);
        var proof = TryReadJson(reportText);
        var reportSecretFindings = FindForbiddenSecretMarkers(reportText);
        var outputSecretFindings = FindForbiddenSecretMarkers(stdout + Environment.NewLine + stderr);
        var redactionOk = reportSecretFindings.Length == 0 && outputSecretFindings.Length == 0;
        var exitCode = completed ? process.ExitCode : -9;

        var data = new
        {
            exit_code = exitCode,
            duration_ms = durationMs,
            timeout_seconds = timeoutSeconds,
            timed_out = !completed,
            script = scriptPath,
            report_path = reportPath,
            markdown_path = markdownPath,
            proof_report_present = proof is not null,
            report_redaction_ok = redactionOk,
            report_secret_findings = reportSecretFindings,
            output_secret_findings = outputSecretFindings,
            local,
            remote,
            local_peer = localPeer,
            remote_peer = remotePeer,
            helper_port = helperPort,
            proof = proof,
            stdout_tail = Tail(stdout, 80),
            stderr_tail = Tail(stderr, 40)
        };

        if (!completed)
        {
            if (json)
            {
                WriteJson(new CliResponse(
                    false,
                    "aike-live-proof",
                    new CliError("aike.live_proof_timeout", $"AIKE live proof timed out after {timeoutSeconds} seconds."),
                    data));
            }
            else
            {
                Console.Error.WriteLine($"AIKE live proof timed out after {timeoutSeconds} seconds.");
            }

            return 3;
        }

        if (process.ExitCode == 0 && proof is null)
        {
            if (json)
            {
                WriteJson(new CliResponse(
                    false,
                    "aike-live-proof",
                    new CliError("aike.live_proof_report_missing", "AIKE live proof finished but did not produce a readable JSON report."),
                    data));
            }
            else
            {
                Console.Error.WriteLine("AIKE live proof finished but did not produce a readable JSON report.");
            }

            return 4;
        }

        if (process.ExitCode == 0 && !redactionOk)
        {
            if (json)
            {
                WriteJson(new CliResponse(
                    false,
                    "aike-live-proof",
                    new CliError("aike.live_proof_redaction_failed", "AIKE live proof output contains forbidden raw-key markers."),
                    data));
            }
            else
            {
                Console.Error.WriteLine("AIKE live proof output contains forbidden raw-key markers.");
            }

            return 5;
        }

        if (process.ExitCode == 0)
        {
            WriteJson(new CliResponse(true, "aike-live-proof", null, data));
            return 0;
        }

        if (json)
        {
            WriteJson(new CliResponse(
                false,
                "aike-live-proof",
                new CliError("aike.live_proof_failed", $"AIKE live proof failed with exit code {process.ExitCode}."),
                data));
        }
        else
        {
            Console.Error.WriteLine($"AIKE live proof failed with exit code {process.ExitCode}.");
            Console.Error.WriteLine(Tail(stderr, 20));
        }

        return 2;
    }

    private static async Task<bool> WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup: the caller will report timeout even if the process already exited.
        }
    }

    private static string? ResolveAikeProofScriptPath(string configPath)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;
        var manualSaPath = TryReadManualSaPath(configPath);
        if (!string.IsNullOrWhiteSpace(manualSaPath))
        {
            var resolvedManualSa = Path.GetFullPath(Path.Combine(configDirectory, manualSaPath));
            var manualSaDirectory = Path.GetDirectoryName(resolvedManualSa);
            var scriptDirectory = manualSaDirectory is null ? null : Directory.GetParent(manualSaDirectory)?.FullName;
            if (scriptDirectory is not null)
            {
                var candidate = Path.Combine(scriptDirectory, "run-aike-protocol0-proof-suite.ps1");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var dir = new DirectoryInfo(configDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "windows-agent", "experiments", "manual-sa", "run-aike-protocol0-proof-suite.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? TryReadManualSaPath(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            return document.RootElement.TryGetProperty("manual_sa_path", out var value)
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryReadJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string[] FindForbiddenSecretMarkers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return ForbiddenSecretMarkers
            .Where(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddNamedArgument(ProcessStartInfo process, string name, string value)
    {
        process.ArgumentList.Add(name);
        process.ArgumentList.Add(value);
    }

    private static int ParseIntOption(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? MatchFirst(string text, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
        return match.Success && match.Groups.Count > 1
            ? match.Groups[1].Value.Trim()
            : null;
    }

    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var all = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.None);
        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }

    private static async Task<CommandResponse> SendAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);

        await using var writer = new StreamWriter(client.GetStream(), Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync(request).ConfigureAwait(false);

        var responseJson = await reader.ReadLineAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return CommandResponse.Fail(GetCommand(request), "empty_response");
        }

        return JsonSerializer.Deserialize<CommandResponse>(responseJson, JsonOptions)
            ?? CommandResponse.Fail(GetCommand(request), "invalid_response_json");
    }

    private static int WriteResponse(CommandResponse response, bool json)
    {
        if (!json)
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return response.Success ? 0 : 2;
        }

        if (response.Success)
        {
            WriteJson(new CliResponse(true, response.Command, null, response.Data));
            return 0;
        }

        var error = NormalizeServiceError(response.Error);
        WriteJson(new CliResponse(false, response.Command, error, null));
        return 2;
    }

    private static int WriteCliResult(bool success, string command, CliError error, bool json)
        => WriteCliResult(success, command, error, data: null, json);

    private static int WriteCliResult(bool success, string command, CliError? error, object? data, bool json)
    {
        if (json)
        {
            WriteJson(new CliResponse(success, command, error, data));
            return success ? 0 : 1;
        }

        if (success)
        {
            Console.WriteLine(JsonSerializer.Serialize(CommandResponse.Ok(command, data), JsonOptions));
            return 0;
        }

        Console.Error.WriteLine(error?.Message ?? "command failed");
        return success ? 0 : 1;
    }

    private static void WriteJson(CliResponse response) =>
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));

    private static CliError NormalizeServiceError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return new CliError("service.error", "Service returned an unknown error.");
        }

        if (error.Contains(':', StringComparison.Ordinal))
        {
            var parts = error.Split(':', 2, StringSplitOptions.TrimEntries);
            return new CliError($"service.{parts[0]}", parts[1]);
        }

        return new CliError("service.error", error);
    }

    private static string GetCommand(string request)
    {
        var parts = request.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "unknown" : parts[0];
    }

    private static async Task<int> ResolveListenPortAsync(string? configPath)
    {
        var path = configPath ?? FindDefaultConfigPath();
        if (!File.Exists(path))
        {
            return 9000;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<ClientConfig>(stream, JsonOptions).ConfigureAwait(false);
            return config?.ListenPort > 0 ? config.ListenPort : 9000;
        }
        catch (Exception)
        {
            return 9000;
        }
    }

    private static string FindDefaultConfigPath()
    {
        static string? ExistingPath(string basePath, string fileName)
        {
            var fullPath = Path.GetFullPath(Path.Combine(basePath, fileName));
            return File.Exists(fullPath) ? fullPath : null;
        }

        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "config.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config.example.json"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var config = ExistingPath(dir.FullName, "config.json");
            if (config is not null)
            {
                return config;
            }

            var example = ExistingPath(dir.FullName, "config.example.json");
            if (example is not null)
            {
                return example;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config.example.json"));
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "/?";

    private static bool HasCommand(string[] args) => GetCommandIndex(args) >= 0;

    private static int GetCommandIndex(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (IsOptionWithValue(args[i]))
            {
                i++;
                continue;
            }

            if (!IsPositional(args[i]) || IsHelp(args[i]))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static string[] GetPositionals(string[] args, int commandIndex)
    {
        var values = new List<string>();
        for (var i = commandIndex + 1; i < args.Length; i++)
        {
            if (IsOptionWithValue(args[i]))
            {
                i++;
                continue;
            }

            if (IsPositional(args[i]))
            {
                values.Add(args[i]);
            }
        }

        return values.ToArray();
    }

    private static bool IsPositional(string arg) =>
        !string.IsNullOrWhiteSpace(arg) && !arg.StartsWith("-", StringComparison.Ordinal);

    private static bool IsOptionWithValue(string arg) =>
        arg.Equals("--config", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--port", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--endpoint-port", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--selector", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--selector-protocol", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--ttl-seconds", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--local", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--remote", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--local-peer", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--remote-peer", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--route-id", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--profile", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--host-in-spi", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--peer-in-spi", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--payload-file", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--output", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--envelope-secret", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--recipient-x25519-public", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--recipient-public-key", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--sender", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--recipient", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--helper-listen-host", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--helper-port", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--agent-node-id", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--control-plane-ports", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--tcp-port", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--udp-port", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--wait-seconds", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--timeout-seconds", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--script", StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(string[] args) =>
        args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AdaptiveClient.Cli");
        Console.WriteLine("Usage:");
        Console.WriteLine("  AdaptiveClient.Cli status [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli connect <peer> [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli rekey <peer> [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli cleanup <peer> [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli maintenance [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli self-test [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli manual-sa-plan <peer> [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli peer-install <peer> --payload-file <payload.json> [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli peer-install-envelope --payload-file <payload.json> --output <envelope.json> [--recipient-x25519-public <x25519:key>] [--envelope-secret <fallback-secret>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli aike-key-export [--local <ip>] [--remote <ip>] [--local-peer <id>] [--remote-peer <id>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli aike-live-proof [--local <ip>] [--remote <ip>] [--timeout-seconds <seconds>] [--keep-sa] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli endpoint-status [--config <config.json>] [--port <port>] [--json]");
        Console.WriteLine("  AdaptiveClient.Cli endpoint-update <node_id> <ip> [--selector icmp] [--endpoint-port <port>] [--ttl-seconds <seconds>] [--config <config.json>] [--port <listen-port>] [--json]");
        Console.WriteLine();
        Console.WriteLine("Defaults:");
        Console.WriteLine("  --config: ./config.json or ./config.example.json if exists, otherwise nearest config.example.json in parent folders");
        Console.WriteLine("  --port: listen_port from config (fallback 9000)");
        Console.WriteLine("  127.0.0.1 is used for TCP IPC");
    }
}

internal sealed record CliResponse(
    bool Success,
    string Command,
    CliError? Error,
    object? Data);

internal sealed record CliError(string Code, string Message);

internal sealed record CommandResponse(
    bool Success,
    string Command,
    string? Error,
    object? Data)
{
    public static CommandResponse Ok(string command, object? data) => new(true, command, null, data);

    public static CommandResponse Fail(string command, string error) => new(false, command, error, null);
}

internal sealed record ControlCommand(
    string Command,
    string? Peer = null,
    string? SessionId = null,
    Dictionary<string, string>? Parameters = null);

internal sealed record ClientConfig(
    string NodeId,
    int ListenPort,
    string ManualSaPath,
    Dictionary<string, PeerConfig> Peers,
    Dictionary<string, string> IdentityPublicKeys,
    RoutePolicy RoutePolicy,
    RetryPolicy RetryPolicy);

internal sealed record PeerConfig(
    string Endpoint,
    string OverlayEndpoint,
    string PublicIdentityKey,
    string AllowedSelector);

internal sealed record RoutePolicy(
    string Mode,
    int HopCount,
    string[] CandidateIntermediates);

internal sealed record RetryPolicy(
    int ConnectTimeoutMs,
    int MaxAttempts,
    int BackoffMs,
    int CleanupStaleAfterSeconds);
