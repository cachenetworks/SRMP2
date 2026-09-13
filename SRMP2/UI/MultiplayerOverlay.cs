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

        var height = _network.IsConnected ? 520f : 305f;
        GUILayout.BeginArea(new Rect(18f, 18f, 430f, height), GUI.skin.box);
        GUILayout.Label($"SRMP2 {BuildInfo.Version}  |  F8 toggles this panel");
        GUILayout.Label($"Status: {_network.StatusText}");

        if (!_network.IsConnected)
            DrawConnectPanel();
        else
            DrawConnectedPanel();

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Hide panel"))
            Visible = false;
        GUILayout.EndArea();
    }

    private void DrawConnectPanel()
    {
        GUILayout.Space(8f);
        GUILayout.Label("Rancher name");
        _username = GUILayout.TextField(_username, Protocol.MaxUsernameLength);

        GUILayout.Space(6f);
        GUILayout.Label("Host / IP");
        _host = GUILayout.TextField(_host, 120);

        GUILayout.Label("Port (TCP + UDP)");
        _port = GUILayout.TextField(_port, 5);

        GUILayout.Space(10f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Host game"))
            _network.StartHost(_username, ParsePort());
        if (GUILayout.Button("Join game"))
            _network.Join(_host, ParsePort(), _username);
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        GUILayout.Label("The host must allow/forward both TCP and UDP on the selected port for Internet play.");
    }

    private void DrawConnectedPanel()
    {
        GUILayout.Space(6f);
        GUILayout.Label($"Mode: {_network.Mode}   Local ID: {_network.LocalPlayerId}");
        GUILayout.Label($"SR2 player hook: {(_multiplayer.HasLocalPlayer ? "ready" : "waiting for gameplay")}");
        GUILayout.Label($"Remote avatars: {_multiplayer.RemotePlayerCount}");

        GUILayout.Space(8f);
        GUILayout.Label("Players");
        var peers = _network.Peers.ToArray();
        if (peers.Length == 0)
            GUILayout.Label("(none)");
        else
        {
            foreach (var peer in peers)
            {
                var local = peer.Id == _network.LocalPlayerId ? " (you)" : string.Empty;
                var scene = string.IsNullOrWhiteSpace(peer.SceneName) ? string.Empty : $"  [{peer.SceneName}]";
                GUILayout.Label($"#{peer.Id} {peer.Username}{local}{scene}");
            }
        }

        GUILayout.Space(8f);
        GUILayout.Label("Chat");
        foreach (var line in _multiplayer.ChatLines)
            GUILayout.Label(line);

        GUILayout.BeginHorizontal();
        _chat = GUILayout.TextField(_chat, Protocol.MaxChatLength);
        if (GUILayout.Button("Send", GUILayout.Width(70f)))
            SendChat();
        GUILayout.EndHorizontal();

        GUILayout.Space(8f);
        if (GUILayout.Button("Disconnect"))
            _network.Disconnect();
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
