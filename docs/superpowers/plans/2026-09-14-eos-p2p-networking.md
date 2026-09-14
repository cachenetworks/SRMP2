# EOS P2P Networking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace SRMP2's direct TCP/UDP Internet transport with EOS Connect + Lobby + P2P so hosts receive a seven-character join code and players do not need router port forwarding.

**Architecture:** Keep the existing SRMP2 gameplay packet protocol and `NetworkSession` event API, but carry its reliable control messages over EOS P2P channel 0 and movement snapshots over EOS P2P channel 1. A small SRMP2-specific P/Invoke layer talks directly to `EOSSDK-Win64-Shipping.dll`; EOS Device ID provides anonymous Product User IDs, an EOS lobby uses the seven-character code as its Lobby ID, and EOS P2P uses NAT traversal/relay. SRMP2 creates and releases only its own EOS platform handle and never calls global `EOS_Shutdown` because Slime Rancher 2 also uses EOS.

**Tech Stack:** C# / .NET 6, MelonLoader, Slime Rancher 2 IL2CPP, Epic Online Services C API.

**Spec:** Conversation-approved design: old-SRMP-style EOS lobby code + EOS P2P, no manual port forwarding.

## Global Constraints

- Do not modify `SRMP2/EOS/EosSettings.cs` or its credentials.
- Keep existing SR2 player synchronization and `NetworkSession` events working.
- Use EOS P2P channel 0 as reliable ordered control and channel 1 as unreliable unordered movement.
- Enable EOS relays (`AllowRelays`).
- Maximum EOS P2P payload is 1170 bytes; reject larger control payloads rather than silently truncating them.
- Do not call global `EOS_Shutdown` from the mod.
- Do not use `GUILayout` or editable IMGUI text controls because SR2 strips those bindings.

---

### Task 1: Minimal EOS native bridge

**Files:**
- Create: `SRMP2/EOS/EosNative.cs`
- Create: `SRMP2/EOS/EosRuntime.cs`

**Interfaces:**
- Produces: `EosRuntime.Initialize()`, `EosRuntime.Tick()`, `EosRuntime.LoginDeviceId(...)`, lobby/P2P handles, packet send/receive helpers.

- [ ] Define only the EOS result values, structs, callbacks, and C exports used by SRMP2.
- [ ] Use `CallingConvention.Cdecl`, sequential Pack=8 structures, UTF-8 unmanaged strings, and persistent socket ID storage.
- [ ] Initialize an SRMP2 EOS platform using existing `EosSettings.Load()` values without editing that settings file.
- [ ] Device-ID login: `CreateDeviceId` -> `Login`; on `InvalidUser`, call `CreateUser`.
- [ ] Configure P2P relay control to `AllowRelays` after login.
- [ ] Release only SRMP2's platform handle during disposal.

### Task 2: EOS lobby and P2P session transport

**Files:**
- Replace internals: `SRMP2/Networking/NetworkSession.cs`

**Interfaces:**
- Preserve: `StartHost`, `Disconnect`, `SendSnapshot`, `SendChat`, `SendScene`, `Pump`, peer/events/properties.
- Add: `string ServerCode { get; }`, `void JoinCode(string code, string username)`.
- Compatibility: `Join(host, port, username)` may remain as a wrapper/failure message but is no longer the normal Internet transport.

- [ ] Host login and create an EOS lobby with a random seven-character ID, max 8 users, join-by-ID enabled, host migration disabled.
- [ ] Register a P2P connection-request callback for socket `SRMP2` and accept only current lobby members.
- [ ] Client joins lobby by ID, copies lobby details, resolves the owner Product User ID, then sends the existing `Hello` message over reliable P2P.
- [ ] Host validates protocol/version, assigns player ID, sends existing `Welcome`/`PeerJoined` messages and maintains ProductUserId-to-player mappings.
- [ ] Drain EOS packets from `Pump()` after `Platform.Tick()` and route control vs movement channels.
- [ ] Forward snapshots through channel 1 with `UnreliableUnordered`; forward control frames through channel 0 with `ReliableOrdered`.
- [ ] Destroy/leave the lobby and close P2P connections on disconnect while resetting local state even if EOS cleanup callbacks fail.

### Task 3: EOS join-code UI

**Files:**
- Modify: `SRMP2/UI/MultiplayerOverlay.cs`
- Optional delete: `SRMP2/Networking/InviteCode.cs`

**Interfaces:**
- Consumes: `NetworkSession.ServerCode`, `NetworkSession.JoinCode`.

- [ ] Host button calls `StartHost`; show the EOS lobby code once creation succeeds.
- [ ] Join button reads clipboard or saved MelonPreferences `JoinCode` and calls `JoinCode`.
- [ ] Remove encoded-IP/direct-port-forward wording from the normal flow.
- [ ] Keep only SR2-compatible `GUI.Label`, `GUI.Button`, and clipboard APIs.

### Task 4: Runtime/build documentation and verification

**Files:**
- Modify: `README.md`
- Modify only if required: `SRMP2/SRMP2.csproj`

- [ ] Document EOS lobby-code host/join behavior and that no manual TCP/UDP 6996 forwarding is required for EOS sessions.
- [ ] Document that Slime Rancher 2's bundled `EOSSDK-Win64-Shipping.dll` is used; the uploaded DLL is a compatible development reference and does not need to replace the game's copy.
- [ ] Run source-level/static verification for all P/Invoke layouts against the known EOS generated bindings.
- [ ] Run `dotnet build .\SRMP2\SRMP2.csproj -c Debug -p:Platform=x64` when game/MelonLoader references are available.
- [ ] Runtime test in two SR2 clients: host creates code, client joins from different network without port forwarding, handshake completes, remote movement appears, disconnect cleans up.
