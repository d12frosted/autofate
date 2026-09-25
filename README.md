# Autofate
![Autofate logo](https://raw.githubusercontent.com/notmugi/autofate/refs/heads/main/Autofate/images/icon.png)  

An FFXIV [Dalamud](https://github.com/goatcorp/Dalamud) plugin that automates FATE farming,
built on puni.sh integrations (ECommons foundation, EzIPC inter-plugin communication).

> [!WARNING]
> **This plugin is VIBE CODED.** It was built almost entirely by prompting an AI, and i do NOT claim to be a programmer. I want this to be abundantly clear so there is no discourse. Use it if you like, or don't if you don't. It works well in practice, but the code is what it is. Use
> at your own risk; Automation is frowned upon and can get you banned. but frankly puni.sh users already know this so idk why i'm even mentioning it.
>
> **Pull requests and help are very welcome.** If you're a real developer and want to clean
> things up, fix the work-in-progress features, or add anything, please open an issue or PR. I'd be happy to take a look and include more human-authored code, as I'd like to get this code off of the slop codebase eventually. 

---

## Features

### Farming modules:
- **Leveling**: farm fates to level your current class to a target level. Automatically teleports to best zone for your current level, with a configurable level based zone cap).
- **Single Zone**: farm one zone (set it to your current zone or pick from a list).
- **Shared FATEs**: rotate through ShB / EW / DT zones, track completion and leave when complete. Only zones with FATEs at your level (plus the "levels above" allowance) are visited, best fit first: zones whose lowest FATEs are at or below your level, highest first, then ones only reachable through the allowance.
- **Atma**: the 12 ARR zodiac relic zones.
- **Demiatma**: the 6 Dawntrail phantom relic zones.
- **Luminous Crystals** & **Memories**: the Heavensward anima relic zones.
- **Manual**: build your own zone list, set fates-per-zone, reset counters, optionally loop it or terminate it when complete.

Collection modes (Atma/Demiatma/Luminous/Memories) track the required items in your inventory, moves on when a zone's items are done, and stops when the whole list is collected.

### Fate engine
- Enable/disable fate types: **Battle, Boss, Defend, Escort** (Collect is WIP, see below).
- **Collect fates** are farmed end to end: gather while nothing is hitting you, fight when the ground is empty, hand in when the bag is full. A batch goes in as soon as it is enough to finish the fate instead of waiting for the next fight, and in the last minute we deliver whatever we hold and move on rather than farming a timer that can no longer pay out.
- **A turn-in is a committed run.** Mobs on our back are what makes a hand-in drag on (an interact needs us standing still), so we finish off whatever is nearly dead, outrun the rest along the spawn center to NPC line where fate mobs leash, then turn in clean.
- Prioritize fates **lower on their timer** instead of closest, with a minimum-time cutoff.
- **A fate that has not started yet is started, not waited out.** A fate that sits in the list without a timer is waiting for someone to talk to its "!" NPC. It competes with the running fates on the full duration it gets once started: we travel to it, find the NPC and start it ourselves, and try the talk again if it does not take. If there is no start NPC to be found we hold the spawn point for up to two minutes in case it goes live on its own, then leave it alone until it does.
- **The Forlorn and the Forlorn Maiden are killed first.** Whenever one is up in our fate we drop whatever we were doing and go kill it: the Twist of Fate buff it leaves behind raises EXP and gemstones on every fate until we leave the zone, which is worth more than this one fate, and it despawns on its own if we take our time. It outranks the sticky target, stray aggro, mass-pull, the defend/escort peel, and collect-fate gathering.
- Run fates up to **N levels above** your level (default 2).
- **Auto level-sync** to the target fate: Sync upon arrival to fate so as not to accidentally sync with fates along the path.
- **Pull style**: Safe, Yolo, or Auto (the default: Yolo on a tank, Safe on everything else).
  - **Safe** fights one mob at a time. Anything of the fate's hitting you comes first; otherwise it picks the mob with the fewest idle enemies around it, weighed against distance, so melee DPS and healers don't walk into packs. A mob standing with others is kited: we stand 18y from it on the side away from its neighbours, pull it with the job's ranged attack (Unmend, Piercing Talon, Shield Lob, ...; ranged jobs and healers just attack), and melee jobs back off a bit further so it comes to them away from the pack. Monk has no ranged attack and walks in; a pull that doesn't land within a few seconds falls back to walking in too.
  - **Yolo** is mass pull, with a configurable enemy cap (can only adhere to this as best as reasonably possible).
- **Mass-pull stays local.** With nothing on us we walk to the nearest mob, however far. Once something is on us, only mobs within the pull radius (20y by default) get pulled; the rest wait until the pile is dead. Walking further would drag the pile along until it drops aggro, and then we'd walk back for it.
- **FATE blacklist**: never navigate to named fates.
- **Follow party leader**: skip our own pathing and just run whatever fate the leader drops us
  in (great for multiboxing & farming with friends).

### Combat
- **Rotation backend:** Wrath Combo or Rotation Solver Reborn.
- **Movement / AOE dodging:** **BossMod Reborn AI is required**. it handles all AOE dodging and general avoidance tech.
- If a required backend isn't installed, Autofate refuses to start and tells you in chat.

### Travel
- vnavmesh navigation with automatic mounting / flight (pick your mount).
- Lifestream for between-zone teleporting and an optional end-of-run command + chocobo leveling (see below)
- Optional **teleport to the nearest aetheryte**: when the picked fate is far away, hop to the attuned aetheryte closest to it instead of flying the whole zone (off by default, costs gil, with distance and minimum-saving thresholds).
- **Landing from above.** Flights to a fate aim a few yalms above the landing spot, never at it, so vnavmesh can't stop short underneath a floating island (Ultima Thule, Elpis). Arrival means being on the spot and at its height; if we do end up under the floor, we climb to open air above the spot first and land from there.
- **Aggro picked up in the air is ignored.** Nothing on the ground can reach you while flying, so a mob that tags you mid-trip never costs you the flight: we stay on the mount and keep going. On foot, only something actually hitting you (or your chocobo) stops the trip.
- **Stray aggro is a single-target fight.** While we kill the mob that stopped us, BossMod is pinned to that one target: no auto-targeting and no AOE actions, so the passive mobs standing next to it are left alone instead of being pulled in. The fate-clearing targeting comes back as soon as we move on.

### Chocobo
- Companion stance (Defender/Attacker/Healer) with **auto-Healer when your or the chocobo's HP drops** below a user-defined threshold.
- Auto re-use **Gysahl Greens** before the companion times out.
- **Auto leveling:** travel home, recall, stable, train, feed Thavnairian Onions and stop at your target rank. You can define the location of your houses chocobo stable and have it automatically use the onion to level your chocobo! **(requires onions to be in inventory)**

### Upkeep
- **Food & potions:** scans the inventory for your food and pots, and it will use them before the timer is up.
- **Auto repair:** self-repair with Dark Matter (stops farming if you run out), with a
  durability threshold.

### Gemstones
- Bicolor gemstone buy-list with per-item targets (or continuous buying) and a buy threshold.
- Auto-travel to your captured vendor, buy, and return to farming.
- Tracks **gross gemstones gained** across the session.

### Runtime overlay
- While farming, the main window steps aside for a small always-on overlay: what the bot is doing right now, session stats (FATEs, gemstones held vs the cap, level, deaths), and a progress bar for whatever the run is working towards (shared FATE rank, collectables, target level, gemstone target).
- Pause / resume, stop, and a cog that reopens the settings window without stopping the run. Clicking the status line jumps straight to the Status tab.
- **Pause** stops navigation and shuts the combat backends down while keeping the session, its counters and its current state. It cannot unwind dialogue or a cutscene that is already in flight, and with the AI backend off nothing is dodging for you, so pausing mid-pull will get you killed. A FATE held over a long pause has usually expired, so resuming picks a new one.
- Compact one-line mode, opacity, lock position, or turn the overlay off entirely in the Status tab.

### Stop triggers
- Stop at desired level, gemstone count, chocobo max level, vendor targets met, or (in leveling mode) after dying twice. <- this is to prevent infinitely running overnight and dying over and over like an idiot if you reach a level you do not have gear for.

---

## WIP features

These are present in the code but **disabled/greyed in the UI**. These are features that need
contributions. Search the code for `TODO(WIP)` to find each one:

- **Mender NPC repair**: Currently, only self-repair is automated; NPC routing is unfinished. I didnt have the time, money, or energy to implement a second form of repairs, i work a full time job and just wanted a product that is in a good working state. if you know how to get this working, that would be fantastic. an example of navigating the repair window is currently already available through self repair, and an example of setting a desired npc location is available through the bicolor shop and alternatively through the chocobo stable section.
- **TODO**: i need to add a toggle that allows for occasional random jumps to be thrown in during navigation to look more human and prevent getting stuck
---
## Installing

**As a repo (recommended):** `/xlsettings` → **Experimental** → **Custom Plugin Repositories**,
paste:

```
https://raw.githubusercontent.com/notmugi/autofate/main/repo.json
```

**As a dev plugin:** add the path to `Autofate.dll` under **Dev Plugin Locations**, then load it
from **Plugin Installer → Dev Tools**.

### Commands
- `/autofates`, `/autofate`, `/af`: open the window.
- `/af start`, `/af stop`, `/af toggle`: control farming.
- `/af pause`: pause or resume a running session.

## Required companion plugins
- **vnavmesh**: required for navigation.
- **BossMod / BMR**: required for combat movement.
- **Wrath Combo**, **Bossmod / BMR**, or **Rotation Solver Reborn**: requires at least one, for the rotation.
- **Lifestream**: required Traveling between zones.
- **TextAdvance** required. Spams through dialogue options and hands in collection fate items.

---
## Building

Uses **ECommons as a git submodule** (built from source).

```bash
git clone https://github.com/notmugi/autofate
cd autofate
git submodule update --init --recursive
dotnet build Autofate/Autofate.csproj -c Release
```

Output: `Autofate/bin/x64/Release/Autofate.dll`. The build references your local Dalamud dev
libraries at `~/.xlcore/dalamud/Hooks/dev/` (override with `-p:DalamudLibPath=...`). Targets
`net10.0-windows`, Dalamud API level 15.

### Tests

Decision logic that doesn't need the game (which mob to pull next, and so on) lives in
`Autofate/Logic` and is unit tested by `Autofate.Tests`, which compiles those files directly
instead of referencing the plugin, so it runs anywhere without Dalamud:

```bash
dotnet test Autofate.Tests
```

Code in `Autofate/Logic` must only depend on the BCL.

### Installing a local build (XIV on Mac)

`scripts/install.sh` builds the plugin and registers the output as a dalamud dev plugin in a
XIV on Mac setup, using the dalamud libraries the game already ships with. It also knows about
`--release`, `--no-build`, `--dry-run`, `--status` and `--uninstall`.

```bash
./scripts/install.sh
```

Once registered, running it again works with the game open: dalamud reloads the plugin itself.
Changing the registration does not, since dalamud rewrites its config when the game exits, so
the script refuses to do that while FFXIV is running.
## Updating the published plugin (maintainer)

```bash
./update-build.sh        # clean rebuild, repackage latest.zip, sync repo.json versions
git commit -am "Update build" && git push
```

The version comes from `Autofate/Autofate.csproj` (`<Version>`). Bump it there before running
the script. See [SETUP.md](SETUP.md) for details and the icon/description instructions.

## Contributing

Issues and PRs are welcome! I don't have any formal formatting necessities, just use common sense.
any and all contributions will help make this plogon less slop and more human. :)
