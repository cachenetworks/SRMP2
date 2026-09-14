# SRMP2

SRMP2 is the Slime Rancher 2 port of SRMP, built for MelonLoader / IL2CPP.

The current networking milestone uses **Epic Online Services (EOS)** in the same style as the original SRMP: anonymous EOS Connect Device ID authentication, a short lobby code, and EOS P2P for gameplay traffic. Players do not need to expose a TCP/UDP game port on their router for normal EOS sessions.

## Implemented in 0.1.0

- EOS Connect Device ID login; players do not need to sign in to an Epic account.
- Host creates an EOS lobby with a **7-character server code**.
- Clients join the EOS lobby by that code.
- EOS P2P transport with NAT traversal and relay fallback enabled.
- Reliable ordered control traffic for handshake, peer lifecycle, chat, and scene announcements.
- Unreliable unordered transform snapshots at up to 20 Hz.
- SRMP2 control-message fragmentation for EOS's 1170-byte P2P packet limit.
- Smooth remote-player position/rotation interpolation and teleport detection.
- Up to 8 players per lobby.
- Player list and text-chat receive/display support.
- Main-thread dispatch for Unity-facing network callbacks.
- Current Slime Rancher 2 `SRCharacterController` movement hook.

## Networking flow

Host:

```text
SRMP2
  -> EOS Connect Device ID
  -> Create EOS lobby
  -> Server code, for example: K7P4XQ2
  -> EOS P2P host
```

Client:

```text
Server code
  -> EOS Connect Device ID
  -> Join EOS lobby by ID
  -> Resolve lobby owner Product User ID
  -> EOS P2P handshake
  -> Connected to SRMP2 session
```

EOS P2P is used instead of SRMP2 listening on a public TCP/UDP socket. **Manual TCP/UDP 6996 port forwarding is not required for EOS sessions.** EOS is configured to allow relay fallback when a direct peer connection cannot be established.

## Current limitations

This is **not full SRMP1 feature parity yet**. The current milestone does not yet synchronize slimes/actors, vacpack actions, ammo/inventory, currency, ranch plots/gadgets, world destruction/spawns, or shared save data. Remote players are represented by a placeholder capsule until the real SR2 Beatrix rig is integrated.

The gameplay synchronization layer remains host-authoritative-ready so those systems can be added on top of the EOS transport. See [`docs/PORTING.md`](docs/PORTING.md).

## Requirements

- Slime Rancher 2 on Windows.
- MelonLoader installed into Slime Rancher 2 and launched at least once so `MelonLoader/Il2CppAssemblies` exists.
- EOS product/client settings configured for the SRMP2 development product.
- .NET SDK capable of building the existing `net6.0` MelonLoader project.
- All players should use the same Slime Rancher 2 build and the same SRMP2 build while the protocol is under active development.

### EOS runtime

SRMP2 talks directly to the native Epic Online Services C API through a small P/Invoke bridge. Slime Rancher 2 already ships the Windows x64 EOS runtime at its normal Unity plugin location, and SRMP2 attempts to use that runtime. The separately supplied `EOSSDK-Win64-Shipping.dll` was used as a development/reference build to validate the required exports.

SRMP2 creates and releases its own EOS Platform handle. It intentionally does **not** call the global `EOS_Shutdown`, because Slime Rancher 2 itself also uses EOS.

The EOS product/client values are read through `SRMP2/EOS/EosSettings.cs`. That file is intentionally left under project control; the networking implementation does not rewrite or mutate those credentials at runtime.

## Building

The project reads game/MelonLoader references from `$(GamePath)`.

The repository currently keeps Cache's game path as a fallback in `SRMP2/Directory.Build.props`. On another machine, set the `SLIME_RANCHER_2_PATH` environment variable to the Slime Rancher 2 install directory before building.

```powershell
$env:SLIME_RANCHER_2_PATH = "C:\Program Files (x86)\Steam\steamapps\common\Slime Rancher 2"
dotnet build .\SRMP2\SRMP2.csproj -c Debug -p:Platform=x64
```

If `$(GamePath)\Mods` exists, the build copies `SRMP2.dll` into the game's `Mods` folder automatically.

## Playing

1. Install the built `SRMP2.dll` in `Slime Rancher 2/Mods` on every PC.
2. Start Slime Rancher 2 through MelonLoader.
3. The SRMP2 multiplayer panel is visible by default.
4. The host clicks **Host EOS Game**.
5. Wait for the status to become `Hosting EOS lobby XXXXXXX`.
6. Click **Copy Server Code** and send the 7-character code to the other player.
7. The other player copies the code and clicks **Join copied code**. As a fallback, set `SRMP2.JoinCode` in `MelonPreferences.cfg` and use the saved-code button.
8. Load into gameplay. SRMP2 binds to SR2's `SRCharacterController` and begins transform synchronization.

No router configuration should be necessary for the normal EOS path.

## Transport details

SRMP2 keeps its existing packet protocol above EOS so gameplay systems do not depend directly on Epic APIs:

- **EOS P2P channel 0:** reliable ordered SRMP2 control frames.
- **EOS P2P channel 1:** unreliable unordered player transform snapshots.
- **Socket ID:** `SRMP2`.
- **Lobby size:** 8 players maximum.
- **Host migration:** disabled; the original host remains authoritative.
- **Relay control:** `AllowRelays`.

The original SRMP design is retained where it still makes sense: discrete packets, network-player abstraction, interpolation, and explicit synchronization for individual game systems. SR2's data model is different enough that its world synchronization should be implemented against SR2's actor/model APIs instead of mechanically copying SR1 patches.

The current Slime Rancher 2 `Assembly-CSharp.dll` was used to map the SR2 hooks documented in `docs/PORTING.md`.
