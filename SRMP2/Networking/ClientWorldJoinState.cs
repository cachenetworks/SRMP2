using System;

namespace SRMP2.Networking;

internal enum ClientWorldJoinPhase
{
    WaitingForTarget,
    WaitingForGameplay,
    LoadingLocalSave,
    AligningWorld,
    Ready,
    NeedsLocalSave
}

internal sealed class ClientWorldJoinState
{
    internal string Target { get; private set; } = string.Empty;
    internal ClientWorldJoinPhase Phase { get; private set; } = ClientWorldJoinPhase.WaitingForTarget;

    internal bool SetTarget(string target)
    {
        target = target?.Trim() ?? string.Empty;
        if (string.Equals(Target, target, StringComparison.Ordinal))
            return false;

        Target = target;
        Phase = target.Length == 0
            ? ClientWorldJoinPhase.WaitingForTarget
            : ClientWorldJoinPhase.WaitingForGameplay;
        return true;
    }

    internal void MarkLocalSaveLoadStarted() => Phase = ClientWorldJoinPhase.LoadingLocalSave;

    internal void MarkWorldAlignmentStarted() => Phase = ClientWorldJoinPhase.AligningWorld;

    internal void MarkReady() => Phase = ClientWorldJoinPhase.Ready;

    internal void MarkNeedsLocalSave() => Phase = ClientWorldJoinPhase.NeedsLocalSave;

    internal void ResumeGameplayCheck()
    {
        Phase = Target.Length == 0
            ? ClientWorldJoinPhase.WaitingForTarget
            : ClientWorldJoinPhase.WaitingForGameplay;
    }

    internal void Reset()
    {
        Target = string.Empty;
        Phase = ClientWorldJoinPhase.WaitingForTarget;
    }
}

internal static class SnapshotRenderGate
{
    internal static bool ShouldRender(bool localPlayerReady, bool worldReady) => localPlayerReady && worldReady;
}
