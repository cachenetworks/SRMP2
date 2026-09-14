# Host World Join Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move an EOS-connected SRMP2 client from the main menu into a valid Slime Rancher 2 gameplay session, track the host's real gameplay scene-group target separately from peer scene metadata, and resume player snapshot/remote rendering once gameplay is ready.

**Architecture:** The host publishes `SceneLoader.CurrentSceneGroup.ReferenceId` only when the current group is a real gameplay group. `NetworkSession` carries that value as a distinct reliable host-authoritative world target in `Welcome` and subsequent world-target messages. On clients, `MultiplayerController` uses SR2's own `GameContext.AutoSaveDirector` to continue a local save when still at the main menu, waits for SR2 to establish gameplay/`SceneContext`, then asks `SceneLoader` to align to the host scene group when necessary. Additive Unity chunk initialization remains informational and never drives a world reload.

**Tech Stack:** C# / .NET 6, MelonLoader, Slime Rancher 2 IL2CPP generated bindings, Epic Online Services P2P.

**Spec:** User-provided continuation brief in this task.

## Global Constraints

- Work directly on `srmp2-port`.
- Do not modify `SRMP2/EOS/EosSettings.cs` or any EOS credentials.
- Keep EOS lobby/P2P transport and reliable fragmentation/reassembly unchanged.
- Keep reliable control on EOS channel 0 and movement snapshots on channel 1.
- Never use a raw `SceneManager.LoadScene` for host-world entry.
- Never treat additive SR2 scene initialization as a host world transition.
- Host owns the session world target; clients cannot publish it.
- If no local save is available, keep the network session alive and report that gameplay entry requires a local SR2 save rather than fabricating save state.

---

### Task 1: Host-authoritative world target protocol

**Files:**
- Modify: `SRMP2/Networking/Protocol.cs`
- Modify: `SRMP2/Networking/NetworkSession.cs`
- Create: `tests/SRMP2.Tests/SRMP2.Tests.csproj`
- Create: `tests/SRMP2.Tests/Program.cs`

**Interfaces:**
- Produce: `NetworkSession.HostWorldTarget`, `NetworkSession.PublishHostWorldTarget(string)`, `NetworkSession.HostWorldTargetChanged`.
- Preserve: peer `SceneName` and `SceneChanged` as informational metadata.

- [ ] Write a failing protocol/state test proving only the host can publish a normalized world target and that an unchanged target is ignored.
- [ ] Run the test and confirm it fails because the world-target API does not exist yet.
- [ ] Add a bounded world-target string, a reliable control message, and the target to `Welcome`.
- [ ] Run the test and project build until both pass.
- [ ] Commit the protocol milestone.

### Task 2: SR2-native client gameplay entry

**Files:**
- Modify: `SRMP2/Multiplayer/MultiplayerController.cs`

**Interfaces:**
- Consume: `SceneLoader.CurrentSceneGroup`, `SceneGroup.ReferenceId`, `SceneGroup.IsGameplay`, `SceneGroupList.GetSceneGroupFromReferenceId`, `GameContext.AutoSaveDirector.GetSaveToContinue()`, `AutoSaveDirector.BeginLoad(...)`.

- [ ] Add an explicit client world-load state machine that never reacts to additive Unity chunk names.
- [ ] Host publishes its gameplay scene-group reference when it changes.
- [ ] Client in the main menu selects SR2's normal continue save and calls the game's save loader once.
- [ ] Client waits for SR2 gameplay state before attempting scene-group alignment.
- [ ] When the local gameplay group differs from the host target, resolve the actual `SceneGroup` through `SceneGroupList` and load it through `SceneLoader`.
- [ ] Log each state boundary once and keep connection alive on recoverable load prerequisites.
- [ ] Build after each IL2CPP API integration correction.

### Task 3: Snapshot/remote lifecycle confirmation

**Files:**
- Modify: `SRMP2/Multiplayer/MultiplayerController.cs`
- Modify only if required: `SRMP2/Multiplayer/RemotePlayer.cs`

- [ ] Preserve snapshot-driven lazy remote spawning.
- [ ] Log the first remote avatar spawn for each player without logging movement frames.
- [ ] Clear/reacquire local gameplay bindings only on real player destruction or session/world transition.
- [ ] Verify sequence/interpolation/snap behavior remains unchanged.

### Task 4: Documentation and verification

**Files:**
- Modify: `docs/PORTING.md`
- Delete before commit: temporary `tools/BindingProbe/` investigation files.

- [ ] Document scene-group identity and the current local-save bootstrap limitation.
- [ ] Run `dotnet clean .\SRMP2\SRMP2.csproj`.
- [ ] Run `dotnet build .\SRMP2\SRMP2.csproj -c Debug -p:Platform=x64`.
- [ ] Run the protocol/state test harness.
- [ ] Inspect `git diff` and confirm EOS settings/credentials were untouched.
- [ ] Commit the completed host-world join milestone to `srmp2-port`.
