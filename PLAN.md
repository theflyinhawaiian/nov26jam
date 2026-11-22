# Plan: 2-player browser MOBA shooter (Unity 6 + FishNet + AWS)

## 1. Locked decisions

| Area | Decision |
|---|---|
| Engine | Unity 6 (6000.6.10f1). Web build for the client, Linux Dedicated Server build for the server. One project. |
| Netcode | FishNet 4.7.4 + Bayou 4.1.5 (WebSocket) transport. Bayou is also used in the editor, so dev matches prod. |
| Authority ("middle") | Clients own their own movement; the server checks it's plausible. The server decides all hits (rewinding to the shot's tick). The server owns all health, projectiles, the nexus and match state. |
| Players | 2 per match, competitive, anonymous nickname, keyboard and mouse, desktop browsers only. |
| Lobby | A **Find match** queue that calls `MatchManager.CreateMatch(...)`. Room codes can be added later as a second caller. |
| Match isolation | One server process. Each match loads its own copy of the arena scene with its own 2D physics; players only see objects in their own match. |
| Out of scope | Reconnect, late join, chat, accounts, art, sound, mobile, towers, minions, jungle monsters. |
| Hosting | One EC2 t3.micro in us-east-1 with an Elastic IP. Caddy serves the web build and handles TLS and WSS; the game server runs as a systemd service on the same box. |
| IaC | Terraform, with local state. |
| DNS | Porkbun, one manual A record: `jam.<domain>` → Elastic IP. |
| Deploy | Manual: local builds plus `scripts/deploy.sh`. |
| Budget | $30 total for a one-month life. About $12 a month expected. `terraform destroy` at the end. |

## 2. Architecture

```
Browser ──HTTPS──▶ Caddy :443 ──▶ /srv/web (Unity Web build)
   │
   └──WSS /  ───▶ Caddy :443 ──ws──▶ 127.0.0.1:7770  game-server (FishNet + Bayou, headless Linux)
                                                      ├─ Menu/Lobby (connections, nicknames, queue)
                                                      ├─ Arena copy #1 (match A, own 2D physics)
                                                      └─ Arena copy #2 (match B, own 2D physics)
```

- One origin means no cross-origin issues and one Let's Encrypt certificate, which Caddy handles automatically.
- Turn off Unity's web build compression and let Caddy compress instead (`encode zstd gzip`), so no special headers are needed for `.br` files.
- **Settled in M1:** Bayou has no URL-path setting, so the client connects to `wss://host/` and Caddy sends requests with `Upgrade: websocket` to the game server and serves everything else as static files. That keeps it to one port.

## 3. Arena

One screen is 32 × 18 units (orthographic camera size 9, fixed 16:9 canvas), so the arena is **128 × 90 units** (4 screens wide, 5 tall). It's mirrored left to right: Blue on the left, Red on the right.

```
y=90 ┌──────────────────────────────────────────────┐
     │================ TOP LANE ====================│  flank route
y=72 │::::: jungle: one-way platforms, cover :::::::│
y=54 │[NEXUS]============ MID LANE ==========[NEXUS]│  nexus + spawn
y=36 │::::: jungle: one-way platforms, cover :::::::│
y=18 │================ BOTTOM LANE =================│  flank route
y=0  └──────────────────────────────────────────────┘
       Blue half              │              Red half
```

- **Nexus:** one at each outer edge of the middle lane. Players spawn just inside their own nexus.
- **The top and bottom lanes are flank routes,** so the jungle near each base has to lead into the middle lane: drop-throughs down from the top, jump pads up from the bottom.
- **Moving between levels:** drop through one-way platforms going down (down + jump), jump pads going up. Put pads near each base and near the centre so defenders can rotate as fast as attackers.
- **Greybox only:** solid ground, one-way platforms, and jump pads as triggers.

## 4. Gameplay systems

**Player**
- Kinematic body moved by hand with `Cast` checks against the match's own 2D physics.
- Running, jumping, gravity, one-way platforms and jump pads.
  - One-way platforms go on their own layer. Ignore that layer when moving up, or for about 0.25 seconds after a drop-through input.
  - Jump pads are triggers that set your upward speed, handled on the owning client.
- Aim follows the mouse in any direction. Left click fires; the number keys 1/2 switch weapons.
- 100 HP. **Fixed 10-second respawn** at your base, via a server-forced teleport.

**Weapons** are ScriptableObjects shared by client and server. Each has a **fire-rate cooldown only**: no ammo, no reloading.

| | Rifle (hitscan) | Grenade launcher (projectile) |
|---|---|---|
| Range | About 24 units (¾ of a screen), so the middle lane can't be sniped from spawn | Limited by its arc. Tune so speed² ÷ gravity is about 20 units. |
| Damage | ~10 | ~40 at the centre, linear falloff, radius ~2.5 |
| Cooldown | ~0.2 seconds | ~1.0 second |
| Notes | Raycast blocked by solid ground, not by one-way platforms | Explodes on terrain, players or the nexus. **Damages the shooter too.** Despawns after 5 seconds. |

**Nexus:** ~1500 HP, damaged by both weapons. Static and owned by the server.

**Match flow**
- Menu → nickname → **Find match** → waiting screen → arena with a 3-second countdown → play.
- No time limit.
- Ends when a nexus is destroyed, or when a player disconnects (the other player wins). Then results, then back to the menu.

**HUD:** your HP, both nexus HP bars, current weapon and cooldown, respawn countdown, and an arrow at the screen edge pointing to the off-screen opponent.

**Camera:** follows the player smoothly and stops at the arena edges.

## 5. Netcode design

| Thing | Who decides | How |
|---|---|---|
| Player position and aim | Owning client | FishNet `NetworkTransform` with client authority; others see it slightly delayed and smoothed. |
| Movement plausibility | Server | Each tick, compare distance moved with what's possible (run speed, jump-pad speed, maximum fall speed, plus tolerance). On failure, send a forced-teleport RPC to the owner. Respawns use the same RPC. |
| Hitscan hits | Server | The client fires immediately and draws a local tracer, then sends weapon, aim, origin and `PreciseTick` (`LastPacketTick`) to the server. The server checks the cooldown and the origin, looks up each opponent in its own per-tick `PositionHistory` at that tick (minus a tunable offset) and tests the ray against those hitboxes. It raycasts terrain in the match's own 2D physics, then applies damage and relays the tracer. **FishNet's `RollbackManager` is a Pro feature and is stubbed in the free package**, so we use our own `PositionHistory` + `LagCompensation`. |
| Grenades | Server | Not network objects. The client sends start position, velocity and tick. After checking, the server broadcasts them, and every client simulates the same arc. **Only the server detects the impact.** Other players' clients get the grenade late, so they play the arc from the thrower as they see them, on a clock running about 1.33× until it catches up with the real grenade (fast-forwarding instead made it pop in several units away from the thrower). It broadcasts "exploded at this position", and clients remove their copy and show the explosion. |
| Player and nexus health | Server | `SyncVar<int>` |
| Death, respawn, match state | Server | Synced state plus RPCs (countdown, respawn time, winner). |
| Nickname | Server | Sent by the client after connecting; trimmed to 16 characters and stored per connection. |

**Rules for running several matches in one process:**
- **Every server-side physics query goes through `scene.GetPhysicsScene2D()`,** never the global `Physics2D` calls. Wrap this in a helper.
- Find objects through the `Match` object, never with `FindObjectOfType` or static singletons.

**Matchmaking:**
- A FIFO queue. When it holds two players, call `CreateMatch(p1, p2)`.
- `CreateMatch` loads a stacked copy of the arena scene for just those two connections, with its own physics, sets up the players and nexuses, and tracks the match. It unloads the copy when the match ends.
- Room codes later: a `Dictionary<string, Match>` plus a version of `CreateMatch()` that waits for a second player.

**Server settings:** tick rate 30 Hz, `Application.targetFrameRate = 30`, run with `-batchmode -nographics`.

## 6. Project layout

```
nov26jam/
├─ PLAN.md
├─ .gitignore
├─ Game/                      (Unity project)
│  └─ Assets/
│     ├─ FishNet/             (vendored FishNet 4.7.4R + Bayou 4.1.5 from the official .unitypackage releases)
│     └─ _Game/
│        ├─ Scenes/           Boot, Menu, Arena
│        ├─ Scripts/
│        │  ├─ Shared/        weapon data, constants, match-physics helper
│        │  ├─ Net/           network setup, matchmaking, match manager
│        │  ├─ Gameplay/      player controller, weapons, health, nexus, respawn
│        │  └─ UI/            menu, HUD, results
│        ├─ Data/Weapons/
│        └─ Prefabs/
├─ infra/
│  ├─ main.tf                 default VPC, security group, EC2, EIP, budget alarm
│  ├─ variables.tf            my_ip, domain, key name
│  ├─ outputs.tf              public IP (goes into Porkbun)
│  ├─ user_data.sh            Caddy, game user and folders, systemd unit
│  └─ Caddyfile
└─ scripts/
   └─ deploy.sh               copies Builds/Web → /srv/web and Builds/Server → /opt/game, restarts the service
```

FishNet and Bayou are vendored into `Assets/FishNet` from their official `.unitypackage` releases. The git/UPM sources at those tags are missing files (Bayou's `Request.cs`, parts of FishNet's Yak) and don't compile. To upgrade, import a newer `.unitypackage` over the top. Multiplayer Play Mode is 3.0.0, the version the editor pins.

## 7. Infrastructure

- **EC2:** t3.micro, Ubuntu 24.04 x86-64, 8 GB gp3, default VPC, us-east-1.
- **Security group:** 80 and 443 open to everyone (plus 7771 if `/ws` doesn't work); 22 only from your IP.
- **Elastic IP:** keeps the Porkbun record stable.
- **user_data:** installs Caddy and writes the Caddyfile, then sets up `game-server.service`:
  - runs `/opt/game/Server.x86_64 -batchmode -nographics -logfile -` (logs go to journald: `journalctl -u game-server`)
  - restarts on failure and runs as a non-root user.
- **Budget alarm:** `aws_budgets_budget` at $25, emailing on actual or forecast spend.
- **Estimated cost per month:** about $7.60 (t3.micro) + $3.65 (public IPv4) + $0.65 (disk) ≈ **$12**. Outbound data is well within the free 100 GB.
- **End of the month:** `terraform destroy`, then delete the Porkbun record.

## 8. Milestones

**M0 · Setup.** `git init`, `.gitignore`, Unity project, FishNet and Bayou at pinned versions, Multiplayer Play Mode.

**M1 · End-to-end test with placeholder squares.** This is the riskiest milestone and comes first. Locally verified: two concurrent isolated matches and server-side rewound hitscan (median rewind error ≈ 0.2 units at 0 ms). Left to verify: browser over WSS on AWS, and tuning the rewind offset under real latency.
- Two squares move with client-authoritative sync in a browser, over WSS, through Caddy, on EC2 created by Terraform.
- **Two matches run at once in one process and can't see each other.**
- **`RollbackManager` plus a raycast in the match's own 2D physics hits a moving target** under 100–150 ms of simulated lag.
- Settle whether Bayou can use the `/ws` path or needs `:7771`.
- **Done when** two browsers on different machines see each other move on `https://jam.<domain>` and the rollback test passes.

**M2 · Movement and arena.** Kinematic controller, one-way platforms, drop-through, jump pads, camera, the 128×90 greybox, spawns.

**M3 · Combat.** Done and verified locally with bots: deaths and respawns, grenade splash, nexus destroyed → win/lose message.
- Rifle with server-side rewind.
- Grenades simulated identically on every client, explosions decided by the server.
- Health, death, 10-second respawn.
- Nexus health and the win condition.

**M4 · Match flow.** Menu, nickname, Find match queue, waiting screen, countdown, results, disconnect = win, unloading the arena copy. Also retry the lobby connection automatically: in M1, Bayou sometimes dropped connections when 4 clients connected at the same instant.
- Done and verified locally with bots: 3-second countdown (no moving or firing until it ends), nexus win → results → requeue into a second match, disconnect = win, two concurrent matches, arena copies unloaded, reconnect with backoff when the server isn't up yet.
- `GameMenu` (IMGUI) is the player-facing flow; Esc opens a "leave match" prompt. `DebugHud` is now debug and stats only.
- A match where a player hasn't loaded the arena after 20 seconds is cancelled with no winner.
- Not done: reconnecting into a match in progress (out of scope). The title "NEXUS DUEL" is a placeholder.
- Deployed (`bde09e3`) and played in browsers on production. Fixed after a playtest: other players' grenades appeared in mid-air away from the thrower (see Grenades in §5).

**M5 · Validation and HUD.** Server checks for movement and cooldowns, shot-origin check, HUD, opponent arrow, tuning.

**M6 · Ship and playtest.** Deploy, playtest, tune, then destroy at the end of the month.

**Local testing:**
- Multiplayer Play Mode: the main editor runs the server plus a client; a virtual player is the second client.
- FishNet's latency simulator for lag.
- Deploy a browser build to the production box at the end of each milestone.

## 9. Building and deploying

Commit first, so the build stamp shows the commit instead of "<hash>-dirty". Close the Unity editor, because batch mode can't open a project that's already open. Then, in PowerShell:

```powershell
scripts\build-and-deploy.ps1                      # build server + web, then deploy both
scripts\build-and-deploy.ps1 -Target web          # or: -Target server
scripts\build-and-deploy.ps1 -SkipDeploy          # build only
scripts\build-and-deploy.ps1 -SkipBuild           # deploy the existing builds
```

The script works from the repo root, because Unity deletes the output folder before building and fails if a shell is sitting in it. It stops if tracked files have uncommitted changes (`-AllowDirty` overrides this) or if the editor is open. It builds the Linux dedicated server into `Builds/Server` (about 1 minute) and the web client into `Builds/Web` (about 4 minutes). Logs go to `Builds/build-server.log` and `Builds/build-web.log`, and the script prints each build's result and stamp. The server build reorders `Game/Assets/DefaultPrefabObjects.asset` (harmless), and the script undoes that so the web build's stamp isn't "-dirty". Finally it runs `scripts/deploy.sh` through Git Bash. Deploying needs the SSH key at `~/.ssh/nov26jam`. `-Unity <path>` and `-GitBash <path>` override the default install paths.

`deploy.sh` copies the builds over SSH to the box. The server build goes to `/opt/game` and restarts `game-server`; the web build goes to `/srv/web`. It ends by printing the service status.

**Check it worked:**

```bash
ssh -i ~/.ssh/nov26jam ubuntu@jam.pmullins.dev 'systemctl is-active game-server; ss -ltn | grep 7770'
ssh -i ~/.ssh/nov26jam ubuntu@jam.pmullins.dev 'sudo journalctl -u game-server -n 50 --no-pager'
```

Then hard-refresh https://jam.pmullins.dev/ (Ctrl+F5). The debug panel in the top-left shows the client and server build stamps; both should match the commit you deployed.

**Before DNS resolves**, point the script at the Elastic IP: `scripts\build-and-deploy.ps1 -DeployHost <ip>` (or `DEPLOY_HOST=<ip> scripts/deploy.sh` from Git Bash).

**Local bot test before deploying** (optional), in Git Bash from the repo root:

```bash
UNITY="/c/Program Files/Unity/Hub/Editor/6000.6.10f1/Editor/Unity.exe"
"$UNITY" -batchmode -quit -projectPath Game -executeMethod Jam.EditorTools.BuildScripts.BuildWindowsDev -logFile Builds/build-win.log
Builds/Win/Game.exe -batchmode -nographics -server -logFile Builds/srv.log &
Builds/Win/Game.exe -batchmode -nographics -client -smoketest -smokemode duel -smokeduration 25 -logFile Builds/a.log &
Builds/Win/Game.exe -batchmode -nographics -client -smoketest -smokemode duel -smokeduration 25 -logFile Builds/b.log &
sleep 30; grep -h "SMOKE RESULT" Builds/a.log Builds/b.log; taskkill //F //IM Game.exe
```

Other bot options: `-smokemode attack|passive|selfnade`, `-smokerequeue` (queue again after each match), `-latency <ms>` (simulated lag).

## 10. Risks

| Risk | What to do |
|---|---|
| ~~`RollbackManager` may not work with 2D colliders~~ | It's stubbed in the free FishNet, so we use the fallback: `PositionHistory` + `LagCompensation` (done in M1). |
| ~~Bayou + Caddy WebSocket path~~ | Settled: Caddy matches on the `Upgrade` header at `/`. |
| Browsers throttle hidden tabs; no reconnect | Accept it. Set FishNet's timeout to about 15 seconds. |
| TCP hiccups cause stutter | ~100 ms smoothing buffer on remote players. |
| No time limit means defensive play can stall | Tune nexus HP; add a time limit later only if playtests stall. |
| Web build size and load time | Caddy compression, IL2CPP "Smaller" code optimization, stripping, no assets. |

## 11. Left for later

Room codes, towers, minion waves, jungle monsters, spawn protection, more weapons, reconnect, art and sound.
