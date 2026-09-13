using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly ConcurrentDictionary<int, PeerInfo> _peers = new();
    private readonly ConcurrentDictionary<int, HostConnection> _hostConnections = new();
    private readonly SemaphoreSlim _clientSendLock = new(1, 1);
    private readonly SemaphoreSlim _udpSendLock = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource _cancellation;
    private TcpListener _listener;
    private TcpClient _client;
    private NetworkStream _clientStream;
    private UdpClient _udp;
    private IPEndPoint _serverUdpEndpoint;
    private int _nextPlayerId = 1;
    private string _localUsername = "Rancher";
    private float _nextUdpHelloTime;

    public NetworkSession(Action<string> logger)
    {
        _log = logger ?? (_ => { });
    }

    public SessionMode Mode { get; private set; } = SessionMode.Offline;
    public string StatusText { get; private set; } = "Offline";
    public int LocalPlayerId { get; private set; }
    public Guid SessionId { get; private set; } = Guid.Empty;
    public int Port { get; private set; } = Protocol.DefaultPort;
    public bool IsConnected => Mode == SessionMode.Host || (Mode == SessionMode.Client && LocalPlayerId > 0);
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

        if (Mode == SessionMode.Client && LocalPlayerId > 0 && UnityEngine.Time.unscaledTime >= _nextUdpHelloTime)
        {
            _nextUdpHelloTime = UnityEngine.Time.unscaledTime + 2f;
            SendClientUdpHello();
        }
    }

    public bool TryGetPeer(int id, out PeerInfo peer) => _peers.TryGetValue(id, out peer);

    public void StartHost(string username, int port)
    {
        username = Protocol.CleanUsername(username);
        port = NormalizePort(port);
        StopInternal("Restarting session", notify: false);

        try
        {
            var cts = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Any, port);
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            listener.Start(Protocol.MaxPlayers);

            lock (_stateLock)
            {
                _cancellation = cts;
                _listener = listener;
                _udp = udp;
                Mode = SessionMode.Host;
                Port = port;
                LocalPlayerId = 1;
                SessionId = Guid.NewGuid();
                _nextPlayerId = 1;
                _localUsername = username;
                _peers.Clear();
                _peers[LocalPlayerId] = new PeerInfo(LocalPlayerId, username);
            }

            SetStatus($"Hosting on TCP/UDP {port}");
            _ = Task.Run(() => HostAcceptLoop(cts.Token));
            _ = Task.Run(() => HostUdpLoop(cts.Token));
        }
        catch (Exception ex)
        {
            _log($"Could not start host: {ex}");
            StopInternal("Host failed", notify: true);
            SetStatus($"Host failed: {ex.Message}");
        }
    }

    public void Join(string host, int port, string username)
    {
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        username = Protocol.CleanUsername(username);
        port = NormalizePort(port);
        StopInternal("Restarting session", notify: false);

        var cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            _cancellation = cts;
            Mode = SessionMode.Client;
            Port = port;
            LocalPlayerId = 0;
            SessionId = Guid.Empty;
            _localUsername = username;
            _peers.Clear();
        }

        SetStatus($"Connecting to {host}:{port}...");
        _ = Task.Run(() => ConnectClient(host, port, username, cts.Token));
    }

    public void Disconnect() => StopInternal("Disconnected", notify: true);

    public void SendSnapshot(PlayerSnapshot snapshot)
    {
        if (!IsConnected || snapshot.PlayerId != LocalPlayerId || SessionId == Guid.Empty)
            return;

        var bytes = Protocol.BuildSnapshot(SessionId, snapshot);
        if (Mode == SessionMode.Host)
        {
            foreach (var connection in _hostConnections.Values)
            {
                var endpoint = connection.UdpEndpoint;
                if (endpoint != null)
                    _ = SendUdp(bytes, endpoint);
            }
        }
        else if (_serverUdpEndpoint != null)
        {
            _ = SendUdp(bytes, _serverUdpEndpoint);
        }
    }

    public void SendChat(string text)
    {
        text = Protocol.CleanChat(text);
        if (text.Length == 0 || !IsConnected)
            return;

        if (Mode == SessionMode.Host)
        {
            Enqueue(() => ChatReceived?.Invoke(LocalPlayerId, text));
            var frame = Protocol.BuildTcpFrame(Protocol.MessageKind.Chat, writer =>
            {
                writer.Write(LocalPlayerId);
                writer.Write(text);
            });
            Broadcast(frame);
        }
        else
        {
            SendClientFrame(Protocol.BuildTcpFrame(Protocol.MessageKind.Chat, writer => writer.Write(text)));
        }
    }

    public void SendScene(string sceneName)
    {
        if (!IsConnected)
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
            Broadcast(frame);
        }
        else
        {
            SendClientFrame(Protocol.BuildTcpFrame(Protocol.MessageKind.SceneChanged, writer => writer.Write(sceneName)));
        }
    }

    private async Task ConnectClient(string host, int port, string username, CancellationToken token)
    {
        try
        {
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                client.Close();
                return;
            }

            var stream = client.GetStream();
            var remote = (IPEndPoint)client.Client.RemoteEndPoint;
            var udp = new UdpClient(0);

            lock (_stateLock)
            {
                if (token.IsCancellationRequested)
                {
                    client.Close();
                    udp.Close();
                    return;
                }
                _client = client;
                _clientStream = stream;
                _udp = udp;
                _serverUdpEndpoint = new IPEndPoint(remote.Address, port);
            }

            var hello = Protocol.BuildTcpFrame(Protocol.MessageKind.Hello, writer =>
            {
                writer.Write(Protocol.Version);
                writer.Write(SRMP2.BuildInfo.Version);
                writer.Write(username);
            });
            await SendFrame(stream, _clientSendLock, hello, token).ConfigureAwait(false);

            _ = Task.Run(() => ClientUdpLoop(token));
            await ClientReadLoop(stream, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _log($"Client connection failed: {ex}");
                SetStatus($"Connection failed: {ex.Message}");
                StopInternal("Connection failed", notify: true);
            }
        }
    }

    private async Task ClientReadLoop(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await ReadFrame(stream, token).ConfigureAwait(false);
                if (frame == null)
                    break;
                HandleClientFrame(frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log($"Client receive loop ended: {ex}");
        }

        if (!token.IsCancellationRequested && Mode == SessionMode.Client)
        {
            SetStatus("Connection closed");
            StopInternal("Connection closed", notify: true);
        }
    }

    private void HandleClientFrame(byte[] frame)
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
                var session = new Guid(reader.ReadBytes(16));
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

                SetStatus($"Connected as {_localUsername} (#{assignedId})");
                Enqueue(() =>
                {
                    foreach (var peer in receivedPeers)
                    {
                        if (peer.Id != LocalPlayerId)
                            PeerJoined?.Invoke(peer);
                    }
                });
                SendClientUdpHello();
                break;
            }
            case Protocol.MessageKind.Reject:
            {
                var reason = Protocol.ReadBoundedString(reader, 256);
                SetStatus($"Rejected: {reason}");
                StopInternal("Rejected", notify: true);
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
                SendClientFrame(Protocol.BuildTcpFrame(Protocol.MessageKind.Pong));
                break;
            case Protocol.MessageKind.Disconnect:
                SetStatus("Server closed the session");
                StopInternal("Server closed", notify: true);
                break;
        }
    }

    private async Task HostAcceptLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => AcceptHostPeer(client, token));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _log($"Accept failed: {ex}");
            }
        }
    }

    private async Task AcceptHostPeer(TcpClient client, CancellationToken token)
    {
        HostConnection connection = null;
        try
        {
            var stream = client.GetStream();
            var helloFrame = await ReadFrame(stream, token).ConfigureAwait(false);
            if (helloFrame == null)
                return;

            using var helloStream = new MemoryStream(helloFrame, writable: false);
            using var reader = new BinaryReader(helloStream, Encoding.UTF8, leaveOpen: false);
            if ((Protocol.MessageKind)reader.ReadByte() != Protocol.MessageKind.Hello)
                throw new InvalidDataException("Expected hello packet.");

            var protocol = reader.ReadInt32();
            var modVersion = Protocol.ReadBoundedString(reader, 64);
            var username = Protocol.CleanUsername(Protocol.ReadBoundedString(reader, Protocol.MaxUsernameLength));

            if (protocol != Protocol.Version)
            {
                await SendRawReject(stream, $"Protocol mismatch. Host={Protocol.Version}, client={protocol}", token).ConfigureAwait(false);
                return;
            }
            if (!string.Equals(modVersion, SRMP2.BuildInfo.Version, StringComparison.Ordinal))
            {
                await SendRawReject(stream, $"SRMP2 version mismatch. Host={SRMP2.BuildInfo.Version}, client={modVersion}", token).ConfigureAwait(false);
                return;
            }
            if (_hostConnections.Count + 1 >= Protocol.MaxPlayers)
            {
                await SendRawReject(stream, "Server is full.", token).ConfigureAwait(false);
                return;
            }

            var id = Interlocked.Increment(ref _nextPlayerId);
            var peer = new PeerInfo(id, username);
            connection = new HostConnection(id, username, modVersion, client, stream);
            _hostConnections[id] = connection;
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
            await connection.Send(welcome, token).ConfigureAwait(false);

            var joined = Protocol.BuildTcpFrame(Protocol.MessageKind.PeerJoined, writer =>
            {
                writer.Write(id);
                writer.Write(username);
                writer.Write(peer.SceneName ?? string.Empty);
            });
            Broadcast(joined, exceptPlayerId: id);
            Enqueue(() => PeerJoined?.Invoke(peer));
            _log($"{username} (#{id}) connected using SRMP2 {modVersion}.");

            while (!token.IsCancellationRequested)
            {
                var frame = await ReadFrame(stream, token).ConfigureAwait(false);
                if (frame == null)
                    break;
                HandleHostFrame(connection, frame);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log($"Peer connection ended: {ex.Message}");
        }
        finally
        {
            if (connection != null)
                RemoveHostPeer(connection.Id);
            try { client.Close(); } catch { }
        }
    }

    private void HandleHostFrame(HostConnection connection, byte[] frame)
    {
        using var stream = new MemoryStream(frame, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var kind = (Protocol.MessageKind)reader.ReadByte();

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
                Broadcast(outgoing);
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
                Broadcast(outgoing, exceptPlayerId: connection.Id);
                break;
            }
            case Protocol.MessageKind.Ping:
                _ = connection.Send(Protocol.BuildTcpFrame(Protocol.MessageKind.Pong), _cancellation?.Token ?? CancellationToken.None);
                break;
            case Protocol.MessageKind.Disconnect:
                connection.Close();
                break;
        }
    }

    private async Task HostUdpLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _log($"Host UDP receive failed: {ex.Message}");
                continue;
            }

            if (!Protocol.TryReadUdp(result.Buffer, out var packet) || packet.SessionId != SessionId)
                continue;
            if (!_hostConnections.TryGetValue(packet.SenderId, out var connection))
                continue;
            if (!IsSameRemoteAddress(connection, result.RemoteEndPoint))
                continue;

            if (packet.Kind == Protocol.UdpKind.Hello)
            {
                connection.UdpEndpoint = result.RemoteEndPoint;
                continue;
            }

            if (packet.Kind != Protocol.UdpKind.PlayerSnapshot)
                continue;

            if (connection.UdpEndpoint == null)
                connection.UdpEndpoint = result.RemoteEndPoint;
            else if (!connection.UdpEndpoint.Equals(result.RemoteEndPoint))
                continue;

            var snapshot = packet.Snapshot;
            Enqueue(() => SnapshotReceived?.Invoke(snapshot));

            foreach (var target in _hostConnections.Values)
            {
                if (target.Id == connection.Id || target.UdpEndpoint == null)
                    continue;
                _ = SendUdp(result.Buffer, target.UdpEndpoint);
            }
        }
    }

    private async Task ClientUdpLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    _log($"Client UDP receive failed: {ex.Message}");
                continue;
            }

            if (!Protocol.TryReadUdp(result.Buffer, out var packet))
                continue;
            if (SessionId == Guid.Empty || packet.SessionId != SessionId)
                continue;
            if (packet.Kind != Protocol.UdpKind.PlayerSnapshot || packet.SenderId == LocalPlayerId)
                continue;

            var snapshot = packet.Snapshot;
            Enqueue(() => SnapshotReceived?.Invoke(snapshot));
        }
    }

    private bool IsSameRemoteAddress(HostConnection connection, IPEndPoint udpEndpoint)
    {
        try
        {
            var tcpEndpoint = (IPEndPoint)connection.Client.Client.RemoteEndPoint;
            return tcpEndpoint.Address.Equals(udpEndpoint.Address)
                   || tcpEndpoint.Address.MapToIPv6().Equals(udpEndpoint.Address.MapToIPv6());
        }
        catch
        {
            return false;
        }
    }

    private void SendClientUdpHello()
    {
        if (Mode != SessionMode.Client || LocalPlayerId <= 0 || SessionId == Guid.Empty || _serverUdpEndpoint == null)
            return;
        var hello = Protocol.BuildUdpHello(SessionId, LocalPlayerId);
        _ = SendUdp(hello, _serverUdpEndpoint);
    }

    private async Task SendUdp(byte[] data, IPEndPoint endpoint)
    {
        var udp = _udp;
        if (udp == null || endpoint == null)
            return;

        try
        {
            await _udpSendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await udp.SendAsync(data, data.Length, endpoint).ConfigureAwait(false);
            }
            finally
            {
                _udpSendLock.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        catch (Exception ex)
        {
            _log($"UDP send failed: {ex.Message}");
        }
    }

    private void SendClientFrame(byte[] frame)
    {
        var stream = _clientStream;
        var token = _cancellation?.Token ?? CancellationToken.None;
        if (stream == null || Mode != SessionMode.Client)
            return;
        _ = SendFrameSafe(stream, _clientSendLock, frame, token);
    }

    private async Task SendFrameSafe(NetworkStream stream, SemaphoreSlim gate, byte[] frame, CancellationToken token)
    {
        try
        {
            await SendFrame(stream, gate, frame, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _log($"TCP send failed: {ex.Message}");
        }
    }

    private void Broadcast(byte[] frame, int exceptPlayerId = -1)
    {
        var token = _cancellation?.Token ?? CancellationToken.None;
        foreach (var connection in _hostConnections.Values)
        {
            if (connection.Id == exceptPlayerId)
                continue;
            _ = connection.Send(frame, token);
        }
    }

    private void RemoveHostPeer(int id)
    {
        if (!_hostConnections.TryRemove(id, out var connection))
            return;

        connection.Close();
        _peers.TryRemove(id, out _);
        var left = Protocol.BuildTcpFrame(Protocol.MessageKind.PeerLeft, writer => writer.Write(id));
        Broadcast(left, exceptPlayerId: id);
        Enqueue(() => PeerLeft?.Invoke(id));
    }

    private async Task SendRawReject(NetworkStream stream, string reason, CancellationToken token)
    {
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var frame = Protocol.BuildTcpFrame(Protocol.MessageKind.Reject, writer => writer.Write(reason));
            await SendFrame(stream, gate, frame, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }
    }

    private static async Task SendFrame(NetworkStream stream, SemaphoreSlim gate, byte[] frame, CancellationToken token)
    {
        if (frame.Length > Protocol.MaxFrameBytes)
            throw new InvalidDataException("Frame is too large.");

        var length = BitConverter.GetBytes(frame.Length);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(length, 0, length.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<byte[]> ReadFrame(NetworkStream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactly(stream, lengthBytes, token).ConfigureAwait(false))
            return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > Protocol.MaxFrameBytes)
            throw new InvalidDataException($"Invalid frame length: {length}.");

        var body = new byte[length];
        return await ReadExactly(stream, body, token).ConfigureAwait(false) ? body : null;
    }

    private static async Task<bool> ReadExactly(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    private void StopInternal(string status, bool notify)
    {
        SessionMode oldMode;
        CancellationTokenSource cts;
        TcpListener listener;
        TcpClient client;
        UdpClient udp;
        HostConnection[] hostConnections;

        lock (_stateLock)
        {
            oldMode = Mode;
            if (oldMode == SessionMode.Offline && _cancellation == null)
                return;

            Mode = SessionMode.Offline;
            LocalPlayerId = 0;
            SessionId = Guid.Empty;
            _serverUdpEndpoint = null;

            cts = _cancellation;
            listener = _listener;
            client = _client;
            udp = _udp;
            hostConnections = _hostConnections.Values.ToArray();

            _cancellation = null;
            _listener = null;
            _client = null;
            _clientStream = null;
            _udp = null;
            _hostConnections.Clear();
            _peers.Clear();
        }

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { client?.Close(); } catch { }
        try { udp?.Close(); } catch { }
        foreach (var connection in hostConnections)
            connection.Close();

        SetStatus(status);
        if (notify && oldMode != SessionMode.Offline)
            Enqueue(() => SessionEnded?.Invoke());
    }

    private void SetStatus(string status)
    {
        StatusText = status;
        Enqueue(() => StatusChanged?.Invoke(status));
    }

    private void Enqueue(Action action)
    {
        if (action != null)
            _mainThread.Enqueue(action);
    }

    private static int NormalizePort(int port)
        => port is > 0 and <= 65535 ? port : Protocol.DefaultPort;

    public void Dispose()
    {
        StopInternal("Offline", notify: false);
        _clientSendLock.Dispose();
        _udpSendLock.Dispose();
    }

    private sealed class HostConnection
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        internal HostConnection(int id, string username, string modVersion, TcpClient client, NetworkStream stream)
        {
            Id = id;
            Username = username;
            ModVersion = modVersion;
            Client = client;
            Stream = stream;
        }

        internal int Id { get; }
        internal string Username { get; }
        internal string ModVersion { get; }
        internal TcpClient Client { get; }
        internal NetworkStream Stream { get; }
        internal IPEndPoint UdpEndpoint { get; set; }

        internal Task Send(byte[] frame, CancellationToken token)
            => SendFrameSafeInternal(Stream, _sendLock, frame, token);

        private static async Task SendFrameSafeInternal(NetworkStream stream, SemaphoreSlim gate, byte[] frame, CancellationToken token)
        {
            try
            {
                await SendFrame(stream, gate, frame, token).ConfigureAwait(false);
            }
            catch
            {
                // The owning receive loop removes dead peers. Avoid tearing down the host from a send race.
            }
        }

        internal void Close()
        {
            try { Client.Close(); } catch { }
        }
    }
}
