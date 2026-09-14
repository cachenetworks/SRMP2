using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SRMP2.EOS;

namespace SRMP2.Networking;

public enum SessionMode
{
    Offline,
    Host,
    Client
}

public sealed class PeerInfo
{
    public PeerInfo(int id, string username, string sceneName = "")
    {
        Id = id;
        Username = username;
        SceneName = sceneName ?? string.Empty;
    }

    public int Id { get; }
    public string Username { get; }
    public string SceneName { get; internal set; }
}

public sealed class NetworkSession : IDisposable
{
    private const byte ControlChannel = 0;
    private const byte MovementChannel = 1;
    private const uint ControlMagic = 0x32435453; // "STC2"
    private const int ControlHeaderBytes = 8;
    private const int ControlFragmentPayloadBytes = EosNative.MaxP2PPacketSize - ControlHeaderBytes;
    private const string LobbyAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private readonly Action<string> _log;
    private readonly EosRuntime _eos;
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly ConcurrentDictionary<int, PeerInfo> _peers = new();
    private readonly Dictionary<IntPtr, HostPeer> _hostPeersByUser = new();
    private readonly Dictionary<int, HostPeer> _hostPeersById = new();
    private readonly Dictionary<(IntPtr RemoteUser, ushort MessageId), ControlAssembly> _controlAssemblies = new();

    private int _nextPlayerId = 1;
    private ushort _nextControlMessageId;
    private string _localUsername = "Rancher";
    private string _lobbyId = string.Empty;
    private IntPtr _hostUserId;
    private bool _sessionReady;
    private bool _disposed;

    public NetworkSession(Action<string> logger)
    {
        _log = logger ?? (_ => { });
        _eos = new EosRuntime(_log);
    }

    public SessionMode Mode { get; private set; } = SessionMode.Offline;
    public string StatusText { get; private set; } = "Offline";
    public int LocalPlayerId { get; private set; }
    public Guid SessionId { get; private set; } = Guid.Empty;
    public int Port => Protocol.DefaultPort; // retained for API compatibility; EOS P2P does not require this port.
    public string ServerCode => _lobbyId;
    public bool IsConnected => _sessionReady;
    public IReadOnlyCollection<PeerInfo> Peers => _peers.Values.OrderBy(x => x.Id).ToArray();

    public event Action<PeerInfo> PeerJoined;
    public event Action<int> PeerLeft;
    public event Action<PlayerSnapshot> SnapshotReceived;
    public event Action<int, string> ChatReceived;
    public event Action<int, string> SceneChanged;
    public event Action<string> StatusChanged;
    public event Action SessionEnded;

    public void Pump()
    {
        DrainMainThreadQueue();

        try
        {
            _eos.Tick();
        }
        catch (Exception ex)
        {
            _log($"EOS tick failed: {ex.Message}");
        }

        if (_eos.IsLoggedIn)
        {
            var guard = 0;
            while (guard++ < 256 && _eos.TryReceive(out var remoteUser, out var channel, out var data))
            {
                try
                {
                    if (channel == ControlChannel)
                        HandleControlTransport(remoteUser, data);
                    else if (channel == MovementChannel)
                        HandleMovementTransport(remoteUser, data);
                }
                catch (Exception ex)
                {
                    _log($"EOS packet handling failed: {ex.Message}");
                }
            }
        }

        DrainMainThreadQueue();
    }

    private void DrainMainThreadQueue()
    {
        while (_mainThread.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log($"Main-thread network callback failed: {ex}");
            }
        }
    }

    public bool TryGetPeer(int id, out PeerInfo peer) => _peers.TryGetValue(id, out peer);

    public void StartHost(string username, int port)
    {
        if (_disposed)
            return;

        StopInternal("Restarting session", notify: false);
        _localUsername = Protocol.CleanUsername(username);
        Mode = SessionMode.Host;
        SetStatus("Signing in to EOS...");

        _eos.EnsureLoggedIn(_localUsername, (success, error) =>
        {
            if (Mode != SessionMode.Host)
                return;
            if (!success)
            {
                FailPendingSession($"EOS login failed: {error}");
                return;
            }

            CreateHostLobby(attempt: 0);
        });
    }

    public void JoinCode(string code, string username)
    {
        if (_disposed)
            return;

        code = NormalizeServerCode(code);
        if (!IsValidServerCode(code))
        {
            SetStatus("Invalid server code. Expected 7 letters/numbers.");
            return;
        }

        StopInternal("Restarting session", notify: false);
        _localUsername = Protocol.CleanUsername(username);
        _lobbyId = code;
        Mode = SessionMode.Client;
        SetStatus($"Signing in to EOS for lobby {code}...");

        _eos.EnsureLoggedIn(_localUsername, (success, error) =>
        {
            if (Mode != SessionMode.Client || !string.Equals(_lobbyId, code, StringComparison.Ordinal))
                return;
            if (!success)
            {
                FailPendingSession($"EOS login failed: {error}");
                return;
            }

            SetStatus($"Joining EOS lobby {code}...");
            _eos.JoinLobby(code, (result, actualLobbyId) =>
            {
                if (Mode != SessionMode.Client)
                    return;
                if (result != EosNative.Result.Success)
                {
                    FailPendingSession($"EOS lobby join failed: {result}");
                    return;
                }

                _lobbyId = string.IsNullOrWhiteSpace(actualLobbyId) ? code : actualLobbyId;
                if (!_eos.TryGetLobbyOwner(_lobbyId, out _hostUserId) || _hostUserId == IntPtr.Zero)
                {
                    FailPendingSession("EOS joined the lobby but could not resolve its owner.");
                    return;
                }

                _eos.RegisterP2PCallbacks(OnP2PConnectionRequest, OnP2PConnectionClosed);
                var accept = _eos.AcceptConnection(_hostUserId);
                if (accept != EosNative.Result.Success)
                    _log($"EOS client AcceptConnection returned {accept}; initial SendPacket will still request the connection.");

                var hello = Protocol.BuildTcpFrame(Protocol.MessageKind.Hello, writer =>
                {
                    writer.Write(Protocol.Version);
                    writer.Write(BuildInfo.Version);
                    writer.Write(_localUsername);
                });

                if (!SendControl(_hostUserId, hello, disableAutoAccept: false))
                {
                    FailPendingSession("EOS could not send the initial handshake to the host.");
                    return;
                }

                SetStatus($"Connecting to EOS host for {_lobbyId}...");
            });
        });
    }

    public void Join(string host, int port, string username)
    {
        // Kept for source compatibility with earlier SRMP2 builds. The Internet
        // transport is now EOS lobby/P2P, so the first argument is treated as a code.
        JoinCode(host, username);
    }

    public void Disconnect()
    {
        if (Mode == SessionMode.Client && _sessionReady && _hostUserId != IntPtr.Zero)
            SendControl(_hostUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.Disconnect));
        else if (Mode == SessionMode.Host && _sessionReady)
            BroadcastControl(Protocol.BuildTcpFrame(Protocol.MessageKind.Disconnect));

        StopInternal("Disconnected", notify: true);
    }

    public void SendSnapshot(PlayerSnapshot snapshot)
    {
        if (!_sessionReady || snapshot.PlayerId != LocalPlayerId || SessionId == Guid.Empty)
            return;

        var bytes = Protocol.BuildSnapshot(SessionId, snapshot);
        if (Mode == SessionMode.Host)
        {
            foreach (var peer in _hostPeersById.Values.ToArray())
                SendMovement(peer.ProductUserId, bytes);
        }
        else if (Mode == SessionMode.Client && _hostUserId != IntPtr.Zero)
        {
            SendMovement(_hostUserId, bytes);
        }
    }

    public void SendChat(string text)
    {
        text = Protocol.CleanChat(text);
        if (text.Length == 0 || !_sessionReady)
            return;

        if (Mode == SessionMode.Host)
        {
            Enqueue(() => ChatReceived?.Invoke(LocalPlayerId, text));
            var frame = Protocol.BuildTcpFrame(Protocol.MessageKind.Chat, writer =>
            {
                writer.Write(LocalPlayerId);
                writer.Write(text);
            });
            BroadcastControl(frame);
        }
        else if (_hostUserId != IntPtr.Zero)
        {
            SendControl(_hostUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.Chat, writer => writer.Write(text)));
        }
    }

    public void SendScene(string sceneName)
    {
        if (!_sessionReady)
            return;

        sceneName ??= string.Empty;
        if (sceneName.Length > Protocol.MaxSceneLength)
            sceneName = sceneName[..Protocol.MaxSceneLength];

        if (_peers.TryGetValue(LocalPlayerId, out var localPeer))
            localPeer.SceneName = sceneName;

        if (Mode == SessionMode.Host)
        {
            var frame = Protocol.BuildTcpFrame(Protocol.MessageKind.SceneChanged, writer =>
            {
                writer.Write(LocalPlayerId);
                writer.Write(sceneName);
            });
            BroadcastControl(frame);
        }
        else if (_hostUserId != IntPtr.Zero)
        {
            SendControl(_hostUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.SceneChanged, writer => writer.Write(sceneName)));
        }
    }

    private void CreateHostLobby(int attempt)
    {
        if (Mode != SessionMode.Host)
            return;
        if (attempt >= 5)
        {
            FailPendingSession("EOS could not allocate a unique server code after 5 attempts.");
            return;
        }

        var code = GenerateServerCode();
        SetStatus($"Creating EOS lobby {code}...");
        _eos.CreateLobby(code, (result, actualLobbyId) =>
        {
            if (Mode != SessionMode.Host)
                return;

            if (result == EosNative.Result.DuplicateNotAllowed)
            {
                CreateHostLobby(attempt + 1);
                return;
            }
            if (result != EosNative.Result.Success)
            {
                FailPendingSession($"EOS lobby creation failed: {result}");
                return;
            }

            _lobbyId = string.IsNullOrWhiteSpace(actualLobbyId) ? code : actualLobbyId;
            SessionId = Guid.NewGuid();
            LocalPlayerId = 1;
            _nextPlayerId = 1;
            _peers.Clear();
            _peers[LocalPlayerId] = new PeerInfo(LocalPlayerId, _localUsername);
            _hostPeersById.Clear();
            _hostPeersByUser.Clear();
            _sessionReady = true;
            _eos.RegisterP2PCallbacks(OnP2PConnectionRequest, OnP2PConnectionClosed);
            SetStatus($"Hosting EOS lobby {_lobbyId}");
            _log($"EOS SRMP2 lobby ready. Server code: {_lobbyId}");
        });
    }

    private void OnP2PConnectionRequest(IntPtr remoteUserId)
    {
        if (remoteUserId == IntPtr.Zero)
            return;

        if (Mode == SessionMode.Host)
        {
            if (!_sessionReady || !_eos.IsLobbyMember(_lobbyId, remoteUserId))
            {
                _eos.CloseConnection(remoteUserId);
                return;
            }
        }
        else if (Mode == SessionMode.Client && remoteUserId != _hostUserId)
        {
            _eos.CloseConnection(remoteUserId);
            return;
        }

        var result = _eos.AcceptConnection(remoteUserId);
        if (result != EosNative.Result.Success)
            _log($"EOS AcceptConnection failed: {result}");
    }

    private void OnP2PConnectionClosed(IntPtr remoteUserId, int reason)
    {
        if (Mode == SessionMode.Host)
        {
            if (_hostPeersByUser.TryGetValue(remoteUserId, out var peer))
                RemoveHostPeer(peer.Id, closeConnection: false);
        }
        else if (Mode == SessionMode.Client && remoteUserId == _hostUserId && _sessionReady)
        {
            _log($"EOS host connection closed (reason {reason}).");
            StopInternal("EOS host connection closed", notify: true);
        }
    }

    private void HandleControlTransport(IntPtr remoteUserId, byte[] packet)
    {
        if (!TryReassembleControl(remoteUserId, packet, out var frame))
            return;

        if (Mode == SessionMode.Host)
            HandleHostControl(remoteUserId, frame);
        else if (Mode == SessionMode.Client && remoteUserId == _hostUserId)
            HandleClientControl(frame);
    }

    private void HandleHostControl(IntPtr remoteUserId, byte[] frame)
    {
        if (frame == null || frame.Length == 0)
            return;

        using var stream = new MemoryStream(frame, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var kind = (Protocol.MessageKind)reader.ReadByte();

        if (!_hostPeersByUser.TryGetValue(remoteUserId, out var connection))
        {
            if (kind != Protocol.MessageKind.Hello)
                return;
            HandleHostHello(remoteUserId, reader);
            return;
        }

        switch (kind)
        {
            case Protocol.MessageKind.Chat:
            {
                var text = Protocol.CleanChat(Protocol.ReadBoundedString(reader, Protocol.MaxChatLength));
                if (text.Length == 0)
                    return;
                Enqueue(() => ChatReceived?.Invoke(connection.Id, text));
                var outgoing = Protocol.BuildTcpFrame(Protocol.MessageKind.Chat, writer =>
                {
                    writer.Write(connection.Id);
                    writer.Write(text);
                });
                BroadcastControl(outgoing);
                break;
            }
            case Protocol.MessageKind.SceneChanged:
            {
                var scene = Protocol.ReadBoundedString(reader, Protocol.MaxSceneLength);
                if (_peers.TryGetValue(connection.Id, out var peer))
                    peer.SceneName = scene;
                Enqueue(() => SceneChanged?.Invoke(connection.Id, scene));
                var outgoing = Protocol.BuildTcpFrame(Protocol.MessageKind.SceneChanged, writer =>
                {
                    writer.Write(connection.Id);
                    writer.Write(scene);
                });
                BroadcastControl(outgoing, exceptPlayerId: connection.Id);
                break;
            }
            case Protocol.MessageKind.Ping:
                SendControl(remoteUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.Pong));
                break;
            case Protocol.MessageKind.Disconnect:
                RemoveHostPeer(connection.Id, closeConnection: true);
                break;
        }
    }

    private void HandleHostHello(IntPtr remoteUserId, BinaryReader reader)
    {
        try
        {
            var protocol = reader.ReadInt32();
            var modVersion = Protocol.ReadBoundedString(reader, 64);
            var username = Protocol.CleanUsername(Protocol.ReadBoundedString(reader, Protocol.MaxUsernameLength));

            if (protocol != Protocol.Version)
            {
                SendReject(remoteUserId, $"Protocol mismatch. Host={Protocol.Version}, client={protocol}");
                return;
            }
            if (!string.Equals(modVersion, BuildInfo.Version, StringComparison.Ordinal))
            {
                SendReject(remoteUserId, $"SRMP2 version mismatch. Host={BuildInfo.Version}, client={modVersion}");
                return;
            }
            if (!_eos.IsLobbyMember(_lobbyId, remoteUserId))
            {
                SendReject(remoteUserId, "EOS user is not a member of this lobby.");
                _eos.CloseConnection(remoteUserId);
                return;
            }
            if (_hostPeersById.Count + 1 >= Protocol.MaxPlayers)
            {
                SendReject(remoteUserId, "Server is full.");
                return;
            }

            var id = ++_nextPlayerId;
            var peer = new PeerInfo(id, username);
            var connection = new HostPeer(id, username, modVersion, remoteUserId);
            _hostPeersById[id] = connection;
            _hostPeersByUser[remoteUserId] = connection;
            _peers[id] = peer;

            var peerSnapshot = _peers.Values.OrderBy(x => x.Id).ToArray();
            var welcome = Protocol.BuildTcpFrame(Protocol.MessageKind.Welcome, writer =>
            {
                writer.Write(Protocol.Version);
                writer.Write(id);
                writer.Write(SessionId.ToByteArray());
                writer.Write(peerSnapshot.Length);
                foreach (var existing in peerSnapshot)
                {
                    writer.Write(existing.Id);
                    writer.Write(existing.Username);
                    writer.Write(existing.SceneName ?? string.Empty);
                }
            });
            SendControl(remoteUserId, welcome);

            var joined = Protocol.BuildTcpFrame(Protocol.MessageKind.PeerJoined, writer =>
            {
                writer.Write(id);
                writer.Write(username);
                writer.Write(peer.SceneName ?? string.Empty);
            });
            BroadcastControl(joined, exceptPlayerId: id);
            Enqueue(() => PeerJoined?.Invoke(peer));
            _log($"{username} (#{id}) connected over EOS P2P using SRMP2 {modVersion}.");
        }
        catch (Exception ex)
        {
            _log($"Rejected malformed EOS hello: {ex.Message}");
            SendReject(remoteUserId, "Malformed handshake.");
        }
    }

    private void HandleClientControl(byte[] frame)
    {
        using var stream = new MemoryStream(frame, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var kind = (Protocol.MessageKind)reader.ReadByte();

        switch (kind)
        {
            case Protocol.MessageKind.Welcome:
            {
                var protocol = reader.ReadInt32();
                if (protocol != Protocol.Version)
                    throw new InvalidDataException($"Protocol mismatch. Server={protocol}, client={Protocol.Version}");

                var assignedId = reader.ReadInt32();
                var sessionBytes = reader.ReadBytes(16);
                if (sessionBytes.Length != 16)
                    throw new InvalidDataException("Invalid EOS welcome session id.");
                var session = new Guid(sessionBytes);
                var peerCount = reader.ReadInt32();
                if (peerCount < 1 || peerCount > Protocol.MaxPlayers)
                    throw new InvalidDataException("Invalid peer count.");

                var receivedPeers = new List<PeerInfo>(peerCount);
                for (var i = 0; i < peerCount; i++)
                {
                    var id = reader.ReadInt32();
                    var name = Protocol.CleanUsername(Protocol.ReadBoundedString(reader, Protocol.MaxUsernameLength));
                    var scene = Protocol.ReadBoundedString(reader, Protocol.MaxSceneLength);
                    receivedPeers.Add(new PeerInfo(id, name, scene));
                }

                LocalPlayerId = assignedId;
                SessionId = session;
                _peers.Clear();
                foreach (var peer in receivedPeers)
                    _peers[peer.Id] = peer;
                _sessionReady = true;

                SetStatus($"Connected via EOS as {_localUsername} (#{assignedId})");
                Enqueue(() =>
                {
                    foreach (var peer in receivedPeers)
                    {
                        if (peer.Id != LocalPlayerId)
                            PeerJoined?.Invoke(peer);
                    }
                });
                break;
            }
            case Protocol.MessageKind.Reject:
            {
                var reason = Protocol.ReadBoundedString(reader, 256);
                StopInternal($"Rejected: {reason}", notify: true);
                break;
            }
            case Protocol.MessageKind.PeerJoined:
            {
                var id = reader.ReadInt32();
                var name = Protocol.CleanUsername(Protocol.ReadBoundedString(reader, Protocol.MaxUsernameLength));
                var scene = Protocol.ReadBoundedString(reader, Protocol.MaxSceneLength);
                var peer = new PeerInfo(id, name, scene);
                _peers[id] = peer;
                Enqueue(() => PeerJoined?.Invoke(peer));
                break;
            }
            case Protocol.MessageKind.PeerLeft:
            {
                var id = reader.ReadInt32();
                _peers.TryRemove(id, out _);
                Enqueue(() => PeerLeft?.Invoke(id));
                break;
            }
            case Protocol.MessageKind.Chat:
            {
                var senderId = reader.ReadInt32();
                var text = Protocol.ReadBoundedString(reader, Protocol.MaxChatLength);
                Enqueue(() => ChatReceived?.Invoke(senderId, text));
                break;
            }
            case Protocol.MessageKind.SceneChanged:
            {
                var senderId = reader.ReadInt32();
                var scene = Protocol.ReadBoundedString(reader, Protocol.MaxSceneLength);
                if (_peers.TryGetValue(senderId, out var peer))
                    peer.SceneName = scene;
                Enqueue(() => SceneChanged?.Invoke(senderId, scene));
                break;
            }
            case Protocol.MessageKind.Ping:
                SendControl(_hostUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.Pong));
                break;
            case Protocol.MessageKind.Disconnect:
                StopInternal("Server closed the EOS session", notify: true);
                break;
        }
    }

    private void HandleMovementTransport(IntPtr remoteUserId, byte[] bytes)
    {
        if (!_sessionReady || !Protocol.TryReadUdp(bytes, out var packet))
            return;
        if (packet.Kind != Protocol.UdpKind.PlayerSnapshot || packet.SessionId != SessionId)
            return;

        if (Mode == SessionMode.Host)
        {
            if (!_hostPeersByUser.TryGetValue(remoteUserId, out var sender))
                return;
            if (packet.SenderId != sender.Id)
                return;

            var snapshot = packet.Snapshot;
            Enqueue(() => SnapshotReceived?.Invoke(snapshot));
            foreach (var target in _hostPeersById.Values.ToArray())
            {
                if (target.Id != sender.Id)
                    SendMovement(target.ProductUserId, bytes);
            }
        }
        else if (Mode == SessionMode.Client && remoteUserId == _hostUserId && packet.SenderId != LocalPlayerId)
        {
            var snapshot = packet.Snapshot;
            Enqueue(() => SnapshotReceived?.Invoke(snapshot));
        }
    }

    private bool SendControl(IntPtr remoteUserId, byte[] frame, bool disableAutoAccept = true)
    {
        if (remoteUserId == IntPtr.Zero || frame == null || frame.Length == 0 || frame.Length > Protocol.MaxFrameBytes)
            return false;

        var messageId = NextControlMessageId();
        var fragmentCount = (frame.Length + ControlFragmentPayloadBytes - 1) / ControlFragmentPayloadBytes;
        if (fragmentCount <= 0 || fragmentCount > byte.MaxValue)
            return false;

        for (var index = 0; index < fragmentCount; index++)
        {
            var offset = index * ControlFragmentPayloadBytes;
            var count = Math.Min(ControlFragmentPayloadBytes, frame.Length - offset);
            var packet = new byte[ControlHeaderBytes + count];
            using (var stream = new MemoryStream(packet, writable: true))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(ControlMagic);
                writer.Write(messageId);
                writer.Write((byte)index);
                writer.Write((byte)fragmentCount);
                writer.Write(frame, offset, count);
            }

            var result = _eos.Send(
                remoteUserId,
                ControlChannel,
                packet,
                EosNative.PacketReliability.ReliableOrdered,
                disableAutoAccept);
            if (result != EosNative.Result.Success)
            {
                _log($"EOS reliable send failed: {result}");
                return false;
            }
        }

        return true;
    }

    private bool TryReassembleControl(IntPtr remoteUserId, byte[] packet, out byte[] frame)
    {
        frame = null;
        if (packet == null || packet.Length < ControlHeaderBytes)
            return false;

        using var stream = new MemoryStream(packet, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != ControlMagic)
            return false;

        var messageId = reader.ReadUInt16();
        var fragmentIndex = reader.ReadByte();
        var fragmentCount = reader.ReadByte();
        if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
            return false;

        var payloadLength = packet.Length - ControlHeaderBytes;
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
            Buffer.BlockCopy(packet, ControlHeaderBytes, payload, 0, payloadLength);

        var key = (remoteUserId, messageId);
        if (!_controlAssemblies.TryGetValue(key, out var assembly) || assembly.Fragments.Length != fragmentCount)
        {
            assembly = new ControlAssembly(fragmentCount);
            _controlAssemblies[key] = assembly;
        }

        if (assembly.Fragments[fragmentIndex] == null)
        {
            assembly.Fragments[fragmentIndex] = payload;
            assembly.Received++;
        }
        if (assembly.Received != fragmentCount)
            return false;

        var total = 0;
        foreach (var fragment in assembly.Fragments)
        {
            if (fragment == null)
                return false;
            total += fragment.Length;
            if (total > Protocol.MaxFrameBytes)
            {
                _controlAssemblies.Remove(key);
                return false;
            }
        }

        frame = new byte[total];
        var destination = 0;
        foreach (var fragment in assembly.Fragments)
        {
            Buffer.BlockCopy(fragment, 0, frame, destination, fragment.Length);
            destination += fragment.Length;
        }
        _controlAssemblies.Remove(key);
        return true;
    }

    private void SendMovement(IntPtr remoteUserId, byte[] bytes)
    {
        var result = _eos.Send(
            remoteUserId,
            MovementChannel,
            bytes,
            EosNative.PacketReliability.UnreliableUnordered,
            disableAutoAccept: true);
        if (result != EosNative.Result.Success && result != EosNative.Result.NoConnection)
            _log($"EOS movement send failed: {result}");
    }

    private void BroadcastControl(byte[] frame, int exceptPlayerId = -1)
    {
        foreach (var connection in _hostPeersById.Values.ToArray())
        {
            if (connection.Id != exceptPlayerId)
                SendControl(connection.ProductUserId, frame);
        }
    }

    private void SendReject(IntPtr remoteUserId, string reason)
    {
        SendControl(remoteUserId, Protocol.BuildTcpFrame(Protocol.MessageKind.Reject, writer => writer.Write(reason)));
    }

    private void RemoveHostPeer(int id, bool closeConnection)
    {
        if (!_hostPeersById.TryGetValue(id, out var connection))
            return;

        _hostPeersById.Remove(id);
        _hostPeersByUser.Remove(connection.ProductUserId);
        _peers.TryRemove(id, out _);
        RemoveControlAssembliesFor(connection.ProductUserId);
        if (closeConnection)
            _eos.CloseConnection(connection.ProductUserId);

        var left = Protocol.BuildTcpFrame(Protocol.MessageKind.PeerLeft, writer => writer.Write(id));
        BroadcastControl(left, exceptPlayerId: id);
        Enqueue(() => PeerLeft?.Invoke(id));
    }

    private void RemoveControlAssembliesFor(IntPtr remoteUserId)
    {
        var keys = _controlAssemblies.Keys.Where(x => x.RemoteUser == remoteUserId).ToArray();
        foreach (var key in keys)
            _controlAssemblies.Remove(key);
    }

    private void FailPendingSession(string status)
    {
        _log(status);
        StopInternal(status, notify: true);
    }

    private void StopInternal(string status, bool notify)
    {
        var oldMode = Mode;
        var oldLobbyId = _lobbyId;
        var oldHostUserId = _hostUserId;
        var remoteUsers = _hostPeersByUser.Keys.ToArray();
        var wasActive = oldMode != SessionMode.Offline;

        Mode = SessionMode.Offline;
        _sessionReady = false;
        LocalPlayerId = 0;
        SessionId = Guid.Empty;
        _hostUserId = IntPtr.Zero;
        _lobbyId = string.Empty;
        _nextPlayerId = 1;
        _peers.Clear();
        _hostPeersById.Clear();
        _hostPeersByUser.Clear();
        _controlAssemblies.Clear();

        if (wasActive && _eos.IsLoggedIn)
        {
            if (oldMode == SessionMode.Host && !string.IsNullOrWhiteSpace(oldLobbyId))
                _eos.DestroyLobby(oldLobbyId);
            else if (oldMode == SessionMode.Client && !string.IsNullOrWhiteSpace(oldLobbyId))
                _eos.LeaveLobby(oldLobbyId);

            if (oldHostUserId != IntPtr.Zero)
                _eos.CloseConnection(oldHostUserId);
            foreach (var remote in remoteUsers)
                _eos.CloseConnection(remote);
        }

        SetStatus(status);
        if (notify && wasActive)
            Enqueue(() => SessionEnded?.Invoke());
    }

    private void SetStatus(string status)
    {
        StatusText = status ?? string.Empty;
        Enqueue(() => StatusChanged?.Invoke(StatusText));
    }

    private void Enqueue(Action action)
    {
        if (action != null)
            _mainThread.Enqueue(action);
    }

    private ushort NextControlMessageId()
    {
        unchecked
        {
            _nextControlMessageId++;
            if (_nextControlMessageId == 0)
                _nextControlMessageId++;
            return _nextControlMessageId;
        }
    }

    private static string GenerateServerCode()
    {
        var chars = new char[7];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = LobbyAlphabet[RandomNumberGenerator.GetInt32(LobbyAlphabet.Length)];
        return new string(chars);
    }

    private static string NormalizeServerCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;
        var builder = new StringBuilder(7);
        foreach (var c in code.Trim().ToUpperInvariant())
        {
            if (!char.IsWhiteSpace(c) && c != '-')
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool IsValidServerCode(string code)
    {
        if (code == null || code.Length != 7)
            return false;
        foreach (var c in code)
        {
            if (LobbyAlphabet.IndexOf(c) < 0)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        StopInternal("Offline", notify: false);
        _eos.Dispose();
        _disposed = true;
    }

    private sealed class HostPeer
    {
        internal HostPeer(int id, string username, string modVersion, IntPtr productUserId)
        {
            Id = id;
            Username = username;
            ModVersion = modVersion;
            ProductUserId = productUserId;
        }

        internal int Id { get; }
        internal string Username { get; }
        internal string ModVersion { get; }
        internal IntPtr ProductUserId { get; }
    }

    private sealed class ControlAssembly
    {
        internal ControlAssembly(int fragmentCount)
        {
            Fragments = new byte[fragmentCount][];
        }

        internal byte[][] Fragments { get; }
        internal int Received { get; set; }
    }
}
