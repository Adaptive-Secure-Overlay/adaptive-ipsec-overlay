namespace AdaptiveClient.Core.Models;

/// <summary>
/// Outcome of <see cref="AikeSessionManager.ProcessMessage"/> invocation.
/// </summary>
public sealed record ProcessResult(
    bool Success,
    SessionState? Session,
    AikeMessage? OutboundMessage,
    string? Error)
{
    public bool RequiresResponse => OutboundMessage is not null;

    public static ProcessResult Accepted(SessionState session) => new(
        Success: true,
        Session: session,
        OutboundMessage: null,
        Error: null);

    public static ProcessResult AcceptedWithResponse(SessionState session, AikeMessage outboundMessage) => new(
        Success: true,
        Session: session,
        OutboundMessage: outboundMessage,
        Error: null);

    public static ProcessResult Rejected(string error, SessionState? session = null) => new(
        Success: false,
        Session: session,
        OutboundMessage: null,
        Error: error);
}
