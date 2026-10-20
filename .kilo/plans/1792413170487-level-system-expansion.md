# Battlesky Level System Expansion

## Context

Current level system lives in `Assets/Scripts/Network/NetworkLevelManager.cs`:
- 3 levels, timer-driven: L1 asteroids (45s) -> L2 asteroids+enemies (60s) -> L3 indefinite
- `NetworkEnemySpawner.cs` spawns simple chase AI at Level 2+
- `NetworkAsteroidSpawner.cs` spawns asteroids randomly around first player
- Ship flight is manual-thrust only (`ShipFlightModel.cs`, `NetworkPlayer.cs`)

Related files: `EnemyShip.cs`, `AsteroidSpawner.cs`, `FloatingAsteroid.cs`, `BattleSkyNetworkManager.cs`, `Player.prefab`

---

## Design Decisions (baseline accepted)

1. **Level 1 asteroid belt** — Fixed bounded belt region; asteroids respawn inside the belt when destroyed/despawned so the field persists.
2. **Level 2+ capital ships** — Large high-health capital ships that spawn fighter drones; capital ships are priority targets.
3. **Auto-forward movement** — Constant base forward acceleration; W/S modifies speed multiplier (ship always drifts forward).
4. **Minimap** — Top-right square panel; player = center dot; enemies = red dots; asteroids = grey dots; world-bounds border.
5. **Star Wars-style ship** — New `Player_StarWars.prefab` replacing `Player.prefab` as the networked player ship.
6. **Continuous difficulty** — Spawn rate / max count escalate within each level every 30s, not just between levels.

---

## Tasks (ordered)

### 1. Auto-forward movement (ship always drifts forward)
**Files:** `Assets/Scripts/ShipFlightModel.cs`, `Assets/Scripts/Network/NetworkPlayer.cs`

- Add `[SerializeField] float _baseForwardAccel` (e.g. 5f) to both.
- In `ApplyMainThrust()` / `ApplyMainThrust()` equivalent, add `baseAccel` to the forward force each fixed step, scaled by a speed multiplier (W/S adjusts multiplier from 0 to 1+).
- Preserve boost logic on top of base thrust.

### 2. Level 1 asteroid belt with respawn
**Files:** `Assets/Scripts/Network/NetworkAsteroidSpawner.cs`, `Assets/Scripts/AsteroidSpawner.cs`

- Add `[SerializeField] Transform _beltCenter` and `[SerializeField] float _beltRadius`.
- Spawn position = `beltCenter.position + Random.insideUnitSphere * beltRadius` (clamped to belt).
- Track destroyed asteroids; respawn a replacement inside the belt when one is destroyed or despawned (keeps count at `maxAsteroids`).
- Keep existing despawn-radius behavior as safety fallback.

### 3. Capital ship + fighter AI (Level 2+)
**New file:** `Assets/Scripts/Network/CapitalShip.cs`
**Modified:** `Assets/Scripts/Network/EnemyShip.cs`, `Assets/Scripts/Network/NetworkEnemySpawner.cs`

- `CapitalShip : NetworkBehaviour`: high health (~500), slow turn/thrust, spawns 2–3 fighters (existing `EnemyShip` prefab) at intervals, fires heavy bolts at nearest player.
- `EnemyShip`: keep existing chase AI; tune turn rate / fire rate so fighters feel distinct from capital ships.
- `NetworkEnemySpawner`: at Level 2+ spawn capital ships (1 per N players, capped); each capital ship self-manages its fighter squadron. Level 3 increases capital ship count and spawn rate.

### 4. Continuous within-level difficulty scaling
**File:** `Assets/Scripts/Network/NetworkLevelManager.cs`

- Track `_timeInLevel`; every 30s increase a `DifficultyMultiplier` (start 1.0, +0.25 per interval, cap ~2.0).
- Expose `public static float DifficultyMultiplier`.
- Spawners (`NetworkAsteroidSpawner`, `NetworkEnemySpawner`) read `DifficultyMultiplier` to scale `spawnRate` (divide) and `maxAsteroids`/`maxEnemies` (multiply).
- Level advance timers remain (45s / 60s) but difficulty now also ramps within a level.

### 5. Minimap UI (top-right)
**New file:** `Assets/Scripts/Minimap.cs`

- Square panel anchored top-right of canvas.
- World bounds drawn as square border; player dot at center; enemies = red dots; asteroids = grey dots.
- Only entities within `mapRadius` (e.g. 1000m) shown; positions scaled via `worldToMap` zoom.
- Update every frame; read from `NetworkPlayer[]`, `EnemyShip[]`, asteroid list (cache references, don't `FindObjectsOfType` every frame if possible).
- Depends on a Canvas existing in the scene; warn/log if none found.

### 6. Star Wars-style player ship prefab
**New asset:** `Assets/Models/Player_StarWars.prefab`

- Mirror existing `Player.prefab` structure: `NetworkPlayer`, `ShipWeaponSystem`, `ShipHealth`/`ShieldSystem` (or rely on NetworkPlayer's integrated systems), `ShipCameraRig`, `NetworkRigidbodyReliable`, child firePoints, shield mesh child named "shield".
- New mesh/model if available; otherwise reuse existing ship mesh with adjusted scale/material.
- Keep component field names consistent with `BattleSkyDoctor` expectations (ShipHealth or NetworkPlayer present; replication component present).

### 7. Wire new prefab into network manager
**File:** `Assets/Scripts/Network/BattleSkyNetworkManager.cs`

- Add `[SerializeField] GameObject playerShipPrefab` (already exists; assign `Player_StarWars.prefab` in inspector).
- `OnValidate` already syncs `playerPrefab = playerShipPrefab`.

### 8. Register new spawnables
**File:** scene `Assets/Scenes/SampleScene.unity` + `BattleSkyNetworkManager` inspector

- Add `CapitalShip` prefab to `NetworkManager.spawnPrefabs` so clients can spawn it.
- `BattleSkyDoctor.CheckRegisteredSpawnables` will validate this.

---

## Risks / Edge Cases

- **Entity overload:** Capital ships spawning fighters can exceed entity caps; enforce combined max (capital ships + fighters) per level.
- **Minimap performance:** Avoid `FindObjectsOfType` every frame; maintain registered lists or query once per second.
- **Auto-forward control feel:** Base forward accel must be tunable; expose as serialized field, not hardcoded.
- **Belt center assignment:** `_beltCenter` must be assigned in scene or default to player's initial position; warn if null.
- **Network spawnables:** `CapitalShip` and fighter prefabs need `NetworkIdentity` and must be in `spawnPrefabs`, or clients won't see them.

---

## Validation

1. Run `BattleSkyDoctor` batchmode (`Unity.exe -batchmode -nographics -quit -executeMethod BattleSkyDoctor.Run`); expect exit code 0.
2. Playtest Level 1: asteroid belt persists (respawns), ship drifts forward without input.
3. Playtest Level 2: capital ship appears, spawns fighters, both target and shoot the player.
4. Verify minimap renders with colored dots and bounds border.
5. Verify `NetworkManager.spawnPrefabs` includes bullet, both asteroids, capital ship, fighter.

---

## Open Questions (deferred)

- None blocking. Asset mesh for Star Wars ship can reuse existing model if no new mesh is provided.