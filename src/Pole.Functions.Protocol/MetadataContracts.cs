namespace Pole.Functions.Protocol;

public enum FunctionTriggerKind
{
    Http,
    Timer
}

public enum FunctionAgentLifecycleState
{
    Accepting,
    Draining
}

public sealed record FunctionAgentMetadataResponse(
    string ProtocolVersion,
    string RuntimeVersion,
    string AgentInstanceId,
    string FunctionAppId,
    string RevisionId,
    FunctionAgentLifecycleState LifecycleState,
    IReadOnlyList<FunctionHandlerMetadata> Handlers);

public sealed record FunctionHandlerMetadata(
    string Name,
    FunctionTriggerKind TriggerKind,
    int MaxConcurrencyPerInstance);
