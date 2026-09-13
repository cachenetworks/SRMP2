# SRMP -> SRMP2 port map

This document records the SR2 types confirmed from the current uploaded `Assembly-CSharp.dll` and how the SRMP1 systems map onto them.

## Networking model

SRMP2 0.1 uses two channels on the same port:

- **TCP:** handshake, peer join/leave, chat, scene metadata, future reliable world/state events.
- **UDP:** high-frequency player transforms and, later, actor transforms where loss is preferable to head-of-line blocking.

The host owns the session ID and player IDs. Clients cannot choose a player ID in accepted transform packets: the host associates the UDP sender with the already-authenticated TCP peer before relaying snapshots. Unity-facing callbacks are queued back to the main thread.

## Confirmed SR2 hooks

### Player transform

`Il2CppMonomiPark.SlimeRancher.Player.CharacterController.SRCharacterController`

Confirmed members include `Position`, `Rotation`, velocity accessors, `Awake`, `Update`, and `LateUpdate`. Milestone 0.1 reads `Position` and `Rotation` directly for movement replication.

SRMP1 equivalent: the local player's transform sampled by `NetworkPlayer`, sent periodically in `PacketPlayerPosition`, and interpolated by remote `NetworkPlayer` instances.

### Player state / resources

`Il2Cpp.PlayerState`

Relevant members include vacuum/ammo access, health/energy/currency state, `Damage`, and `Heal`.

`Il2CppMonomiPark.SlimeRancher.DataModel.PlayerModel`

Relevant members include position/rotation/model transform plus persisted player resources/currency.

Planned authority: host validates and relays resource/inventory mutations. Snapshot packets should not be used as permission to mutate authoritative inventory or currency.

### Vacpack and ammo

`Il2CppMonomiPark.SlimeRancher.Player.PlayerItems.VacuumItem`

Confirmed actions include `VacTriggered`, `VacCanceled`, `ShootTriggered`, `ShootCanceled`, `Consume`, and `Expel`.

`Il2CppMonomiPark.SlimeRancher.Player.AmmoSlotManager`

Confirmed APIs cover selected slot, counts, decrement/clear, and adding items.

Planned packets:

- `VacState` (vac on/off, aim state)
- `VacShoot` (authoritative launch request/result)
- `AmmoSlotChanged`
- `InventoryDelta`

The host should be authoritative for item consumption and spawned actors so a client cannot create inventory or duplicate world objects by replaying local events.

### Actors / slimes / world objects

`Il2Cpp.IdentifiableActor`

Confirmed model/actor-ID lifecycle methods include `GetActorId`, `SetModel`, `InitModel`, and destruction callbacks.

`Il2CppMonomiPark.SlimeRancher.DataModel.ActorModel`

Confirmed members include position/rotation, transform copy/update, and lifecycle state.

`Il2CppMonomiPark.SlimeRancher.DataModel.GameModel`

Confirmed actor/identifiable APIs include identifiable registration/lookup, actor enumeration, actor-model creation/instantiation, actor registration, and identifiable-model destruction.

`Il2Cpp.Destroyer`

Confirmed destruction entry points include actor and gadget destruction.

This is the basis for stable network actor IDs. The next world-sync layer should maintain a host-side table:

`NetworkActorId -> SR2 actor ID/model -> current owner/authority -> last transform revision`

Reliable spawn/despawn/identity packets should be TCP. Transform updates can be UDP with sequence/revision checks. A late-joining client should receive a reliable world baseline followed by live deltas.

### Scene / region loading

`Il2CppMonomiPark.SlimeRancher.SceneManagement.SceneLoader`

Confirmed members include scene-group loading and current-scene-group access.

Milestone 0.1 announces Unity scene names only. Full SR2 synchronization should move to scene-group/region identity so actor visibility and hibernation match SR2's streaming model.

## Recommended implementation order

1. **Transport + player transforms** — implemented in 0.1.
2. **Real Beatrix remote avatar** — reuse an SR2 Beatrix rig/animator without touching the local player controller.
3. **Vacpack visual/actions** — reliable start/stop/shoot events, host-authoritative item launch.
4. **Actor registry** — stable host network IDs for slimes, plorts, food, resources, and other `IdentifiableActor` objects.
5. **Spawn/despawn + transform ownership** — baseline on join, live deltas, actor hibernation/region handling.
6. **Ammo/inventory + currency/resources** — authoritative deltas with validation.
7. **Ranch plots/gadgets/interactables** — explicit action packets and state snapshots.
8. **Shared save** — host owns the canonical multiplayer save; clients receive required state and never overwrite the host from stale local data.
9. **Compatibility/versioning** — game-build fingerprint, mod manifest exchange, protocol migration, and feature flags.

## Things intentionally not copied from SRMP1

- SR1-only `SRSingleton`/director assumptions where SR2 exposes different data-model APIs.
- Direct per-frame snapping for remote players. SRMP2 interpolates snapshots.
- Trusting client state for future inventory/world mutations.
- A dependency on Lidgren for the first SR2 transport layer. SRMP2 uses the .NET socket APIs already available to the MelonLoader mod.
