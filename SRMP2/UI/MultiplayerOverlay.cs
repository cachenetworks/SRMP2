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
    private readonly string _host;
    private readonly int _port;

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
            var hostEntry = category.CreateEntry("Host", "127.0.0.1", "Host / IP");
            var portEntry = category.CreateEntry("Port", Protocol.DefaultPort, "TCP + UDP port");

            _username = Protocol.CleanUsername(usernameEntry.Value);
            _host = string.IsNullOrWhiteSpace(hostEntry.Value) ? "127.0.0.1" : hostEntry.Value.Trim();
            _port = NormalizePort(portEntry.Value);
        }
        catch
        {
            _username = defaultUsername;
            _host = "127.0.0.1";
            _port = Protocol.DefaultPort;
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
        var height = _network.IsConnected ? 500f : 300f;

        // Avoid GUILayout and all editable IMGUI controls. SR2 strips several
        // state-object methods used internally by GUI.TextField/GUILayout.
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
        Label(x, ref y, width, $"Host / IP: {_host}");
        Label(x, ref y, width, $"Port: {_port} (TCP + UDP)");

        y += 10f;
        var half = (width - 8f) * 0.5f;
        if (GUI.Button(new Rect(x, y, half, 30f), "Host game"))
            _network.StartHost(_username, _port);
        if (GUI.Button(new Rect(x + half + 8f, y, half, 30f), "Join game"))
            _network.Join(_host, _port, _username);
        y += 40f;

        GUI.Label(new Rect(x, y, width, 42f),
            "Change Username, Host, or Port in MelonPreferences.cfg, then restart SR2.");
        y += 46f;
        GUI.Label(new Rect(x, y, width, 42f),
            "Internet hosts must allow/forward both TCP and UDP on the selected port.");
    }

    private void DrawConnectedPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, $"Mode: {_network.Mode}   Local ID: {_network.LocalPlayerId}");
        Label(x, ref y, width, $"SR2 player hook: {(_multiplayer.HasLocalPlayer ? "ready" : "waiting for gameplay")}");
        Label(x, ref y, width, $"Remote avatars: {_multiplayer.RemotePlayerCount}");
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
            if (y > 400f)
                break;
            Label(x, ref y, width, line);
        }

        GUI.Label(new Rect(x, y, width, 24f), "Chat input temporarily disabled in SR2 IMGUI compatibility mode.");
        y += 32f;

        if (GUI.Button(new Rect(x, y, width, 28f), "Disconnect"))
            _network.Disconnect();
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
