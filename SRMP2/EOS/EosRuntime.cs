using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SRMP2.Networking;

namespace SRMP2.EOS;

internal sealed class EosRuntime : IDisposable
{
    private static IntPtr _nativeLibraryHandle;

    private readonly Action<string> _log;
    private readonly List<Action<bool, string>> _loginWaiters = new();
    private readonly EosSocketId _socketId = new("SRMP2");

    private IntPtr _platform;
    private IntPtr _connect;
    private IntPtr _lobby;
    private IntPtr _p2p;
    private bool _loginInProgress;
    private bool _disposed;
    private string _loginDisplayName = "Rancher";

    private EosNative.CreateDeviceIdCallback _createDeviceIdCallback;
    private EosNative.LoginCallback _loginCallback;
    private EosNative.CreateUserCallback _createUserCallback;
    private EosNative.LobbyResultCallback _createLobbyCallback;
    private EosNative.LobbyResultCallback _joinLobbyCallback;
    private EosNative.LobbyResultCallback _cleanupLobbyCallback;
    private EosNative.IncomingConnectionRequestCallback _connectionRequestCallback;
    private EosNative.RemoteConnectionClosedCallback _connectionClosedCallback;
    private ulong _connectionRequestNotification;
    private ulong _connectionClosedNotification;

    internal EosRuntime(Action<string> log)
    {
        _log = log ?? (_ => { });
    }

    internal bool IsInitialized => _platform != IntPtr.Zero;
    internal bool IsLoggedIn => LocalUserId != IntPtr.Zero;
    internal IntPtr LocalUserId { get; private set; }

    internal bool Initialize(out string error)
    {
        error = string.Empty;
        if (_disposed)
        {
            error = "EOS runtime is disposed.";
            return false;
        }
        if (IsInitialized)
            return true;

        var settings = EosSettings.Load();
        if (settings == null || !settings.IsComplete)
        {
            error = "EOS settings are incomplete.";
            return false;
        }

        try
        {
            EnsureNativeLibraryLoaded();

            using var productName = new EosUtf8("SRMP2");
            using var productVersion = new EosUtf8(BuildInfo.Version);
            var initializeOptions = new EosNative.InitializeOptions
            {
                ApiVersion = 4,
                ProductName = productName.Pointer,
                ProductVersion = productVersion.Pointer
            };

            var initializeResult = EosNative.EOS_Initialize(ref initializeOptions);
            if (initializeResult != EosNative.Result.Success && initializeResult != EosNative.Result.AlreadyConfigured)
            {
                error = $"EOS_Initialize failed: {initializeResult}";
                return false;
            }

            var cacheDirectory = Path.Combine(EosSettings.ConfigDirectory, "EOSCache");
            Directory.CreateDirectory(cacheDirectory);

            using var productId = new EosUtf8(settings.ProductId);
            using var sandboxId = new EosUtf8(settings.SandboxId);
            using var deploymentId = new EosUtf8(settings.DeploymentId);
            using var clientId = new EosUtf8(settings.ClientId);
            using var clientSecret = new EosUtf8(settings.ClientSecret);
            using var cacheDir = new EosUtf8(cacheDirectory);

            var platformOptions = new EosNative.PlatformOptions
            {
                ApiVersion = 14,
                ProductId = productId.Pointer,
                SandboxId = sandboxId.Pointer,
                DeploymentId = deploymentId.Pointer,
                ClientCredentials = new EosNative.ClientCredentials
                {
                    ClientId = clientId.Pointer,
                    ClientSecret = clientSecret.Pointer
                },
                IsServer = 0,
                Flags = 0x00002UL, // EOS_PF_DISABLE_OVERLAY
                CacheDirectory = cacheDir.Pointer,
                TickBudgetInMilliseconds = 0
            };

            _platform = EosNative.EOS_Platform_Create(ref platformOptions);
            if (_platform == IntPtr.Zero)
            {
                error = "EOS_Platform_Create returned null.";
                return false;
            }

            _connect = EosNative.EOS_Platform_GetConnectInterface(_platform);
            _lobby = EosNative.EOS_Platform_GetLobbyInterface(_platform);
            _p2p = EosNative.EOS_Platform_GetP2PInterface(_platform);
            if (_connect == IntPtr.Zero || _lobby == IntPtr.Zero || _p2p == IntPtr.Zero)
            {
                error = "EOS platform did not expose Connect, Lobby, and P2P interfaces.";
                ReleasePlatform();
                return false;
            }

            var relayOptions = new EosNative.SetRelayControlOptions
            {
                ApiVersion = 1,
                RelayControl = EosNative.RelayControl.AllowRelays
            };
            var relayResult = EosNative.EOS_P2P_SetRelayControl(_p2p, ref relayOptions);
            if (relayResult != EosNative.Result.Success)
                _log($"EOS relay configuration returned {relayResult}; direct P2P may still work.");

            _log("EOS platform initialized for SRMP2.");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"EOSSDK-Win64-Shipping.dll was not found: {ex.Message}";
            ReleasePlatform();
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"EOS SDK is incompatible with SRMP2: {ex.Message}";
            ReleasePlatform();
            return false;
        }
        catch (Exception ex)
        {
            error = $"EOS initialization failed: {ex.Message}";
            ReleasePlatform();
            return false;
        }
    }

    private static void EnsureNativeLibraryLoaded()
    {
        if (_nativeLibraryHandle != IntPtr.Zero)
            return;

        if (NativeLibrary.TryLoad(EosNative.LibraryName, out _nativeLibraryHandle))
            return;

        var gamePluginPath = Path.Combine(
            AppContext.BaseDirectory,
            "SlimeRancher2_Data",
            "Plugins",
            "x86_64",
            EosNative.LibraryName);

        if (File.Exists(gamePluginPath) && NativeLibrary.TryLoad(gamePluginPath, out _nativeLibraryHandle))
            return;

        // Let the following DllImport call raise the normal DllNotFoundException,
        // which is converted into a useful SRMP2 status message by Initialize().
        _nativeLibraryHandle = IntPtr.Zero;
    }

    internal void Tick()
    {
        if (_platform != IntPtr.Zero)
            EosNative.EOS_Platform_Tick(_platform);
    }

    internal void EnsureLoggedIn(string displayName, Action<bool, string> completion)
    {
        completion ??= (_, _) => { };
        if (IsLoggedIn)
        {
            completion(true, string.Empty);
            return;
        }

        if (!Initialize(out var error))
        {
            completion(false, error);
            return;
        }

        _loginWaiters.Add(completion);
        if (_loginInProgress)
            return;

        _loginDisplayName = Protocol.CleanUsername(displayName);
        _loginInProgress = true;

        using var deviceModel = new EosUtf8("PC Windows");
        var options = new EosNative.CreateDeviceIdOptions
        {
            ApiVersion = 1,
            DeviceModel = deviceModel.Pointer
        };

        _createDeviceIdCallback = OnCreateDeviceId;
        EosNative.EOS_Connect_CreateDeviceId(_connect, ref options, IntPtr.Zero, _createDeviceIdCallback);
    }

    private void OnCreateDeviceId(ref EosNative.CreateDeviceIdCallbackInfo data)
    {
        if (data.ResultCode != EosNative.Result.Success && data.ResultCode != EosNative.Result.DuplicateNotAllowed)
        {
            FinishLogin(false, $"EOS CreateDeviceId failed: {data.ResultCode}");
            return;
        }

        BeginDeviceLogin();
    }

    private void BeginDeviceLogin()
    {
        using var displayName = new EosUtf8(_loginDisplayName);
        var credentials = new EosNative.ConnectCredentials
        {
            ApiVersion = 1,
            Token = IntPtr.Zero,
            Type = EosNative.ExternalCredentialType.DeviceidAccessToken
        };
        var userInfo = new EosNative.UserLoginInfo
        {
            ApiVersion = 2,
            DisplayName = displayName.Pointer,
            NsaIdToken = IntPtr.Zero
        };

        var credentialsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<EosNative.ConnectCredentials>());
        var userInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<EosNative.UserLoginInfo>());
        try
        {
            Marshal.StructureToPtr(credentials, credentialsPtr, false);
            Marshal.StructureToPtr(userInfo, userInfoPtr, false);
            var options = new EosNative.LoginOptions
            {
                ApiVersion = 2,
                Credentials = credentialsPtr,
                UserLoginInfo = userInfoPtr
            };

            _loginCallback = OnLogin;
            EosNative.EOS_Connect_Login(_connect, ref options, IntPtr.Zero, _loginCallback);
        }
        finally
        {
            Marshal.FreeHGlobal(credentialsPtr);
            Marshal.FreeHGlobal(userInfoPtr);
        }
    }

    private void OnLogin(ref EosNative.LoginCallbackInfo data)
    {
        if (data.ResultCode == EosNative.Result.Success && data.LocalUserId != IntPtr.Zero)
        {
            LocalUserId = data.LocalUserId;
            FinishLogin(true, string.Empty);
            return;
        }

        if (data.ResultCode == EosNative.Result.InvalidUser && data.ContinuanceToken != IntPtr.Zero)
        {
            var options = new EosNative.CreateUserOptions
            {
                ApiVersion = 1,
                ContinuanceToken = data.ContinuanceToken
            };
            _createUserCallback = OnCreateUser;
            EosNative.EOS_Connect_CreateUser(_connect, ref options, IntPtr.Zero, _createUserCallback);
            return;
        }

        FinishLogin(false, $"EOS Connect.Login failed: {data.ResultCode}");
    }

    private void OnCreateUser(ref EosNative.CreateUserCallbackInfo data)
    {
        if (data.ResultCode == EosNative.Result.Success && data.LocalUserId != IntPtr.Zero)
        {
            LocalUserId = data.LocalUserId;
            FinishLogin(true, string.Empty);
            return;
        }

        FinishLogin(false, $"EOS Connect.CreateUser failed: {data.ResultCode}");
    }

    private void FinishLogin(bool success, string error)
    {
        _loginInProgress = false;
        if (success)
            _log("EOS Device ID login complete.");

        var callbacks = _loginWaiters.ToArray();
        _loginWaiters.Clear();
        foreach (var callback in callbacks)
        {
            try { callback(success, error); }
            catch (Exception ex) { _log($"EOS login callback failed: {ex.Message}"); }
        }
    }

    internal void CreateLobby(string code, Action<EosNative.Result, string> completion)
    {
        if (!IsLoggedIn)
        {
            completion?.Invoke(EosNative.Result.InvalidUser, string.Empty);
            return;
        }

        using var bucket = new EosUtf8("SRMP2");
        using var lobbyId = new EosUtf8(code);
        var options = new EosNative.CreateLobbyOptions
        {
            ApiVersion = 9,
            LocalUserId = LocalUserId,
            MaxLobbyMembers = Protocol.MaxPlayers,
            PermissionLevel = EosNative.LobbyPermissionLevel.Joinviapresence,
            PresenceEnabled = 0,
            AllowInvites = 1,
            BucketId = bucket.Pointer,
            DisableHostMigration = 1,
            EnableRTCRoom = 0,
            LocalRTCOptions = IntPtr.Zero,
            LobbyId = lobbyId.Pointer,
            EnableJoinById = 1,
            RejoinAfterKickRequiresInvite = 1,
            AllowedPlatformIds = IntPtr.Zero,
            AllowedPlatformIdsCount = 0,
            CrossplayOptOut = 0
        };

        _createLobbyCallback = (ref EosNative.LobbyResultCallbackInfo data) =>
        {
            var id = EosNative.PtrToUtf8(data.LobbyId);
            completion?.Invoke(data.ResultCode, id);
        };
        EosNative.EOS_Lobby_CreateLobby(_lobby, ref options, IntPtr.Zero, _createLobbyCallback);
    }

    internal void JoinLobby(string code, Action<EosNative.Result, string> completion)
    {
        if (!IsLoggedIn)
        {
            completion?.Invoke(EosNative.Result.InvalidUser, string.Empty);
            return;
        }

        using var lobbyId = new EosUtf8(code);
        var options = new EosNative.JoinLobbyByIdOptions
        {
            ApiVersion = 2,
            LobbyId = lobbyId.Pointer,
            LocalUserId = LocalUserId,
            PresenceEnabled = 0,
            LocalRTCOptions = IntPtr.Zero,
            CrossplayOptOut = 0
        };

        _joinLobbyCallback = (ref EosNative.LobbyResultCallbackInfo data) =>
        {
            var id = EosNative.PtrToUtf8(data.LobbyId);
            completion?.Invoke(data.ResultCode, id);
        };
        EosNative.EOS_Lobby_JoinLobbyById(_lobby, ref options, IntPtr.Zero, _joinLobbyCallback);
    }

    internal bool TryGetLobbyOwner(string lobbyId, out IntPtr owner)
    {
        owner = IntPtr.Zero;
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(lobbyId))
            return false;

        using var id = new EosUtf8(lobbyId);
        var copy = new EosNative.CopyLobbyDetailsHandleOptions
        {
            ApiVersion = 1,
            LobbyId = id.Pointer,
            LocalUserId = LocalUserId
        };
        var result = EosNative.EOS_Lobby_CopyLobbyDetailsHandle(_lobby, ref copy, out var details);
        if (result != EosNative.Result.Success || details == IntPtr.Zero)
            return false;

        try
        {
            var options = new EosNative.LobbyDetailsGetLobbyOwnerOptions { ApiVersion = 1 };
            owner = EosNative.EOS_LobbyDetails_GetLobbyOwner(details, ref options);
            return owner != IntPtr.Zero;
        }
        finally
        {
            EosNative.EOS_LobbyDetails_Release(details);
        }
    }

    internal bool IsLobbyMember(string lobbyId, IntPtr productUserId)
    {
        if (!IsLoggedIn || productUserId == IntPtr.Zero || string.IsNullOrWhiteSpace(lobbyId))
            return false;

        using var id = new EosUtf8(lobbyId);
        var copy = new EosNative.CopyLobbyDetailsHandleOptions
        {
            ApiVersion = 1,
            LobbyId = id.Pointer,
            LocalUserId = LocalUserId
        };
        var result = EosNative.EOS_Lobby_CopyLobbyDetailsHandle(_lobby, ref copy, out var details);
        if (result != EosNative.Result.Success || details == IntPtr.Zero)
            return false;

        try
        {
            var countOptions = new EosNative.LobbyDetailsGetMemberCountOptions { ApiVersion = 1 };
            var count = EosNative.EOS_LobbyDetails_GetMemberCount(details, ref countOptions);
            for (uint i = 0; i < count; i++)
            {
                var memberOptions = new EosNative.LobbyDetailsGetMemberByIndexOptions
                {
                    ApiVersion = 1,
                    MemberIndex = i
                };
                if (EosNative.EOS_LobbyDetails_GetMemberByIndex(details, ref memberOptions) == productUserId)
                    return true;
            }
            return false;
        }
        finally
        {
            EosNative.EOS_LobbyDetails_Release(details);
        }
    }

    internal void DestroyLobby(string lobbyId)
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(lobbyId))
            return;

        using var id = new EosUtf8(lobbyId);
        var options = new EosNative.DestroyLobbyOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            LobbyId = id.Pointer
        };
        _cleanupLobbyCallback ??= IgnoreLobbyCleanup;
        EosNative.EOS_Lobby_DestroyLobby(_lobby, ref options, IntPtr.Zero, _cleanupLobbyCallback);
    }

    internal void LeaveLobby(string lobbyId)
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(lobbyId))
            return;

        using var id = new EosUtf8(lobbyId);
        var options = new EosNative.LeaveLobbyOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            LobbyId = id.Pointer
        };
        _cleanupLobbyCallback ??= IgnoreLobbyCleanup;
        EosNative.EOS_Lobby_LeaveLobby(_lobby, ref options, IntPtr.Zero, _cleanupLobbyCallback);
    }

    private static void IgnoreLobbyCleanup(ref EosNative.LobbyResultCallbackInfo data)
    {
    }

    internal void RegisterP2PCallbacks(Action<IntPtr> connectionRequest, Action<IntPtr, int> connectionClosed)
    {
        RemoveP2PCallbacks();
        if (!IsLoggedIn)
            return;

        _connectionRequestCallback = (ref EosNative.IncomingConnectionRequestInfo info) =>
            connectionRequest?.Invoke(info.RemoteUserId);
        var requestOptions = new EosNative.AddNotifyPeerConnectionRequestOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            SocketId = _socketId.Pointer
        };
        _connectionRequestNotification = EosNative.EOS_P2P_AddNotifyPeerConnectionRequest(
            _p2p, ref requestOptions, IntPtr.Zero, _connectionRequestCallback);

        _connectionClosedCallback = (ref EosNative.RemoteConnectionClosedInfo info) =>
            connectionClosed?.Invoke(info.RemoteUserId, info.Reason);
        var closeOptions = new EosNative.AddNotifyPeerConnectionClosedOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            SocketId = _socketId.Pointer
        };
        _connectionClosedNotification = EosNative.EOS_P2P_AddNotifyPeerConnectionClosed(
            _p2p, ref closeOptions, IntPtr.Zero, _connectionClosedCallback);
    }

    internal EosNative.Result AcceptConnection(IntPtr remoteUserId)
    {
        if (!IsLoggedIn || remoteUserId == IntPtr.Zero)
            return EosNative.Result.InvalidParameters;

        var options = new EosNative.AcceptConnectionOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            RemoteUserId = remoteUserId,
            SocketId = _socketId.Pointer
        };
        return EosNative.EOS_P2P_AcceptConnection(_p2p, ref options);
    }

    internal EosNative.Result CloseConnection(IntPtr remoteUserId)
    {
        if (!IsLoggedIn || remoteUserId == IntPtr.Zero)
            return EosNative.Result.InvalidParameters;

        var options = new EosNative.CloseConnectionOptions
        {
            ApiVersion = 1,
            LocalUserId = LocalUserId,
            RemoteUserId = remoteUserId,
            SocketId = _socketId.Pointer
        };
        return EosNative.EOS_P2P_CloseConnection(_p2p, ref options);
    }

    internal EosNative.Result Send(
        IntPtr remoteUserId,
        byte channel,
        byte[] data,
        EosNative.PacketReliability reliability,
        bool disableAutoAccept = true)
    {
        if (!IsLoggedIn || remoteUserId == IntPtr.Zero || data == null || data.Length == 0)
            return EosNative.Result.InvalidParameters;
        if (data.Length > EosNative.MaxP2PPacketSize)
            return EosNative.Result.LimitExceeded;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var options = new EosNative.SendPacketOptions
            {
                ApiVersion = 3,
                LocalUserId = LocalUserId,
                RemoteUserId = remoteUserId,
                SocketId = _socketId.Pointer,
                Channel = channel,
                DataLengthBytes = (uint)data.Length,
                Data = handle.AddrOfPinnedObject(),
                AllowDelayedDelivery = 0,
                Reliability = reliability,
                DisableAutoAcceptConnection = disableAutoAccept ? 1 : 0
            };
            return EosNative.EOS_P2P_SendPacket(_p2p, ref options);
        }
        finally
        {
            handle.Free();
        }
    }

    internal bool TryReceive(out IntPtr remoteUserId, out byte channel, out byte[] data)
    {
        remoteUserId = IntPtr.Zero;
        channel = 0;
        data = null;
        if (!IsLoggedIn)
            return false;

        var sizeOptions = new EosNative.GetNextReceivedPacketSizeOptions
        {
            ApiVersion = 2,
            LocalUserId = LocalUserId,
            RequestedChannel = IntPtr.Zero
        };
        var sizeResult = EosNative.EOS_P2P_GetNextReceivedPacketSize(_p2p, ref sizeOptions, out var packetSize);
        if (sizeResult == EosNative.Result.NotFound)
            return false;
        if (sizeResult != EosNative.Result.Success || packetSize == 0 || packetSize > EosNative.MaxP2PPacketSize)
            return false;

        var buffer = new byte[(int)packetSize];
        var socketBuffer = Marshal.AllocHGlobal(sizeof(int) + 33);
        try
        {
            for (var i = 0; i < sizeof(int) + 33; i++)
                Marshal.WriteByte(socketBuffer, i, 0);
            Marshal.WriteInt32(socketBuffer, 0, 1); // EOS_P2P_SOCKETID_API_LATEST

            var receiveOptions = new EosNative.ReceivePacketOptions
            {
                ApiVersion = 2,
                LocalUserId = LocalUserId,
                MaxDataSizeBytes = packetSize,
                RequestedChannel = IntPtr.Zero
            };
            var receiveResult = EosNative.EOS_P2P_ReceivePacket(
                _p2p,
                ref receiveOptions,
                out remoteUserId,
                socketBuffer,
                out channel,
                buffer,
                out var bytesWritten);

            if (receiveResult != EosNative.Result.Success || remoteUserId == IntPtr.Zero || bytesWritten == 0)
                return false;
            if (bytesWritten > buffer.Length)
                return false;
            if (bytesWritten != buffer.Length)
                Array.Resize(ref buffer, (int)bytesWritten);

            data = buffer;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(socketBuffer);
        }
    }

    private void RemoveP2PCallbacks()
    {
        if (_p2p == IntPtr.Zero)
            return;

        if (_connectionRequestNotification != EosNative.InvalidNotificationId)
        {
            EosNative.EOS_P2P_RemoveNotifyPeerConnectionRequest(_p2p, _connectionRequestNotification);
            _connectionRequestNotification = EosNative.InvalidNotificationId;
        }
        if (_connectionClosedNotification != EosNative.InvalidNotificationId)
        {
            EosNative.EOS_P2P_RemoveNotifyPeerConnectionClosed(_p2p, _connectionClosedNotification);
            _connectionClosedNotification = EosNative.InvalidNotificationId;
        }
    }

    private void ReleasePlatform()
    {
        RemoveP2PCallbacks();
        LocalUserId = IntPtr.Zero;
        _connect = IntPtr.Zero;
        _lobby = IntPtr.Zero;
        _p2p = IntPtr.Zero;

        if (_platform != IntPtr.Zero)
        {
            EosNative.EOS_Platform_Release(_platform);
            _platform = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleasePlatform();
        _socketId.Dispose();

        // Never call EOS_Shutdown here: Slime Rancher 2 itself also uses EOS.
        // Do not free _nativeLibraryHandle either; the game may share that module.
    }
}
