namespace Pole.Functions.Protocol;

public static class PoleFunctionProtocol
{
    public const string Version = "1.0";
    public const string BasePath = "/_pole/v1";

    public const string MetadataPath = BasePath + "/metadata";
    public const string LivenessPath = BasePath + "/health/live";
    public const string ReadinessPath = BasePath + "/health/ready";
    public const string ReserveAttemptPath = BasePath + "/attempts:reserve";
    public const string AgentStatusPath = BasePath + "/status";

    public static string ExecuteAttemptPath(string attemptId) =>
        $"{BasePath}/attempts/{Uri.EscapeDataString(attemptId)}:execute";

    public static string AttemptStatusPath(string attemptId) =>
        $"{BasePath}/attempts/{Uri.EscapeDataString(attemptId)}";
}

public static class PoleFunctionProtocolErrors
{
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string InvalidRequest = "invalid_request";
    public const string UnknownHandler = "unknown_handler";
    public const string AttemptConflict = "attempt_conflict";
    public const string CapacityExhausted = "capacity_exhausted";
    public const string AgentDraining = "agent_draining";
    public const string ReservationExpired = "reservation_expired";
    public const string InvalidReservationToken = "invalid_reservation_token";
    public const string InvalidPayload = "invalid_payload";
    public const string RequestTooLarge = "request_too_large";
}
