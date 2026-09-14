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

if (failures.Count == 0)
{
    Console.WriteLine("PASS: HostWorldTargetState");
    return 0;
}

foreach (var failure in failures)
    Console.Error.WriteLine($"FAIL: {failure}");
return 1;
