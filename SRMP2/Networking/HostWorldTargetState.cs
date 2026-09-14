using System;

namespace SRMP2.Networking;

internal sealed class HostWorldTargetState
{
    private readonly int _maxLength;

    internal HostWorldTargetState(int maxLength)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        _maxLength = maxLength;
    }

    internal string Current { get; private set; } = string.Empty;

    internal bool TryUpdate(string value, out string normalized)
    {
        normalized = Normalize(value);
        if (string.Equals(Current, normalized, StringComparison.Ordinal))
            return false;

        Current = normalized;
        return true;
    }

    internal void Reset() => Current = string.Empty;

    private string Normalize(string value)
    {
        value = value?.Trim() ?? string.Empty;
        return value.Length <= _maxLength ? value : value[.._maxLength];
    }
}
