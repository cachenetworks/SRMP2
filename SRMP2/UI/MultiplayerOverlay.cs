using System;
using System.Linq;
using SRMP2.Multiplayer;
using SRMP2.Networking;
using UnityEngine;

namespace SRMP2.UI;

internal sealed class MultiplayerOverlay
{
    private readonly NetworkSession _network;
    private readonly MultiplayerController _multiplayer;

    private string _username = "Rancher";
    private string _host = "127.0.0.1";
    private string _port = Protocol.DefaultPort.ToString();
    private string _chat = string.Empty;

    internal MultiplayerOverlay(NetworkSession network, MultiplayerController multiplayer)
    {
        _network = network;
        _multiplayer = multiplayer;

        try
        {
            var user = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(user))
                _username = Protocol.CleanUsername(user);
        }
        catch
        {
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
        var height = _network.IsConnected ? 520f : 305f;

        // Avoid GUILayout entirely here. Slime Rancher 2's IL2CPP build can strip
        // GUILayout.BeginArea overloads, causing MelonLoader's method-unstripping
        // fallback to throw every frame. The immediate-mode GUI calls below are
        // simpler bindings and don't require the stripped layout API.
        GUI.Box(new Rect(left, top, width, height), string.Empty);

        var x = left + 12f;
        var y = top + 10f;
        var contentWidth = width - 24f;

        Label(x, ref y, contentWidth, $"SRMP2 {BuildInfo.Version}  |  F8 toggles this panel");
        Label(x, ref y, contentWidth, $"Status: {_network.StatusText}");
        y += 6f;

        if (!_network.IsConnected)
            DrawConnectPanel(x, ref y, contentWidth);
        else
            DrawConnectedPanel(x, ref y, contentWidth);

        if (GUI.Button(new Rect(x, top + height - 36f, contentWidth, 26f), "Hide panel"))
            Visible = false;
    }

    private void DrawConnectPanel(float x, ref float y, float width)
    {
        Label(x, ref y, width, "Rancher name");
        _username = GUI.TextField(new Rect(x, y, width, 24f), _username, Protocol.MaxUsernameLength);
        y += 30f;

        Label(x, ref y, width, "Host / IP");
        _host = GUI.TextField(new Rect(x, y, width, 24f), _host, 120);
        y += 30f;

        Label(x, ref y, width, "Port (TCP + UDP)");
        _port = GUI.TextField(new Rect(x, y, width, 24f), _port, 5);
        y += 34f;

        var half = (width - 8f) * 0.5f;
        if (GUI.Button(new Rect(x, y, half, 28f), "Host game"))
            _network.StartHost(_username, ParsePort());
        if (GUI.Button(new Rect(x + half + 8f, y, half, 28f), "Join game"))
            _network.Join(_host, ParsePort(), _username);
        y += 36f;

        GUI.Label(new Rect(x, y, width, 42f),
            "The host must allow/forward both TCP and UDP on the selected port for Internet play.");
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
            if (y > 392f)
                break;
            Label(x, ref y, width, line);
        }

        var sendWidth = 70f;
        _chat = GUI.TextField(new Rect(x, y, width - sendWidth - 8f, 24f), _chat, Protocol.MaxChatLength);
        if (GUI.Button(new Rect(x + width - sendWidth, y, sendWidth, 24f), "Send"))
            SendChat();
        y += 32f;

        if (GUI.Button(new Rect(x, y, width, 26f), "Disconnect"))
            _network.Disconnect();
    }

    private static void Label(float x, ref float y, float width, string text)
    {
        GUI.Label(new Rect(x, y, width, 22f), text);
        y += 22f;
    }

    private void SendChat()
    {
        var text = Protocol.CleanChat(_chat);
        if (text.Length == 0)
            return;
        _network.SendChat(text);
        _chat = string.Empty;
    }

    private int ParsePort()
    {
        if (!int.TryParse(_port, out var port) || port <= 0 || port > 65535)
        {
            port = Protocol.DefaultPort;
            _port = port.ToString();
        }
        return port;
    }
}
