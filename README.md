## ⚠️ AI DISCLAIMER: This project is a vibecoded project.
I have not doublechecked every line of code in this repository.

# GK2 Performance

A BepInEx 5 mod for **Graveyard Keeper 2** that removes stutter and freezes while walking, using doors and opening menus. It doesn't change how the game looks or plays, and it never touches your save files.

[Steam Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3809616755)

## What it fixes

| Problem | Fix | Setting |
|---|---|---|
| Walking judders: the player moves at the 50 Hz physics rate while the screen draws faster | Smooths the player's movement between physics steps | `SmoothPlayerMotion` |
| Short freezes while walking, when buildings and props come into view and load from disk | Starts loading them before they reach the edge of the screen | `PrewarmPadding` |
| Hitches the first time you walk into an unvisited part of an area | Loads everything an area needs when it loads, mostly behind the loading screen | `PreloadWorld` |
| Decor pops in all at once, and is thrown away and recreated at the screen edge | Spawns off-screen decor a little at a time and keeps it until it's well out of view | `OffscreenPreload` |
| Freezes when a decor prefab is needed for the first time | Loads it in the background first | `AsyncPrefabWarmup` |
| 0.5–0.8 s freeze on every door or staircase inside the same area | Skips the memory cleanup that causes it; leaving an area, sleeping and loading still clean up | `SkipCleanupOnLocalTeleport` |
| ~0.5 s delay when pressing Esc: the game takes a screenshot for its bug reporter first | Skips the screenshot and turns off the bug reporter (Shift+F5 and its pause-menu button) | `DisableBugReporter` |
| Every menu lags the first time it opens | Loads all menus in the background once you're in the world | `PrefetchMenus` |
| The camera jumps when you open a menu while walking | Keeps the camera moving at a normal pace for the first frames | `SmoothCameraOnMenuOpen` |
| CPU wasted on the game's many log messages | Stops recording where each info or warning message came from; errors still do | `DisableInfoStackTraces` |
| With [No More Running Back](https://steamcommunity.com/sharedfiles/filedetails/?id=3806668942): a second hitch right after a chest opens, because its queue next to chests is built twice | Skips the repeat when the queue was just built | `SkipNotepadRebuild` |
| With [No More Running Back](https://steamcommunity.com/sharedfiles/filedetails/?id=3806668942): ~0.25 s freeze when opening a chest in a big storage area like the yard (21 storages) | Redraws the chest window in one go instead of one storage at a time (270 ms → 35 ms) | `BatchNotepadChestRedraw` |

The last two only do something when No More Running Back is installed. Without it, the mod works the same and those two settings have no effect.

## Install

Requires [BepInEx 5](https://github.com/BepInEx/BepInEx/releases/latest).

**Steam Workshop:** subscribe. With the GK2 Workshop auto-loader installed, it loads on the next game start.

**Manual:** download `GK2Performance-<version>.zip` from the [Releases](../../releases) page and copy its `BepInEx` folder into the game folder (Steam → right-click Graveyard Keeper 2 → Manage → Browse local files) and merge the folders:

```
Graveyard Keeper 2
└─ BepInEx
   └─ plugins
      └─ GK2Performance
         └─ GK2Performance.dll
```

`BepInEx/LogOutput.log` should then contain `GK2 Performance 1.0.3 loaded.`

**Uninstall:** delete `BepInEx/plugins/GK2Performance`, and optionally `BepInEx/config/gk2.performance.cfg`.

## Settings

`BepInEx/config/gk2.performance.cfg` is created on first start. Every fix can be turned off on its own; changes apply after restarting the game.

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `EnableOptimizations` | `true` | Master switch. `false` turns every fix off. |
| Motion | `SmoothPlayerMotion` | `true` | Smooth walking. |
| Motion | `SmoothCameraOnMenuOpen` | `true` | No camera jump when a menu opens while walking. |
| Preload | `PreloadWorld` | `true` | Load everything an area needs when it loads. Uses more memory. |
| Preload | `PreloadBudgetMs` | `2` | Milliseconds per frame the preloader may use during play (more behind the loading screen). |
| Preload | `PreloadInstancesPerPrefab` | `4` | Ready-made copies to keep per object type. `0` only loads them. |
| Streaming | `PrewarmPadding` | `6` | How far outside the screen (world units) objects start loading. The game uses `1`. |
| Streaming | `OffscreenPreload` | `true` | Spawn off-screen decor early and keep it while it's close. |
| Streaming | `OffscreenBudgetMs` | `1.0` | Milliseconds per frame for spawning off-screen decor. |
| Streaming | `OffscreenMaxPerFrame` | `24` | Max off-screen objects spawned per frame. |
| Streaming | `PreloadIncludesSceneObjects` | `false` | Also spawn scene objects early. These can have scripts or sounds, so it's off. |
| Streaming | `AsyncPrefabWarmup` | `true` | Load decor in the background before it's needed. |
| Streaming | `SkipCleanupOnLocalTeleport` | `true` | No freeze on doors and stairs within the same area. |
| Menus | `DisableBugReporter` | `true` | No screenshot delay on Esc; turns off the bug reporter. |
| Menus | `PrefetchMenus` | `true` | Load all menus in the background. |
| Menus | `SkipNotepadRebuild` | `true` | No More Running Back: build the chest queue once instead of twice. |
| Menus | `BatchNotepadChestRedraw` | `true` | No More Running Back: redraw the chest window in one go. |
| Logging | `DisableInfoStackTraces` | `true` | Cheaper log messages. |

## Safety

- Everything happens in memory. The mod never reads or writes save data, so you can add or remove it at any time, even mid-save.
- Nothing looks different. Objects are only loaded or kept early while they're off screen. One small exception: particles from decor just off screen may drift into view, where the game would cut them off at the edge.
- If a game update breaks one of the mod's patches, the mod removes all of them and the game runs unmodded. The reason is logged in `BepInEx/LogOutput.log`.

## Compatibility

Tested with [No More Running Back](https://steamcommunity.com/sharedfiles/filedetails/?id=3806668942) (installs `GK2Notepad.dll`), [GK2 Sort To Nearby Chests](https://steamcommunity.com/sharedfiles/filedetails/?id=3809824680), Better Auto Crafting and GK2 Move Stations.

No More Running Back switches itself off when another mod patches its code. This mod never patches it; it only reads and sets a few of its values and patches the game's own code instead.

## How the No More Running Back fixes work

- **Chest queue built twice:** when a chest opens, the queue next to it is built, and built again two frames later. If the first build just happened, the second is cancelled. The first open of a chest window always keeps both.
- **Chest window freeze:** after a chest window opens or closes, the mod's chest lock feature redraws every storage in the window one by one. After each one the game lays out the whole window again, which is 22 full layout passes in the yard. This mod takes over that redraw: the same storages are redrawn in the same order, and the layout runs once at the end. The window and the queue look exactly the same.

## Building

Needs the .NET SDK. The project references the game's own files from the install folder; nothing is copied into it.

```
dotnet build -c Release
# game installed somewhere else:
dotnet build -c Release -p:GameDir="E:\Steam\steamapps\common\Graveyard Keeper 2"
```

The DLL ends up in `bin/Release/GK2Performance.dll`. `nuget.config` pins nuget.org as the only package source.

**Making a release:** copy the DLL to `dist/BepInEx/plugins/GK2Performance/` (and `workshop/content/BepInEx/plugins/GK2Performance/` for the Workshop), zip the `dist/BepInEx` folder as `GK2Performance-<version>.zip` and attach it to a GitHub Release. Both folders are git-ignored.

## Changelog

- **1.0.3:** with No More Running Back, no ~0.25 s freeze when opening a chest in a big storage area like the yard.
- **1.0.2:** with No More Running Back, no second hitch right after a chest opens.
- **1.0.1:** removed the chest-window speed-ups (they could leave No More Running Back's queue empty); the pause menu's "Report a bug" button is hidden; no camera jump when opening a chest while walking.
- **1.0.0:** first release.
