using SRMP2.Networking;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
        failures.Add(message);
}

var state = new HostWorldTargetState(maxLength: 12);

Check(state.TryUpdate("  conservatory  ", out var first), "first valid target should be accepted");
Check(first == "conservatory", "target should be trimmed");
Check(state.Current == "conservatory", "current target should update");
Check(!state.TryUpdate("conservatory", out _), "unchanged target should not be published twice");
Check(state.TryUpdate("abcdefghijklmnop", out var bounded), "changed long target should be accepted");
Check(bounded == "abcdefghijkl", "target should be bounded to protocol maximum");
Check(state.TryUpdate(string.Empty, out var cleared), "clearing an existing target should be observable");
Check(cleared == string.Empty && state.Current == string.Empty, "clearing should reset current target");
Check(!state.TryUpdate("   ", out _), "equivalent empty target should not republish");

var join = new ClientWorldJoinState();
Check(join.Phase == ClientWorldJoinPhase.WaitingForTarget, "client join should start waiting for a host target");
Check(join.SetTarget("world-a"), "a new host target should reset the client join flow");
Check(join.Target == "world-a" && join.Phase == ClientWorldJoinPhase.WaitingForGameplay, "target should move the client to gameplay bootstrap");
Check(!join.SetTarget("world-a"), "the same host target should not restart the client join flow");
join.MarkLocalSaveLoadStarted();
Check(join.Phase == ClientWorldJoinPhase.LoadingLocalSave, "local save load should be tracked");
join.MarkWorldAlignmentStarted();
Check(join.Phase == ClientWorldJoinPhase.AligningWorld, "scene-group alignment should be tracked");
join.MarkReady();
Check(join.Phase == ClientWorldJoinPhase.Ready, "matching gameplay world should mark join ready");
Check(join.SetTarget("world-b") && join.Phase == ClientWorldJoinPhase.WaitingForGameplay, "host world transition should restart alignment");
join.MarkNeedsLocalSave();
Check(join.Phase == ClientWorldJoinPhase.NeedsLocalSave, "missing local save should remain an explicit recoverable state");
join.ResumeGameplayCheck();
Check(join.Phase == ClientWorldJoinPhase.WaitingForGameplay, "a newly available local save should resume gameplay bootstrap");
join.Reset();
Check(join.Target == string.Empty && join.Phase == ClientWorldJoinPhase.WaitingForTarget, "reset should clear client join state");

Check(!SnapshotRenderGate.ShouldRender(localPlayerReady: false, worldReady: true), "remote snapshots should not render before the local player exists");
Check(!SnapshotRenderGate.ShouldRender(localPlayerReady: true, worldReady: false), "remote snapshots should not render before client world alignment");
Check(SnapshotRenderGate.ShouldRender(localPlayerReady: true, worldReady: true), "remote snapshots should render once both prerequisites are ready");

if (failures.Count == 0)
{
    Console.WriteLine("PASS: SRMP2 world/session state tests");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine($"FAIL: {failure}");
return 1;
