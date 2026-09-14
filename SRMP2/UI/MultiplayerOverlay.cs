using System;
using System.Linq;
using System.Threading.Tasks;
using MelonLoader;
using SRMP2.Multiplayer;
using SRMP2.Networking;
using UnityEngine;

namespace SRMP2.UI;

internal sealed class MultiplayerOverlay
{
    private readonly NetworkSession _network;
    private readonly MultiplayerController _multiplayer;

    private readonly string _username;
    private readonly string _host;
    private readonly int _port;
    private readonly string _savedJoinCode;

    private string _hostInviteCode = string.Empty;
    private string _inviteStatus = string.Empty;
    private bool _creatingInviteCode;

    internal MultiplayerOverlay(NetworkSession network, MultiplayerController multiplayer)
    {
        _network = network;
        _multiplayer = multiplayer;

        var defaultUsername = "Rancher";
        try
        {
            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
                defaultUsername = Protocol.CleanUsername(user);
        }
        catch
        {
        }

        try
        {
            var category = MelonPreferences.CreateCategory("SRMP2", "SRMP2 Multiplayer");
            var usernameEntry = category.CreateEntry("Username", defaultUsername, "Rancher name");
            var hostEntry = category.CreateEntry("Host", "127.0.0.1", "Direct-connect host / IP");
            var portEntry = category.CreateEntry("Port", Protocol.DefaultPort, "TCP + UDP port");
            var joinCodeEntry = category.CreateEntry("JoinCode", string.Empty, "Fallback invite code when clipboard is unavailable");

            _username = Protocol.CleanUsername(usernameEntry.Value);
            _host = string.IsNullOrWhiteSpace(hostEntry.Value) ? "127.0.0.1" : hostEntry.Value.Trim();
            _port = NormalizePort(portEntry.Value);
            _savedJoinCode = joinCodeEntry.Value?.Trim() ?? string.Empty;
        }
        catch
        {
            _username = defaultUsername;
            _host = "127.0.0.1";
            _port = Protocol.DefaultPort;
            _savedJoinCode = string.Empty;
        }
    }

    internal bool Visible { get; set; } = true;

    internal void Draw()
    {
        if (!Visible)
            return;

        const float left = 18f;
        const float top = 18f;
        const float width = 430f;
        var height = _network.IsConnected ? 530f : 350f;

        GUI.Box(new Rect(left, top, width, height), string.Empty);

        var x = left + 12f;
        var y = top + 10f;
        var contentWidth = width - 24f;

        Label(x, ref y, contentWidth, $"SRMP2 {BuildInfo.Version}");
        Label(x, ref y, contentWidth, $"Status: {_network.StatusText}");
        y += 6f;

        if (!_network.IsConnected)
            DrawConnectPanel(x, ref y, contentWidth);
        else
            DrawConnectedPanel(x, ref y, contentWidth);
    }

    private void DrawConnectPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, $"Rancher: {_username}");
        Label(x, ref y, width, "SRMP-style invite codes");

        y += 6f;
        var half = (width - 8f) * 0.5f;
        if (GUI.Button(new Rect(x, y, half, 30f), "Host + make code"))
            StartHostWithInviteCode();
        if (GUI.Button(new Rect(x + half + 8f, y, half, 30f), "Join copied code"))
            JoinInviteCode(ReadJoinCode());
        y += 40f;

        if (!string.IsNullOrWhiteSpace(_savedJoinCode))
        {
            if (GUI.Button(new Rect(x, y, width, 28f), $"Join saved code: {_savedJoinCode}"))
                JoinInviteCode(_savedJoinCode);
            y += 36f;
        }

        if (!string.IsNullOrWhiteSpace(_inviteStatus))
        {
            GUI.Label(new Rect(x, y, width, 42f), _inviteStatus);
            y += 46f;
        }

        y += 4f;
        Label(x, ref y, width, "Direct connect fallback");
        Label(x, ref y, width, $"Host / IP: {_host}");
        Label(x, ref y, width, $"Port: {_port} (TCP + UDP)");

        if (GUI.Button(new Rect(x, y, width, 28f), "Join configured IP directly"))
            _network.Join(_host, _port, _username);
        y += 36f;

        GUI.Label(new Rect(x, y, width, 42f),
            "Invite codes currently compact the host IPv4 + port. Internet hosting still requires TCP + UDP forwarding.");
    }

    private void DrawConnectedPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, $"Mode: {_network.Mode}   Local ID: {_network.LocalPlayerId}");
        Label(x, ref y, width, $"SR2 player hook: {(_multiplayer.HasLocalPlayer ? "ready" : "waiting for gameplay")}");
        Label(x, ref y, width, $"Remote avatars: {_multiplayer.RemotePlayerCount}");

        if (_network.Mode == SessionMode.Host)
        {
            y += 4f;
            Label(x, ref y, width, "Server Code");

            if (_creatingInviteCode)
            {
                Label(x, ref y, width, "Generating invite code...");
            }
            else if (!string.IsNullOrWhiteSpace(_hostInviteCode))
            {
                Label(x, ref y, width, _hostInviteCode);
                if (GUI.Button(new Rect(x, y, width, 28f), "Copy Server Code"))
                    CopyInviteCode();
                y += 36f;
            }
            else
            {
                Label(x, ref y, width, "Code unavailable");
                if (GUI.Button(new Rect(x, y, width, 28f), "Retry code generation"))
                    _ = GenerateHostInviteCodeAsync();
                y += 36f;
            }
        }

        y += 4f;
        Label(x, ref y, width, "Players");
        var peers = _network.Peers.ToArray();
        if (peers.Length == 0)
        {
            Label(x, ref y, width, "(none)");
        }
        else
        {
            foreach (var peer in peers)
            {
                var local = peer.Id == _network.LocalPlayerId ? " (you)" : string.Empty;
                var scene = string.IsNullOrWhiteSpace(peer.SceneName) ? string.Empty : $"  [{peer.SceneName}]";
                Label(x, ref y, width, $"#{peer.Id} {peer.Username}{local}{scene}");
            }
        }

        y += 4f;
        Label(x, ref y, width, "Chat");
        foreach (var line in _multiplayer.ChatLines)
        {
            if (y > 430f)
                break;
            Label(x, ref y, width, line);
        }

        GUI.Label(new Rect(x, y, width, 24f), "Chat input temporarily disabled in SR2 IMGUI compatibility mode.");
        y += 32f;

        if (GUI.Button(new Rect(x, y, width, 28f), "Disconnect"))
        {
            _network.Disconnect();
            _hostInviteCode = string.Empty;
            _inviteStatus = string.Empty;
        }
    }

    private void StartHostWithInviteCode()
    {
        _network.StartHost(_username, _port);
        _hostInviteCode = string.Empty;
        _inviteStatus = "Generating server code...";
        _ = GenerateHostInviteCodeAsync();
    }

    private async Task GenerateHostInviteCodeAsync()
    {
        if (_creatingInviteCode || _network.Mode != SessionMode.Host)
            return;

        _creatingInviteCode = true;
        try
        {
            var code = await InviteCode.CreateForHostAsync(_network.Port).ConfigureAwait(false);
            _hostInviteCode = code;
            _inviteStatus = $"Server Code: {code}";
        }
        catch (Exception ex)
        {
            _hostInviteCode = string.Empty;
            _inviteStatus = $"Could not create server code: {ex.Message}";
        }
        finally
        {
            _creatingInviteCode = false;
        }
    }

    private string ReadJoinCode()
    {
        try
        {
            var clipboard = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrWhiteSpace(clipboard))
                return clipboard.Trim();
        }
        catch
        {
        }

        return _savedJoinCode;
    }

    private void JoinInviteCode(string code)
    {
        if (!InviteCode.TryDecode(code, out var host, out var port))
        {
            _inviteStatus = "Invalid server code. Copy the host's code first, or set SRMP2.JoinCode in MelonPreferences.cfg.";
            return;
        }

        _inviteStatus = $"Joining {host}:{port} from server code...";
        _network.Join(host, port, _username);
    }

    private void CopyInviteCode()
    {
        if (string.IsNullOrWhiteSpace(_hostInviteCode))
            return;

        try
        {
            GUIUtility.systemCopyBuffer = _hostInviteCode;
            _inviteStatus = "Server code copied to clipboard.";
        }
        catch
        {
            _inviteStatus = $"Server Code: {_hostInviteCode} (clipboard unavailable)";
        }
    }

    private static void Label(float x, ref float y, float width, string text)
    {
        GUI.Label(new Rect(x, y, width, 22f), text);
        y += 22f;
    }

    private static int NormalizePort(int port)
    {
        return port > 0 && port <= 65535 ? port : Protocol.DefaultPort;
    }
}
