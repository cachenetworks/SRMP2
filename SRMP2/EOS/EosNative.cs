using System;
using System.Runtime.InteropServices;

namespace SRMP2.EOS;

internal static class EosNative
{
    internal const string LibraryName = "EOSSDK-Win64-Shipping.dll";
    internal const ulong InvalidNotificationId = 0;
    internal const int MaxP2PPacketSize = 1170;

    internal enum Result : int
    {
        Success = 0,
        NoConnection = 1,
        InvalidCredentials = 2,
        InvalidUser = 3,
        InvalidAuth = 4,
        AccessDenied = 5,
        MissingPermissions = 6,
        TooManyRequests = 8,
        AlreadyPending = 9,
        InvalidParameters = 10,
        InvalidRequest = 11,
        IncompatibleVersion = 13,
        NotConfigured = 14,
        AlreadyConfigured = 15,
        NotImplemented = 16,
        Canceled = 17,
        NotFound = 18,
        OperationWillRetry = 19,
        NoChange = 20,
        VersionMismatch = 21,
        LimitExceeded = 22,
        Disabled = 23,
        DuplicateNotAllowed = 24,
        InvalidSandboxId = 26,
        TimedOut = 27,
        InvalidDeployment = 32,
        InvalidProduct = 33,
        InvalidProductUserID = 34,
        ServiceFailure = 35,
        CacheDirectoryMissing = 36,
        CacheDirectoryInvalid = 37,
        InvalidState = 38,
        RequestInProgress = 39,
        NetworkDisconnected = 41
    }

    internal enum ExternalCredentialType : int
    {
        DeviceidAccessToken = 10
    }

    internal enum LobbyPermissionLevel : int
    {
        Publicadvertised = 0,
        Joinviapresence = 1,
        Inviteonly = 2
    }

    internal enum PacketReliability : int
    {
        UnreliableUnordered = 0,
        ReliableUnordered = 1,
        ReliableOrdered = 2
    }

    internal enum RelayControl : int
    {
        NoRelays = 0,
        AllowRelays = 1,
        ForceRelays = 2
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct InitializeOptions
    {
        internal int ApiVersion;
        internal IntPtr AllocateMemoryFunction;
        internal IntPtr ReallocateMemoryFunction;
        internal IntPtr ReleaseMemoryFunction;
        internal IntPtr ProductName;
        internal IntPtr ProductVersion;
        internal IntPtr Reserved;
        internal IntPtr SystemInitializeOptions;
        internal IntPtr OverrideThreadAffinity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ClientCredentials
    {
        internal IntPtr ClientId;
        internal IntPtr ClientSecret;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct PlatformOptions
    {
        internal int ApiVersion;
        internal IntPtr Reserved;
        internal IntPtr ProductId;
        internal IntPtr SandboxId;
        internal ClientCredentials ClientCredentials;
        internal int IsServer;
        internal IntPtr EncryptionKey;
        internal IntPtr OverrideCountryCode;
        internal IntPtr OverrideLocaleCode;
        internal IntPtr DeploymentId;
        internal ulong Flags;
        internal IntPtr CacheDirectory;
        internal uint TickBudgetInMilliseconds;
        internal IntPtr RTCOptions;
        internal IntPtr IntegratedPlatformOptionsContainerHandle;
        internal IntPtr SystemSpecificOptions;
        internal IntPtr TaskNetworkTimeoutSeconds;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CreateDeviceIdOptions
    {
        internal int ApiVersion;
        internal IntPtr DeviceModel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ConnectCredentials
    {
        internal int ApiVersion;
        internal IntPtr Token;
        internal ExternalCredentialType Type;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct UserLoginInfo
    {
        internal int ApiVersion;
        internal IntPtr DisplayName;
        internal IntPtr NsaIdToken;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LoginOptions
    {
        internal int ApiVersion;
        internal IntPtr Credentials;
        internal IntPtr UserLoginInfo;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CreateUserOptions
    {
        internal int ApiVersion;
        internal IntPtr ContinuanceToken;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CreateDeviceIdCallbackInfo
    {
        internal Result ResultCode;
        internal IntPtr ClientData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LoginCallbackInfo
    {
        internal Result ResultCode;
        internal IntPtr ClientData;
        internal IntPtr LocalUserId;
        internal IntPtr ContinuanceToken;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CreateUserCallbackInfo
    {
        internal Result ResultCode;
        internal IntPtr ClientData;
        internal IntPtr LocalUserId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CreateLobbyOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal uint MaxLobbyMembers;
        internal LobbyPermissionLevel PermissionLevel;
        internal int PresenceEnabled;
        internal int AllowInvites;
        internal IntPtr BucketId;
        internal int DisableHostMigration;
        internal int EnableRTCRoom;
        internal IntPtr LocalRTCOptions;
        internal IntPtr LobbyId;
        internal int EnableJoinById;
        internal int RejoinAfterKickRequiresInvite;
        internal IntPtr AllowedPlatformIds;
        internal uint AllowedPlatformIdsCount;
        internal int CrossplayOptOut;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct JoinLobbyByIdOptions
    {
        internal int ApiVersion;
        internal IntPtr LobbyId;
        internal IntPtr LocalUserId;
        internal int PresenceEnabled;
        internal IntPtr LocalRTCOptions;
        internal int CrossplayOptOut;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LobbyResultCallbackInfo
    {
        internal Result ResultCode;
        internal IntPtr ClientData;
        internal IntPtr LobbyId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CopyLobbyDetailsHandleOptions
    {
        internal int ApiVersion;
        internal IntPtr LobbyId;
        internal IntPtr LocalUserId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LobbyDetailsGetLobbyOwnerOptions
    {
        internal int ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LobbyDetailsGetMemberCountOptions
    {
        internal int ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LobbyDetailsGetMemberByIndexOptions
    {
        internal int ApiVersion;
        internal uint MemberIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct DestroyLobbyOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr LobbyId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct LeaveLobbyOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr LobbyId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct SetRelayControlOptions
    {
        internal int ApiVersion;
        internal RelayControl RelayControl;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct AddNotifyPeerConnectionRequestOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr SocketId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct AddNotifyPeerConnectionClosedOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr SocketId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct AcceptConnectionOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr SocketId;
        internal IntPtr RemoteUserId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct CloseConnectionOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr RemoteUserId;
        internal IntPtr SocketId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct SendPacketOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr RemoteUserId;
        internal IntPtr SocketId;
        internal byte Channel;
        internal uint DataLengthBytes;
        internal IntPtr Data;
        internal int AllowDelayedDelivery;
        internal PacketReliability Reliability;
        internal int DisableAutoAcceptConnection;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct GetNextReceivedPacketSizeOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal IntPtr RequestedChannel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ReceivePacketOptions
    {
        internal int ApiVersion;
        internal IntPtr LocalUserId;
        internal uint MaxDataSizeBytes;
        internal IntPtr RequestedChannel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct IncomingConnectionRequestInfo
    {
        internal IntPtr ClientData;
        internal IntPtr LocalUserId;
        internal IntPtr RemoteUserId;
        internal IntPtr SocketId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct RemoteConnectionClosedInfo
    {
        internal IntPtr ClientData;
        internal IntPtr LocalUserId;
        internal IntPtr RemoteUserId;
        internal IntPtr SocketId;
        internal int Reason;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CreateDeviceIdCallback(ref CreateDeviceIdCallbackInfo data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LoginCallback(ref LoginCallbackInfo data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CreateUserCallback(ref CreateUserCallbackInfo data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LobbyResultCallback(ref LobbyResultCallbackInfo data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void IncomingConnectionRequestCallback(ref IncomingConnectionRequestInfo data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void RemoteConnectionClosedCallback(ref RemoteConnectionClosedInfo data);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_Initialize(ref InitializeOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_Platform_Create(ref PlatformOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Platform_Release(IntPtr platformHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Platform_Tick(IntPtr platformHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_Platform_GetConnectInterface(IntPtr platformHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_Platform_GetLobbyInterface(IntPtr platformHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_Platform_GetP2PInterface(IntPtr platformHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Connect_CreateDeviceId(IntPtr handle, ref CreateDeviceIdOptions options, IntPtr clientData, CreateDeviceIdCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Connect_Login(IntPtr handle, ref LoginOptions options, IntPtr clientData, LoginCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Connect_CreateUser(IntPtr handle, ref CreateUserOptions options, IntPtr clientData, CreateUserCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Lobby_CreateLobby(IntPtr handle, ref CreateLobbyOptions options, IntPtr clientData, LobbyResultCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Lobby_JoinLobbyById(IntPtr handle, ref JoinLobbyByIdOptions options, IntPtr clientData, LobbyResultCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_Lobby_CopyLobbyDetailsHandle(IntPtr handle, ref CopyLobbyDetailsHandleOptions options, out IntPtr lobbyDetailsHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_LobbyDetails_GetLobbyOwner(IntPtr lobbyDetailsHandle, ref LobbyDetailsGetLobbyOwnerOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint EOS_LobbyDetails_GetMemberCount(IntPtr lobbyDetailsHandle, ref LobbyDetailsGetMemberCountOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr EOS_LobbyDetails_GetMemberByIndex(IntPtr lobbyDetailsHandle, ref LobbyDetailsGetMemberByIndexOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_LobbyDetails_Release(IntPtr lobbyDetailsHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Lobby_DestroyLobby(IntPtr handle, ref DestroyLobbyOptions options, IntPtr clientData, LobbyResultCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_Lobby_LeaveLobby(IntPtr handle, ref LeaveLobbyOptions options, IntPtr clientData, LobbyResultCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_SetRelayControl(IntPtr handle, ref SetRelayControlOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong EOS_P2P_AddNotifyPeerConnectionRequest(IntPtr handle, ref AddNotifyPeerConnectionRequestOptions options, IntPtr clientData, IncomingConnectionRequestCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_P2P_RemoveNotifyPeerConnectionRequest(IntPtr handle, ulong notificationId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong EOS_P2P_AddNotifyPeerConnectionClosed(IntPtr handle, ref AddNotifyPeerConnectionClosedOptions options, IntPtr clientData, RemoteConnectionClosedCallback callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EOS_P2P_RemoveNotifyPeerConnectionClosed(IntPtr handle, ulong notificationId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_AcceptConnection(IntPtr handle, ref AcceptConnectionOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_CloseConnection(IntPtr handle, ref CloseConnectionOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_SendPacket(IntPtr handle, ref SendPacketOptions options);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_GetNextReceivedPacketSize(IntPtr handle, ref GetNextReceivedPacketSizeOptions options, out uint outPacketSizeBytes);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern Result EOS_P2P_ReceivePacket(
        IntPtr handle,
        ref ReceivePacketOptions options,
        out IntPtr outPeerId,
        IntPtr outSocketId,
        out byte outChannel,
        [Out] byte[] outData,
        out uint outBytesWritten);

    internal static string PtrToUtf8(IntPtr value)
        => value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;
}

internal sealed class EosUtf8 : IDisposable
{
    internal EosUtf8(string value)
    {
        Pointer = string.IsNullOrEmpty(value) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);
    }

    internal IntPtr Pointer { get; }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
            Marshal.FreeCoTaskMem(Pointer);
    }
}

internal sealed class EosSocketId : IDisposable
{
    private const int SocketNameBytes = 33;
    private const int Size = sizeof(int) + SocketNameBytes;

    internal EosSocketId(string name)
    {
        Pointer = Marshal.AllocHGlobal(Size);
        for (var i = 0; i < Size; i++)
            Marshal.WriteByte(Pointer, i, 0);

        Marshal.WriteInt32(Pointer, 0, 1);
        var bytes = System.Text.Encoding.ASCII.GetBytes(name ?? string.Empty);
        var length = Math.Min(32, bytes.Length);
        Marshal.Copy(bytes, 0, IntPtr.Add(Pointer, sizeof(int)), length);
    }

    internal IntPtr Pointer { get; }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
            Marshal.FreeHGlobal(Pointer);
    }
}
