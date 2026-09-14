using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using SRMP2.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SRMP2.Multiplayer;

public sealed class MultiplayerController : IDisposable
{
    private const float SnapshotInterval = 0.05f; // 20 Hz
    private const float SnapshotKeepAlive = 0.75f;

    private readonly NetworkSession _network;
    private readonly Action<string> _log;
    private readonly Dictionary<int, RemotePlayer> _remotePlayers = new();
    private readonly Queue<string> _chatLines = new();

    private SRCharacterController _localPlayer;
    private float _nextPlayerSearch;
    private float _nextSnapshot;
    private float _lastSnapshotSentAt;
    private Vector3 _lastSentPosition;
    private Quaternion _lastSentRotation = Quaternion.identity;
    private ushort _sequence;
    private bool _wasConnected;
    private string _lastAnnouncedScene = string.Empty;

    public MultiplayerController(NetworkSession network, Action<string> logger)
    {
        _network = network;
        _log = logger ?? (_ => { });

        _network.PeerJoined += OnPeerJoined;
        _network.PeerLeft += OnPeerLeft;
        _network.SnapshotReceived += OnSnapshotReceived;
        _network.ChatReceived += OnChatReceived;
        _network.SceneChanged += OnSceneChanged;
        _network.SessionEnded += OnSessionEnded;
    }

    public int RemotePlayerCount => _remotePlayers.Count;
    public bool HasLocalPlayer => _localPlayer != null;
    public IReadOnlyList<string> ChatLines => _chatLines.ToArray();

    public void Update()
    {
        var connected = _network.IsConnected;
        if (connected && !_wasConnected)
        {
            AnnounceActiveSceneIfChanged(force: true);
            AddChatLine($"* Connected to SRMP2 session {_network.SessionId}.");
        }
        _wasConnected = connected;

        FindLocalPlayerIfNeeded();

        if (connected && _localPlayer != null && Time.unscaledTime >= _nextSnapshot)
        {
            _nextSnapshot = Time.unscaledTime + SnapshotInterval;
            SendLocalSnapshotIfNeeded();
        }

        foreach (var remote in _remotePlayers.Values)
            remote.Update();
    }

    public void OnSceneInitialized(string sceneName)
    {
        // Slime Rancher 2 streams many additive scene chunks while the player stays
        // alive. Clearing the local controller for every initialized chunk caused
        // continual FindObjectOfType rebinding and log spam. Let Unity's destroyed-
        // object null semantics tell us when the real player controller is gone.
        if (_localPlayer == null)
            _nextPlayerSearch = 0f;

        AnnounceActiveSceneIfChanged(force: false);
    }

    public void ClearRemotePlayers()
    {
        foreach (var remote in _remotePlayers.Values)
            remote.Destroy();
        _remotePlayers.Clear();
    }

    private void FindLocalPlayerIfNeeded()
    {
        if (_localPlayer != null)
            return;
        if (Time.unscaledTime < _nextPlayerSearch)
            return;

        _nextPlayerSearch = Time.unscaledTime + 1f;
        try
        {
            _localPlayer = UnityEngine.Object.FindObjectOfType<SRCharacterController>();
            if (_localPlayer != null)
            {
                _lastSentPosition = _localPlayer.Position;
                _lastSentRotation = _localPlayer.Rotation;
                _lastSnapshotSentAt = 0f;
                _log("Bound to SR2 SRCharacterController.");
            }
        }
        catch (Exception ex)
        {
            _log($"Could not locate SRCharacterController: {ex.Message}");
        }
    }

    private void AnnounceActiveSceneIfChanged(bool force)
    {
        if (!_network.IsConnected)
            return;

        try
        {
            var scene = SceneManager.GetActiveScene().name ?? string.Empty;
            if (!force && string.Equals(scene, _lastAnnouncedScene, StringComparison.Ordinal))
                return;

            _lastAnnouncedScene = scene;
            _network.SendScene(scene);
        }
        catch (Exception ex)
        {
            _log($"Could not announce active scene: {ex.Message}");
        }
    }

    private void SendLocalSnapshotIfNeeded()
    {
        try
        {
            var position = _localPlayer.Position;
            var rotation = _localPlayer.Rotation;
            var moved = Vector3.SqrMagnitude(position - _lastSentPosition) > 0.0004f;
            var rotated = Quaternion.Angle(rotation, _lastSentRotation) > 0.20f;
            var keepAlive = Time.unscaledTime - _lastSnapshotSentAt >= SnapshotKeepAlive;

            if (!moved && !rotated && !keepAlive)
                return;

            _sequence++;
            _network.SendSnapshot(new PlayerSnapshot(_network.LocalPlayerId, _sequence, position, rotation));
            _lastSentPosition = position;
            _lastSentRotation = rotation;
            _lastSnapshotSentAt = Time.unscaledTime;
        }
        catch (Exception ex)
        {
            // The actual player object can still disappear during a real player/world
            // transition. In that case release it and let the normal search reacquire it.
            _localPlayer = null;
            _nextPlayerSearch = Time.unscaledTime + 0.25f;
            _log($"Player snapshot skipped: {ex.Message}");
        }
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        if (peer.Id == _network.LocalPlayerId)
            return;
        AddChatLine($"* {peer.Username} joined.");
    }

    private void OnPeerLeft(int playerId)
    {
        var name = _remotePlayers.TryGetValue(playerId, out var knownRemote)
            ? knownRemote.Peer.Username
            : $"Player {playerId}";
        if (_remotePlayers.Remove(playerId, out var remote))
            remote.Destroy();
        AddChatLine($"* {name} left.");
    }

    private void OnSnapshotReceived(PlayerSnapshot snapshot)
    {
        if (snapshot.PlayerId == _network.LocalPlayerId)
            return;

        if (!_remotePlayers.TryGetValue(snapshot.PlayerId, out var remote))
        {
            if (!_network.TryGetPeer(snapshot.PlayerId, out var peer))
                peer = new PeerInfo(snapshot.PlayerId, $"Rancher {snapshot.PlayerId}");

            remote = new RemotePlayer(peer);
            _remotePlayers[snapshot.PlayerId] = remote;
        }
        else if (_network.TryGetPeer(snapshot.PlayerId, out var currentPeer))
        {
            remote.UpdatePeer(currentPeer);
        }

        remote.ApplySnapshot(snapshot);
    }

    private void OnChatReceived(int playerId, string message)
    {
        var username = _network.TryGetPeer(playerId, out var peer) ? peer.Username : $"Player {playerId}";
        AddChatLine($"{username}: {message}");
    }

    private void OnSceneChanged(int playerId, string sceneName)
    {
        if (_remotePlayers.TryGetValue(playerId, out var remote)
            && _network.TryGetPeer(playerId, out var peer))
        {
            remote.UpdatePeer(peer);
        }
    }

    private void OnSessionEnded()
    {
        ClearRemotePlayers();
        _localPlayer = null;
        _sequence = 0;
        _wasConnected = false;
        _lastAnnouncedScene = string.Empty;
        AddChatLine("* Session ended.");
    }

    private void AddChatLine(string line)
    {
        _chatLines.Enqueue(line);
        while (_chatLines.Count > 10)
            _chatLines.Dequeue();
        _log(line);
    }

    public void Dispose()
    {
        _network.PeerJoined -= OnPeerJoined;
        _network.PeerLeft -= OnPeerLeft;
        _network.SnapshotReceived -= OnSnapshotReceived;
        _network.ChatReceived -= OnChatReceived;
        _network.SceneChanged -= OnSceneChanged;
        _network.SessionEnded -= OnSessionEnded;
        ClearRemotePlayers();
    }
}
