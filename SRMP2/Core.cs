using MelonLoader;
using SRMP2.Multiplayer;
using SRMP2.Networking;
using SRMP2.UI;
using UnityEngine;

[assembly: MelonInfo(typeof(SRMP2.Core), SRMP2.BuildInfo.Name, SRMP2.BuildInfo.Version, SRMP2.BuildInfo.Author, null)]
[assembly: MelonGame("MonomiPark", "SlimeRancher2")]

namespace SRMP2;

internal static class BuildInfo
{
    internal const string Name = "SRMP2";
    internal const string Version = "0.1.0";
    internal const string Author = "Cache Networks / SRMP";
}

public sealed class Core : MelonMod
{
    private NetworkSession _network;
    private MultiplayerController _multiplayer;
    private MultiplayerOverlay _overlay;
    private bool _inputWarningShown;
    private bool _guiFailureShown;

    public override void OnInitializeMelon()
    {
        _network = new NetworkSession(message => LoggerInstance.Msg(message));
        _multiplayer = new MultiplayerController(_network, message => LoggerInstance.Msg(message));
        _overlay = new MultiplayerOverlay(_network, _multiplayer);

        LoggerInstance.Msg($"{BuildInfo.Name} {BuildInfo.Version} initialized.");
        LoggerInstance.Msg("Press F8 to toggle the SRMP2 multiplayer panel.");
        LoggerInstance.Msg("SR2 bindings: SRCharacterController movement sync enabled.");
    }

    public override void OnUpdate()
    {
        _network?.Pump();
        _multiplayer?.Update();

        try
        {
            if (Input.GetKeyDown(KeyCode.F8) && _overlay != null)
                _overlay.Visible = !_overlay.Visible;
        }
        catch (Exception ex)
        {
            if (!_inputWarningShown)
            {
                _inputWarningShown = true;
                LoggerInstance.Warning($"F8 hotkey is unavailable with this Unity input configuration: {ex.Message}");
                LoggerInstance.Warning("The SRMP2 panel remains visible by default.");
            }
        }
    }

    public override void OnGUI()
    {
        if (_overlay == null || !_overlay.Visible)
            return;

        try
        {
            _overlay.Draw();
        }
        catch (Exception ex)
        {
            // IL2CPP games can strip Unity IMGUI overloads that aren't used by the
            // base game. Never let a missing binding throw once per rendered frame.
            _overlay.Visible = false;
            if (!_guiFailureShown)
            {
                _guiFailureShown = true;
                LoggerInstance.Error($"SRMP2 overlay disabled because a Unity IMGUI binding is unavailable: {ex}");
                LoggerInstance.Warning("Networking will continue running; restart after updating SRMP2 to retry the panel.");
            }
        }
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        _multiplayer?.OnSceneInitialized(sceneName);
    }

    public override void OnDeinitializeMelon()
    {
        try { _multiplayer?.Dispose(); } catch { }
        try { _network?.Dispose(); } catch { }
        _multiplayer = null;
        _network = null;
        _overlay = null;
    }
}
