namespace Pole.Functions.Protocol;

public static class PoleFunctionAgentToken
{
    public const string Type = "pole-function-agent+jwt";

    public static class Claims
    {
        public const string TenantId = "pole_tenant_id";
        public const string FunctionAppId = "pole_function_app_id";
        public const string RevisionId = "pole_revision_id";
        public const string TargetUid = "pole_target_uid";
        public const string Operation = "pole_operation";
        public const string AttemptId = "pole_attempt_id";
        public const string InvocationId = "pole_invocation_id";
        public const string HandlerName = "pole_handler_name";
        public const string AttemptNumber = "pole_attempt_number";
    }

    public static class Operations
    {
        public const string Metadata = "metadata";
        public const string ReserveAttempt = "attempt.reserve";
        public const string ExecuteAttempt = "attempt.execute";
        public const string ObserveAttempt = "attempt.observe";
        public const string AgentStatus = "agent.status";
    }
}
