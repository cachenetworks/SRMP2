using System;
using System.Linq;
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
    private readonly string _savedJoinCode;

    private string _inviteStatus = string.Empty;

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
            var joinCodeEntry = category.CreateEntry(
                "JoinCode",
                string.Empty,
                "EOS lobby code fallback when clipboard is unavailable");

            _username = Protocol.CleanUsername(usernameEntry.Value);
            _savedJoinCode = joinCodeEntry.Value?.Trim() ?? string.Empty;
        }
        catch
        {
            _username = defaultUsername;
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
        var height = _network.IsConnected ? 535f : 305f;

        GUI.Box(new Rect(left, top, width, height), string.Empty);

        var x = left + 12f;
        var y = top + 10f;
        var contentWidth = width - 24f;

        Label(x, ref y, contentWidth, $"SRMP2 {BuildInfo.Version}");
        Label(x, ref y, contentWidth, $"Status: {_network.StatusText}");
        y += 6f;

        if (_network.IsConnected)
            DrawConnectedPanel(x, ref y, contentWidth);
        else
            DrawConnectPanel(x, ref y, contentWidth);
    }

    private void DrawConnectPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, $"Rancher: {_username}");
        Label(x, ref y, width, "Epic Online Services lobby + P2P");
        y += 6f;

        if (_network.Mode != SessionMode.Offline)
        {
            GUI.Label(new Rect(x, y, width, 42f),
                "Connecting through EOS. This can take a few seconds on the first Device ID login.");
            y += 48f;

            if (GUI.Button(new Rect(x, y, width, 30f), "Cancel"))
            {
                _network.Disconnect();
                _inviteStatus = string.Empty;
            }
            y += 40f;
            return;
        }

        var half = (width - 8f) * 0.5f;
        if (GUI.Button(new Rect(x, y, half, 30f), "Host EOS Game"))
            StartHost();
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

        GUI.Label(new Rect(x, y, width, 50f),
            "EOS handles NAT traversal and relay fallback. SRMP2 does not require manual TCP/UDP 6996 port forwarding for EOS sessions.");
    }

    private void DrawConnectedPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, $"Mode: {_network.Mode}   Local ID: {_network.LocalPlayerId}");
        Label(x, ref y, width, $"EOS Lobby: {_network.ServerCode}");
        Label(x, ref y, width, $"SR2 player hook: {(_multiplayer.HasLocalPlayer ? "ready" : "waiting for gameplay")}");
        Label(x, ref y, width, $"Remote avatars: {_multiplayer.RemotePlayerCount}");

        if (_network.Mode == SessionMode.Host)
        {
            y += 4f;
            Label(x, ref y, width, "Server Code");
            Label(x, ref y, width, string.IsNullOrWhiteSpace(_network.ServerCode) ? "(waiting for EOS)" : _network.ServerCode);

            if (!string.IsNullOrWhiteSpace(_network.ServerCode))
            {
                if (GUI.Button(new Rect(x, y, width, 28f), "Copy Server Code"))
                    CopyInviteCode();
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
                if (y > 350f)
                    break;

                var local = peer.Id == _network.LocalPlayerId ? " (you)" : string.Empty;
                var scene = string.IsNullOrWhiteSpace(peer.SceneName) ? string.Empty : $"  [{peer.SceneName}]";
                Label(x, ref y, width, $"#{peer.Id} {peer.Username}{local}{scene}");
            }
        }

        y += 4f;
        Label(x, ref y, width, "Chat");
        foreach (var line in _multiplayer.ChatLines)
        {
            if (y > 445f)
                break;
            Label(x, ref y, width, line);
        }

        GUI.Label(new Rect(x, y, width, 24f),
            "Chat input temporarily disabled in SR2 IMGUI compatibility mode.");
        y += 32f;

        if (GUI.Button(new Rect(x, y, width, 28f), "Disconnect"))
        {
            _network.Disconnect();
            _inviteStatus = string.Empty;
        }
    }

    private void StartHost()
    {
        _inviteStatus = "Signing in and creating an EOS lobby...";
        _network.StartHost(_username, Protocol.DefaultPort);
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
        if (string.IsNullOrWhiteSpace(code))
        {
            _inviteStatus = "Copy a 7-character SRMP2 EOS server code first, or set SRMP2.JoinCode in MelonPreferences.cfg.";
            return;
        }

        _inviteStatus = $"Joining EOS lobby {code.Trim().ToUpperInvariant()}...";
        _network.JoinCode(code, _username);
    }

    private void CopyInviteCode()
    {
        var code = _network.ServerCode;
        if (string.IsNullOrWhiteSpace(code))
            return;

        try
        {
            GUIUtility.systemCopyBuffer = code;
            _inviteStatus = "Server code copied to clipboard.";
        }
        catch
        {
            _inviteStatus = $"Server Code: {code} (clipboard unavailable)";
        }
    }

    private static void Label(float x, ref float y, float width, string text)
    {
        GUI.Label(new Rect(x, y, width, 22f), text);
        y += 22f;
    }
}
