using System;
using SRMP2.Networking;
using UnityEngine;

namespace SRMP2.Multiplayer;

internal sealed class RemotePlayer
{
    private const float InterpolationSeconds = 0.10f;

    private readonly GameObject _root;
    private readonly GameObject _fallbackBody;
    private readonly TextMesh _label;
    private readonly BeatrixVisualLoader _visualLoader;
    private readonly Action<string> _log;
    private readonly RemoteVisualUpgradeState _visualUpgrade = new();
    private GameObject _beatrixVisual;
    private Vector3 _fromPosition;
    private Vector3 _targetPosition;
    private Quaternion _fromRotation = Quaternion.identity;
    private Quaternion _targetRotation = Quaternion.identity;
    private float _snapshotStartedAt;
    private ushort _lastSequence;
    private bool _hasSnapshot;

    internal RemotePlayer(PeerInfo peer, BeatrixVisualLoader visualLoader, Action<string> logger)
    {
        Peer = peer;
        _visualLoader = visualLoader;
        _log = logger ?? (_ => { });
        _root = new GameObject($"SRMP2 Remote - {peer.Username} ({peer.Id})");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        _fallbackBody = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        _fallbackBody.name = "Body";
        _fallbackBody.transform.SetParent(_root.transform, worldPositionStays: false);
        _fallbackBody.transform.localPosition = new Vector3(0f, 1f, 0f);
        _fallbackBody.transform.localScale = new Vector3(0.72f, 1f, 0.72f);

        var collider = _fallbackBody.GetComponent<Collider>();
        if (collider != null)
            collider.enabled = false;

        var renderer = _fallbackBody.GetComponent<Renderer>();
        if (renderer != null)
        {
            try
            {
                var hue = Mathf.Repeat(peer.Id * 0.173f, 1f);
                renderer.material.color = Color.HSVToRGB(hue, 0.55f, 1f);
            }
            catch
            {
                // Material access can vary between Unity/HDRP revisions; the default material is fine.
            }
        }

        var nameTag = new GameObject("NameTag");
        nameTag.transform.SetParent(_root.transform, worldPositionStays: false);
        nameTag.transform.localPosition = new Vector3(0f, 2.45f, 0f);
        _label = nameTag.AddComponent<TextMesh>();
        _label.text = peer.Username;
        _label.anchor = TextAnchor.MiddleCenter;
        _label.alignment = TextAlignment.Center;
        _label.characterSize = 0.12f;
        _label.fontSize = 32;

        _visualLoader.RequestLoad();
    }

    internal PeerInfo Peer { get; private set; }

    internal void UpdatePeer(PeerInfo peer)
    {
        Peer = peer;
        if (_label != null)
            _label.text = peer.Username;
    }

    internal void ApplySnapshot(PlayerSnapshot snapshot)
    {
        if (_hasSnapshot && !IsSequenceNewer(snapshot.Sequence, _lastSequence))
            return;

        _lastSequence = snapshot.Sequence;
        if (!_hasSnapshot || Vector3.Distance(_root.transform.position, snapshot.Position) > 15f)
        {
            _root.transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            _fromPosition = snapshot.Position;
            _targetPosition = snapshot.Position;
            _fromRotation = snapshot.Rotation;
            _targetRotation = snapshot.Rotation;
            _snapshotStartedAt = Time.unscaledTime;
            _hasSnapshot = true;
            return;
        }

        _fromPosition = _root.transform.position;
        _fromRotation = _root.transform.rotation;
        _targetPosition = snapshot.Position;
        _targetRotation = snapshot.Rotation;
        _snapshotStartedAt = Time.unscaledTime;
        _hasSnapshot = true;
    }

    internal void Update()
    {
        if (_hasSnapshot)
        {
            var t = Mathf.Clamp01((Time.unscaledTime - _snapshotStartedAt) / InterpolationSeconds);
            _root.transform.position = Vector3.Lerp(_fromPosition, _targetPosition, t);
            _root.transform.rotation = Quaternion.Slerp(_fromRotation, _targetRotation, t);
        }

        if (_label != null && Camera.main != null)
        {
            var camera = Camera.main.transform;
            var direction = _label.transform.position - camera.position;
            if (direction.sqrMagnitude > 0.0001f)
                _label.transform.rotation = Quaternion.LookRotation(direction);
        }

        UpdateVisual();
    }

    internal void Destroy()
    {
        _beatrixVisual = null;
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
    }

    private void UpdateVisual()
    {
        if (_visualUpgrade.Phase != RemoteVisualUpgradePhase.Fallback)
            return;

        if (_visualLoader.Phase == BeatrixVisualLoadPhase.Failed)
        {
            if (_visualUpgrade.TryBeginUpgrade())
            {
                _visualUpgrade.MarkFailed();
                _log($"Beatrix visual fallback for remote #{Peer.Id} ({Peer.Username}): shared assets unavailable.");
            }
            return;
        }

        if (_visualLoader.Phase != BeatrixVisualLoadPhase.Ready || !_visualUpgrade.TryBeginUpgrade())
            return;

        if (_visualLoader.TryCreateVisual(_root.transform, out _beatrixVisual, out var reason))
        {
            if (_fallbackBody != null)
                _fallbackBody.SetActive(false);
            _visualUpgrade.MarkSucceeded();
            _log($"Upgraded remote player #{Peer.Id} ({Peer.Username}) to Beatrix visual.");
            return;
        }

        _visualUpgrade.MarkFailed();
        _log($"Beatrix visual fallback for remote #{Peer.Id} ({Peer.Username}): {reason}.");
    }

    private static bool IsSequenceNewer(ushort candidate, ushort previous)
    {
        // RFC1982-style serial arithmetic for a wrapping 16-bit sequence number.
        return candidate != previous && (ushort)(candidate - previous) < 32768;
    }
}
