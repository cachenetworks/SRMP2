# SRMP2

SRMP2 is the Slime Rancher 2 port of SRMP, built for MelonLoader / IL2CPP.

This branch is the first playable networking milestone. It establishes the multiplayer transport and hooks the current Slime Rancher 2 `SRCharacterController` so two or more game clients can host/join a session, see remote movement, and chat. It intentionally uses a simple placeholder remote avatar while the SR2 Beatrix model, animator, vacpack, actors, ranch state, inventory, and save authority are ported on top of the stable transport.

## Implemented in 0.1.0

- Host or join directly from an in-game F8 panel.
- Versioned TCP handshake and peer lifecycle.
- UDP transform snapshots at up to 20 Hz.
- Smooth remote-player position/rotation interpolation and teleport detection.
- Up to 8 players per session.
- Player list and text chat.
- Scene-name announcements to peers.
- Main-thread dispatch for Unity-facing network callbacks.
- Same-port TCP + UDP networking, defaulting to SRMP's port `6996`.
- No third-party networking library dependency.

## Current limitations

This is **not full SRMP1 feature parity yet**. The first milestone does not yet synchronize slimes/actors, vacpack actions, ammo/inventory, currency, ranch plots/gadgets, world destruction/spawns, or shared save data. Remote players are represented by a placeholder capsule until the real SR2 Beatrix rig is integrated.

The architecture is deliberately host-authoritative-ready so those systems can be added without replacing the transport again. See [`docs/PORTING.md`](docs/PORTING.md).

## Requirements

- Slime Rancher 2 on Windows.
- MelonLoader installed into Slime Rancher 2 and launched at least once so `MelonLoader/Il2CppAssemblies` exists.
- .NET SDK capable of building the existing `net6.0` MelonLoader project.
- All players should use the same Slime Rancher 2 build and the same SRMP2 build while the protocol is under active development.

## Building

The project reads game/MelonLoader references from `$(GamePath)`.

The repository currently keeps Cache's game path as a fallback in `SRMP2/Directory.Build.props`. On another machine, set the `SLIME_RANCHER_2_PATH` environment variable to your Slime Rancher 2 install directory before building.

```powershell
$env:SLIME_RANCHER_2_PATH = "C:\Program Files (x86)\Steam\steamapps\common\Slime Rancher 2"
dotnet build .\SRMP2\SRMP2.csproj -c Release
```

If `$(GamePath)\Mods` exists, the build copies `SRMP2.dll` into the game's `Mods` folder automatically.

## Playing

1. Install the built `SRMP2.dll` in `Slime Rancher 2/Mods` on every PC.
2. Start Slime Rancher 2 through MelonLoader.
3. Press **F8** to show/hide the SRMP2 panel.
4. Enter a rancher name.
5. One player clicks **Host game**. Other players enter that host/IP and click **Join game**.
6. For Internet hosting, allow/forward **both TCP and UDP** on the selected port. The default is `6996`.
7. Load into gameplay. SRMP2 automatically binds to SR2's `SRCharacterController` and begins transform synchronization.

## Development direction

The original SRMP design is being retained where it still makes sense: discrete packets, a network-player abstraction, interpolation, and explicit synchronization for game systems. SR2's data model is different enough that its world synchronization should be implemented against SR2's actor/model APIs instead of mechanically copying SR1 patches.

The uploaded current `Assembly-CSharp.dll` was used to map the SR2 hooks documented in `docs/PORTING.md`.
