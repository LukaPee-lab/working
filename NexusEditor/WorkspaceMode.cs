namespace NexusEditor;

public enum WorkspaceMode
{
    Event,
    Research
}

public enum WorkspaceSwitchResult
{
    Cancel,
    DiscardChanges,
    ExportAndSwitch
}

public sealed record WorkspaceCardState(bool IsAvailable, string StatusText)
{
    public static WorkspaceCardState Ready(string statusText) => new(true, statusText);

    public static WorkspaceCardState Unavailable(string statusText) => new(false, statusText);
}

public sealed class WorkspaceRequestedEventArgs : EventArgs
{
    public WorkspaceRequestedEventArgs(WorkspaceMode workspace)
    {
        Workspace = workspace;
    }

    public WorkspaceMode Workspace { get; }
}
