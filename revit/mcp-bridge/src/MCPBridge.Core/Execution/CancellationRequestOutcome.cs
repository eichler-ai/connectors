namespace MCPBridge.Core.Execution;

public enum CancellationRequestOutcome
{
    Acknowledged,
    NotFound,
    AlreadyTerminal,
}
