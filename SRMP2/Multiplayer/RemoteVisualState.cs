namespace SRMP2.Multiplayer;

internal enum BeatrixVisualLoadPhase
{
    NotStarted,
    Loading,
    Ready,
    Failed
}

internal sealed class BeatrixVisualLoadState
{
    internal BeatrixVisualLoadPhase Phase { get; private set; } = BeatrixVisualLoadPhase.NotStarted;

    internal bool TryBeginLoad()
    {
        if (Phase != BeatrixVisualLoadPhase.NotStarted)
            return false;

        Phase = BeatrixVisualLoadPhase.Loading;
        return true;
    }

    internal void MarkReady()
    {
        if (Phase == BeatrixVisualLoadPhase.Loading)
            Phase = BeatrixVisualLoadPhase.Ready;
    }

    internal void MarkFailed()
    {
        if (Phase == BeatrixVisualLoadPhase.Loading)
            Phase = BeatrixVisualLoadPhase.Failed;
    }

    internal void Reset()
    {
        Phase = BeatrixVisualLoadPhase.NotStarted;
    }
}

internal enum RemoteVisualUpgradePhase
{
    Fallback,
    Upgrading,
    Installed,
    Failed
}

internal sealed class RemoteVisualUpgradeState
{
    internal RemoteVisualUpgradePhase Phase { get; private set; } = RemoteVisualUpgradePhase.Fallback;

    internal bool TryBeginUpgrade()
    {
        if (Phase != RemoteVisualUpgradePhase.Fallback)
            return false;

        Phase = RemoteVisualUpgradePhase.Upgrading;
        return true;
    }

    internal void MarkSucceeded()
    {
        if (Phase == RemoteVisualUpgradePhase.Upgrading)
            Phase = RemoteVisualUpgradePhase.Installed;
    }

    internal void MarkFailed()
    {
        if (Phase == RemoteVisualUpgradePhase.Upgrading)
            Phase = RemoteVisualUpgradePhase.Failed;
    }

    internal void Reset()
    {
        Phase = RemoteVisualUpgradePhase.Fallback;
    }
}
