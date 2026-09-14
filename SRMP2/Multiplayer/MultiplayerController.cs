using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppMonomiPark.SlimeRancher.Player.CharacterController;
using Il2CppMonomiPark.SlimeRancher.SceneManagement;
using SRMP2.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SRMP2.Multiplayer;

public sealed class MultiplayerController : IDisposable
{
    private const float SnapshotInterval = 0.05f; // 20 Hz
    private const float SnapshotKeepAlive = 0.75f;
    private const float WorldStatePollInterval = 0.25f;
    private const float WorldAlignmentRetrySeconds = 15f;

    private readonly NetworkSession _network;
    private readonly Action<string> _log;
    private readonly Dictionary<int, RemotePlayer> _remotePlayers = new();
    private readonly Queue<string> _chatLines = new();
    private readonly ClientWorldJoinState _clientWorldJoin = new();

    private SRCharacterController _localPlayer;
    private SceneLoader _sceneLoader;
    private GameContext _gameContext;
    private SceneContext _sceneContext;
    private TeleportablePlayer _teleportablePlayer;
    private float _nextPlayerSearch;
    private float _nextWorldStatePoll;
    private float _worldAlignmentRequestedAt;
    private float _nextSnapshot;
    private float _lastSnapshotSentAt;
    private Vector3 _lastSentPosition;
    private Quaternion _lastSentRotation = Quaternion.identity;
    private PlayerSnapshot _latestHostSnapshot;
    private bool _hasHostSnapshot;
    private ushort _sequence;
    private bool _wasConnected;
    private string _lastAnnouncedScene = string.Empty;
    private string _lastWorldSyncError = string.Empty;

    public MultiplayerController(NetworkSession network, Action<string> logger)
    {
        _network = network;
        _log = logger ?? (_ => { });

        _network.PeerJoined += OnPeerJoined;
        _network.PeerLeft += OnPeerLeft;
        _network.SnapshotReceived += OnSnapshotReceived;
        _network.ChatReceived += OnChatReceived;
        _network.SceneChanged += OnSceneChanged;
        _network.HostWorldTargetChanged += OnHostWorldTargetChanged;
        _network.SessionEnded += OnSessionEnded;
    }

    public int RemotePlayerCount => _remotePlayers.Count;
    public bool HasLocalPlayer => _localPlayer != null;
    public string WorldSyncStatus => _network.Mode == SessionMode.Client
        ? _clientWorldJoin.Phase.ToString()
        : (_network.HostWorldTarget.Length > 0 ? "PublishingGameplayWorld" : "WaitingForGameplayWorld");
    public IReadOnlyList<string> ChatLines => _chatLines.ToArray();

    public void Update()
    {
        var connected = _network.IsConnected;
        if (connected && !_wasConnected)
        {
            AnnounceActiveSceneIfChanged(force: true);
            if (_network.Mode == SessionMode.Client)
                ApplyHostWorldTarget(_network.HostWorldTarget);
            AddChatLine($"* Connected to SRMP2 session {_network.SessionId}.");
        }
        _wasConnected = connected;

        UpdateSessionWorldState();
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

        _nextWorldStatePoll = 0f;
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

    private void UpdateSessionWorldState()
    {
        if (!_network.IsConnected || Time.unscaledTime < _nextWorldStatePoll)
            return;

        _nextWorldStatePoll = Time.unscaledTime + WorldStatePollInterval;

        try
        {
            FindWorldBindingsIfNeeded();

            if (_network.Mode == SessionMode.Host)
                UpdateHostWorldTarget();
            else if (_network.Mode == SessionMode.Client)
                UpdateClientWorldJoin();

            _lastWorldSyncError = string.Empty;
        }
        catch (Exception ex)
        {
            if (!string.Equals(_lastWorldSyncError, ex.Message, StringComparison.Ordinal))
            {
                _lastWorldSyncError = ex.Message;
                _log($"World synchronization check failed: {ex.Message}");
            }
            _nextWorldStatePoll = Time.unscaledTime + 2f;
        }
    }

    private void FindWorldBindingsIfNeeded()
    {
        if (_sceneLoader == null)
            _sceneLoader = UnityEngine.Object.FindObjectOfType<SceneLoader>();
        if (_gameContext == null)
            _gameContext = UnityEngine.Object.FindObjectOfType<GameContext>();
        if (_sceneContext == null)
            _sceneContext = UnityEngine.Object.FindObjectOfType<SceneContext>();
        if (_teleportablePlayer == null)
            _teleportablePlayer = UnityEngine.Object.FindObjectOfType<TeleportablePlayer>();
    }

    private void UpdateHostWorldTarget()
    {
        if (_sceneLoader == null || _sceneLoader.IsSceneLoadInProgress)
            return;

        var currentGroup = _sceneLoader.CurrentSceneGroup;
        var worldTarget = currentGroup != null && currentGroup.IsGameplay
            ? currentGroup.ReferenceId?.Trim() ?? string.Empty
            : string.Empty;

        if (worldTarget.Length == 0)
        {
            if (_network.HostWorldTarget.Length > 0 && _sceneLoader.IsCurrentSceneGroupMainMenu())
            {
                _network.PublishHostWorldTarget(string.Empty);
                _log("Host returned to the SR2 main menu; cleared multiplayer world target.");
            }
            return;
        }

        if (string.Equals(worldTarget, _network.HostWorldTarget, StringComparison.Ordinal))
            return;

        _network.PublishHostWorldTarget(worldTarget);
        _log($"Host world target: {worldTarget}");
    }

    private void UpdateClientWorldJoin()
    {
        var target = _clientWorldJoin.Target;
        if (target.Length == 0 || _sceneLoader == null || _sceneLoader.IsSceneLoadInProgress)
            return;

        var currentGroup = _sceneLoader.CurrentSceneGroup;
        var currentWorld = currentGroup != null && currentGroup.IsGameplay
            ? currentGroup.ReferenceId?.Trim() ?? string.Empty
            : string.Empty;

        if (currentWorld.Length > 0)
        {
            if (string.Equals(currentWorld, target, StringComparison.Ordinal))
            {
                if (_clientWorldJoin.Phase != ClientWorldJoinPhase.Ready)
                {
                    _clientWorldJoin.MarkReady();
                    _nextPlayerSearch = 0f;
                    _log($"Client world load complete: {target}");
                }
                return;
            }

            if (_clientWorldJoin.Phase == ClientWorldJoinPhase.AligningWorld)
            {
                if (Time.unscaledTime - _worldAlignmentRequestedAt < WorldAlignmentRetrySeconds)
                    return;

                _log($"Client scene-group alignment to '{target}' did not complete; retrying through SR2 teleport handling.");
                _clientWorldJoin.ResumeGameplayCheck();
            }
            if (_sceneContext == null || _teleportablePlayer == null || !_hasHostSnapshot)
                return;

            var canTeleport = _teleportablePlayer.CanTeleport();
            if (!canTeleport.Item1)
                return;

            var targetGroup = _sceneLoader.SceneGroupList?.GetSceneGroupFromReferenceId(target);
            if (targetGroup == null || !targetGroup.IsGameplay)
            {
                _log($"Could not resolve host gameplay scene group '{target}'.");
                return;
            }

            var spacing = 1.5f + (Math.Max(0, _network.LocalPlayerId - 2) * 0.5f);
            var spawnOffset = (_latestHostSnapshot.Rotation * Vector3.right) * spacing;
            var spawnPosition = _latestHostSnapshot.Position + spawnOffset;

            _clientWorldJoin.MarkWorldAlignmentStarted();
            _worldAlignmentRequestedAt = Time.unscaledTime;
            ClearRemotePlayers();
            _log($"Aligning client scene group '{currentWorld}' -> '{target}' near host player.");
            _teleportablePlayer.TeleportTo(spawnPosition, targetGroup, default, overlayEnabled: true);
            return;
        }

        if (_clientWorldJoin.Phase == ClientWorldJoinPhase.LoadingLocalSave
            || _clientWorldJoin.Phase == ClientWorldJoinPhase.AligningWorld)
        {
            return;
        }

        if (_gameContext == null || _gameContext.AutoSaveDirector == null)
            return;

        var autoSave = _gameContext.AutoSaveDirector;
        if (!autoSave.HasContinue())
        {
            if (_clientWorldJoin.Phase != ClientWorldJoinPhase.NeedsLocalSave)
            {
                _clientWorldJoin.MarkNeedsLocalSave();
                _log("Client has no SR2 continue save. Create/load a local save once; SRMP2 will keep the EOS session connected.");
            }
            return;
        }

        if (_clientWorldJoin.Phase == ClientWorldJoinPhase.NeedsLocalSave)
            _clientWorldJoin.ResumeGameplayCheck();

        var summary = autoSave.GetSaveToContinue();
        if (summary == null || summary.IsInvalid)
        {
            _clientWorldJoin.MarkNeedsLocalSave();
            _log("SR2 reported an invalid continue save; multiplayer world loading was not started.");
            return;
        }

        _clientWorldJoin.MarkLocalSaveLoadStarted();
        ClearRemotePlayers();
        _log($"Beginning client world load through SR2 save pipeline for host target '{target}'.");
        autoSave.BeginLoad(summary.SaveIdentifier, null);
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

        if (_network.Mode == SessionMode.Client && snapshot.PlayerId == 1)
        {
            _latestHostSnapshot = snapshot;
            _hasHostSnapshot = true;
        }

        var worldReady = _network.Mode != SessionMode.Client
            || _clientWorldJoin.Phase == ClientWorldJoinPhase.Ready;
        if (!SnapshotRenderGate.ShouldRender(_localPlayer != null, worldReady))
            return;

        if (!_remotePlayers.TryGetValue(snapshot.PlayerId, out var remote))
        {
            if (!_network.TryGetPeer(snapshot.PlayerId, out var peer))
                peer = new PeerInfo(snapshot.PlayerId, $"Rancher {snapshot.PlayerId}");

            remote = new RemotePlayer(peer);
            _remotePlayers[snapshot.PlayerId] = remote;
            _log($"Spawned remote player #{snapshot.PlayerId} ({peer.Username}) from gameplay snapshot.");
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

    private void OnHostWorldTargetChanged(string worldTarget)
    {
        if (_network.Mode == SessionMode.Client)
        {
            ApplyHostWorldTarget(worldTarget);
            return;
        }

        if (_network.Mode == SessionMode.Host)
            ClearRemotePlayers();
    }

    private void ApplyHostWorldTarget(string worldTarget)
    {
        if (!_clientWorldJoin.SetTarget(worldTarget))
            return;

        ClearRemotePlayers();
        _hasHostSnapshot = false;
        _latestHostSnapshot = default;
        _worldAlignmentRequestedAt = 0f;
        _nextWorldStatePoll = 0f;

        if (_clientWorldJoin.Target.Length > 0)
            _log($"Received host world target: {_clientWorldJoin.Target}");
    }

    private void OnSessionEnded()
    {
        ClearRemotePlayers();
        _localPlayer = null;
        _sequence = 0;
        _wasConnected = false;
        _lastAnnouncedScene = string.Empty;
        _lastWorldSyncError = string.Empty;
        _clientWorldJoin.Reset();
        _hasHostSnapshot = false;
        _latestHostSnapshot = default;
        _sceneLoader = null;
        _gameContext = null;
        _sceneContext = null;
        _teleportablePlayer = null;
        _worldAlignmentRequestedAt = 0f;
        _nextWorldStatePoll = 0f;
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
        _network.HostWorldTargetChanged -= OnHostWorldTargetChanged;
        _network.SessionEnded -= OnSessionEnded;
        ClearRemotePlayers();
    }
}
