using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AdaptiveClient.Core;
using AdaptiveClient.Core.Crypto;
using AdaptiveClient.Core.Models;

namespace AdaptiveClient.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(IsHelp))
        {
            PrintHelp();
            return 0;
        }

        var configPath = Value(args, "--config") ?? FindDefaultConfigPath();
        var config = await ClientConfig.LoadAsync(configPath).ConfigureAwait(false);
        using var logger = StructuredLogger.Create(Value(args, "--log"), config.NodeId);

        var manualSa = new ProcessManualSaRunner(config.ManualSaPath, logger);
        var validateRunner = new ProcessManualSaPlanValidator(config.ManualSaPath, logger);
        var identityStore = new PersistentIdentityStore(config.IdentityStorePath, logger);
        var orchestrator = new SessionRegistry(config, manualSa, validateRunner, identityStore, logger);
        var server = new ControlPlaneServer(config, orchestrator, logger);
        var maintenance = new LifecycleMaintenanceService(config, orchestrator, logger);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

            logger.Info("service_starting", new
            {
                config_path = Path.GetFullPath(configPath),
                config.NodeId,
                config.ListenPort,
                config.ManualSaPath,
                config.ManualSaInstallMode,
                config.PeerInstallDeliveryMode,
                config.ManualSaStateDir,
                config.ManualSaSessionIndexPath,
                config.PeerInstallReplayCachePath,
                config.EndpointRegistryPath,
                maintenance = config.Maintenance
            });

        var maintenanceTask = Task.CompletedTask;
        try
        {
            await orchestrator.InitializeAsync(cts.Token).ConfigureAwait(false);
            maintenanceTask = maintenance.RunAsync(cts.Token);
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            logger.Info("service_stopped", new { reason = "cancellation" });
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            logger.Info("service_stopped", new { reason = "ctrl_c" });
            return 0;
        }
        catch (Exception ex)
        {
            logger.Error("service_failed", ex, null);
            return 1;
        }
        finally
        {
            cts.Cancel();
            await StopBackgroundTaskAsync(maintenanceTask, logger).ConfigureAwait(false);
        }
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "/?";

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

    private static void PrintHelp()
    {
        Console.WriteLine("AdaptiveClient.Service");
        Console.WriteLine("Usage:");
        Console.WriteLine("  AdaptiveClient.Service --config <config.json> [--log <events.jsonl>]");
        Console.WriteLine();
        Console.WriteLine("The service skeleton reads config.json, listens on TCP port 9000 by default,");
        Console.WriteLine("and exposes status/connect/cleanup/manual-sa-plan/self-test/endpoint-* commands for AdaptiveClient.Cli.");
    }

    private static async Task StopBackgroundTaskAsync(Task task, StructuredLogger logger)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            logger.Error("background_task_failed_on_shutdown", ex, null);
        }
    }
}

internal interface ISessionOrchestrator
{
    Task<CommandResponse> StartConnect(string peerId, CancellationToken cancellationToken);
    Task<CommandResponse> RekeyPeer(string peerId, CancellationToken cancellationToken);
    Task<CommandResponse> CleanupPeer(string peerId, CancellationToken cancellationToken);
    Task<CommandResponse> PeerCleanup(string peerId, CancellationToken cancellationToken);
    Task<CommandResponse> ManualSaPlan(string peerId);
    Task<CommandResponse> PeerInstall(PeerInstallRequest request, CancellationToken cancellationToken);
    Task<CommandResponse> RunMaintenance(CancellationToken cancellationToken);
    CommandResponse EndpointStatus();
    CommandResponse EndpointUpdate(EndpointUpdateRequest request);
    CommandResponse Status();
}

internal sealed class LifecycleMaintenanceService(
    ClientConfig config,
    ISessionOrchestrator orchestrator,
    StructuredLogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var policy = config.Maintenance;
        if (policy?.Enabled is not true)
        {
            logger.Info("maintenance_disabled", new { reason = "maintenance.enabled is false" });
            return;
        }

        var intervalSeconds = Math.Max(5, policy.IntervalSeconds);
        logger.Info("maintenance_started", new
        {
            interval_seconds = intervalSeconds,
            auto_rekey = policy.AutoRekey,
            expire_cleanup = policy.ExpireCleanup
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
                var result = await orchestrator.RunMaintenance(cancellationToken).ConfigureAwait(false);
                if (!result.Success)
                {
                    logger.Info("maintenance_tick_failed", new { result.Error });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Info("maintenance_stopped", new { reason = "cancellation" });
        }
    }
}

internal sealed class ControlPlaneServer(
    ClientConfig config,
    ISessionOrchestrator orchestrator,
    StructuredLogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listenAddress = ResolveListenAddress(config.ListenHost);
        var listener = new TcpListener(listenAddress, config.ListenPort);
        listener.Start();
        logger.Info("control_plane_listening", new { address = config.ListenHost, port = config.ListenPort });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static IPAddress ResolveListenAddress(string? listenHost)
    {
        if (string.IsNullOrWhiteSpace(listenHost)
            || string.Equals(listenHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(listenHost, out var address)
            ? address
            : throw new InvalidOperationException($"Invalid listen_host: {listenHost}");
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logger.Info("client_connected", new { remote });

        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client.GetStream(), Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var response = await ParseAndDispatchAsync(line, logger, cancellationToken).ConfigureAwait(false);
            await WriteResponseAsync(writer, response, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Info("client_disconnected_before_request", new { remote, reason = ex.Message });
        }
        catch (Exception ex)
        {
            logger.Error("client_handler_failed", ex, new { remote });
        }
    }

    private async Task<CommandResponse> ParseAndDispatchAsync(string? rawLine, StructuredLogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return CommandResponse.Fail("service", "empty_request");
        }

        var line = rawLine.Trim();
        if (line.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var request = JsonSerializer.Deserialize<ControlCommand>(line, ProgramJson.Options);
                if (request is null)
                {
                    return CommandResponse.Fail("service", "invalid_json");
                }

                logger.Info("command_received", new { request.Command, request.Peer, request.SessionId });
                return await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return CommandResponse.Fail("service", "invalid_json");
            }
        }

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return CommandResponse.Fail("service", "empty_request");
        }

        var command = parts[0].ToLowerInvariant();
        var peer = parts.Length > 1 ? parts[1].Trim() : null;
        logger.Info("command_received", new { command, peer });
        return await DispatchAsync(command, peer, cancellationToken).ConfigureAwait(false);
    }

    private Task<CommandResponse> DispatchAsync(string command, string? peer, CancellationToken cancellationToken)
    {
        return command switch
        {
            "status" => Task.FromResult(orchestrator.Status()),
            "endpoint-status" => Task.FromResult(orchestrator.EndpointStatus()),
            "endpoint-update" => Task.FromResult(orchestrator.EndpointUpdate(ParseEndpointUpdate(peer))),
            "connect" => orchestrator.StartConnect(peer ?? string.Empty, cancellationToken),
            "rekey" => orchestrator.RekeyPeer(peer ?? string.Empty, cancellationToken),
            "cleanup" => orchestrator.CleanupPeer(peer ?? string.Empty, cancellationToken),
            "peer-cleanup" => orchestrator.PeerCleanup(peer ?? string.Empty, cancellationToken),
            "manual-sa-plan" => orchestrator.ManualSaPlan(peer ?? string.Empty),
            "peer-install" => orchestrator.PeerInstall(new PeerInstallRequest(PeerId: peer), cancellationToken),
            "maintenance" => orchestrator.RunMaintenance(cancellationToken),
            "self-test" => RunSelfTestAsync(),
            _ => Task.FromResult(CommandResponse.Fail(command, $"unknown_command:{command}"))
        };
    }

    private Task<CommandResponse> DispatchAsync(ControlCommand request, CancellationToken cancellationToken)
    {
        var command = request.Command?.ToLowerInvariant() ?? "unknown";
        if (command == "endpoint-update")
        {
            return Task.FromResult(orchestrator.EndpointUpdate(ParseEndpointUpdate(request)));
        }

        if (command == "peer-install")
        {
            return orchestrator.PeerInstall(ParsePeerInstall(request), cancellationToken);
        }

        return DispatchAsync(command, request.Peer, cancellationToken);
    }

    private static PeerInstallRequest ParsePeerInstall(ControlCommand request)
    {
        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Param(string key) => parameters.TryGetValue(key, out var value) ? value : null;

        return new PeerInstallRequest(
            PeerId: FirstNonEmpty(Param("peer_id"), request.Peer),
            RemotePeerId: Param("remote_peer_id"),
            SessionId: FirstNonEmpty(Param("session_id"), request.SessionId),
            RouteId: Param("route_id"),
            LocalIp: Param("local_ip"),
            RemoteIp: Param("remote_ip"),
            TrafficSelectorProtocol: FirstNonEmpty(Param("traffic_selector_protocol"), Param("selector_protocol"), Param("selector")),
            InboundSpi: Param("inbound_spi"),
            OutboundSpi: Param("outbound_spi"),
            InCipherKey: FirstNonEmpty(Param("in_cipher_key"), Param("inbound_cipher_key")),
            InAuthKey: FirstNonEmpty(Param("in_auth_key"), Param("inbound_auth_key")),
            OutCipherKey: FirstNonEmpty(Param("out_cipher_key"), Param("outbound_cipher_key")),
            OutAuthKey: FirstNonEmpty(Param("out_auth_key"), Param("outbound_auth_key")),
            IdentitySignatureMode: Param("identity_signature_mode"),
            Source: FirstNonEmpty(Param("source"), "encrypted_control_plane_payload"),
            EnvelopeVersion: Param("envelope_version"),
            EnvelopeMode: Param("envelope_mode"),
            EnvelopeEncryption: Param("envelope_encryption"),
            EnvelopeKdf: Param("envelope_kdf"),
            EnvelopeSenderId: Param("envelope_sender_id"),
            EnvelopeRecipientId: Param("envelope_recipient_id"),
            EnvelopeEphemeralPublicKey: Param("envelope_ephemeral_public_key"),
            EnvelopeNonce: Param("envelope_nonce"),
            EnvelopeCiphertext: Param("envelope_ciphertext"),
            EnvelopeTag: Param("envelope_tag"));
    }

    private static EndpointUpdateRequest ParseEndpointUpdate(ControlCommand request)
    {
        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeId = parameters.TryGetValue("node_id", out var parameterNodeId)
            ? parameterNodeId
            : request.Peer;
        parameters.TryGetValue("ip", out var ip);
        parameters.TryGetValue("selector_protocol", out var selectorProtocol);
        if (string.IsNullOrWhiteSpace(selectorProtocol))
        {
            parameters.TryGetValue("selector", out selectorProtocol);
        }

        var port = ParseNullableInt(parameters, "port");
        var ttlSeconds = ParseNullableInt(parameters, "ttl_seconds");

        return new EndpointUpdateRequest(
            NodeId: nodeId,
            Ip: ip,
            Port: port,
            SelectorProtocol: selectorProtocol,
            TtlSeconds: ttlSeconds);
    }

    private static EndpointUpdateRequest ParseEndpointUpdate(string? args)
    {
        var tokens = SplitCommandLine(args);
        var selector = Value(tokens, "--selector") ?? Value(tokens, "--selector-protocol");
        var port = ParseNullableInt(tokens, "--endpoint-port") ?? ParseNullableInt(tokens, "--port");
        var ttlSeconds = ParseNullableInt(tokens, "--ttl-seconds");
        var positionals = tokens.Where(token => !token.StartsWith("-", StringComparison.Ordinal)).ToArray();

        return new EndpointUpdateRequest(
            NodeId: positionals.ElementAtOrDefault(0),
            Ip: positionals.ElementAtOrDefault(1),
            Port: port,
            SelectorProtocol: selector,
            TtlSeconds: ttlSeconds);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string[] SplitCommandLine(string? args) =>
        string.IsNullOrWhiteSpace(args)
            ? []
            : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private static int? ParseNullableInt(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static int? ParseNullableInt(string[] args, string name) =>
        int.TryParse(Value(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static Task<CommandResponse> RunSelfTestAsync()
    {
        try
        {
            var materialSmoke = AikeMessageCodecSmoke.RunEstablishedAndDeriveEspMaterial();
            if (materialSmoke.Success)
            {
                var ed25519Smoke = AikeMessageCodecSmoke.RunEd25519SignedEstablishedRoundTrip();
                if (!ed25519Smoke.Success)
                {
                    return Task.FromResult(CommandResponse.Fail("self-test", ed25519Smoke.Error ?? "ed25519 smoke failed"));
                }

                var dualIdentityExportSmoke = AikeMessageCodecSmoke.RunDualEd25519ManualSaKeyExport();
                if (!dualIdentityExportSmoke.Success)
                {
                    return Task.FromResult(CommandResponse.Fail("self-test", dualIdentityExportSmoke.Error ?? "dual ed25519 export smoke failed"));
                }

                return Task.FromResult(CommandResponse.Ok("self-test", new
                {
                    smoke_method = "RunEstablishedAndDeriveEspMaterial",
                    smoke_status = "pass",
                    identity_signature_smoke = "ed25519_pass",
                    dual_identity_export_smoke = dualIdentityExportSmoke.IdentitySignatureMode,
                    dual_identity_export_session_id = dualIdentityExportSmoke.SessionId,
                    final_state = materialSmoke.FinalState.ToString(),
                    material_summary = new
                    {
                        protocol = materialSmoke.Protocol,
                        inbound_spi = materialSmoke.InboundSpi,
                        outbound_spi = materialSmoke.OutboundSpi
                    }
                }));
            }

            var establishedSmoke = AikeMessageCodecSmoke.RunKe1Ke2EstablishedRoundTrip();
            if (!establishedSmoke.Success)
            {
                return Task.FromResult(CommandResponse.Fail("self-test", establishedSmoke.Error ?? "ke1-ke2 smoke failed"));
            }

            return Task.FromResult(CommandResponse.Ok("self-test", new
            {
                smoke_method = "RunKe1Ke2EstablishedRoundTrip",
                smoke_status = "pass_with_fallback",
                smoke_warning = "full_esp_material_smoke_fallback",
                final_state = establishedSmoke.FinalState.ToString(),
                material_summary = new
                {
                    protocol = (string?)null,
                    inbound_spi = (uint?)null,
                    outbound_spi = (uint?)null
                }
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(CommandResponse.Fail("self-test", ex.Message));
        }
    }

    private static Task WriteResponseAsync(
        StreamWriter writer,
        CommandResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, ProgramJson.Options);
        return writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }
}

internal sealed class SessionRegistry(
    ClientConfig config,
    IManualSaRunner manualSaRunner,
    IManualSaPlanValidator manualSaPlanValidator,
    PersistentIdentityStore identityStore,
    StructuredLogger logger)
    : ISessionOrchestrator
{
    private readonly object gate = new();
    private readonly Dictionary<string, ManagedSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> peerToSessionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly EndpointRegistry endpointRegistry = new(config, logger);
    private readonly PersistentSessionIndex sessionIndex = new(config.ManualSaSessionIndexPath, logger);
    private readonly PersistentPeerInstallReplayCache peerInstallReplayCache = new(config.PeerInstallReplayCachePath, logger);
    private static readonly TimeSpan IdentityProofMaxAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdentityProofClockSkew = TimeSpan.FromSeconds(30);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalInstallMode(config.ManualSaInstallMode))
        {
            sessionIndex.PruneMissingFiles();
            return;
        }

        var staleRecords = sessionIndex.Load();
        logger.Info("manual_sa_session_index_loaded", new
        {
            path = config.ManualSaSessionIndexPath,
            count = staleRecords.Length,
            startup_cleanup_enabled = true
        });

        foreach (var record in staleRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(record.StatePath))
            {
                sessionIndex.Remove(record.SessionId);
                continue;
            }

            var cleanup = await CleanupStatePathAsync(
                    peerId: record.PeerId,
                    sessionId: record.SessionId,
                    statePath: record.StatePath,
                    planFilePath: record.PlanFilePath,
                    localIp: record.LocalIp,
                    remoteIp: record.RemoteIp,
                    selectorProtocol: record.TrafficSelectorProtocol,
                    reason: "startup_stale_cleanup",
                    cancellationToken)
                .ConfigureAwait(false);

            sessionIndex.Remove(record.SessionId);
            logger.Info("manual_sa_startup_stale_cleanup_completed", new
            {
                record.PeerId,
                record.SessionId,
                cleanup
            });
        }
    }

    public Task<CommandResponse> StartConnect(string peerId, CancellationToken cancellationToken) =>
        StartConnectLifecycleAsync(peerId, "connect", cancellationToken);

    public Task<CommandResponse> RekeyPeer(string peerId, CancellationToken cancellationToken) =>
        StartConnectLifecycleAsync(peerId, "rekey", cancellationToken);

    public async Task<CommandResponse> RunMaintenance(CancellationToken cancellationToken)
    {
        var policy = config.Maintenance;
        if (policy?.Enabled is not true)
        {
            return CommandResponse.Ok("maintenance", new
            {
                enabled = false,
                auto_rekey = false,
                expire_cleanup = false,
                expired_cleanup_count = 0,
                due_rekey_count = 0,
                actions = Array.Empty<object>()
            });
        }

        var now = DateTimeOffset.UtcNow;
        ManagedSession[] expired;
        ManagedSession[] dueForRekey;
        lock (gate)
        {
            expired = policy.ExpireCleanup
                ? sessions.Values
                    .Where(session => session.Phase == AikeSessionPhase.Established && now >= session.NotAfter)
                    .ToArray()
                : [];
            var expiredIds = expired
                .Select(session => session.SessionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            dueForRekey = policy.AutoRekey
                ? sessions.Values
                    .Where(session => session.Phase == AikeSessionPhase.Established
                                      && now >= session.RekeyAt
                                      && now < session.NotAfter
                                      && !expiredIds.Contains(session.SessionId))
                    .ToArray()
                : [];
        }

        var actions = new List<object>();
        foreach (var session in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await CleanupPeerLifecycleAsync(
                    session.PeerId,
                    commandName: "maintenance-cleanup",
                    deliverToPeer: true,
                    cancellationToken)
                .ConfigureAwait(false);
            actions.Add(new
            {
                action = "expire_cleanup",
                session.PeerId,
                session.SessionId,
                success = response.Success,
                response.Error
            });
        }

        foreach (var session in dueForRekey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await StartConnectLifecycleAsync(session.PeerId, "rekey", cancellationToken)
                .ConfigureAwait(false);
            actions.Add(new
            {
                action = "auto_rekey",
                session.PeerId,
                previous_session_id = session.SessionId,
                success = response.Success,
                response.Error
            });
        }

        logger.Info("maintenance_tick_completed", new
        {
            enabled = true,
            auto_rekey = policy.AutoRekey,
            expire_cleanup = policy.ExpireCleanup,
            expired_cleanup_count = expired.Length,
            due_rekey_count = dueForRekey.Length,
            action_count = actions.Count
        });

        return CommandResponse.Ok("maintenance", new
        {
            enabled = true,
            auto_rekey = policy.AutoRekey,
            expire_cleanup = policy.ExpireCleanup,
            now,
            expired_cleanup_count = expired.Length,
            due_rekey_count = dueForRekey.Length,
            actions
        });
    }

    public async Task<CommandResponse> PeerInstall(PeerInstallRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var encryptedEnvelopeAccepted = HasEncryptedPeerInstallEnvelope(request);
            var encryptedEnvelopeMode = encryptedEnvelopeAccepted
                ? FirstNonEmpty(request.EnvelopeMode, PeerInstallEnvelope.ModePsk)
                : "plaintext_legacy";
            var envelopeReplayKey = encryptedEnvelopeAccepted
                ? BuildPeerInstallEnvelopeReplayKey(request)
                : null;
            if (envelopeReplayKey is not null && IsPeerInstallEnvelopeReplay(envelopeReplayKey))
            {
                return CommandResponse.Fail("peer-install", "replay_detected:peer_install_envelope");
            }

            request = encryptedEnvelopeAccepted
                ? DecryptPeerInstallEnvelope(request)
                : request;
            var errors = new List<string>();
            var peerId = Required(request.PeerId, "peer_id", errors);
            var sessionId = Required(request.SessionId, "session_id", errors);
            var routeId = Required(request.RouteId, "route_id", errors);
            var localIp = Required(request.LocalIp, "local_ip", errors);
            var remoteIp = Required(request.RemoteIp, "remote_ip", errors);
            var selectorProtocol = FirstNonEmpty(
                request.TrafficSelectorProtocol,
                ResolveConfigSelectorProtocol(peerId),
                ManualSaInstallPlan.DefaultTrafficSelectorProtocol);
            var remotePeerId = FirstNonEmpty(request.RemotePeerId, config.NodeId);
            var source = FirstNonEmpty(request.Source, "encrypted_control_plane_payload");

            if (!uint.TryParse(request.InboundSpi, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inboundSpi))
            {
                errors.Add("inbound_spi must be an unsigned integer.");
            }

            if (!uint.TryParse(request.OutboundSpi, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outboundSpi))
            {
                errors.Add("outbound_spi must be an unsigned integer.");
            }

            var inCipher = DecodeHexField(request.InCipherKey, "in_cipher_key", errors);
            var inAuth = DecodeHexField(request.InAuthKey, "in_auth_key", errors);
            var outCipher = DecodeHexField(request.OutCipherKey, "out_cipher_key", errors);
            var outAuth = DecodeHexField(request.OutAuthKey, "out_auth_key", errors);

            if (errors.Count > 0)
            {
                return CommandResponse.Fail("peer-install", $"invalid_peer_install_payload:{string.Join("; ", errors)}");
            }

            var plan = new ManualSaInstallPlan(
                LocalIp: localIp,
                RemoteIp: remoteIp,
                IpsecProtocol: ManualSaInstallPlan.IpsecProtocolEspNumeric,
                TrafficSelectorProtocol: selectorProtocol,
                Profile: EspProfiles.ManualSa,
                InboundSpi: inboundSpi,
                OutboundSpi: outboundSpi,
                InboundCipherKeyPlaceholder: inCipher,
                InboundAuthKeyPlaceholder: inAuth,
                OutboundCipherKeyPlaceholder: outCipher,
                OutboundAuthKeyPlaceholder: outAuth);

            var shapeValidation = ManualSaInstallPlanValidator.Validate(plan);
            if (!shapeValidation.IsValid)
            {
                return CommandResponse.Fail("peer-install", $"invalid_peer_install_plan:{string.Join("; ", shapeValidation.Errors)}");
            }

            var validateResult = await RunPlanValidationAsync(peerId, sessionId, plan).ConfigureAwait(false);
            var installResult = validateResult.Success && IsLocalInstallMode(config.ManualSaInstallMode)
                ? await RunLocalInstallAsync(peerId, sessionId, plan, cancellationToken).ConfigureAwait(false)
                : ManualSaLocalInstallResult.NotExecuted();
            var effectivePlan = installResult.ActualInboundSpi is uint actualInboundSpi
                ? plan with { InboundSpi = actualInboundSpi }
                : plan;
            var manualSaInstalled = IsLocalInstallMode(config.ManualSaInstallMode) && installResult.Installed;
            var installLifecycleSucceeded = validateResult.Success && (!IsLocalInstallMode(config.ManualSaInstallMode) || manualSaInstalled);
            var lifecycleState = ResolvePeerInstallLifecycleState(validateResult.Success, config.ManualSaInstallMode, manualSaInstalled);
            var now = DateTimeOffset.UtcNow;
            var localEndpoint = new EndpointRecord(
                NodeId: peerId,
                Ip: localIp,
                Port: null,
                SelectorProtocol: selectorProtocol,
                Source: source,
                Sequence: 0,
                ValidFrom: now,
                ExpiresAt: null,
                Signature: "peer-install-payload");
            var remoteEndpoint = new EndpointRecord(
                NodeId: remotePeerId,
                Ip: remoteIp,
                Port: null,
                SelectorProtocol: selectorProtocol,
                Source: source,
                Sequence: 0,
                ValidFrom: now,
                ExpiresAt: null,
                Signature: "peer-install-payload");
            var managed = new ManagedSession(
                PeerId: peerId,
                SessionId: sessionId,
                StartedAt: now,
                Phase: installLifecycleSucceeded ? AikeSessionPhase.Established : AikeSessionPhase.Failed,
                LifecycleState: lifecycleState,
                RouteId: routeId,
                TranscriptHash: null,
                NotAfter: now.AddHours(1),
                RekeyAt: now.AddSeconds(3600 * 3 / 4),
                InstallMode: config.ManualSaInstallMode,
                ManualSaValidated: validateResult.Success,
                ManualSaValidateExitCode: validateResult.ExitCode,
                ManualSaValidationError: validateResult.Stderr,
                StatePath: installResult.StatePath,
                PlanFilePath: installResult.PlanFilePath,
                PlanFileDeleted: installResult.PlanFileDeleted,
                PlanFileDeleteError: installResult.PlanFileDeleteError,
                ManualSaInstalled: manualSaInstalled,
                ManualSaInstallExitCode: installResult.ExitCode,
                ManualSaInstallError: installResult.Error,
                ManualSaInstallStdoutSummary: installResult.StdoutSummary,
                LocalEndpoint: localEndpoint,
                RemoteEndpoint: remoteEndpoint,
                IdentitySignatureMode: request.IdentitySignatureMode,
                TrafficSelectorProtocol: selectorProtocol,
                InboundSpi: effectivePlan.InboundSpi,
                OutboundSpi: effectivePlan.OutboundSpi,
                KeyLengths: new DirectionalKeys(
                    Convert.ToHexString(inCipher),
                    Convert.ToHexString(inAuth),
                    Convert.ToHexString(outCipher),
                    Convert.ToHexString(outAuth)).ToLogSafe(),
                Plan: effectivePlan.ToLogSafe());

            ManagedSession? previousManaged = null;
            lock (gate)
            {
                if (peerToSessionId.TryGetValue(peerId, out var oldSessionId))
                {
                    sessions.TryGetValue(oldSessionId, out previousManaged);
                }
            }

            object? previousCleanup = null;
            if (installLifecycleSucceeded
                && previousManaged is not null
                && previousManaged.ManualSaInstalled
                && !string.Equals(previousManaged.SessionId, managed.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                previousCleanup = await CleanupManagedSessionAsync(previousManaged, "peer_install_replace_cleanup", cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (gate)
            {
                if (peerToSessionId.TryGetValue(peerId, out var oldSessionId))
                {
                    sessions.Remove(oldSessionId);
                }

                peerToSessionId[peerId] = managed.SessionId;
                sessions[managed.SessionId] = managed;
            }

            if (managed.ManualSaInstalled && !string.IsNullOrWhiteSpace(managed.StatePath))
            {
                sessionIndex.Upsert(PersistedSessionRecord.From(managed));
            }

            if (installLifecycleSucceeded)
            {
                MarkPeerInstallEnvelopeAccepted(envelopeReplayKey);
            }

            logger.Info("peer_install_orchestration_completed", new
            {
                peer_id = peerId,
                remote_peer_id = remotePeerId,
                session_id = managed.SessionId,
                route_id = managed.RouteId,
                lifecycle_state = managed.LifecycleState,
                manual_sa_validated = managed.ManualSaValidated,
                manual_sa_installed = managed.ManualSaInstalled,
                install_mode = managed.InstallMode,
                previous_cleanup = previousCleanup
            });

            return CommandResponse.Ok("peer-install", new
            {
                action = manualSaInstalled ? "peer_install_installed" : "peer_install_validated",
                peer_id = peerId,
                remote_peer_id = remotePeerId,
                session_id = managed.SessionId,
                route_id = managed.RouteId,
                phase = managed.Phase.ToString(),
                lifecycle_state = managed.LifecycleState,
                install_mode = managed.InstallMode,
                payload_transport = encryptedEnvelopeAccepted ? "encrypted_envelope" : "plaintext_legacy",
                payload_envelope_mode = encryptedEnvelopeAccepted ? encryptedEnvelopeMode : null,
                endpoints = new
                {
                    local = ToEndpointResponse(localEndpoint),
                    remote = ToEndpointResponse(remoteEndpoint)
                },
                material_summary = new
                {
                    local_ip = plan.LocalIp,
                    remote_ip = plan.RemoteIp,
                    ipsec_protocol = NormalizeProtocolForDisplay(plan.IpsecProtocol),
                    traffic_selector_protocol = plan.TrafficSelectorProtocol,
                    inbound_spi = effectivePlan.InboundSpi,
                    outbound_spi = effectivePlan.OutboundSpi,
                    key_lengths = managed.KeyLengths
                },
                manual_sa_validate = new
                {
                    success = validateResult.Success,
                    exit_code = validateResult.ExitCode,
                    duration_ms = validateResult.DurationMs,
                    stdout_summary = validateResult.StdoutSummary,
                    stderr = validateResult.Stderr
                },
                manual_sa_install = new
                {
                    state_path = managed.StatePath,
                    plan_file_path = managed.PlanFilePath,
                    plan_file_deleted = managed.PlanFileDeleted,
                    plan_file_delete_error = managed.PlanFileDeleteError,
                    installed = managed.ManualSaInstalled,
                    exit_code = managed.ManualSaInstallExitCode,
                    error = managed.ManualSaInstallError,
                    stdout_summary = managed.ManualSaInstallStdoutSummary
                },
                previous_session_cleanup = previousCleanup,
                peer_install_ack = new
                {
                    ack_type = "peer_install_ack",
                    status = installLifecycleSucceeded ? "accepted" : "failed",
                    peer_id = peerId,
                    remote_peer_id = remotePeerId,
                    session_id = managed.SessionId,
                    route_id = managed.RouteId,
                    validated = managed.ManualSaValidated,
                    installed = managed.ManualSaInstalled,
                    raw_keys_logged = false,
                    payload_transport = encryptedEnvelopeAccepted ? "encrypted_envelope" : "plaintext_legacy",
                    envelope_mode = encryptedEnvelopeAccepted ? encryptedEnvelopeMode : null
                }
            });
        }
        catch (Exception ex)
        {
            logger.Error("peer_install_orchestration_failed", ex, new { request.PeerId, request.SessionId });
            return CommandResponse.Fail("peer-install", ex.Message);
        }
    }

    private PeerInstallRequest DecryptPeerInstallEnvelope(PeerInstallRequest request)
    {
        if (!int.TryParse(request.EnvelopeVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException("peer install envelope version is invalid.");
        }

        var envelope = new PeerInstallEncryptedEnvelope(
            EnvelopeVersion: version,
            EnvelopeMode: FirstNonEmpty(request.EnvelopeMode, PeerInstallEnvelope.ModePsk),
            EnvelopeEncryption: RequiredEnvelopeField(request.EnvelopeEncryption, "envelope_encryption"),
            EnvelopeKdf: RequiredEnvelopeField(request.EnvelopeKdf, "envelope_kdf"),
            EnvelopeSenderId: RequiredEnvelopeField(request.EnvelopeSenderId, "envelope_sender_id"),
            EnvelopeRecipientId: RequiredEnvelopeField(request.EnvelopeRecipientId, "envelope_recipient_id"),
            SessionId: RequiredEnvelopeField(request.SessionId, "session_id"),
            RouteId: RequiredEnvelopeField(request.RouteId, "route_id"),
            EnvelopeEphemeralPublicKey: request.EnvelopeEphemeralPublicKey,
            EnvelopeNonce: RequiredEnvelopeField(request.EnvelopeNonce, "envelope_nonce"),
            EnvelopeCiphertext: RequiredEnvelopeField(request.EnvelopeCiphertext, "envelope_ciphertext"),
            EnvelopeTag: RequiredEnvelopeField(request.EnvelopeTag, "envelope_tag"));

        ValidatePeerInstallEnvelopeSource(request, envelope);

        var values = string.Equals(envelope.EnvelopeMode, PeerInstallEnvelope.ModeX25519, StringComparison.OrdinalIgnoreCase)
            ? DecryptX25519PeerInstallEnvelope(envelope)
            : DecryptPskPeerInstallEnvelope(envelope);
        string? Value(string key) => values.TryGetValue(key, out var value) ? value : null;
        return new PeerInstallRequest(
            PeerId: FirstNonEmpty(request.PeerId, Value("peer_id")),
            RemotePeerId: FirstNonEmpty(Value("remote_peer_id"), envelope.EnvelopeSenderId),
            SessionId: FirstNonEmpty(Value("session_id"), envelope.SessionId),
            RouteId: FirstNonEmpty(Value("route_id"), envelope.RouteId),
            LocalIp: Value("local_ip"),
            RemoteIp: Value("remote_ip"),
            TrafficSelectorProtocol: FirstNonEmpty(Value("traffic_selector_protocol"), Value("selector_protocol"), Value("selector")),
            InboundSpi: Value("inbound_spi"),
            OutboundSpi: Value("outbound_spi"),
            InCipherKey: FirstNonEmpty(Value("in_cipher_key"), Value("inbound_cipher_key")),
            InAuthKey: FirstNonEmpty(Value("in_auth_key"), Value("inbound_auth_key")),
            OutCipherKey: FirstNonEmpty(Value("out_cipher_key"), Value("outbound_cipher_key")),
            OutAuthKey: FirstNonEmpty(Value("out_auth_key"), Value("outbound_auth_key")),
            IdentitySignatureMode: Value("identity_signature_mode"),
            Source: "encrypted_control_plane_envelope");
    }

    private void ValidatePeerInstallEnvelopeSource(PeerInstallRequest request, PeerInstallEncryptedEnvelope envelope)
    {
        if (envelope.EnvelopeVersion != PeerInstallEnvelope.Version)
        {
            throw new InvalidOperationException($"unsupported peer install envelope version: {envelope.EnvelopeVersion}");
        }

        if (!string.Equals(envelope.EnvelopeRecipientId, config.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("peer install envelope recipient does not match local node id.");
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId)
            && !string.Equals(request.SessionId, envelope.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("peer install envelope session id does not match command session id.");
        }

        if (!string.IsNullOrWhiteSpace(request.RouteId)
            && !string.Equals(request.RouteId, envelope.RouteId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("peer install envelope route id does not match command route id.");
        }

        var senderKnown = string.Equals(envelope.EnvelopeSenderId, config.NodeId, StringComparison.OrdinalIgnoreCase)
            || config.Peers.ContainsKey(envelope.EnvelopeSenderId);
        if (!senderKnown)
        {
            throw new InvalidOperationException($"peer install envelope sender is not configured: {envelope.EnvelopeSenderId}");
        }
    }

    private IReadOnlyDictionary<string, string> DecryptPskPeerInstallEnvelope(PeerInstallEncryptedEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(config.PeerInstallEnvelopeSecret))
        {
            throw new InvalidOperationException("peer_install_envelope_secret is required for PSK peer-install payloads.");
        }

        return PeerInstallEnvelope.Decrypt(envelope, config.PeerInstallEnvelopeSecret);
    }

    private IReadOnlyDictionary<string, string> DecryptX25519PeerInstallEnvelope(PeerInstallEncryptedEnvelope envelope)
    {
        var identity = identityStore.LoadOrCreate(config.NodeId, ResolvePeerIdentity(config.NodeId));
        if (string.IsNullOrWhiteSpace(identity.PeerInstallEnvelopePublicKey))
        {
            throw new InvalidOperationException("local identity has no peer install envelope public key.");
        }

        if (!identityStore.TryExportPeerInstallEnvelopePrivateKey(config.NodeId, out var privateKey))
        {
            throw new InvalidOperationException("local peer install envelope private key is not available.");
        }

        try
        {
            return PeerInstallEnvelope.DecryptForRecipient(envelope, privateKey, identity.PeerInstallEnvelopePublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private static bool HasEncryptedPeerInstallEnvelope(PeerInstallRequest request) =>
        !string.IsNullOrWhiteSpace(request.EnvelopeCiphertext)
        || !string.IsNullOrWhiteSpace(request.EnvelopeNonce)
        || !string.IsNullOrWhiteSpace(request.EnvelopeTag);

    private static string? BuildPeerInstallEnvelopeReplayKey(PeerInstallRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnvelopeSenderId)
            || string.IsNullOrWhiteSpace(request.EnvelopeRecipientId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.RouteId)
            || string.IsNullOrWhiteSpace(request.EnvelopeNonce))
        {
            return null;
        }

        return string.Join(
            '|',
            "peer-install-envelope-v1",
            FirstNonEmpty(request.EnvelopeMode, PeerInstallEnvelope.ModePsk),
            request.EnvelopeSenderId.Trim(),
            request.EnvelopeRecipientId.Trim(),
            request.SessionId.Trim(),
            request.RouteId.Trim(),
            request.EnvelopeNonce.Trim());
    }

    private bool IsPeerInstallEnvelopeReplay(string replayKey)
    {
        return peerInstallReplayCache.Contains(replayKey);
    }

    private void MarkPeerInstallEnvelopeAccepted(string? replayKey)
    {
        if (string.IsNullOrWhiteSpace(replayKey))
        {
            return;
        }

        peerInstallReplayCache.MarkAccepted(replayKey);
    }

    private static string RequiredEnvelopeField(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{field} is required for encrypted peer-install payloads.")
            : value.Trim();

    private async Task<CommandResponse> StartConnectLifecycleAsync(
        string peerId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(peerId))
            {
                return CommandResponse.Fail(reason, $"{reason}_requires_peer");
            }

            if (!config.Peers.TryGetValue(peerId, out var peerDescriptorFromConfig))
            {
                return CommandResponse.Fail(reason, $"unknown_peer:{peerId}");
            }

            var localPeer = BuildLocalPeerDescriptor();
            var remotePeer = BuildRemotePeerDescriptor(peerId, peerDescriptorFromConfig);
            var now = DateTimeOffset.UtcNow;
            var notAfter = now.AddHours(1);
            var localEndpoint = endpointRegistry.Resolve(config.NodeId, config);
            var remoteEndpoint = endpointRegistry.Resolve(peerId, config);
            var selectorProtocol = ResolvePlanSelectorProtocol(remoteEndpoint.Record, localEndpoint.Record, peerDescriptorFromConfig);
            var spis = GenerateSessionSpis();
            var routeId = $"{config.RoutePolicy.Mode}:{config.NodeId}->{peerId}:{Guid.NewGuid():N}";
            var keyExport = AikeManualSaKeyExporter.Export(new AikeManualSaKeyExportRequest(
                    HostIp: localEndpoint.Record.Ip,
                    PeerIp: remoteEndpoint.Record.Ip,
                    HostPeerId: localPeer.PeerId,
                    PeerPeerId: remotePeer.PeerId,
                    RouteId: routeId,
                    EspProfile: EspProfiles.ManualSa.Name,
                    HostInboundSpi: spis.InboundSpi,
                    PeerInboundSpi: spis.OutboundSpi),
                localPeer,
                remotePeer,
                identityStore);

            if (!keyExport.Success || keyExport.Host is null || string.IsNullOrWhiteSpace(keyExport.SessionId))
            {
                return CommandResponse.Fail(reason, $"aike_key_export_failed:{keyExport.Error}");
            }

            var installPlan = BuildManualSaPlanFromDirectionalKeys(
                localEndpoint.Record.Ip,
                remoteEndpoint.Record.Ip,
                selectorProtocol,
                spis,
                keyExport.Host);

            var validateResult = await RunPlanValidationAsync(peerId, keyExport.SessionId, installPlan)
                .ConfigureAwait(false);
            var useTwoPhasePeerInstall = ShouldUseTwoPhasePeerInstall(config);
            ManualSaLocalCreateResult? localCreateResult = null;
            ManualSaLocalCompleteResult? localCompleteResult = null;
            var installResult = ManualSaLocalInstallResult.NotExecuted();
            if (validateResult.Success && IsLocalInstallMode(config.ManualSaInstallMode))
            {
                if (useTwoPhasePeerInstall)
                {
                    localCreateResult = await RunLocalCreateAsync(peerId, keyExport.SessionId, installPlan, cancellationToken)
                        .ConfigureAwait(false);
                    installResult = new ManualSaLocalInstallResult(
                        StatePath: localCreateResult.StatePath,
                        PlanFilePath: localCreateResult.PlanFilePath,
                        PlanFileDeleted: false,
                        PlanFileDeleteError: null,
                        Installed: false,
                        ExitCode: localCreateResult.ExitCode,
                        Error: localCreateResult.Error,
                        StdoutSummary: localCreateResult.StdoutSummary,
                        ActualInboundSpi: localCreateResult.ActualInboundSpi);
                }
                else
                {
                    installResult = await RunLocalInstallAsync(peerId, keyExport.SessionId, installPlan, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            var effectiveInstallPlan = installResult.ActualInboundSpi is uint actualInboundSpi
                ? installPlan with { InboundSpi = actualInboundSpi }
                : installPlan;
            var finalInstallPlan = effectiveInstallPlan;
            var manualSaInstalled = IsLocalInstallMode(config.ManualSaInstallMode) && installResult.Installed;
            var installLifecycleSucceeded = validateResult.Success
                && (!IsLocalInstallMode(config.ManualSaInstallMode)
                    || (useTwoPhasePeerInstall
                        ? localCreateResult?.Created == true
                        : manualSaInstalled));
            var lifecycleState = useTwoPhasePeerInstall
                ? localCreateResult?.Created == true ? "local_create_completed" : "local_create_failed"
                : ResolveLifecycleState(validateResult.Success, config.ManualSaInstallMode, manualSaInstalled);
            var rekeyAt = now.AddSeconds(3600 * 3 / 4);
            var previousSessionId = ResolveSessionIdForPlan(peerId);
            var managed = new ManagedSession(
                PeerId: peerId,
                SessionId: keyExport.SessionId,
                StartedAt: now,
                Phase: installLifecycleSucceeded ? AikeSessionPhase.Established : AikeSessionPhase.Failed,
                LifecycleState: lifecycleState,
                RouteId: keyExport.RouteId ?? routeId,
                TranscriptHash: keyExport.TranscriptHash,
                NotAfter: notAfter,
                RekeyAt: rekeyAt,
                InstallMode: config.ManualSaInstallMode,
                ManualSaValidated: validateResult.Success,
                ManualSaValidateExitCode: validateResult.ExitCode,
                ManualSaValidationError: validateResult.Stderr,
                StatePath: installResult.StatePath,
                PlanFilePath: installResult.PlanFilePath,
                PlanFileDeleted: installResult.PlanFileDeleted,
                PlanFileDeleteError: installResult.PlanFileDeleteError,
                ManualSaInstalled: manualSaInstalled,
                ManualSaInstallExitCode: installResult.ExitCode,
                ManualSaInstallError: installResult.Error,
                ManualSaInstallStdoutSummary: installResult.StdoutSummary,
                LocalEndpoint: localEndpoint.Record,
                RemoteEndpoint: remoteEndpoint.Record,
                IdentitySignatureMode: keyExport.IdentitySignatureMode,
                TrafficSelectorProtocol: selectorProtocol,
                InboundSpi: effectiveInstallPlan.InboundSpi,
                OutboundSpi: effectiveInstallPlan.OutboundSpi,
                KeyLengths: keyExport.Host.ToLogSafe(),
                Plan: effectiveInstallPlan.ToLogSafe());

            ManagedSession? previousManaged = null;
            lock (gate)
            {
                if (peerToSessionId.TryGetValue(peerId, out var oldSessionId))
                {
                    sessions.TryGetValue(oldSessionId, out previousManaged);
                }
            }

            var peerInstallDelivery = await TryDeliverPeerInstallAsync(
                    peerId,
                    peerDescriptorFromConfig,
                    managed,
                    effectiveInstallPlan,
                    keyExport.Peer,
                    installLifecycleSucceeded,
                    cancellationToken)
                .ConfigureAwait(false);
            var peerLifecycleRequired = IsPeerLifecycleDeliveryRequired(config);
            if (peerLifecycleRequired && !peerInstallDelivery.Success)
            {
                object? localRollback = null;
                if (managed.ManualSaInstalled || (useTwoPhasePeerInstall && !string.IsNullOrWhiteSpace(managed.StatePath)))
                {
                    localRollback = await CleanupManagedSessionAsync(
                            managed,
                            "peer_install_delivery_failed_rollback",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                logger.Info("aike_connect_lifecycle_peer_delivery_failed", new
                {
                    peer_id = peerId,
                    previous_session_id = previousSessionId.StartsWith("dry-run-", StringComparison.OrdinalIgnoreCase) ? null : previousSessionId,
                    session_id = managed.SessionId,
                    lifecycle_state = managed.LifecycleState,
                    end_to_end_lifecycle_state = "peer_install_delivery_failed",
                    peer_install_delivery = peerInstallDelivery.ToLogSafe(),
                    local_rollback = localRollback
                });

                return CommandResponse.Fail(
                    reason,
                    $"{reason}_peer_install_delivery_failed:{peerInstallDelivery.Error ?? peerInstallDelivery.AckStatus ?? "unknown"}");
            }

            if (useTwoPhasePeerInstall)
            {
                if (peerInstallDelivery.PeerInboundSpi is not uint peerInboundSpi)
                {
                    var localRollback = await CleanupManagedSessionAsync(
                            managed,
                            "peer_install_missing_spi_rollback",
                            cancellationToken)
                        .ConfigureAwait(false);
                    var peerCleanupRollback = await TryDeliverPeerCleanupAsync(
                            peerId,
                            peerDescriptorFromConfig,
                            managed.SessionId,
                            cleanupSucceeded: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                    logger.Info("aike_connect_lifecycle_peer_spi_missing", new
                    {
                        peer_id = peerId,
                        session_id = managed.SessionId,
                        peer_install_delivery = peerInstallDelivery.ToLogSafe(),
                        local_rollback = localRollback,
                        peer_cleanup_rollback = peerCleanupRollback.ToLogSafe()
                    });

                    return CommandResponse.Fail(reason, $"{reason}_peer_install_missing_inbound_spi");
                }

                localCompleteResult = await RunLocalCompleteAsync(
                        peerId,
                        managed.SessionId,
                        managed.StatePath ?? throw new InvalidOperationException("two-phase local install has no state path"),
                        managed.PlanFilePath,
                        peerInboundSpi,
                        cancellationToken)
                    .ConfigureAwait(false);
                manualSaInstalled = localCompleteResult.Completed;
                installLifecycleSucceeded = localCompleteResult.Completed;
                lifecycleState = ResolveLifecycleState(validateResult.Success, config.ManualSaInstallMode, manualSaInstalled);
                finalInstallPlan = effectiveInstallPlan with { OutboundSpi = peerInboundSpi };
                managed = managed with
                {
                    Phase = installLifecycleSucceeded ? AikeSessionPhase.Established : AikeSessionPhase.Failed,
                    LifecycleState = lifecycleState,
                    ManualSaInstalled = manualSaInstalled,
                    ManualSaInstallExitCode = localCompleteResult.ExitCode,
                    ManualSaInstallError = localCompleteResult.Error,
                    ManualSaInstallStdoutSummary = localCompleteResult.StdoutSummary,
                    PlanFileDeleted = localCompleteResult.PlanFileDeleted,
                    PlanFileDeleteError = localCompleteResult.PlanFileDeleteError,
                    PlanFilePath = localCompleteResult.PlanFileDeleted ? null : managed.PlanFilePath,
                    OutboundSpi = peerInboundSpi,
                    Plan = finalInstallPlan.ToLogSafe()
                };

                if (!localCompleteResult.Completed)
                {
                    var localRollback = await CleanupManagedSessionAsync(
                            managed,
                            "local_complete_failed_rollback",
                            cancellationToken)
                        .ConfigureAwait(false);
                    var peerCleanupRollback = await TryDeliverPeerCleanupAsync(
                            peerId,
                            peerDescriptorFromConfig,
                            managed.SessionId,
                            cleanupSucceeded: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                    logger.Info("aike_connect_lifecycle_local_complete_failed", new
                    {
                        peer_id = peerId,
                        session_id = managed.SessionId,
                        local_complete = localCompleteResult,
                        local_rollback = localRollback,
                        peer_cleanup_rollback = peerCleanupRollback.ToLogSafe()
                    });

                    return CommandResponse.Fail(reason, $"{reason}_local_complete_failed:{localCompleteResult.Error ?? "unknown"}");
                }
            }

            var endToEndLifecycleState = ResolveEndToEndInstallLifecycleState(
                installLifecycleSucceeded,
                managed.InstallMode,
                peerInstallDelivery);

            object? previousCleanup = null;
            if (installLifecycleSucceeded
                && previousManaged is not null
                && previousManaged.ManualSaInstalled
                && !string.Equals(previousManaged.SessionId, managed.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                previousCleanup = await CleanupManagedSessionAsync(previousManaged, "rekey_replace_cleanup", cancellationToken)
                    .ConfigureAwait(false);
            }

            lock (gate)
            {
                if (peerToSessionId.TryGetValue(peerId, out var oldSessionId))
                {
                    sessions.Remove(oldSessionId);
                }

                peerToSessionId[peerId] = managed.SessionId;
                sessions[managed.SessionId] = managed;
            }

            if (managed.ManualSaInstalled && !string.IsNullOrWhiteSpace(managed.StatePath))
            {
                sessionIndex.Upsert(PersistedSessionRecord.From(managed));
            }

            logger.Info("aike_connect_lifecycle_validated", new
            {
                peer_id = peerId,
                previous_session_id = previousSessionId.StartsWith("dry-run-", StringComparison.OrdinalIgnoreCase) ? null : previousSessionId,
                session_id = managed.SessionId,
                phase = managed.Phase.ToString(),
                lifecycle_state = managed.LifecycleState,
                route_id = managed.RouteId,
                manual_sa_validated = managed.ManualSaValidated,
                manual_sa_installed = managed.ManualSaInstalled,
                identity_signature_mode = managed.IdentitySignatureMode,
                install_mode = managed.InstallMode,
                peer_lifecycle_delivery_required = peerLifecycleRequired,
                end_to_end_lifecycle_state = endToEndLifecycleState,
                previous_cleanup = previousCleanup,
                peer_install_delivery = peerInstallDelivery.ToLogSafe()
            });

            return CommandResponse.Ok(reason, new
            {
                action = BuildLifecycleAction(reason, config.ManualSaInstallMode, manualSaInstalled),
                peer_id = peerId,
                previous_session_id = previousSessionId.StartsWith("dry-run-", StringComparison.OrdinalIgnoreCase) ? null : previousSessionId,
                session_id = managed.SessionId,
                route_id = managed.RouteId,
                phase = managed.Phase.ToString(),
                lifecycle_state = managed.LifecycleState,
                end_to_end_lifecycle_state = endToEndLifecycleState,
                install_mode = managed.InstallMode,
                peer_lifecycle_delivery_required = peerLifecycleRequired,
                identity_signature_mode = managed.IdentitySignatureMode,
                not_after = managed.NotAfter,
                rekey_at = managed.RekeyAt,
                endpoints = new
                {
                    local = ToEndpointResponse(localEndpoint),
                    remote = ToEndpointResponse(remoteEndpoint)
                },
                material_summary = new
                {
                    local_ip = finalInstallPlan.LocalIp,
                    remote_ip = finalInstallPlan.RemoteIp,
                    ipsec_protocol = NormalizeProtocolForDisplay(finalInstallPlan.IpsecProtocol),
                    traffic_selector_protocol = finalInstallPlan.TrafficSelectorProtocol,
                    inbound_spi = finalInstallPlan.InboundSpi,
                    outbound_spi = finalInstallPlan.OutboundSpi,
                    key_lengths = keyExport.Host.ToLogSafe()
                },
                install_command = new
                {
                    executable = config.ManualSaPath,
                    arguments = installResult.PlanFilePath is not null && installResult.StatePath is not null
                        ? BuildLocalInstallArgs(installResult.StatePath, installResult.PlanFilePath)
                        : BuildInstallArgs(peerId, managed.SessionId, finalInstallPlan),
                    validate_only = IsValidateOnlyMode(managed.InstallMode),
                    not_executed_install = installResult.ExitCode is null
                },
                cleanup_command = new
                {
                    executable = config.ManualSaPath,
                    state_arguments = string.IsNullOrWhiteSpace(managed.StatePath) ? null : BuildStateDeleteArgs(managed.StatePath),
                        selector_arguments = BuildCleanupArgs(finalInstallPlan.LocalIp, finalInstallPlan.RemoteIp, finalInstallPlan.TrafficSelectorProtocol)
                    },
                identity = new
                {
                    local_peer_id = localPeer.PeerId,
                    local_key_id = localPeer.KeyId,
                    local_public_identity_key = localPeer.PublicIdentityKey,
                    remote_peer_id = remotePeer.PeerId,
                    remote_key_id = remotePeer.KeyId,
                    remote_public_identity_key = remotePeer.PublicIdentityKey,
                    signature_mode = managed.IdentitySignatureMode
                },
                manual_sa_validate = new
                {
                    success = validateResult.Success,
                    exit_code = validateResult.ExitCode,
                    duration_ms = validateResult.DurationMs,
                    stdout_summary = validateResult.StdoutSummary,
                    stderr = validateResult.Stderr
                },
                manual_sa_install = new
                {
                    two_phase = useTwoPhasePeerInstall,
                    create = localCreateResult is null ? null : new
                    {
                        created = localCreateResult.Created,
                        exit_code = localCreateResult.ExitCode,
                        error = localCreateResult.Error,
                        actual_inbound_spi = localCreateResult.ActualInboundSpi,
                        stdout_summary = localCreateResult.StdoutSummary
                    },
                    complete = localCompleteResult is null ? null : new
                    {
                        completed = localCompleteResult.Completed,
                        exit_code = localCompleteResult.ExitCode,
                        error = localCompleteResult.Error,
                        stdout_summary = localCompleteResult.StdoutSummary
                    },
                    state_path = managed.StatePath,
                    plan_file_path = managed.PlanFilePath,
                    plan_file_deleted = managed.PlanFileDeleted,
                    plan_file_delete_error = managed.PlanFileDeleteError,
                    installed = managed.ManualSaInstalled,
                    exit_code = managed.ManualSaInstallExitCode,
                    error = managed.ManualSaInstallError,
                    stdout_summary = managed.ManualSaInstallStdoutSummary
                },
                previous_session_cleanup = previousCleanup,
                peer_install_bundle = BuildPeerInstallBundle(
                    peerId,
                    managed.SessionId,
                    managed.RouteId,
                    finalInstallPlan,
                    keyExport.Peer?.ToLogSafe()),
                peer_install_delivery = peerInstallDelivery.ToResponse(),
                next_step = string.Equals(config.PeerInstallDeliveryMode, ClientConfig.PeerInstallDeliveryModeServiceToService, StringComparison.OrdinalIgnoreCase)
                    ? "end-to-end service lifecycle completed; inspect end_to_end_lifecycle_state and peer_install_delivery"
                    : IsValidateOnlyMode(managed.InstallMode)
                        ? "peer-side install orchestration is external; service stores a validated local install plan without raw keys in status"
                        : "local install lifecycle executed; peer-side install orchestration still requires encrypted control-plane transfer"
            });
        }
        catch (Exception ex)
        {
            logger.Error("aike_connect_lifecycle_failed", ex, new { peer_id = peerId, reason });
            return CommandResponse.Fail(reason, ex.Message);
        }
    }

    public Task<CommandResponse> CleanupPeer(string peerId, CancellationToken cancellationToken) =>
        CleanupPeerLifecycleAsync(peerId, commandName: "cleanup", deliverToPeer: true, cancellationToken);

    public Task<CommandResponse> PeerCleanup(string peerId, CancellationToken cancellationToken) =>
        CleanupPeerLifecycleAsync(peerId, commandName: "peer-cleanup", deliverToPeer: false, cancellationToken);

    private async Task<CommandResponse> CleanupPeerLifecycleAsync(
        string peerId,
        string commandName,
        bool deliverToPeer,
        CancellationToken cancellationToken)
    {
        var sessionId = string.Empty;
        ManagedSession? activeSession = null;

        if (string.IsNullOrWhiteSpace(peerId))
        {
            return CommandResponse.Fail(commandName, $"{commandName}_requires_peer");
        }

        if (!config.Peers.TryGetValue(peerId, out var peer))
        {
            return CommandResponse.Fail(commandName, $"unknown_peer:{peerId}");
        }

        lock (gate)
        {
            if (peerToSessionId.TryGetValue(peerId, out var existingSessionId))
            {
                sessionId = existingSessionId;
                sessions.TryGetValue(sessionId, out activeSession);
            }
            else
            {
                sessionId = $"cleanup-{Guid.NewGuid():N}";
            }
        }

        try
        {
            var localEndpoint = endpointRegistry.Resolve(config.NodeId, config);
            var remoteEndpoint = endpointRegistry.Resolve(peerId, config);
            var localIp = activeSession?.Plan.LocalIp ?? localEndpoint.Record.Ip;
            var remoteIp = activeSession?.Plan.RemoteIp ?? remoteEndpoint.Record.Ip;
            var selectorProtocol = activeSession?.TrafficSelectorProtocol
                ?? ResolvePlanSelectorProtocol(remoteEndpoint.Record, localEndpoint.Record, peer);
            var selectorCleanupArgs = BuildCleanupArgs(localIp, remoteIp, selectorProtocol);
            var statePath = activeSession?.StatePath;
            var stateCleanupArgs = string.IsNullOrWhiteSpace(statePath)
                ? null
                : BuildStateDeleteArgs(statePath);

            logger.Info("cleanup_requested", new
            {
                peer_id = peerId,
                session_id = sessionId,
                local_ip = localIp,
                remote_ip = remoteIp,
                selector_protocol = selectorProtocol,
                state_path = statePath
            });

            ManualSaResult? stateCleanupResult = null;
            ManualSaResult result;
            var cleanupMethod = "selector_cleanup";

            if (IsValidateOnlyMode(config.ManualSaInstallMode) && activeSession?.ManualSaInstalled != true)
            {
                result = ManualSaResult.SkippedResult("validate_only: no local manual-SA state was installed");
                cleanupMethod = "validate_only_session_cleanup";
            }
            else if (stateCleanupArgs is not null)
            {
                stateCleanupResult = await manualSaRunner.RunAsync(
                        "delete",
                        stateCleanupArgs,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (stateCleanupResult.ExitCode == 0)
                {
                    result = stateCleanupResult;
                    cleanupMethod = "state_delete";
                }
                else
                {
                    result = await manualSaRunner.RunAsync(
                            "cleanup",
                            selectorCleanupArgs,
                            cancellationToken)
                        .ConfigureAwait(false);
                    cleanupMethod = "selector_cleanup_after_state_delete_failed";
                }
            }
            else
            {
                result = await manualSaRunner.RunAsync(
                        "cleanup",
                        selectorCleanupArgs,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.Info("cleanup_completed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                manual_sa_exit_code = result.ExitCode,
                manual_sa_skipped = result.IsSkipped,
                cleanup_method = cleanupMethod
            });

            var peerCleanupDelivery = deliverToPeer
                ? await TryDeliverPeerCleanupAsync(peerId, peer, sessionId, result.ExitCode == 0, cancellationToken)
                    .ConfigureAwait(false)
                : PeerCleanupDeliveryResult.Skipped(config.PeerInstallDeliveryMode, "inbound peer-cleanup does not relay further");

            logger.Info("cleanup_delivery_completed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                command = commandName,
                peer_lifecycle_delivery_required = deliverToPeer && IsPeerLifecycleDeliveryRequired(config),
                peer_cleanup_delivery = peerCleanupDelivery.ToLogSafe()
            });

            if (deliverToPeer && IsPeerLifecycleDeliveryRequired(config) && !peerCleanupDelivery.Success)
            {
                return CommandResponse.Fail(
                    commandName,
                    $"{commandName}_peer_cleanup_delivery_failed:{peerCleanupDelivery.Error ?? peerCleanupDelivery.Action ?? "unknown"}");
            }

            return CommandResponse.Ok(commandName, new
            {
                action = result.IsSkipped ? "cleanup_skipped" : "cleanup_executed",
                peer_id = peerId,
                session_id = sessionId,
                peer_lifecycle_delivery_required = deliverToPeer && IsPeerLifecycleDeliveryRequired(config),
                cleanup_method = cleanupMethod,
                endpoints = new
                {
                    local = ToEndpointResponse(localEndpoint),
                    remote = ToEndpointResponse(remoteEndpoint)
                },
                cleanup_command = new
                {
                    executable = config.ManualSaPath,
                    state_arguments = stateCleanupArgs,
                    selector_arguments = selectorCleanupArgs
                },
                state_cleanup_result = stateCleanupResult is null ? null : ToManualSaResultSummary(stateCleanupResult),
                manual_sa_result = ToManualSaResultSummary(result),
                peer_cleanup_delivery = peerCleanupDelivery.ToResponse()
            });
        }
        catch (Exception ex)
        {
            logger.Error("cleanup_failed", ex, new { peer_id = peerId, session_id = sessionId, command = commandName });
            return CommandResponse.Fail(commandName, $"{commandName}_failed:{peerId}");
        }
        finally
        {
            var planFilePath = activeSession?.PlanFilePath;
            if (!string.IsNullOrWhiteSpace(planFilePath))
            {
                var deleteError = TryDeletePlanFile(planFilePath);
                if (deleteError is not null)
                {
                    logger.Info("manual_sa_plan_file_cleanup_failed", new
                    {
                        peer_id = peerId,
                        session_id = sessionId,
                        plan_file = planFilePath,
                        reason = deleteError
                    });
                }
            }

            var statePath = activeSession?.StatePath;
            if (!string.IsNullOrWhiteSpace(statePath))
            {
                var deleteError = TryDeletePlanFile(statePath);
                if (deleteError is not null)
                {
                    logger.Info("manual_sa_state_file_cleanup_failed", new
                    {
                        peer_id = peerId,
                        session_id = sessionId,
                        state_file = statePath,
                        reason = deleteError
                    });
                }
            }

            lock (gate)
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    sessions.Remove(sessionId);
                    sessionIndex.Remove(sessionId);
                }

                if (peerToSessionId.TryGetValue(peerId, out var existingSessionId)
                    && string.Equals(existingSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    peerToSessionId.Remove(peerId);
                }
            }
        }
    }

    public CommandResponse Status()
    {
        var identity = identityStore.LoadOrCreate(config.NodeId, ResolvePeerIdentity(config.NodeId));
        lock (gate)
        {
            return CommandResponse.Ok("status", new
            {
                node_id = config.NodeId,
                listen_host = config.ListenHost,
                listen_port = config.ListenPort,
                session_count = sessions.Count,
                route_policy = config.RoutePolicy.Mode,
                manual_sa_install_mode = config.ManualSaInstallMode,
                manual_sa_exempt_tcp_ports = config.ManualSaExemptTcpPorts,
                peer_install_delivery_mode = config.PeerInstallDeliveryMode,
                peer_lifecycle_delivery_required = IsPeerLifecycleDeliveryRequired(config),
                maintenance = config.Maintenance,
                peer_install_replay_cache = peerInstallReplayCache.ToStatus(),
                identity = BuildStatusIdentity(identity),
                sessions = sessions.Values
                    .OrderBy(s => s.PeerId)
                    .Select(s => new
                    {
                        peer_id = s.PeerId,
                        session_id = s.SessionId,
                        phase = s.Phase.ToString(),
                        lifecycle_state = s.LifecycleState,
                        route_id = s.RouteId,
                        started_at = s.StartedAt,
                        not_after = s.NotAfter,
                        rekey_at = s.RekeyAt,
                        rekey_due = DateTimeOffset.UtcNow >= s.RekeyAt,
                        install_mode = s.InstallMode,
                        identity_signature_mode = s.IdentitySignatureMode,
                        manual_sa_validated = s.ManualSaValidated,
                        manual_sa_validate_exit_code = s.ManualSaValidateExitCode,
                        manual_sa_install = new
                        {
                            state_path = s.StatePath,
                            plan_file_path = s.PlanFilePath,
                            plan_file_deleted = s.PlanFileDeleted,
                            plan_file_delete_error = s.PlanFileDeleteError,
                            installed = s.ManualSaInstalled,
                            exit_code = s.ManualSaInstallExitCode,
                            error = s.ManualSaInstallError,
                            stdout_summary = s.ManualSaInstallStdoutSummary
                        },
                        local_endpoint = ToEndpointResponse(s.LocalEndpoint),
                        remote_endpoint = ToEndpointResponse(s.RemoteEndpoint),
                        traffic_selector_protocol = s.TrafficSelectorProtocol,
                        inbound_spi = s.InboundSpi,
                        outbound_spi = s.OutboundSpi,
                        key_lengths = s.KeyLengths,
                        plan = s.Plan
                    })
            });
        }
    }

    public CommandResponse EndpointStatus()
    {
        var peers = config.Peers.Keys
            .Append(config.NodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(nodeId => nodeId, StringComparer.OrdinalIgnoreCase)
            .Select(nodeId => ToEndpointResponse(endpointRegistry.Resolve(nodeId, config)))
            .ToArray();

        return CommandResponse.Ok("endpoint-status", new
        {
            node_id = config.NodeId,
            default_ttl_seconds = endpointRegistry.DefaultTtlSeconds,
            registry_count = endpointRegistry.Count,
            records = endpointRegistry.Snapshot().Select(ToEndpointResponse).ToArray(),
            resolved = peers
        });
    }

    public CommandResponse EndpointUpdate(EndpointUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            return CommandResponse.Fail("endpoint-update", "endpoint_update_requires_node_id");
        }

        if (string.IsNullOrWhiteSpace(request.Ip))
        {
            return CommandResponse.Fail("endpoint-update", "endpoint_update_requires_ip");
        }

        if (!IPAddress.TryParse(request.Ip, out _))
        {
            return CommandResponse.Fail("endpoint-update", $"invalid_endpoint_ip:{request.Ip}");
        }

        if (request.Port is <= 0 or > 65535)
        {
            return CommandResponse.Fail("endpoint-update", $"invalid_endpoint_port:{request.Port}");
        }

        var selectorProtocol = string.IsNullOrWhiteSpace(request.SelectorProtocol)
            ? ResolveConfigSelectorProtocol(request.NodeId)
            : request.SelectorProtocol.Trim();

        var updated = endpointRegistry.Update(
            request.NodeId.Trim(),
            request.Ip.Trim(),
            request.Port,
            selectorProtocol,
            request.TtlSeconds);

        logger.Info("endpoint_updated", new
        {
            node_id = updated.NodeId,
            ip = updated.Ip,
            port = updated.Port,
            selector_protocol = updated.SelectorProtocol,
            source = updated.Source,
            sequence = updated.Sequence,
            expires_at = updated.ExpiresAt
        });

        return CommandResponse.Ok("endpoint-update", new
        {
            updated = ToEndpointResponse(endpointRegistry.Resolve(updated.NodeId, config))
        });
    }

    public async Task<CommandResponse> ManualSaPlan(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId))
        {
            return CommandResponse.Fail("manual-sa-plan", "manual_sa_plan_requires_peer");
        }

        if (!config.Peers.TryGetValue(peerId, out var peer))
        {
            return CommandResponse.Fail("manual-sa-plan", $"unknown_peer:{peerId}");
        }

        ManagedSession? activeSession = null;
        lock (gate)
        {
            if (peerToSessionId.TryGetValue(peerId, out var activeSessionId))
            {
                sessions.TryGetValue(activeSessionId, out activeSession);
            }
        }

        if (activeSession is not null)
        {
            return CommandResponse.Ok("manual-sa-plan", new
            {
                peer_id = peerId,
                dry_run = false,
                validate_only = IsValidateOnlyMode(activeSession.InstallMode),
                not_executed_install = activeSession.ManualSaInstallExitCode is null,
                from_active_session = true,
                session_id = activeSession.SessionId,
                route_id = activeSession.RouteId,
                lifecycle_state = activeSession.LifecycleState,
                phase = activeSession.Phase.ToString(),
                install_mode = activeSession.InstallMode,
                not_after = activeSession.NotAfter,
                rekey_at = activeSession.RekeyAt,
                manual_sa_path = config.ManualSaPath,
                ipsec_protocol = NormalizeProtocolForDisplay(activeSession.Plan.IpsecProtocol),
                traffic_selector_protocol = activeSession.TrafficSelectorProtocol,
                endpoints = new
                {
                    local = ToEndpointResponse(activeSession.LocalEndpoint),
                    remote = ToEndpointResponse(activeSession.RemoteEndpoint)
                },
                material_summary = new
                {
                    local_ip = activeSession.Plan.LocalIp,
                    remote_ip = activeSession.Plan.RemoteIp,
                    ipsec_protocol = NormalizeProtocolForDisplay(activeSession.Plan.IpsecProtocol),
                    traffic_selector_protocol = activeSession.TrafficSelectorProtocol,
                    inbound_spi = activeSession.InboundSpi,
                    outbound_spi = activeSession.OutboundSpi,
                    key_lengths = activeSession.KeyLengths
                },
                install_command = new
                {
                    executable = config.ManualSaPath,
                    arguments = activeSession.StatePath is not null && activeSession.PlanFilePath is not null
                        ? BuildLocalInstallArgs(activeSession.StatePath, activeSession.PlanFilePath)
                        : BuildInstallArgs(peerId, activeSession.SessionId, activeSession.Plan),
                    validate_only = IsValidateOnlyMode(activeSession.InstallMode),
                    not_executed = activeSession.ManualSaInstallExitCode is null,
                    not_executed_install = activeSession.ManualSaInstallExitCode is null
                },
                cleanup_command = new
                {
                    executable = config.ManualSaPath,
                    state_arguments = string.IsNullOrWhiteSpace(activeSession.StatePath) ? null : BuildStateDeleteArgs(activeSession.StatePath),
                    selector_arguments = BuildCleanupArgs(activeSession.Plan.LocalIp, activeSession.Plan.RemoteIp, activeSession.TrafficSelectorProtocol)
                },
                manual_sa_validate = new
                {
                    success = activeSession.ManualSaValidated,
                    exit_code = activeSession.ManualSaValidateExitCode,
                    stderr = activeSession.ManualSaValidationError
                },
                manual_sa_install = new
                {
                    state_path = activeSession.StatePath,
                    plan_file_path = activeSession.PlanFilePath,
                    plan_file_deleted = activeSession.PlanFileDeleted,
                    plan_file_delete_error = activeSession.PlanFileDeleteError,
                    installed = activeSession.ManualSaInstalled,
                    exit_code = activeSession.ManualSaInstallExitCode,
                    error = activeSession.ManualSaInstallError,
                    stdout_summary = activeSession.ManualSaInstallStdoutSummary
                },
                peer_install_bundle = BuildPeerInstallBundle(
                    activeSession.PeerId,
                    activeSession.SessionId,
                    activeSession.RouteId,
                    activeSession.Plan,
                    ToPeerKeyLengths(activeSession.Plan))
            });
        }

        var sessionId = ResolveSessionIdForPlan(peerId);
        var routeId = $"{config.RoutePolicy.Mode}:{config.NodeId}->{peerId}:{sessionId}";
        var localPeer = BuildLocalPeerDescriptor();
        var remotePeer = BuildRemotePeerDescriptor(peerId, peer);
        var localEndpoint = endpointRegistry.Resolve(config.NodeId, config);
        var remoteEndpoint = endpointRegistry.Resolve(peerId, config);
        var selectorProtocol = ResolvePlanSelectorProtocol(remoteEndpoint.Record, localEndpoint.Record, peer);
        var localIp = localEndpoint.Record.Ip;
        var remoteIp = remoteEndpoint.Record.Ip;
        var spis = DerivePlanSpis(peerId);
        var installPlan = BuildManualSaPlan(peerId, sessionId, localIp, remoteIp, selectorProtocol, spis);
        var validateResult = await RunPlanValidationAsync(peerId, sessionId, installPlan);

        return CommandResponse.Ok("manual-sa-plan", new
        {
            peer_id = peerId,
            dry_run = true,
            validate_only = true,
            not_executed_install = true,
            session_id = sessionId,
            route_id = routeId,
            manual_sa_path = config.ManualSaPath,
            ipsec_protocol = NormalizeProtocolForDisplay(installPlan.IpsecProtocol),
            traffic_selector_protocol = installPlan.TrafficSelectorProtocol,
            endpoints = new
            {
                local = ToEndpointResponse(localEndpoint),
                remote = ToEndpointResponse(remoteEndpoint)
            },
            material_summary = new
            {
                local_ip = installPlan.LocalIp,
                remote_ip = installPlan.RemoteIp,
                ipsec_protocol = NormalizeProtocolForDisplay(installPlan.IpsecProtocol),
                traffic_selector_protocol = installPlan.TrafficSelectorProtocol,
                inbound_spi = installPlan.InboundSpi,
                outbound_spi = installPlan.OutboundSpi,
                inbound_cipher_key_length = installPlan.Profile.KeyLengths.InboundCipherKeyLength,
                inbound_auth_key_length = installPlan.Profile.KeyLengths.InboundAuthKeyLength,
                outbound_cipher_key_length = installPlan.Profile.KeyLengths.OutboundCipherKeyLength,
                outbound_auth_key_length = installPlan.Profile.KeyLengths.OutboundAuthKeyLength
            },
            install_command = new
            {
                executable = config.ManualSaPath,
                arguments = BuildInstallArgs(peerId, sessionId, installPlan),
                validate_only = true,
                not_executed = true,
                not_executed_install = true
            },
            cleanup_command = new
            {
                executable = config.ManualSaPath,
                arguments = BuildCleanupArgs(localIp, remoteIp, selectorProtocol)
            },
            peer = new
            {
                peer_id = remotePeer.PeerId,
                endpoint = remoteEndpoint.Record.Ip,
                port = remoteEndpoint.Record.Port,
                endpoint_source = remoteEndpoint.Record.Source,
                endpoint_sequence = remoteEndpoint.Record.Sequence,
                endpoint_expires_at = remoteEndpoint.Record.ExpiresAt,
                config_endpoint = peer.Endpoint,
                config_overlay_endpoint = peer.OverlayEndpoint,
                public_identity_key = peer.PublicIdentityKey,
                trusted_local_key_id = localPeer.KeyId,
                trusted_remote_key_id = remotePeer.KeyId
            },
            note = "dry-run plan only: install command is not executed",
            manual_sa_validate = new
            {
                success = validateResult.Success,
                exit_code = validateResult.ExitCode,
                duration_ms = validateResult.DurationMs,
                stdout_summary = validateResult.StdoutSummary,
                stderr = validateResult.Stderr
            },
            peer_install_bundle = BuildPeerInstallBundle(
                remotePeer.PeerId,
                sessionId,
                routeId,
                installPlan,
                ToPeerKeyLengths(installPlan.ToLogSafe()))
        });
    }

    private ManualSaInstallPlan BuildManualSaPlan(
        string peerId,
        string sessionId,
        string localIp,
        string remoteIp,
        string selectorProtocol,
        (uint InboundSpi, uint OutboundSpi) spis)
    {
        try
        {
            var baselinePlan = AikeMessageCodecSmoke.RunEstablishedAndBuildManualSaInstallPlan();
            if (baselinePlan.Success && baselinePlan.Plan is not null)
            {
                return baselinePlan.Plan with
                {
                    LocalIp = localIp,
                    RemoteIp = remoteIp,
                    TrafficSelectorProtocol = selectorProtocol,
                    InboundSpi = spis.InboundSpi,
                    OutboundSpi = spis.OutboundSpi
                };
            }

            logger.Info("manual_sa_plan_smoke_not_ready", new
            {
                peer_id = peerId,
                session_id = sessionId,
                error = baselinePlan.Error,
                fallback = true
            });
        }
        catch (Exception ex)
        {
            logger.Info("manual_sa_plan_smoke_failed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                error = ex.Message
            });
        }

        return new ManualSaInstallPlan(
            LocalIp: localIp,
            RemoteIp: remoteIp,
            IpsecProtocol: ManualSaInstallPlan.IpsecProtocolEsp,
            TrafficSelectorProtocol: selectorProtocol,
            Profile: EspProfiles.ManualSa,
            InboundSpi: spis.InboundSpi,
            OutboundSpi: spis.OutboundSpi,
            InboundCipherKeyPlaceholder: new byte[EspProfiles.ManualSa.KeyLengths.InboundCipherKeyLength],
            InboundAuthKeyPlaceholder: new byte[EspProfiles.ManualSa.KeyLengths.InboundAuthKeyLength],
            OutboundCipherKeyPlaceholder: new byte[EspProfiles.ManualSa.KeyLengths.OutboundCipherKeyLength],
            OutboundAuthKeyPlaceholder: new byte[EspProfiles.ManualSa.KeyLengths.OutboundAuthKeyLength]);
    }

    private static ManualSaInstallPlan BuildManualSaPlanFromDirectionalKeys(
        string localIp,
        string remoteIp,
        string selectorProtocol,
        (uint InboundSpi, uint OutboundSpi) spis,
        DirectionalKeys keys)
    {
        return new ManualSaInstallPlan(
            LocalIp: localIp,
            RemoteIp: remoteIp,
            IpsecProtocol: ManualSaInstallPlan.IpsecProtocolEspNumeric,
            TrafficSelectorProtocol: selectorProtocol,
            Profile: EspProfiles.ManualSa,
            InboundSpi: spis.InboundSpi,
            OutboundSpi: spis.OutboundSpi,
            InboundCipherKeyPlaceholder: Convert.FromHexString(keys.InCipherKey),
            InboundAuthKeyPlaceholder: Convert.FromHexString(keys.InAuthKey),
            OutboundCipherKeyPlaceholder: Convert.FromHexString(keys.OutCipherKey),
            OutboundAuthKeyPlaceholder: Convert.FromHexString(keys.OutAuthKey));
    }

    private string ResolvePlanSelectorProtocol(
        EndpointRecord remoteEndpoint,
        EndpointRecord localEndpoint,
        PeerConfig peer)
    {
        return FirstNonEmpty(
            remoteEndpoint.SelectorProtocol,
            localEndpoint.SelectorProtocol,
            peer.AllowedSelector,
            ManualSaInstallPlan.DefaultTrafficSelectorProtocol);
    }

    private string ResolveConfigSelectorProtocol(string nodeId)
    {
        return config.Peers.TryGetValue(nodeId, out var peer)
            ? FirstNonEmpty(peer.AllowedSelector, ManualSaInstallPlan.DefaultTrafficSelectorProtocol)
            : ManualSaInstallPlan.DefaultTrafficSelectorProtocol;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return ManualSaInstallPlan.DefaultTrafficSelectorProtocol;
    }

    private static object ToEndpointResponse(EndpointResolution endpoint) => new
    {
        node_id = endpoint.Record.NodeId,
        ip = endpoint.Record.Ip,
        port = endpoint.Record.Port,
        selector_protocol = endpoint.Record.SelectorProtocol,
        endpoint_source = endpoint.Record.Source,
        endpoint_sequence = endpoint.Record.Sequence,
        endpoint_valid_from = endpoint.Record.ValidFrom,
        endpoint_expires_at = endpoint.Record.ExpiresAt,
        signature = endpoint.Record.Signature,
        is_fallback = endpoint.IsFallback,
        is_expired = endpoint.IsExpired
    };

    private static object ToEndpointResponse(EndpointRecord endpoint) => new
    {
        node_id = endpoint.NodeId,
        ip = endpoint.Ip,
        port = endpoint.Port,
        selector_protocol = endpoint.SelectorProtocol,
        endpoint_source = endpoint.Source,
        endpoint_sequence = endpoint.Sequence,
        endpoint_valid_from = endpoint.ValidFrom,
        endpoint_expires_at = endpoint.ExpiresAt,
        signature = endpoint.Signature
    };

    private async Task<ManualSaPlanValidateResult> RunPlanValidationAsync(
        string peerId,
        string sessionId,
        ManualSaInstallPlan plan)
    {
        if (config.ValidateManualSaPlan is false)
        {
            return ManualSaPlanValidateResult.Disabled("manual-sa plan validation disabled by configuration");
        }

        var request = new ManualSaPlanValidationRequest(peerId, sessionId, plan);

        return await manualSaPlanValidator.ValidatePlanAsync(request).ConfigureAwait(false);
    }

    private async Task<ManualSaLocalCreateResult> RunLocalCreateAsync(
        string peerId,
        string sessionId,
        ManualSaInstallPlan plan,
        CancellationToken cancellationToken)
    {
        string? statePath = null;
        string? planFilePath = null;

        try
        {
            Directory.CreateDirectory(config.ManualSaStateDir);
            var filePrefix = $"{SafePathSegment(peerId)}-{SafePathSegment(sessionId)}";
            statePath = Path.Combine(config.ManualSaStateDir, $"{filePrefix}.state");
            planFilePath = Path.Combine(config.ManualSaStateDir, $"{filePrefix}.plan");
            var serializedPlan = ManualSaPlanFileSerializer.ToKeyValuePlan(plan);
            File.WriteAllText(planFilePath, serializedPlan, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var createResult = await manualSaRunner.RunAsync(
                    "create",
                    BuildLocalCreateArgs(statePath, plan),
                    cancellationToken)
                .ConfigureAwait(false);
            var actualInboundSpi = createResult.ExitCode == 0
                ? TryParseManualSaInboundSpi(createResult.Stdout)
                : null;
            var exitCode = createResult.ExitCode;
            var stderrSummary = SanitizeManualSaOutput(createResult.Stderr);
            var stdoutSummary = SanitizeManualSaOutput(createResult.Stdout);
            var error = exitCode == 0 && actualInboundSpi is null
                ? "manual-sa create succeeded but did not report inbound_spi"
                : exitCode == 0
                    ? null
                    : string.IsNullOrWhiteSpace(stderrSummary) ? stdoutSummary : stderrSummary;
            if (exitCode == 0 && actualInboundSpi is null)
            {
                exitCode = 1;
            }

            logger.Info("manual_sa_local_create_completed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                exit_code = exitCode,
                created = exitCode == 0,
                actual_inbound_spi = actualInboundSpi,
                state_path = statePath,
                plan_file_path = planFilePath
            });

            return new ManualSaLocalCreateResult(
                StatePath: statePath,
                PlanFilePath: planFilePath,
                Created: exitCode == 0,
                ExitCode: exitCode,
                Error: error,
                StdoutSummary: stdoutSummary,
                ActualInboundSpi: actualInboundSpi);
        }
        catch (Exception ex)
        {
            logger.Error("manual_sa_local_create_failed", ex, new
            {
                peer_id = peerId,
                session_id = sessionId,
                state_path = statePath,
                plan_file_path = planFilePath
            });

            return new ManualSaLocalCreateResult(
                StatePath: statePath,
                PlanFilePath: planFilePath,
                Created: false,
                ExitCode: -1,
                Error: ex.Message,
                StdoutSummary: null,
                ActualInboundSpi: null);
        }
    }

    private async Task<ManualSaLocalCompleteResult> RunLocalCompleteAsync(
        string peerId,
        string sessionId,
        string statePath,
        string? planFilePath,
        uint peerInboundSpi,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await manualSaRunner.RunAsync(
                    "complete",
                    BuildLocalCompleteArgs(statePath, peerInboundSpi),
                    cancellationToken)
                .ConfigureAwait(false);
            var stdoutSummary = SanitizeManualSaOutput(result.Stdout);
            var stderrSummary = SanitizeManualSaOutput(result.Stderr);
            var error = result.ExitCode == 0
                ? null
                : string.IsNullOrWhiteSpace(stderrSummary) ? stdoutSummary : stderrSummary;
            var deleteError = string.IsNullOrWhiteSpace(planFilePath) ? null : TryDeletePlanFile(planFilePath);
            var planFileDeleted = string.IsNullOrWhiteSpace(planFilePath) || deleteError is null;
            if (deleteError is not null)
            {
                logger.Info("manual_sa_plan_file_cleanup_failed", new
                {
                    peer_id = peerId,
                    session_id = sessionId,
                    plan_file = planFilePath,
                    reason = deleteError
                });
            }

            logger.Info("manual_sa_local_complete_completed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                exit_code = result.ExitCode,
                completed = result.ExitCode == 0,
                peer_inbound_spi = peerInboundSpi,
                state_path = statePath,
                plan_file_deleted = planFileDeleted
            });

            return new ManualSaLocalCompleteResult(
                Completed: result.ExitCode == 0,
                ExitCode: result.ExitCode,
                Error: error,
                StdoutSummary: stdoutSummary,
                PlanFileDeleted: planFileDeleted,
                PlanFileDeleteError: deleteError);
        }
        catch (Exception ex)
        {
            var deleteError = string.IsNullOrWhiteSpace(planFilePath) ? null : TryDeletePlanFile(planFilePath);
            var planFileDeleted = string.IsNullOrWhiteSpace(planFilePath) || deleteError is null;
            logger.Error("manual_sa_local_complete_failed", ex, new
            {
                peer_id = peerId,
                session_id = sessionId,
                state_path = statePath,
                plan_file_deleted = planFileDeleted
            });

            return new ManualSaLocalCompleteResult(
                Completed: false,
                ExitCode: -1,
                Error: ex.Message,
                StdoutSummary: null,
                PlanFileDeleted: planFileDeleted,
                PlanFileDeleteError: deleteError);
        }
    }

    private async Task<ManualSaLocalInstallResult> RunLocalInstallAsync(
        string peerId,
        string sessionId,
        ManualSaInstallPlan plan,
        CancellationToken cancellationToken)
    {
        string? statePath = null;
        string? planFilePath = null;

        try
        {
            Directory.CreateDirectory(config.ManualSaStateDir);
            var filePrefix = $"{SafePathSegment(peerId)}-{SafePathSegment(sessionId)}";
            statePath = Path.Combine(config.ManualSaStateDir, $"{filePrefix}.state");
            planFilePath = Path.Combine(config.ManualSaStateDir, $"{filePrefix}.plan");
            var serializedPlan = ManualSaPlanFileSerializer.ToKeyValuePlan(plan);
            File.WriteAllText(planFilePath, serializedPlan, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var createResult = await manualSaRunner.RunAsync(
                    "create",
                    BuildLocalCreateArgs(statePath, plan),
                    cancellationToken)
                .ConfigureAwait(false);
            uint? actualInboundSpi = null;
            ManualSaResult result = createResult;

            if (createResult.ExitCode == 0)
            {
                actualInboundSpi = TryParseManualSaInboundSpi(createResult.Stdout);
                if (actualInboundSpi is null)
                {
                    result = new ManualSaResult(
                        1,
                        createResult.Stdout,
                        "manual-sa create succeeded but did not report inbound_spi",
                        IsSkipped: false);
                }
                else
                {
                    result = await manualSaRunner.RunAsync(
                            "complete",
                            BuildLocalCompleteArgs(statePath, plan.OutboundSpi),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var stdoutSummary = SanitizeManualSaOutput(result.Stdout);
            var stderrSummary = SanitizeManualSaOutput(result.Stderr);
            var error = result.ExitCode == 0
                ? null
                : string.IsNullOrWhiteSpace(stderrSummary) ? stdoutSummary : stderrSummary;
            var deleteError = TryDeletePlanFile(planFilePath);
            var planFileDeleted = deleteError is null;
            if (deleteError is not null)
            {
                logger.Info("manual_sa_plan_file_cleanup_failed", new
                {
                    peer_id = peerId,
                    session_id = sessionId,
                    plan_file = planFilePath,
                    reason = deleteError
                });
            }

            logger.Info("manual_sa_local_install_completed", new
            {
                peer_id = peerId,
                session_id = sessionId,
                exit_code = result.ExitCode,
                installed = result.ExitCode == 0,
                actual_inbound_spi = actualInboundSpi,
                state_path = statePath,
                plan_file_deleted = planFileDeleted
            });

            return new ManualSaLocalInstallResult(
                StatePath: statePath,
                PlanFilePath: planFileDeleted ? null : planFilePath,
                PlanFileDeleted: planFileDeleted,
                PlanFileDeleteError: deleteError,
                Installed: result.ExitCode == 0,
                ExitCode: result.ExitCode,
                Error: error,
                StdoutSummary: stdoutSummary,
                ActualInboundSpi: actualInboundSpi);
        }
        catch (Exception ex)
        {
            var deleteError = string.IsNullOrWhiteSpace(planFilePath)
                ? null
                : TryDeletePlanFile(planFilePath);
            var planFileDeleted = !string.IsNullOrWhiteSpace(planFilePath) && deleteError is null;
            if (deleteError is not null)
            {
                logger.Info("manual_sa_plan_file_cleanup_failed", new
                {
                    peer_id = peerId,
                    session_id = sessionId,
                    plan_file = planFilePath,
                    reason = deleteError
                });
            }

            logger.Error("manual_sa_local_install_failed", ex, new
            {
                peer_id = peerId,
                session_id = sessionId,
                state_path = statePath,
                plan_file_deleted = planFileDeleted
            });

            return new ManualSaLocalInstallResult(
                StatePath: statePath,
                PlanFilePath: planFileDeleted ? null : planFilePath,
                PlanFileDeleted: planFileDeleted,
                PlanFileDeleteError: deleteError,
                Installed: false,
                ExitCode: -1,
                Error: ex.Message,
                StdoutSummary: null,
                ActualInboundSpi: null);
        }
    }

    private async Task<object> CleanupManagedSessionAsync(
        ManagedSession session,
        string reason,
        CancellationToken cancellationToken)
    {
        var cleanup = await CleanupStatePathAsync(
                peerId: session.PeerId,
                sessionId: session.SessionId,
                statePath: session.StatePath,
                planFilePath: session.PlanFilePath,
                localIp: session.Plan.LocalIp,
                remoteIp: session.Plan.RemoteIp,
                selectorProtocol: session.TrafficSelectorProtocol,
                reason,
                cancellationToken)
            .ConfigureAwait(false);

        sessionIndex.Remove(session.SessionId);
        return cleanup;
    }

    private async Task<object> CleanupStatePathAsync(
        string peerId,
        string sessionId,
        string? statePath,
        string? planFilePath,
        string localIp,
        string remoteIp,
        string selectorProtocol,
        string reason,
        CancellationToken cancellationToken)
    {
        ManualSaResult? stateCleanupResult = null;
        ManualSaResult? selectorCleanupResult = null;
        var cleanupMethod = "none";

        if (!string.IsNullOrWhiteSpace(statePath) && File.Exists(statePath))
        {
            stateCleanupResult = await manualSaRunner.RunAsync(
                    "delete",
                    BuildStateDeleteArgs(statePath),
                    cancellationToken)
                .ConfigureAwait(false);
            cleanupMethod = stateCleanupResult.ExitCode == 0 ? "state_delete" : "state_delete_failed";
        }

        if (stateCleanupResult is null || stateCleanupResult.ExitCode != 0)
        {
            selectorCleanupResult = await manualSaRunner.RunAsync(
                    "cleanup",
                    BuildCleanupArgs(localIp, remoteIp, selectorProtocol),
                    cancellationToken)
                .ConfigureAwait(false);
            cleanupMethod = stateCleanupResult is null
                ? "selector_cleanup"
                : "selector_cleanup_after_state_delete_failed";
        }

        var planDeleteError = TryDeletePlanFile(planFilePath);
        var stateDeleteError = TryDeletePlanFile(statePath);

        logger.Info("manual_sa_session_cleanup_completed", new
        {
            peer_id = peerId,
            session_id = sessionId,
            reason,
            cleanup_method = cleanupMethod,
            state_cleanup_exit_code = stateCleanupResult?.ExitCode,
            selector_cleanup_exit_code = selectorCleanupResult?.ExitCode,
            plan_file_deleted = string.IsNullOrWhiteSpace(planFilePath) || planDeleteError is null,
            plan_file_delete_error = planDeleteError,
            state_file_deleted = string.IsNullOrWhiteSpace(statePath) || stateDeleteError is null,
            state_file_delete_error = stateDeleteError
        });

        return new
        {
            reason,
            cleanup_method = cleanupMethod,
            state_cleanup = stateCleanupResult is null ? null : ToManualSaResultSummary(stateCleanupResult),
            selector_cleanup = selectorCleanupResult is null ? null : ToManualSaResultSummary(selectorCleanupResult),
            plan_file_deleted = string.IsNullOrWhiteSpace(planFilePath) || planDeleteError is null,
            plan_file_delete_error = planDeleteError,
            state_file_deleted = string.IsNullOrWhiteSpace(statePath) || stateDeleteError is null,
            state_file_delete_error = stateDeleteError
        };
    }

    private string ResolveSessionIdForPlan(string peerId)
    {
        lock (gate)
        {
            if (peerToSessionId.TryGetValue(peerId, out var existing))
            {
                return existing;
            }
        }

        return $"dry-run-{Guid.NewGuid():N}";
    }

    private static (uint InboundSpi, uint OutboundSpi) DerivePlanSpis(string peerId)
    {
        unchecked
        {
            var hash = (uint)HashCode.Combine(peerId, "adaptive-adaptive-client");
            return (InboundSpi: 0xA100u + (hash & 0x01FF), OutboundSpi: 0xB200u + (hash & 0x01FF));
        }
    }

    private static (uint InboundSpi, uint OutboundSpi) GenerateSessionSpis()
    {
        var inbound = GenerateSpi();
        var outbound = GenerateSpi();
        while (outbound == inbound)
        {
            outbound = GenerateSpi();
        }

        return (inbound, outbound);
    }

    private static uint GenerateSpi()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt32(bytes);
        }
        while (value < 0x100u);

        return value;
    }

    private static string[] BuildInstallArgs(
        string peerId,
        string sessionId,
        ManualSaInstallPlan plan)
    {
        return
        [
            "--peer", peerId,
            "--session-id", sessionId,
            "--direction", "install",
            "--protocol", plan.TrafficSelectorProtocol,
            "--ipsec-protocol", NormalizeProtocolForDisplay(plan.IpsecProtocol),
            "--local-ip", plan.LocalIp,
            "--remote-ip", plan.RemoteIp,
            "--inbound-spi", plan.InboundSpi.ToString(CultureInfo.InvariantCulture),
            "--outbound-spi", plan.OutboundSpi.ToString(CultureInfo.InvariantCulture)
        ];
    }

    private static string[] BuildInstallArgs(
        string peerId,
        string sessionId,
        ManualSaInstallPlanLog plan)
    {
        return
        [
            "--peer", peerId,
            "--session-id", sessionId,
            "--direction", "install",
            "--protocol", plan.TrafficSelectorProtocol,
            "--ipsec-protocol", NormalizeProtocolForDisplay(plan.IpsecProtocol),
            "--local-ip", plan.LocalIp,
            "--remote-ip", plan.RemoteIp,
            "--inbound-spi", plan.InboundSpi.ToString(CultureInfo.InvariantCulture),
            "--outbound-spi", plan.OutboundSpi.ToString(CultureInfo.InvariantCulture),
            "--keys", "<redacted>"
        ];
    }

    private static string[] BuildLocalInstallArgs(string statePath, string planFilePath)
    {
        return
        [
            "install",
            "--state", statePath,
            "--plan-file", planFilePath
        ];
    }

    private string[] BuildLocalCreateArgs(string statePath, ManualSaInstallPlan plan)
    {
        var args = new List<string>
        {
            "create",
            "--local", plan.LocalIp,
            "--remote", plan.RemoteIp,
            "--state", statePath,
            "--protocol", plan.TrafficSelectorProtocol,
            "--in-cipher-key", Hex(plan.InboundCipherKeyPlaceholder),
            "--in-auth-key", Hex(plan.InboundAuthKeyPlaceholder),
            "--out-cipher-key", Hex(plan.OutboundCipherKeyPlaceholder),
            "--out-auth-key", Hex(plan.OutboundAuthKeyPlaceholder)
        };

        if (!string.IsNullOrWhiteSpace(config.ManualSaExemptTcpPorts))
        {
            args.Add("--exempt-tcp-ports");
            args.Add(config.ManualSaExemptTcpPorts);
        }

        return args.ToArray();
    }

    private static string[] BuildLocalCompleteArgs(string statePath, uint peerInboundSpi)
    {
        return
        [
            "complete",
            "--state", statePath,
            "--peer-spi", peerInboundSpi.ToString(CultureInfo.InvariantCulture)
        ];
    }

    private static uint? TryParseManualSaInboundSpi(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("inbound_spi=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["inbound_spi=".Length..].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return uint.TryParse(
                    value[2..],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var hexSpi)
                    ? hexSpi
                    : null;
            }

            return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var spi)
                ? spi
                : null;
        }

        return null;
    }

    private static string Hex(byte[] bytes) =>
        bytes.Length == 0 ? string.Empty : Convert.ToHexString(bytes).ToLowerInvariant();

    private static string NormalizeProtocolForDisplay(string protocol)
    {
        return protocol.Equals(ManualSaInstallPlan.IpsecProtocolEsp, StringComparison.OrdinalIgnoreCase)
            ? ManualSaInstallPlan.IpsecProtocolEspNumeric
            : protocol;
    }

    private static string[] BuildCleanupArgs(string localIp, string remoteIp, string selectorProtocol)
    {
        return
        [
            "cleanup",
            "--local", localIp,
            "--remote", remoteIp,
            "--protocol", selectorProtocol
        ];
    }

    private static string[] BuildStateDeleteArgs(string statePath)
    {
        return
        [
            "delete",
            "--state", statePath
        ];
    }

    private static bool IsValidateOnlyMode(string mode) =>
        string.Equals(mode, ClientConfig.ManualSaInstallModeValidateOnly, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalInstallMode(string mode) =>
        string.Equals(mode, ClientConfig.ManualSaInstallModeLocalInstall, StringComparison.OrdinalIgnoreCase);

    private async Task<PeerInstallDeliveryResult> TryDeliverPeerInstallAsync(
        string peerId,
        PeerConfig peerConfig,
        ManagedSession managed,
        ManualSaInstallPlan installPlan,
        DirectionalKeys? peerKeys,
        bool installLifecycleSucceeded,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(config.PeerInstallDeliveryMode, ClientConfig.PeerInstallDeliveryModeServiceToService, StringComparison.OrdinalIgnoreCase))
        {
            return PeerInstallDeliveryResult.Skipped(config.PeerInstallDeliveryMode, "peer_install_delivery_mode is disabled");
        }

        if (!installLifecycleSucceeded)
        {
            return PeerInstallDeliveryResult.NotAttempted(config.PeerInstallDeliveryMode, "local install lifecycle did not reach validated state");
        }

        if (peerKeys is null)
        {
            return PeerInstallDeliveryResult.NotAttempted(config.PeerInstallDeliveryMode, "peer directional keys are missing");
        }

        if (!TryParseOverlayEndpoint(peerConfig.OverlayEndpoint, out var host, out var port, out var endpointError))
        {
            return PeerInstallDeliveryResult.Failed(
                config.PeerInstallDeliveryMode,
                peerConfig.OverlayEndpoint,
                0,
                endpointError ?? "invalid peer overlay endpoint");
        }

        var endpoint = $"{host}:{port}";
        var sw = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(1000, config.RetryPolicy.ConnectTimeoutMs));

            var statusResponse = await SendPeerControlCommandAsync(
                    host,
                    port,
                    new ControlCommand("status"),
                    timeout.Token)
                .ConfigureAwait(false);
            var peerIdentity = ExtractPeerServiceIdentity(statusResponse);
            var identityProof = VerifyPeerServiceIdentity(peerIdentity, peerId, peerConfig);
            if (!identityProof.Success)
            {
                sw.Stop();
                return PeerInstallDeliveryResult.Failed(
                    config.PeerInstallDeliveryMode,
                    endpoint,
                    sw.ElapsedMilliseconds,
                    identityProof.Error ?? "peer service identity proof verification failed",
                    identityProof);
            }

            var recipientPublicKey = peerIdentity.PeerInstallEnvelopePublicKey;
            if (string.IsNullOrWhiteSpace(recipientPublicKey))
            {
                sw.Stop();
                return PeerInstallDeliveryResult.Failed(
                    config.PeerInstallDeliveryMode,
                    endpoint,
                    sw.ElapsedMilliseconds,
                    "peer service status did not expose peer_install_envelope_public_key",
                    identityProof);
            }

            if (!X25519EnvelopeIdentity.IsX25519PublicIdentityKey(recipientPublicKey))
            {
                sw.Stop();
                return PeerInstallDeliveryResult.Failed(
                    config.PeerInstallDeliveryMode,
                    endpoint,
                    sw.ElapsedMilliseconds,
                    "peer service envelope public key is not x25519",
                    identityProof);
            }

            var payload = BuildPeerInstallPayload(
                peerId,
                managed.SessionId,
                managed.RouteId,
                installPlan,
                peerKeys,
                managed.IdentitySignatureMode);
            var envelope = PeerInstallEnvelope.EncryptForRecipient(
                payload,
                recipientPublicKey,
                senderId: config.NodeId,
                recipientId: peerId,
                sessionId: managed.SessionId,
                routeId: managed.RouteId);
            var parameters = BuildPeerInstallEnvelopeParameters(peerId, managed.SessionId, managed.RouteId, envelope);
            var peerInstallResponse = await SendPeerControlCommandAsync(
                    host,
                    port,
                    new ControlCommand(
                        Command: "peer-install",
                        Peer: peerId,
                        SessionId: managed.SessionId,
                        Parameters: parameters),
                    timeout.Token)
                .ConfigureAwait(false);

            sw.Stop();
            return PeerInstallDeliveryResult.FromPeerInstallResponse(
                config.PeerInstallDeliveryMode,
                endpoint,
                sw.ElapsedMilliseconds,
                peerInstallResponse,
                identityProof);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return PeerInstallDeliveryResult.Failed(config.PeerInstallDeliveryMode, endpoint, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private async Task<PeerCleanupDeliveryResult> TryDeliverPeerCleanupAsync(
        string peerId,
        PeerConfig peerConfig,
        string sessionId,
        bool cleanupSucceeded,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(config.PeerInstallDeliveryMode, ClientConfig.PeerInstallDeliveryModeServiceToService, StringComparison.OrdinalIgnoreCase))
        {
            return PeerCleanupDeliveryResult.Skipped(config.PeerInstallDeliveryMode, "peer_install_delivery_mode is disabled");
        }

        if (!cleanupSucceeded)
        {
            return PeerCleanupDeliveryResult.NotAttempted(config.PeerInstallDeliveryMode, "local cleanup did not succeed");
        }

        if (!TryParseOverlayEndpoint(peerConfig.OverlayEndpoint, out var host, out var port, out var endpointError))
        {
            return PeerCleanupDeliveryResult.Failed(
                config.PeerInstallDeliveryMode,
                peerConfig.OverlayEndpoint,
                0,
                endpointError ?? "invalid peer overlay endpoint");
        }

        var endpoint = $"{host}:{port}";
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(1000, config.RetryPolicy.ConnectTimeoutMs));
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_node_id"] = config.NodeId,
                ["source_session_id"] = sessionId
            };
            var response = await SendPeerControlCommandAsync(
                    host,
                    port,
                    new ControlCommand(
                        Command: "peer-cleanup",
                        Peer: peerId,
                        SessionId: sessionId,
                        Parameters: parameters),
                    timeout.Token)
                .ConfigureAwait(false);

            sw.Stop();
            return PeerCleanupDeliveryResult.FromPeerCleanupResponse(
                config.PeerInstallDeliveryMode,
                endpoint,
                sw.ElapsedMilliseconds,
                response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return PeerCleanupDeliveryResult.Failed(config.PeerInstallDeliveryMode, endpoint, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private IReadOnlyDictionary<string, string> BuildPeerInstallPayload(
        string peerId,
        string sessionId,
        string routeId,
        ManualSaInstallPlan hostPlan,
        DirectionalKeys peerKeys,
        string? identitySignatureMode)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["peer_id"] = peerId,
            ["remote_peer_id"] = config.NodeId,
            ["session_id"] = sessionId,
            ["route_id"] = routeId,
            ["local_ip"] = hostPlan.RemoteIp,
            ["remote_ip"] = hostPlan.LocalIp,
            ["traffic_selector_protocol"] = hostPlan.TrafficSelectorProtocol,
            ["inbound_spi"] = hostPlan.OutboundSpi.ToString(CultureInfo.InvariantCulture),
            ["outbound_spi"] = hostPlan.InboundSpi.ToString(CultureInfo.InvariantCulture),
            ["in_cipher_key"] = peerKeys.InCipherKey,
            ["in_auth_key"] = peerKeys.InAuthKey,
            ["out_cipher_key"] = peerKeys.OutCipherKey,
            ["out_auth_key"] = peerKeys.OutAuthKey,
            ["identity_signature_mode"] = FirstNonEmpty(identitySignatureMode, "unknown"),
            ["source"] = "service_to_service_x25519_envelope"
        };
    }

    private static Dictionary<string, string> BuildPeerInstallEnvelopeParameters(
        string peerId,
        string sessionId,
        string routeId,
        PeerInstallEncryptedEnvelope envelope)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["peer_id"] = peerId,
            ["session_id"] = sessionId,
            ["route_id"] = routeId,
            ["envelope_version"] = envelope.EnvelopeVersion.ToString(CultureInfo.InvariantCulture),
            ["envelope_mode"] = envelope.EnvelopeMode,
            ["envelope_encryption"] = envelope.EnvelopeEncryption,
            ["envelope_kdf"] = envelope.EnvelopeKdf,
            ["envelope_sender_id"] = envelope.EnvelopeSenderId,
            ["envelope_recipient_id"] = envelope.EnvelopeRecipientId,
            ["envelope_nonce"] = envelope.EnvelopeNonce,
            ["envelope_ciphertext"] = envelope.EnvelopeCiphertext,
            ["envelope_tag"] = envelope.EnvelopeTag
        };

        if (!string.IsNullOrWhiteSpace(envelope.EnvelopeEphemeralPublicKey))
        {
            parameters["envelope_ephemeral_public_key"] = envelope.EnvelopeEphemeralPublicKey;
        }

        return parameters;
    }

    private static async Task<string> SendPeerControlCommandAsync(
        string host,
        int port,
        ControlCommand command,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        var request = JsonSerializer.Serialize(command, ProgramJson.Options);
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
        var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response)
            ? throw new InvalidOperationException("peer service returned an empty response.")
            : response;
    }

    private PeerServiceIdentityStatus ExtractPeerServiceIdentity(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var success = TryGetJsonBoolean(root, "success", out var successValue) && successValue;

        return new PeerServiceIdentityStatus(
            StatusSuccess: success,
            NodeId: TryGetJsonString(root, out var nodeId, "data", "node_id") ? nodeId : null,
            LocalPeerId: TryGetJsonString(root, out var localPeerId, "data", "identity", "local_peer_id") ? localPeerId : null,
            KeyId: TryGetJsonString(root, out var keyId, "data", "identity", "local_key_id") ? keyId : null,
            PublicIdentityKey: TryGetJsonString(root, out var publicIdentityKey, "data", "identity", "local_public_identity_key") ? publicIdentityKey : null,
            PeerInstallEnvelopePublicKey: TryGetJsonString(root, out var envelopePublicKey, "data", "identity", "peer_install_envelope_public_key") ? envelopePublicKey : null,
            ProofVersion: TryGetJsonInt(root, out var proofVersion, "data", "identity", "identity_proof", "version") ? proofVersion : null,
            ProofAlgorithm: TryGetJsonString(root, out var proofAlgorithm, "data", "identity", "identity_proof", "algorithm") ? proofAlgorithm : null,
            ProofPurpose: TryGetJsonString(root, out var proofPurpose, "data", "identity", "identity_proof", "purpose") ? proofPurpose : null,
            ProofIssuedAt: TryGetJsonString(root, out var proofIssuedAt, "data", "identity", "identity_proof", "issued_at") ? proofIssuedAt : null,
            ProofSignature: TryGetJsonString(root, out var proofSignature, "data", "identity", "identity_proof", "signature") ? proofSignature : null);
    }

    private PeerServiceIdentityProofVerification VerifyPeerServiceIdentity(
        PeerServiceIdentityStatus identity,
        string expectedPeerId,
        PeerConfig peerConfig)
    {
        if (!identity.StatusSuccess)
        {
            return PeerServiceIdentityProofVerification.Failed("peer_status_not_success", null);
        }

        var observedPeerId = !string.IsNullOrWhiteSpace(identity.LocalPeerId)
            ? identity.LocalPeerId.Trim()
            : identity.NodeId?.Trim();
        if (string.IsNullOrWhiteSpace(observedPeerId))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_status_missing_identity", identity.KeyId);
        }

        if (!string.Equals(observedPeerId, expectedPeerId, StringComparison.OrdinalIgnoreCase))
        {
            return PeerServiceIdentityProofVerification.Failed(
                $"peer_identity_mismatch:expected={expectedPeerId};actual={observedPeerId}",
                identity.KeyId);
        }

        if (!string.IsNullOrWhiteSpace(identity.NodeId)
            && !string.IsNullOrWhiteSpace(identity.LocalPeerId)
            && !string.Equals(identity.NodeId, identity.LocalPeerId, StringComparison.OrdinalIgnoreCase))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_status_node_id_identity_mismatch", identity.KeyId);
        }

        var expectedPublicIdentityKey = ResolvePeerIdentity(expectedPeerId) ?? peerConfig.PublicIdentityKey;
        if (!Ed25519Identity.IsEd25519PublicIdentityKey(expectedPublicIdentityKey))
        {
            return PeerServiceIdentityProofVerification.Skipped("skipped_unpinned_identity", identity.KeyId);
        }

        if (!string.Equals(identity.PublicIdentityKey, expectedPublicIdentityKey, StringComparison.Ordinal))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_public_identity_key_mismatch", identity.KeyId);
        }

        if (string.IsNullOrWhiteSpace(identity.KeyId)
            || string.IsNullOrWhiteSpace(identity.PublicIdentityKey)
            || string.IsNullOrWhiteSpace(identity.PeerInstallEnvelopePublicKey)
            || string.IsNullOrWhiteSpace(identity.ProofAlgorithm)
            || string.IsNullOrWhiteSpace(identity.ProofPurpose)
            || string.IsNullOrWhiteSpace(identity.ProofIssuedAt)
            || string.IsNullOrWhiteSpace(identity.ProofSignature))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_missing_fields", identity.KeyId);
        }

        if (identity.ProofVersion != 1)
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_unsupported_version", identity.KeyId);
        }

        if (!string.Equals(identity.ProofAlgorithm, Ed25519Identity.SignatureAlgorithm, StringComparison.Ordinal))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_unsupported_algorithm", identity.KeyId);
        }

        if (!string.Equals(identity.ProofPurpose, "peer_install_envelope_public_key_binding", StringComparison.Ordinal))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_wrong_purpose", identity.KeyId);
        }

        if (!DateTimeOffset.TryParse(
                identity.ProofIssuedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var issuedAt))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_bad_issued_at", identity.KeyId);
        }

        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.Add(IdentityProofClockSkew))
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_from_future", identity.KeyId);
        }

        if (now - issuedAt > IdentityProofMaxAge)
        {
            return PeerServiceIdentityProofVerification.Failed("peer_identity_proof_expired", identity.KeyId);
        }

        var message = BuildIdentityProofMessage(
            observedPeerId,
            identity.KeyId,
            identity.PublicIdentityKey,
            identity.PeerInstallEnvelopePublicKey,
            identity.ProofIssuedAt);
        var verified = Ed25519Identity.Verify(
            expectedPublicIdentityKey,
            Encoding.UTF8.GetBytes(message),
            identity.ProofSignature);

        return verified
            ? PeerServiceIdentityProofVerification.Verified(identity.KeyId)
            : PeerServiceIdentityProofVerification.Failed("peer_identity_proof_bad_signature", identity.KeyId);
    }

    private static bool TryParseOverlayEndpoint(string? endpoint, out string host, out int port, out string? error)
    {
        host = string.Empty;
        port = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = "peer overlay_endpoint is empty";
            return false;
        }

        var trimmed = endpoint.Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            error = $"peer overlay_endpoint must be host:port: {endpoint}";
            return false;
        }

        host = trimmed[..separator].Trim().Trim('[', ']');
        var portText = trimmed[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535)
        {
            error = $"peer overlay_endpoint has invalid port: {endpoint}";
            return false;
        }

        return true;
    }

    private static bool TryGetJsonString(JsonElement element, out string? value, params string[] path)
    {
        value = null;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetJsonBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetJsonInt(JsonElement element, out int? value, params string[] path)
    {
        value = null;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.Number || !current.TryGetInt32(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string Required(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
            return string.Empty;
        }

        return value.Trim();
    }

    private static byte[] DecodeHexField(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
            return [];
        }

        try
        {
            return Convert.FromHexString(value.Trim());
        }
        catch (FormatException)
        {
            errors.Add($"{field} must be hex-encoded key material.");
            return [];
        }
    }

    private static string ResolveLifecycleState(bool validated, string installMode, bool installed)
    {
        if (!validated)
        {
            return "install_validation_failed";
        }

        return IsLocalInstallMode(installMode)
            ? installed ? "local_install_completed" : "local_install_failed"
            : "install_validated";
    }

    private static string ResolvePeerInstallLifecycleState(bool validated, string installMode, bool installed)
    {
        if (!validated)
        {
            return "peer_install_validation_failed";
        }

        return IsLocalInstallMode(installMode)
            ? installed ? "peer_local_install_completed" : "peer_local_install_failed"
            : "peer_install_validated";
    }

    private static bool IsPeerLifecycleDeliveryRequired(ClientConfig config) =>
        config.PeerLifecycleDeliveryRequired is true;

    private static bool ShouldUseTwoPhasePeerInstall(ClientConfig config) =>
        IsLocalInstallMode(config.ManualSaInstallMode)
        && string.Equals(config.PeerInstallDeliveryMode, ClientConfig.PeerInstallDeliveryModeServiceToService, StringComparison.OrdinalIgnoreCase);

    private static string ResolveEndToEndInstallLifecycleState(
        bool localLifecycleSucceeded,
        string installMode,
        PeerInstallDeliveryResult peerInstallDelivery)
    {
        if (!localLifecycleSucceeded)
        {
            return IsLocalInstallMode(installMode)
                ? "local_install_failed"
                : "local_validation_failed";
        }

        if (!peerInstallDelivery.Attempted)
        {
            return "local_lifecycle_only";
        }

        if (!peerInstallDelivery.Success)
        {
            return "peer_install_delivery_failed";
        }

        return IsLocalInstallMode(installMode)
            ? "end_to_end_install_completed"
            : "end_to_end_install_validated";
    }

    private static string BuildLifecycleAction(string reason, string installMode, bool installed)
    {
        if (IsLocalInstallMode(installMode))
        {
            return reason == "rekey"
                ? installed ? "rekey_installed" : "rekey_install_failed"
                : installed ? "connect_installed" : "connect_install_failed";
        }

        return reason == "rekey" ? "rekey_validated" : "connect_validated";
    }

    private static object BuildPeerInstallBundle(
        string peerId,
        string sessionId,
        string routeId,
        ManualSaInstallPlan plan,
        object? keyLengths)
    {
        return new
        {
            peer_id = peerId,
            session_id = sessionId,
            route_id = routeId,
            local_ip = plan.RemoteIp,
            remote_ip = plan.LocalIp,
            inbound_spi = plan.OutboundSpi,
            outbound_spi = plan.InboundSpi,
            traffic_selector_protocol = plan.TrafficSelectorProtocol,
            install_mode = "peer_external_orchestration",
            plan_transfer = "encrypted_control_plane_required",
            key_lengths = keyLengths ?? ToPeerKeyLengths(plan.ToLogSafe())
        };
    }

    private static object BuildPeerInstallBundle(
        string peerId,
        string sessionId,
        string routeId,
        ManualSaInstallPlanLog plan,
        object? keyLengths)
    {
        return new
        {
            peer_id = peerId,
            session_id = sessionId,
            route_id = routeId,
            local_ip = plan.RemoteIp,
            remote_ip = plan.LocalIp,
            inbound_spi = plan.OutboundSpi,
            outbound_spi = plan.InboundSpi,
            traffic_selector_protocol = plan.TrafficSelectorProtocol,
            install_mode = "peer_external_orchestration",
            plan_transfer = "encrypted_control_plane_required",
            key_lengths = keyLengths ?? ToPeerKeyLengths(plan)
        };
    }

    private static object ToPeerKeyLengths(ManualSaInstallPlanLog plan) => new
    {
        in_cipher_key_length = plan.OutboundCipherKeyPlaceholderLength,
        in_auth_key_length = plan.OutboundAuthKeyPlaceholderLength,
        out_cipher_key_length = plan.InboundCipherKeyPlaceholderLength,
        out_auth_key_length = plan.InboundAuthKeyPlaceholderLength
    };

    private static object ToManualSaResultSummary(ManualSaResult result) => new
    {
        exit_code = result.ExitCode,
        stdout_summary = SanitizeManualSaOutput(result.Stdout),
        stderr_summary = SanitizeManualSaOutput(result.Stderr),
        skipped = result.IsSkipped
    };

    private static string SanitizeManualSaOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        string[] sensitiveMarkers =
        [
            "in_cipher_key",
            "in_auth_key",
            "out_cipher_key",
            "out_auth_key",
            "--in-cipher-key",
            "--in-auth-key",
            "--out-cipher-key",
            "--out-auth-key"
        ];
        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => sensitiveMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                ? "<redacted_manual_sa_key_material>"
                : line);
        var normalized = string.Join(' ', lines).Trim();
        return normalized.Length <= 1200 ? normalized : normalized.AsSpan(0, 1200).ToString();
    }

    private static string? TryDeletePlanFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string SafePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "session" : builder.ToString();
    }

    private static string? ExtractIpForPlan(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var parts = endpoint.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? parts[0] : endpoint;
    }

    private string ResolvePlanLocalIp(string localReceiverHint)
    {
        if (config.Peers.TryGetValue(config.NodeId, out var localConfigPeer))
        {
            return ExtractIpForPlan(localConfigPeer.OverlayEndpoint)
                ?? ExtractIpForPlan(localConfigPeer.Endpoint)
                ?? localReceiverHint;
        }

        return ExtractIpForPlan(localReceiverHint) ?? config.NodeId;
    }

    private PeerDescriptor BuildLocalPeerDescriptor()
    {
        var identity = identityStore.LoadOrCreate(config.NodeId, ResolvePeerIdentity(config.NodeId));
        return new PeerDescriptor(
            PeerId: config.NodeId,
            KeyId: identity.KeyId,
            PublicIdentityKey: identity.PublicIdentityKey,
            ReceiverHint: config.NodeId,
            TrustSource: PeerTrustSource.Pinned,
            ValidFrom: identity.CreatedAt.AddMinutes(-1),
            ValidTo: identity.CreatedAt.AddYears(10));
    }

    private PeerDescriptor BuildRemotePeerDescriptor(string peerId, PeerConfig peerConfig)
    {
        var remoteKey = ResolvePeerIdentity(peerId) ?? peerConfig.PublicIdentityKey;

        return new PeerDescriptor(
            PeerId: peerId,
            KeyId: remoteKey,
            PublicIdentityKey: remoteKey,
            ReceiverHint: peerConfig.OverlayEndpoint,
            TrustSource: PeerTrustSource.Pinned,
            ValidFrom: DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo: DateTimeOffset.UtcNow.AddYears(1));
    }

    private object BuildStatusIdentity(LocalIdentityRecord identity)
    {
        return new
        {
            local_peer_id = identity.NodeId,
            local_key_id = identity.KeyId,
            local_public_identity_key = identity.PublicIdentityKey,
            peer_install_envelope_public_key = identity.PeerInstallEnvelopePublicKey,
            identity_proof = BuildStatusIdentityProof(identity)
        };
    }

    private object? BuildStatusIdentityProof(LocalIdentityRecord identity)
    {
        if (string.IsNullOrWhiteSpace(identity.PeerInstallEnvelopePublicKey)
            || !identityStore.TryExportPrivateSeed(identity.NodeId, identity.KeyId, out var seed))
        {
            return null;
        }

        try
        {
            var issuedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var message = BuildIdentityProofMessage(
                identity.NodeId,
                identity.KeyId,
                identity.PublicIdentityKey,
                identity.PeerInstallEnvelopePublicKey,
                issuedAt);
            var signature = Ed25519Identity.Sign(seed, Encoding.UTF8.GetBytes(message));
            return new
            {
                version = 1,
                algorithm = Ed25519Identity.SignatureAlgorithm,
                purpose = "peer_install_envelope_public_key_binding",
                issued_at = issuedAt,
                signature
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static string BuildIdentityProofMessage(
        string nodeId,
        string keyId,
        string publicIdentityKey,
        string peerInstallEnvelopePublicKey,
        string issuedAt) =>
        string.Join('\n',
        [
            "AdaptiveClient.Service.IdentityProof.v1",
            nodeId.Trim(),
            keyId.Trim(),
            publicIdentityKey.Trim(),
            peerInstallEnvelopePublicKey.Trim(),
            issuedAt.Trim()
        ]);

    private string? ResolvePeerIdentity(string peerId)
    {
        return config.IdentityPublicKeys.TryGetValue(peerId, out var identityKey)
            ? identityKey
            : null;
    }
}

internal sealed record ManagedSession(
    string PeerId,
    string SessionId,
    DateTimeOffset StartedAt,
    AikeSessionPhase Phase,
    string LifecycleState,
    string RouteId,
    string? TranscriptHash,
    DateTimeOffset NotAfter,
    DateTimeOffset RekeyAt,
    string InstallMode,
    bool ManualSaValidated,
    int ManualSaValidateExitCode,
    string? ManualSaValidationError,
    string? StatePath,
    string? PlanFilePath,
    bool PlanFileDeleted,
    string? PlanFileDeleteError,
    bool ManualSaInstalled,
    int? ManualSaInstallExitCode,
    string? ManualSaInstallError,
    string? ManualSaInstallStdoutSummary,
    EndpointRecord LocalEndpoint,
    EndpointRecord RemoteEndpoint,
    string? IdentitySignatureMode,
    string TrafficSelectorProtocol,
    uint InboundSpi,
    uint OutboundSpi,
    object KeyLengths,
    ManualSaInstallPlanLog Plan);

internal sealed record ManualSaLocalInstallResult(
    string? StatePath,
    string? PlanFilePath,
    bool PlanFileDeleted,
    string? PlanFileDeleteError,
    bool Installed,
    int? ExitCode,
    string? Error,
    string? StdoutSummary,
    uint? ActualInboundSpi)
{
    public static ManualSaLocalInstallResult NotExecuted() =>
        new(null, null, false, null, false, null, null, null, null);
}

internal sealed record ManualSaLocalCreateResult(
    string? StatePath,
    string? PlanFilePath,
    bool Created,
    int? ExitCode,
    string? Error,
    string? StdoutSummary,
    uint? ActualInboundSpi);

internal sealed record ManualSaLocalCompleteResult(
    bool Completed,
    int? ExitCode,
    string? Error,
    string? StdoutSummary,
    bool PlanFileDeleted,
    string? PlanFileDeleteError);

internal sealed record PeerServiceIdentityStatus(
    bool StatusSuccess,
    string? NodeId,
    string? LocalPeerId,
    string? KeyId,
    string? PublicIdentityKey,
    string? PeerInstallEnvelopePublicKey,
    int? ProofVersion,
    string? ProofAlgorithm,
    string? ProofPurpose,
    string? ProofIssuedAt,
    string? ProofSignature);

internal sealed record PeerServiceIdentityProofVerification(
    bool Success,
    string Status,
    string? KeyId,
    string? Error)
{
    public static PeerServiceIdentityProofVerification Verified(string? keyId) =>
        new(true, "verified", keyId, null);

    public static PeerServiceIdentityProofVerification Skipped(string reason, string? keyId) =>
        new(true, reason, keyId, null);

    public static PeerServiceIdentityProofVerification Failed(string error, string? keyId) =>
        new(false, "failed", keyId, error);
}

internal sealed record PeerInstallDeliveryResult(
    string Mode,
    bool Attempted,
    bool Success,
    string? Endpoint,
    long DurationMs,
    string? Error,
    string? AckStatus,
    string? PayloadTransport,
    string? EnvelopeMode,
    string? PeerSessionId,
    uint? PeerInboundSpi,
    string? IdentityProofStatus,
    string? IdentityProofKeyId,
    string? IdentityProofError)
{
    public static PeerInstallDeliveryResult Skipped(string mode, string reason) =>
        new(mode, false, false, null, 0, reason, null, null, null, null, null, null, null, null);

    public static PeerInstallDeliveryResult NotAttempted(string mode, string reason) =>
        new(mode, false, false, null, 0, reason, null, null, null, null, null, null, null, null);

    public static PeerInstallDeliveryResult Failed(
        string mode,
        string? endpoint,
        long durationMs,
        string error,
        PeerServiceIdentityProofVerification? identityProof = null) =>
        new(
            mode,
            true,
            false,
            endpoint,
            durationMs,
            error,
            null,
            null,
            null,
            null,
            null,
            identityProof?.Status,
            identityProof?.KeyId,
            identityProof?.Error);

    public static PeerInstallDeliveryResult FromPeerInstallResponse(
        string mode,
        string endpoint,
        long durationMs,
        string responseJson,
        PeerServiceIdentityProofVerification? identityProof = null)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var success = TryGetBool(root, "success", out var successValue) && successValue;
            var error = success
                ? null
                : TryGetString(root, out var errorMessage, "error") ? errorMessage : "peer-install failed";
            var ackStatus = TryGetString(root, out var ack, "data", "peer_install_ack", "status") ? ack : null;
            var payloadTransport = TryGetString(root, out var transport, "data", "payload_transport") ? transport : null;
            var envelopeMode = TryGetString(root, out var envelope, "data", "payload_envelope_mode") ? envelope : null;
            var peerSessionId = TryGetString(root, out var sessionId, "data", "session_id") ? sessionId : null;
            var peerInboundSpi = TryGetUInt32(root, out var inboundSpi, "data", "material_summary", "inbound_spi")
                ? inboundSpi
                : (uint?)null;
            var accepted = success && string.Equals(ackStatus, "accepted", StringComparison.OrdinalIgnoreCase);
            return new(
                Mode: mode,
                Attempted: true,
                Success: accepted,
                Endpoint: endpoint,
                DurationMs: durationMs,
                Error: accepted ? null : error ?? $"peer_install_ack was {ackStatus ?? "missing"}",
                AckStatus: ackStatus,
                PayloadTransport: payloadTransport,
                EnvelopeMode: envelopeMode,
                PeerSessionId: peerSessionId,
                PeerInboundSpi: peerInboundSpi,
                IdentityProofStatus: identityProof?.Status,
                IdentityProofKeyId: identityProof?.KeyId,
                IdentityProofError: identityProof?.Error);
        }
        catch (JsonException ex)
        {
            return Failed(mode, endpoint, durationMs, $"invalid peer-install response json: {ex.Message}", identityProof);
        }
    }

    public object ToLogSafe() => new
    {
        mode = Mode,
        attempted = Attempted,
        success = Success,
        endpoint = Endpoint,
        duration_ms = DurationMs,
        error = Error,
        ack_status = AckStatus,
        payload_transport = PayloadTransport,
        envelope_mode = EnvelopeMode,
        peer_session_id = PeerSessionId,
        peer_inbound_spi = PeerInboundSpi,
        identity_proof_status = IdentityProofStatus,
        identity_proof_key_id = IdentityProofKeyId,
        identity_proof_error = IdentityProofError
    };

    public object ToResponse() => ToLogSafe();

    private static bool TryGetString(JsonElement element, out string? value, params string[] path)
    {
        value = null;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetUInt32(JsonElement element, out uint value, params string[] path)
    {
        value = 0;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind == JsonValueKind.Number && current.TryGetUInt32(out value))
        {
            return true;
        }

        if (current.ValueKind == JsonValueKind.String
            && uint.TryParse(current.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }
}

internal sealed record PeerCleanupDeliveryResult(
    string Mode,
    bool Attempted,
    bool Success,
    string? Endpoint,
    long DurationMs,
    string? Error,
    string? Action,
    string? PeerSessionId,
    bool? ManualSaSkipped)
{
    public static PeerCleanupDeliveryResult Skipped(string mode, string reason) =>
        new(mode, false, false, null, 0, reason, null, null, null);

    public static PeerCleanupDeliveryResult NotAttempted(string mode, string reason) =>
        new(mode, false, false, null, 0, reason, null, null, null);

    public static PeerCleanupDeliveryResult Failed(string mode, string? endpoint, long durationMs, string error) =>
        new(mode, true, false, endpoint, durationMs, error, null, null, null);

    public static PeerCleanupDeliveryResult FromPeerCleanupResponse(
        string mode,
        string endpoint,
        long durationMs,
        string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var success = TryGetBool(root, out var successValue, "success") && successValue;
            var error = success
                ? null
                : TryGetString(root, out var errorMessage, "error") ? errorMessage : "peer-cleanup failed";
            var action = TryGetString(root, out var actionValue, "data", "action") ? actionValue : null;
            var sessionId = TryGetString(root, out var peerSessionId, "data", "session_id") ? peerSessionId : null;
            var manualSaSkipped = TryGetBool(root, out var skipped, "data", "manual_sa_result", "skipped") ? (bool?)skipped : null;
            return new(
                Mode: mode,
                Attempted: true,
                Success: success,
                Endpoint: endpoint,
                DurationMs: durationMs,
                Error: success ? null : error,
                Action: action,
                PeerSessionId: sessionId,
                ManualSaSkipped: manualSaSkipped);
        }
        catch (JsonException ex)
        {
            return Failed(mode, endpoint, durationMs, $"invalid peer-cleanup response json: {ex.Message}");
        }
    }

    public object ToLogSafe() => new
    {
        mode = Mode,
        attempted = Attempted,
        success = Success,
        endpoint = Endpoint,
        duration_ms = DurationMs,
        error = Error,
        action = Action,
        peer_session_id = PeerSessionId,
        manual_sa_skipped = ManualSaSkipped
    };

    public object ToResponse() => ToLogSafe();

    private static bool TryGetString(JsonElement element, out string? value, params string[] path)
    {
        value = null;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = current.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBool(JsonElement element, out bool value, params string[] path)
    {
        value = false;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        value = current.GetBoolean();
        return true;
    }
}

internal sealed record PersistedSessionRecord(
    string PeerId,
    string SessionId,
    DateTimeOffset StartedAt,
    string InstallMode,
    string? StatePath,
    string? PlanFilePath,
    string LocalIp,
    string RemoteIp,
    string TrafficSelectorProtocol,
    ManualSaInstallPlanLog Plan)
{
    public static PersistedSessionRecord From(ManagedSession session) =>
        new(
            PeerId: session.PeerId,
            SessionId: session.SessionId,
            StartedAt: session.StartedAt,
            InstallMode: session.InstallMode,
            StatePath: session.StatePath,
            PlanFilePath: session.PlanFilePath,
            LocalIp: session.Plan.LocalIp,
            RemoteIp: session.Plan.RemoteIp,
            TrafficSelectorProtocol: session.TrafficSelectorProtocol,
            Plan: session.Plan);
}

internal sealed class PersistentSessionIndex(string path, StructuredLogger logger)
{
    private readonly object gate = new();

    public PersistedSessionRecord[] Load()
    {
        lock (gate)
        {
            return LoadUnlocked();
        }
    }

    public void Upsert(PersistedSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SessionId))
        {
            return;
        }

        lock (gate)
        {
            var records = LoadUnlocked()
                .Where(existing => !string.Equals(existing.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase))
                .Append(record)
                .OrderBy(existing => existing.StartedAt)
                .ToArray();

            SaveUnlocked(records);
            logger.Info("manual_sa_session_index_upserted", new
            {
                path,
                record.PeerId,
                record.SessionId,
                record.StatePath
            });
        }
    }

    public void Remove(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        lock (gate)
        {
            var current = LoadUnlocked();
            var records = current
                .Where(existing => !string.Equals(existing.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (records.Length == current.Length)
            {
                return;
            }

            SaveUnlocked(records);
            logger.Info("manual_sa_session_index_removed", new { path, session_id = sessionId });
        }
    }

    public void PruneMissingFiles()
    {
        lock (gate)
        {
            var current = LoadUnlocked();
            var records = current
                .Where(record => string.IsNullOrWhiteSpace(record.StatePath) || File.Exists(record.StatePath))
                .ToArray();
            if (records.Length == current.Length)
            {
                return;
            }

            SaveUnlocked(records);
            logger.Info("manual_sa_session_index_pruned", new
            {
                path,
                before = current.Length,
                after = records.Length
            });
        }
    }

    private PersistedSessionRecord[] LoadUnlocked()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<PersistedSessionRecord[]>(File.ReadAllText(path), ProgramJson.Options)
                   ?? [];
        }
        catch (Exception ex)
        {
            logger.Info("manual_sa_session_index_ignored", new { path, reason = ex.Message });
            return [];
        }
    }

    private void SaveUnlocked(PersistedSessionRecord[] records)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(records, ProgramJson.Options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

internal sealed record PeerInstallReplayRecord(
    string ReplayHash,
    DateTimeOffset AcceptedAt,
    DateTimeOffset ExpiresAt);

internal sealed class PersistentPeerInstallReplayCache(string path, StructuredLogger logger)
{
    private const int MaxRecords = 4096;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    private readonly object gate = new();
    private Dictionary<string, PeerInstallReplayRecord>? records;

    public bool Contains(string replayKey)
    {
        if (string.IsNullOrWhiteSpace(replayKey))
        {
            return false;
        }

        lock (gate)
        {
            EnsureLoadedUnlocked();
            PruneUnlocked(DateTimeOffset.UtcNow, persist: true);
            return records!.ContainsKey(HashReplayKey(replayKey));
        }
    }

    public void MarkAccepted(string replayKey)
    {
        if (string.IsNullOrWhiteSpace(replayKey))
        {
            return;
        }

        lock (gate)
        {
            EnsureLoadedUnlocked();
            var now = DateTimeOffset.UtcNow;
            PruneUnlocked(now, persist: false);
            var hash = HashReplayKey(replayKey);
            records![hash] = new PeerInstallReplayRecord(
                ReplayHash: hash,
                AcceptedAt: now,
                ExpiresAt: now.Add(Retention));
            TrimUnlocked();
            SaveUnlocked();
            logger.Info("peer_install_replay_cache_accepted", new
            {
                path,
                replay_hash = hash,
                count = records.Count
            });
        }
    }

    public object ToStatus()
    {
        lock (gate)
        {
            EnsureLoadedUnlocked();
            PruneUnlocked(DateTimeOffset.UtcNow, persist: true);
            return new
            {
                path,
                count = records!.Count,
                max_records = MaxRecords,
                retention_seconds = (int)Retention.TotalSeconds
            };
        }
    }

    private void EnsureLoadedUnlocked()
    {
        if (records is not null)
        {
            return;
        }

        records = LoadUnlocked()
            .GroupBy(record => record.ReplayHash, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(record => record.AcceptedAt).First())
            .ToDictionary(record => record.ReplayHash, StringComparer.Ordinal);
        PruneUnlocked(DateTimeOffset.UtcNow, persist: false);
        TrimUnlocked();
    }

    private PeerInstallReplayRecord[] LoadUnlocked()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<PeerInstallReplayRecord[]>(File.ReadAllText(path), ProgramJson.Options)
                   ?? [];
        }
        catch (Exception ex)
        {
            logger.Info("peer_install_replay_cache_ignored", new { path, reason = ex.Message });
            return [];
        }
    }

    private void PruneUnlocked(DateTimeOffset now, bool persist)
    {
        if (records is null || records.Count == 0)
        {
            return;
        }

        var before = records.Count;
        foreach (var key in records
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            records.Remove(key);
        }

        if (persist && before != records.Count)
        {
            SaveUnlocked();
            logger.Info("peer_install_replay_cache_pruned", new { path, before, after = records.Count });
        }
    }

    private void TrimUnlocked()
    {
        if (records is null || records.Count <= MaxRecords)
        {
            return;
        }

        foreach (var key in records
                     .OrderBy(item => item.Value.AcceptedAt)
                     .Take(records.Count - MaxRecords)
                     .Select(item => item.Key)
                     .ToArray())
        {
            records.Remove(key);
        }
    }

    private void SaveUnlocked()
    {
        if (string.IsNullOrWhiteSpace(path) || records is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var ordered = records.Values
            .OrderBy(record => record.AcceptedAt)
            .ToArray();
        var json = JsonSerializer.Serialize(ordered, ProgramJson.Options);
        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string HashReplayKey(string replayKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(replayKey))).ToLowerInvariant();
}

internal sealed class EndpointRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, EndpointRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly string path;
    private readonly StructuredLogger logger;

    public EndpointRegistry(ClientConfig config, StructuredLogger logger)
    {
        this.logger = logger;
        path = config.EndpointRegistryPath;
        DefaultTtlSeconds = config.EndpointRegistry?.DefaultTtlSeconds is > 0
            ? config.EndpointRegistry.DefaultTtlSeconds.Value
            : 300;

        foreach (var (key, value) in config.EndpointRegistry?.Records ?? new Dictionary<string, EndpointRecord>())
        {
            var nodeId = FirstNonEmpty(value.NodeId, key);
            records[nodeId] = Normalize(
                value,
                nodeId,
                source: FirstNonEmpty(value.Source, "config_endpoint_registry"),
                sequence: value.Sequence);
        }

        LoadPersistedRecords();
        PruneExpiredPersistedRecords();
    }

    public int DefaultTtlSeconds { get; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return records.Count;
            }
        }
    }

    public EndpointRecord[] Snapshot()
    {
        lock (gate)
        {
            return records.Values
                .OrderBy(record => record.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public EndpointResolution Resolve(string nodeId, ClientConfig config)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (records.TryGetValue(nodeId, out var record))
            {
                var isExpired = record.ExpiresAt is not null && record.ExpiresAt <= now;
                if (!isExpired)
                {
                    return new EndpointResolution(record, IsFallback: false, IsExpired: false);
                }
            }
        }

        return new EndpointResolution(BuildConfigFallback(nodeId, config), IsFallback: true, IsExpired: false);
    }

    public EndpointRecord Update(
        string nodeId,
        string ip,
        int? port,
        string selectorProtocol,
        int? ttlSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            records.TryGetValue(nodeId, out var current);
            var sequence = (current?.Sequence ?? 0) + 1;
            var effectiveTtl = ttlSeconds is > 0 ? ttlSeconds.Value : DefaultTtlSeconds;
            var updated = new EndpointRecord(
                NodeId: nodeId,
                Ip: ip,
                Port: port,
                SelectorProtocol: FirstNonEmpty(selectorProtocol, current?.SelectorProtocol, ManualSaInstallPlan.DefaultTrafficSelectorProtocol),
                Source: "cli_runtime",
                Sequence: sequence,
                ValidFrom: now,
                ExpiresAt: now.AddSeconds(effectiveTtl),
                Signature: "placeholder");

            records[nodeId] = updated;
            SavePersistedRecords();
            return updated;
        }
    }

    private void LoadPersistedRecords()
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<EndpointRecord[]>(File.ReadAllText(path), ProgramJson.Options)
                         ?? [];
            var now = DateTimeOffset.UtcNow;
            var accepted = 0;
            foreach (var record in loaded)
            {
                if (string.IsNullOrWhiteSpace(record.NodeId)
                    || string.IsNullOrWhiteSpace(record.Ip)
                    || record.ExpiresAt is not null && record.ExpiresAt <= now)
                {
                    continue;
                }

                records[record.NodeId] = Normalize(
                    record,
                    record.NodeId,
                    source: FirstNonEmpty(record.Source, "cli_runtime_persisted"),
                    sequence: record.Sequence);
                accepted++;
            }

            logger.Info("endpoint_registry_persisted_loaded", new { path, loaded = loaded.Length, accepted });
        }
        catch (Exception ex)
        {
            logger.Info("endpoint_registry_persisted_ignored", new { path, reason = ex.Message });
        }
    }

    private void PruneExpiredPersistedRecords()
    {
        var now = DateTimeOffset.UtcNow;
        var before = records.Count;
        foreach (var key in records
                     .Where(pair => pair.Value.ExpiresAt is not null && pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            records.Remove(key);
        }

        if (records.Count != before)
        {
            SavePersistedRecords();
            logger.Info("endpoint_registry_expired_pruned", new { path, before, after = records.Count });
        }
    }

    private void SavePersistedRecords()
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var runtimeRecords = records.Values
            .Where(record => record.Source.StartsWith("cli_runtime", StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(runtimeRecords, ProgramJson.Options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static EndpointRecord BuildConfigFallback(string nodeId, ClientConfig config)
    {
        config.Peers.TryGetValue(nodeId, out var peer);
        var endpoint = FirstNonEmpty(peer?.OverlayEndpoint, peer?.Endpoint, nodeId);
        var parsed = ParseEndpoint(endpoint);

        return new EndpointRecord(
            NodeId: nodeId,
            Ip: parsed.Ip,
            Port: parsed.Port,
            SelectorProtocol: FirstNonEmpty(peer?.AllowedSelector, ManualSaInstallPlan.DefaultTrafficSelectorProtocol),
            Source: parsed.Source,
            Sequence: 0,
            ValidFrom: DateTimeOffset.UnixEpoch,
            ExpiresAt: null,
            Signature: "config-placeholder");
    }

    private static EndpointRecord Normalize(
        EndpointRecord record,
        string nodeId,
        string source,
        long sequence)
    {
        return new EndpointRecord(
            NodeId: nodeId,
            Ip: FirstNonEmpty(record.Ip, nodeId),
            Port: record.Port,
            SelectorProtocol: FirstNonEmpty(record.SelectorProtocol, ManualSaInstallPlan.DefaultTrafficSelectorProtocol),
            Source: source,
            Sequence: sequence,
            ValidFrom: record.ValidFrom == default ? DateTimeOffset.UtcNow : record.ValidFrom,
            ExpiresAt: record.ExpiresAt,
            Signature: FirstNonEmpty(record.Signature, "placeholder"));
    }

    private static (string Ip, int? Port, string Source) ParseEndpoint(string endpoint)
    {
        if (IPEndPoint.TryParse(endpoint, out var parsed))
        {
            return (parsed.Address.ToString(), parsed.Port, "config_peer_endpoint");
        }

        var parts = endpoint.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (parts[0], port, "config_peer_endpoint");
        }

        return (endpoint, null, "config_peer_endpoint");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

internal sealed record EndpointResolution(
    EndpointRecord Record,
    bool IsFallback,
    bool IsExpired);

internal sealed class PersistentIdentityStore(string path, StructuredLogger logger) : IAikeIdentityKeyProvider
{
    private readonly object gate = new();

    public LocalIdentityRecord LoadOrCreate(string nodeId, string? configuredPublicIdentityKey)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new InvalidOperationException("node_id is required for persistent identity.");
        }

        lock (gate)
        {
            var existing = TryLoad(nodeId);
            if (existing is not null)
            {
                var migrated = EnsureProtectedPrivateSeed(existing, nodeId, configuredPublicIdentityKey, out var migratedReason);
                if (migratedReason is not null)
                {
                    Save(migrated);
                    logger.Info("persistent_identity_migrated", new
                    {
                        node_id = migrated.NodeId,
                        key_id = migrated.KeyId,
                        algorithm = migrated.Algorithm,
                        protection = migrated.PrivateSeedProtection,
                        reason = migratedReason,
                        path
                    });
                }

                return migrated;
            }

            var seed = RandomNumberGenerator.GetBytes(32);
            LocalIdentityRecord created;
            try
            {
                var publicIdentityKey = Ed25519Identity.GetPublicIdentityKey(seed);
                var protectedSeed = ProtectPrivateSeed(seed, nodeId, out var seedFingerprint);
                var envelopePrivateKey = X25519EnvelopeIdentity.DerivePrivateKey(seed);
                var envelopePublicKey = X25519EnvelopeIdentity.GetPublicIdentityKey(envelopePrivateKey);
                CryptographicOperations.ZeroMemory(envelopePrivateKey);
                created = new LocalIdentityRecord(
                    NodeId: nodeId.Trim(),
                    KeyId: BuildKeyId(nodeId, publicIdentityKey),
                    PublicIdentityKey: publicIdentityKey,
                    Algorithm: "ed25519-dpapi+x25519-envelope",
                    CreatedAt: DateTimeOffset.UtcNow,
                    ProtectedPrivateSeed: protectedSeed,
                    PrivateSeedProtection: WindowsDpapi.ProtectionName,
                    PrivateSeedFingerprint: seedFingerprint,
                    PeerInstallEnvelopePublicKey: envelopePublicKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }

            Save(created);
            logger.Info("persistent_identity_created", new
            {
                node_id = created.NodeId,
                key_id = created.KeyId,
                algorithm = created.Algorithm,
                protection = created.PrivateSeedProtection,
                private_seed_fingerprint = created.PrivateSeedFingerprint,
                peer_install_envelope_public_key = created.PeerInstallEnvelopePublicKey,
                path
            });

            return created;
        }
    }

    public bool TryExportPrivateSeed(string peerId, string keyId, out byte[] privateSeed)
    {
        privateSeed = [];
        if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(keyId))
        {
            return false;
        }

        lock (gate)
        {
            var record = TryLoad(peerId);
            if (record is null
                || !string.Equals(record.KeyId, keyId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(record.ProtectedPrivateSeed)
                || !string.Equals(record.PrivateSeedProtection, WindowsDpapi.ProtectionName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                privateSeed = WindowsDpapi.UnprotectFromBase64Url(record.ProtectedPrivateSeed, BuildEntropy(peerId));
                return privateSeed.Length == Ed25519Identity.SeedSize;
            }
            catch (Exception ex)
            {
                logger.Info("persistent_identity_private_seed_export_failed", new
                {
                    peer_id = peerId,
                    key_id = keyId,
                    reason = ex.Message
                });
                privateSeed = [];
                return false;
            }
        }
    }

    public bool TryExportPeerInstallEnvelopePrivateKey(string nodeId, out byte[] privateKey)
    {
        privateKey = [];
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        lock (gate)
        {
            var record = TryLoad(nodeId);
            if (record is null
                || string.IsNullOrWhiteSpace(record.ProtectedPrivateSeed)
                || !string.Equals(record.PrivateSeedProtection, WindowsDpapi.ProtectionName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var seed = WindowsDpapi.UnprotectFromBase64Url(record.ProtectedPrivateSeed, BuildEntropy(nodeId));
                try
                {
                    privateKey = X25519EnvelopeIdentity.DerivePrivateKey(seed);
                    return privateKey.Length == X25519EnvelopeIdentity.PrivateKeySize;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                }
            }
            catch (Exception ex)
            {
                logger.Info("persistent_identity_envelope_private_key_export_failed", new
                {
                    node_id = nodeId,
                    reason = ex.Message
                });
                privateKey = [];
                return false;
            }
        }
    }

    private LocalIdentityRecord? TryLoad(string nodeId)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<LocalIdentityRecord>(File.ReadAllText(path), ProgramJson.Options);
            if (record is null
                || !string.Equals(record.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(record.KeyId)
                || string.IsNullOrWhiteSpace(record.PublicIdentityKey))
            {
                logger.Info("persistent_identity_ignored", new { node_id = nodeId, path, reason = "invalid_or_node_mismatch" });
                return null;
            }

            logger.Info("persistent_identity_loaded", new
            {
                node_id = record.NodeId,
                key_id = record.KeyId,
                algorithm = record.Algorithm,
                protection = record.PrivateSeedProtection ?? "none",
                path
            });
            return record;
        }
        catch (Exception ex)
        {
            logger.Info("persistent_identity_ignored", new { node_id = nodeId, path, reason = ex.Message });
            return null;
        }
    }

    private LocalIdentityRecord EnsureProtectedPrivateSeed(
        LocalIdentityRecord record,
        string nodeId,
        string? configuredPublicIdentityKey,
        out string? migrationReason)
    {
        migrationReason = null;

        if (!string.IsNullOrWhiteSpace(record.ProtectedPrivateSeed)
            && string.Equals(record.PrivateSeedProtection, WindowsDpapi.ProtectionName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var existingSeed = WindowsDpapi.UnprotectFromBase64Url(record.ProtectedPrivateSeed, BuildEntropy(nodeId));
                try
                {
                    if (existingSeed.Length == 32)
                    {
                        var envelopePrivateKey = X25519EnvelopeIdentity.DerivePrivateKey(existingSeed);
                        var envelopePublicKey = X25519EnvelopeIdentity.GetPublicIdentityKey(envelopePrivateKey);
                        CryptographicOperations.ZeroMemory(envelopePrivateKey);

                        if (Ed25519Identity.IsEd25519PublicIdentityKey(record.PublicIdentityKey)
                            && string.Equals(record.PeerInstallEnvelopePublicKey, envelopePublicKey, StringComparison.Ordinal))
                        {
                            return record;
                        }

                        migrationReason = "derive_ed25519_and_x25519_public_keys_from_protected_seed";
                        var derivedPublicKey = Ed25519Identity.GetPublicIdentityKey(existingSeed);
                        return record with
                        {
                            KeyId = BuildKeyId(nodeId, derivedPublicKey),
                            PublicIdentityKey = derivedPublicKey,
                            Algorithm = "ed25519-dpapi+x25519-envelope",
                            PrivateSeedFingerprint = Base64Url(SHA256.HashData(existingSeed))[..16],
                            PeerInstallEnvelopePublicKey = envelopePublicKey
                        };
                    }

                    migrationReason = "invalid_protected_seed_length";
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(existingSeed);
                }
            }
            catch (Exception ex)
            {
                migrationReason = $"protected_seed_unprotect_failed:{ex.Message}";
            }
        }
        else
        {
            migrationReason = "missing_protected_seed";
        }

        var seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            var publicIdentityKey = Ed25519Identity.GetPublicIdentityKey(seed);
            var envelopePrivateKey = X25519EnvelopeIdentity.DerivePrivateKey(seed);
            var envelopePublicKey = X25519EnvelopeIdentity.GetPublicIdentityKey(envelopePrivateKey);
            CryptographicOperations.ZeroMemory(envelopePrivateKey);
            var protectedSeed = ProtectPrivateSeed(seed, nodeId, out var seedFingerprint);
            return record with
            {
                KeyId = BuildKeyId(nodeId, publicIdentityKey),
                PublicIdentityKey = publicIdentityKey,
                Algorithm = "ed25519-dpapi+x25519-envelope",
                ProtectedPrivateSeed = protectedSeed,
                PrivateSeedProtection = WindowsDpapi.ProtectionName,
                PrivateSeedFingerprint = seedFingerprint,
                PeerInstallEnvelopePublicKey = envelopePublicKey
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private string ProtectPrivateSeed(byte[] seed, string nodeId, out string seedFingerprint)
    {
        seedFingerprint = Base64Url(SHA256.HashData(seed))[..16];
        return WindowsDpapi.ProtectToBase64Url(seed, BuildEntropy(nodeId));
    }

    private static byte[] BuildEntropy(string nodeId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"AdaptiveClient.Service.IdentityStore.v1|{nodeId.Trim()}"));

    private static string BuildKeyId(string nodeId, string publicIdentityKey)
    {
        var fingerprint = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes($"{nodeId.Trim()}:{publicIdentityKey}")))[..16];
        return $"{nodeId.Trim()}-{fingerprint}";
    }

    private void Save(LocalIdentityRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(record, ProgramJson.Options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record LocalIdentityRecord(
    string NodeId,
    string KeyId,
    string PublicIdentityKey,
    string Algorithm,
    DateTimeOffset CreatedAt,
    string? ProtectedPrivateSeed = null,
    string? PrivateSeedProtection = null,
    string? PrivateSeedFingerprint = null,
    string? PeerInstallEnvelopePublicKey = null);

internal static class WindowsDpapi
{
    public const string ProtectionName = "dpapi-current-user";
    private const int CryptProtectUiForbidden = 0x1;

    public static string ProtectToBase64Url(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI identity protection is supported only on Windows.");
        }

        var inputBytes = plaintext.ToArray();
        var entropyBytes = entropy.ToArray();
        var input = DataBlob.From(inputBytes);
        var entropyBlob = DataBlob.From(entropyBytes);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "AdaptiveClient identity seed",
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                return Base64Url(output.ToArray());
            }
            finally
            {
                output.LocalFree();
            }
        }
        finally
        {
            input.Free();
            entropyBlob.Free();
            CryptographicOperations.ZeroMemory(inputBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
        }
    }

    public static byte[] UnprotectFromBase64Url(string protectedValue, ReadOnlySpan<byte> entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI identity protection is supported only on Windows.");
        }

        var protectedBytes = Base64UrlDecode(protectedValue);
        var entropyBytes = entropy.ToArray();
        var input = DataBlob.From(protectedBytes);
        var entropyBlob = DataBlob.From(entropyBytes);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            try
            {
                return output.ToArray();
            }
            finally
            {
                output.LocalFree();
            }
        }
        finally
        {
            input.Free();
            entropyBlob.Free();
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(entropyBytes);
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob From(byte[] bytes)
        {
            var blob = new DataBlob
            {
                Size = bytes.Length,
                Data = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length)
            };

            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
            }

            return blob;
        }

        public readonly byte[] ToArray()
        {
            if (Size <= 0 || Data == IntPtr.Zero)
            {
                return [];
            }

            var bytes = new byte[Size];
            Marshal.Copy(Data, bytes, 0, Size);
            return bytes;
        }

        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
                Size = 0;
            }
        }

        public void LocalFree()
        {
            if (Data != IntPtr.Zero)
            {
                NativeLocalFree(Data);
                Data = IntPtr.Zero;
                Size = 0;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "LocalFree")]
    private static extern IntPtr NativeLocalFree(IntPtr hMem);
}

internal sealed record EndpointUpdateRequest(
    string? NodeId,
    string? Ip,
    int? Port,
    string? SelectorProtocol,
    int? TtlSeconds);

internal sealed record PeerInstallRequest(
    string? PeerId = null,
    string? RemotePeerId = null,
    string? SessionId = null,
    string? RouteId = null,
    string? LocalIp = null,
    string? RemoteIp = null,
    string? TrafficSelectorProtocol = null,
    string? InboundSpi = null,
    string? OutboundSpi = null,
    string? InCipherKey = null,
    string? InAuthKey = null,
    string? OutCipherKey = null,
    string? OutAuthKey = null,
    string? IdentitySignatureMode = null,
    string? Source = null,
    string? EnvelopeVersion = null,
    string? EnvelopeMode = null,
    string? EnvelopeEncryption = null,
    string? EnvelopeKdf = null,
    string? EnvelopeSenderId = null,
    string? EnvelopeRecipientId = null,
    string? EnvelopeEphemeralPublicKey = null,
    string? EnvelopeNonce = null,
    string? EnvelopeCiphertext = null,
    string? EnvelopeTag = null);

internal sealed record EndpointRecord(
    string NodeId,
    string Ip,
    int? Port,
    string SelectorProtocol,
    string Source,
    long Sequence,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ExpiresAt,
    string? Signature);

internal sealed record ControlCommand(
    string Command,
    string? Peer = null,
    string? SessionId = null,
    Dictionary<string, string>? Parameters = null);

internal sealed record CommandResponse(
    bool Success,
    string Command,
    string? Error,
    object? Data)
{
    public static CommandResponse Ok(string command, object? data) => new(true, command, null, data);

    public static CommandResponse Fail(string command, string error) => new(false, command, error, null);
}

internal interface IManualSaRunner
{
    Task<ManualSaResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

internal sealed class ProcessManualSaRunner(string manualSaPath, StructuredLogger logger) : IManualSaRunner
{
    public async Task<ManualSaResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manualSaPath))
        {
            return new ManualSaResult(-1, string.Empty, $"manual-sa executable not found: {manualSaPath}", IsSkipped: false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = manualSaPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ManualSaResult(-1, string.Empty, $"failed to start manual-sa {command}: {ex.Message}", IsSkipped: false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        sw.Stop();

        logger.Info("manual_sa_process_completed", new
        {
            command,
            exit_code = process.ExitCode,
            duration_ms = sw.ElapsedMilliseconds,
            manual_sa_path = manualSaPath
        });

        return new ManualSaResult(process.ExitCode, stdout, stderr, IsSkipped: false);
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
            // Best-effort cancellation cleanup.
        }
    }
}

internal sealed class SkeletonAikeCrypto : IAikeCrypto
{
    public string CreateSessionId() => Guid.NewGuid().ToString("N");

    public string CreateEphemeralPublicKey(string sessionId) => $"stub-ephemeral-{sessionId[..8]}";

    public AikeMessage SignEnvelope(PeerDescriptor localPeer, AikeMessage envelope, string previousTranscriptHash)
    {
        return envelope with
        {
            Signature = new AikeSignature(
                Algorithm: "Ed25519",
                KeyId: localPeer.KeyId,
                Value: $"sig:{previousTranscriptHash}:{localPeer.KeyId}:{sessionHint(envelope.SessionId)}")
        };
    }

    public bool VerifySignature(AikeMessage message, string expectedPreviousTranscriptHash, PeerDescriptor expectedSender)
    {
        return message.Signature is not null && message.Signature.Value.StartsWith("sig:", StringComparison.Ordinal);
    }

    public string UpdateTranscript(string previousTranscriptHash, AikeMessage acceptedMessage)
    {
        return $"{previousTranscriptHash}|{acceptedMessage.MessageType}:{acceptedMessage.SessionId}";
    }

    public bool VerifyTranscript(string expectedTranscriptHash, AikeMessage finalMessage, PeerDescriptor expectedSender)
    {
        return true;
    }

    public EspKeyMaterial DeriveEspKeys(SessionState session)
    {
        return new EspKeyMaterial(
            InboundCipherKey: GetKeyBytes($"inbound-cipher:{session.SessionId}"),
            InboundAuthKey: GetKeyBytes($"inbound-auth:{session.SessionId}"),
            OutboundCipherKey: GetKeyBytes($"outbound-cipher:{session.SessionId}"),
            OutboundAuthKey: GetKeyBytes($"outbound-auth:{session.SessionId}"));
    }

    private static string sessionHint(string sessionId) =>
        sessionId.Length >= 8 ? sessionId[..8] : sessionId;

    private static byte[] GetKeyBytes(string value)
    {
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(value));
        while (bytes.Count < 32)
        {
            bytes.Add(0);
        }

        return bytes.Take(32).ToArray();
    }
}

internal sealed class SkeletonPeerTrustStore : IPeerTrustStore
{
    public bool IsTrusted(string peerId, string keyId)
    {
        return true;
    }
}

internal sealed record ManualSaResult(int ExitCode, string Stdout, string Stderr, bool IsSkipped)
{
    public static ManualSaResult SkippedResult(string reason) => new(0, reason, string.Empty, IsSkipped: true);
}

internal sealed class StructuredLogger : IDisposable
{
    private readonly object gate = new();
    private readonly StreamWriter? fileWriter;
    private readonly string nodeId;

    private StructuredLogger(StreamWriter? fileWriter, string nodeId)
    {
        this.fileWriter = fileWriter;
        this.nodeId = nodeId;
    }

    public static StructuredLogger Create(string? path, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new StructuredLogger(null, nodeId);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return new StructuredLogger(new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true }, nodeId);
    }

    public void Info(string eventName, object? data) =>
        Write("info", eventName, data, null);

    public void Error(string eventName, Exception ex, object? data) =>
        Write("error", eventName, data, ex);

    private void Write(string level, string eventName, object? data, Exception? exception)
    {
        var entry = new
        {
            ts = DateTimeOffset.UtcNow,
            level,
            node_id = nodeId,
            event_name = eventName,
            data,
            error = exception?.Message,
            exception = exception?.ToString()
        };

        var json = JsonSerializer.Serialize(entry, ProgramJson.Options);
        lock (gate)
        {
            Console.WriteLine(json);
            fileWriter?.WriteLine(json);
        }
    }

    public void Dispose() => fileWriter?.Dispose();
}

internal sealed record ClientConfig(
    string NodeId,
    string ListenHost,
    int ListenPort,
    string ManualSaPath,
    string ManualSaInstallMode,
    string? ManualSaExemptTcpPorts,
    string ManualSaStateDir,
    string ManualSaSessionIndexPath,
    string PeerInstallReplayCachePath,
    string IdentityStorePath,
    string EndpointRegistryPath,
    string? PeerInstallEnvelopeSecret,
    string PeerInstallDeliveryMode,
    bool? PeerLifecycleDeliveryRequired,
    bool? ValidateManualSaPlan,
    EndpointRegistryConfig? EndpointRegistry,
    Dictionary<string, PeerConfig> Peers,
    Dictionary<string, string> IdentityPublicKeys,
    RoutePolicy RoutePolicy,
    MaintenancePolicy? Maintenance,
    RetryPolicy RetryPolicy)
{
    public const string ManualSaInstallModeValidateOnly = "validate_only";
    public const string ManualSaInstallModeLocalInstall = "local_install";
    public const string PeerInstallDeliveryModeDisabled = "disabled";
    public const string PeerInstallDeliveryModeServiceToService = "service_to_service";

    public static async Task<ClientConfig> LoadAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Adaptive client config was not found.", path);
        }

        var fullPath = Path.GetFullPath(path);
        var configDirectory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<ClientConfig>(stream, ProgramJson.Options).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Config JSON was empty.");
        var manualSaStateDir = ResolvePathFromConfigDirectory(
            string.IsNullOrWhiteSpace(config.ManualSaStateDir) ? ".\\state" : config.ManualSaStateDir,
            configDirectory);
        var manualSaSessionIndexPath = ResolvePathFromConfigDirectory(
            string.IsNullOrWhiteSpace(config.ManualSaSessionIndexPath)
                ? Path.Combine(manualSaStateDir, "sessions.index.json")
                : config.ManualSaSessionIndexPath,
            configDirectory);
        var endpointRegistryPath = ResolvePathFromConfigDirectory(
            string.IsNullOrWhiteSpace(config.EndpointRegistryPath) ? ".\\endpoint-registry.json" : config.EndpointRegistryPath,
            configDirectory);
        var peerInstallReplayCachePath = ResolvePathFromConfigDirectory(
            string.IsNullOrWhiteSpace(config.PeerInstallReplayCachePath)
                ? Path.Combine(manualSaStateDir, "peer-install-replay-cache.json")
                : config.PeerInstallReplayCachePath,
            configDirectory);

        var peerInstallDeliveryMode = NormalizePeerInstallDeliveryMode(config.PeerInstallDeliveryMode);
        var peerLifecycleDeliveryRequired = config.PeerLifecycleDeliveryRequired
            ?? string.Equals(peerInstallDeliveryMode, PeerInstallDeliveryModeServiceToService, StringComparison.OrdinalIgnoreCase);

        return config with
        {
            ListenHost = NormalizeListenHost(config.ListenHost),
            ManualSaPath = ResolvePathFromConfigDirectory(config.ManualSaPath, configDirectory),
            ManualSaInstallMode = NormalizeManualSaInstallMode(config.ManualSaInstallMode),
            ManualSaExemptTcpPorts = NormalizeManualSaExemptTcpPorts(config.ManualSaExemptTcpPorts),
            ManualSaStateDir = manualSaStateDir,
            ManualSaSessionIndexPath = manualSaSessionIndexPath,
            PeerInstallReplayCachePath = peerInstallReplayCachePath,
            IdentityStorePath = ResolvePathFromConfigDirectory(
                string.IsNullOrWhiteSpace(config.IdentityStorePath) ? "identity.local.json" : config.IdentityStorePath,
                configDirectory),
            EndpointRegistryPath = endpointRegistryPath,
            PeerInstallEnvelopeSecret = string.IsNullOrWhiteSpace(config.PeerInstallEnvelopeSecret)
                ? "lab-peer-install-envelope-secret-change-me"
                : config.PeerInstallEnvelopeSecret,
            PeerInstallDeliveryMode = peerInstallDeliveryMode,
            PeerLifecycleDeliveryRequired = peerLifecycleDeliveryRequired,
            ValidateManualSaPlan = config.ValidateManualSaPlan ?? true,
            EndpointRegistry = config.EndpointRegistry ?? new EndpointRegistryConfig(300, new Dictionary<string, EndpointRecord>(StringComparer.OrdinalIgnoreCase)),
            Peers = config.Peers ?? new Dictionary<string, PeerConfig>(StringComparer.OrdinalIgnoreCase),
            IdentityPublicKeys = config.IdentityPublicKeys ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Maintenance = NormalizeMaintenancePolicy(config.Maintenance)
        };
    }

    private static string NormalizeListenHost(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();

    private static string NormalizeManualSaExemptTcpPorts(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static MaintenancePolicy NormalizeMaintenancePolicy(MaintenancePolicy? policy)
    {
        if (policy is null)
        {
            return new MaintenancePolicy(
                Enabled: true,
                IntervalSeconds: 30,
                AutoRekey: true,
                ExpireCleanup: true);
        }

        return policy with
        {
            IntervalSeconds = Math.Clamp(policy.IntervalSeconds <= 0 ? 30 : policy.IntervalSeconds, 5, 3600)
        };
    }

    private static string ResolvePathFromConfigDirectory(string path, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(configDirectory, path));
    }

    private static string NormalizeManualSaInstallMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ManualSaInstallModeValidateOnly;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            ManualSaInstallModeValidateOnly => ManualSaInstallModeValidateOnly,
            ManualSaInstallModeLocalInstall => ManualSaInstallModeLocalInstall,
            _ => throw new InvalidOperationException(
                $"Unsupported manual_sa_install_mode '{value}'. Expected '{ManualSaInstallModeValidateOnly}' or '{ManualSaInstallModeLocalInstall}'.")
        };
    }

    private static string NormalizePeerInstallDeliveryMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PeerInstallDeliveryModeDisabled;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            PeerInstallDeliveryModeDisabled => PeerInstallDeliveryModeDisabled,
            PeerInstallDeliveryModeServiceToService => PeerInstallDeliveryModeServiceToService,
            _ => throw new InvalidOperationException(
                $"Unsupported peer_install_delivery_mode '{value}'. Expected '{PeerInstallDeliveryModeDisabled}' or '{PeerInstallDeliveryModeServiceToService}'.")
        };
    }
}

internal interface IManualSaPlanValidator
{
    Task<ManualSaPlanValidateResult> ValidatePlanAsync(ManualSaPlanValidationRequest request);
}

internal sealed record EndpointRegistryConfig(
    int? DefaultTtlSeconds,
    Dictionary<string, EndpointRecord>? Records);

internal sealed record ManualSaPlanValidationRequest(
    string PeerId,
    string SessionId,
    ManualSaInstallPlan Plan);

internal sealed record ManualSaPlanValidateResult(
    bool Success,
    int ExitCode,
    long DurationMs,
    string StdoutSummary,
    string? Stderr)
{
    public static ManualSaPlanValidateResult Disabled(string reason) =>
        new(false, -2, 0, "validation skipped by configuration", reason);

    public static ManualSaPlanValidateResult NotFound(string path) =>
        new(false, -1, 0, "manual-sa executable not found", path);
}

internal sealed class ProcessManualSaPlanValidator(string manualSaPath, StructuredLogger logger) : IManualSaPlanValidator
{
    public async Task<ManualSaPlanValidateResult> ValidatePlanAsync(ManualSaPlanValidationRequest request)
    {
        if (!File.Exists(manualSaPath))
        {
            return ManualSaPlanValidateResult.NotFound(manualSaPath);
        }

        var command = BuildValidatePlanCommand(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = manualSaPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var deleteError = TryDeletePlanFile(command.PlanFilePath);
            if (deleteError is not null)
            {
                logger.Info("manual_sa_plan_file_cleanup_failed", new
                {
                    peer_id = request.PeerId,
                    session_id = request.SessionId,
                    plan_file = command.PlanFilePath,
                    reason = deleteError
                });
            }

            return new ManualSaPlanValidateResult(false, -1, 0, "failed_to_start_manual_sa_validate", ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var cleanupError = TryDeletePlanFile(command.PlanFilePath);

        sw.Stop();

        var summary = SanitizeSummary(stdout);

        logger.Info("manual_sa_validate_plan_completed", new
        {
            peer_id = request.PeerId,
            session_id = request.SessionId,
            exit_code = process.ExitCode,
            duration_ms = sw.ElapsedMilliseconds,
            manual_sa_path = manualSaPath
        });

        if (cleanupError is not null)
        {
            logger.Info("manual_sa_plan_file_cleanup_failed", new
            {
                peer_id = request.PeerId,
                session_id = request.SessionId,
                plan_file = command.PlanFilePath,
                reason = cleanupError
            });
        }

        return new ManualSaPlanValidateResult(
            ExitCode: process.ExitCode,
            Success: process.ExitCode == 0,
            DurationMs: sw.ElapsedMilliseconds,
            StdoutSummary: summary,
            Stderr: process.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(stderr) ? null : stderr);
    }

    private static ManualSaValidateCommand BuildValidatePlanCommand(ManualSaPlanValidationRequest request)
    {
        if (TryWritePlanFile(request.Plan, out var planFilePath))
        {
            return new(["validate-plan", "--plan-file", planFilePath], planFilePath);
        }

        return new ManualSaValidateCommand(
        [
            "validate-plan",
            "--protocol",
            request.Plan.TrafficSelectorProtocol,
            "--local",
            request.Plan.LocalIp,
            "--remote",
            request.Plan.RemoteIp,
            "--in-spi",
            request.Plan.InboundSpi.ToString(CultureInfo.InvariantCulture),
            "--out-spi",
            request.Plan.OutboundSpi.ToString(CultureInfo.InvariantCulture),
            "--in-cipher-key",
            Hex(request.Plan.InboundCipherKeyPlaceholder),
            "--in-auth-key",
            Hex(request.Plan.InboundAuthKeyPlaceholder),
            "--out-cipher-key",
            Hex(request.Plan.OutboundCipherKeyPlaceholder),
            "--out-auth-key",
            Hex(request.Plan.OutboundAuthKeyPlaceholder)
        ],
        null);
    }

    private sealed record ManualSaValidateCommand(string[] Arguments, string? PlanFilePath);

    private static bool TryWritePlanFile(ManualSaInstallPlan plan, out string planFilePath)
    {
        planFilePath = string.Empty;

        try
        {
            var serializedPlan = ManualSaPlanFileSerializer.ToKeyValuePlan(plan);
            planFilePath = Path.Combine(Path.GetTempPath(), $"manual-sa-plan-{Guid.NewGuid():N}.txt");
            File.WriteAllText(planFilePath, serializedPlan, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch
        {
            planFilePath = string.Empty;
            return false;
        }
    }

    private static string Hex(byte[] bytes)
    {
        return bytes.Length == 0
            ? string.Empty
            : Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? TryDeletePlanFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string SanitizeSummary(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var normalized = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 1200 ? normalized : normalized.AsSpan(0, 1200).ToString();
    }
}

internal sealed record PeerConfig(
    string Endpoint,
    string OverlayEndpoint,
    string PublicIdentityKey,
    string AllowedSelector);

internal sealed record RoutePolicy(
    string Mode,
    int HopCount,
    string[] CandidateIntermediates);

internal sealed record MaintenancePolicy(
    bool Enabled,
    int IntervalSeconds,
    bool AutoRekey,
    bool ExpireCleanup);

internal sealed record RetryPolicy(
    int ConnectTimeoutMs,
    int MaxAttempts,
    int BackoffMs,
    int CleanupStaleAfterSeconds);

internal static class ProgramJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}
