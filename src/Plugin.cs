using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Performance fixes for Graveyard Keeper 2.
	/// Nothing is written to save files, so removing the DLL restores the game.
	/// </summary>
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "gk2.performance";
		public const string PluginName = "GK2 Performance";
		public const string PluginVersion = "1.1.1";

		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> EnableOptimizations;
		internal static ConfigEntry<float> PrewarmPadding;
		internal static ConfigEntry<bool> OffscreenPreload;
		internal static ConfigEntry<float> OffscreenBudgetMs;
		internal static ConfigEntry<int> OffscreenMaxPerFrame;
		internal static ConfigEntry<bool> PreloadIncludesSceneObjects;
		internal static ConfigEntry<bool> AsyncPrefabWarmup;
		internal static ConfigEntry<bool> DisableInfoStackTraces;
		internal static ConfigEntry<bool> SkipCleanupOnLocalTeleport;
		internal static ConfigEntry<bool> PreloadWorld;
		internal static ConfigEntry<bool> SmoothPlayerMotion;
		internal static ConfigEntry<bool> DisableBugReporter;
		internal static ConfigEntry<bool> PrefetchMenus;
		internal static ConfigEntry<bool> SkipNotepadRebuild;
		internal static ConfigEntry<bool> BatchNotepadChestRedraw;
		internal static ConfigEntry<bool> SmoothCameraOnMenuOpen;
		internal static ConfigEntry<float> PreloadBudgetMs;
		internal static ConfigEntry<int> PreloadInstancesPerPrefab;
		internal static ConfigEntry<bool> HoldLoadingScreen;
		internal static ConfigEntry<float> MaxHoldSeconds;
		internal static ConfigEntry<bool> PrecreateWindows;
		internal static ConfigEntry<bool> PrewarmMovingObjects;
		internal static ConfigEntry<float> PrewarmBudgetMs;
		internal static ConfigEntry<bool> SmoothWorldUpdates;
		internal static ConfigEntry<bool> PrepareManagers;
		internal static ConfigEntry<bool> CollectGarbageWhenHidden;
		internal static ConfigEntry<bool> SkipBackLightOnTerrain;
		internal static ConfigEntry<BackLightMode> BackLight;

		private Harmony harmony;

		/// <summary>Loads the settings and applies the patches. A feature whose patch no longer fits the game is left out.</summary>
		private void Awake()
		{
			Log = Logger;
			BindConfig();

			harmony = new Harmony(PluginGuid);
			try
			{
				PatchGroups.ApplyAll(harmony);
			}
			catch (Exception ex)
			{
				Log.LogError($"Failed to apply patches, running vanilla. Reason: {ex}");
				harmony.UnpatchSelf();
				return;
			}

			ApplyStackTraceSettings();
			DisableInfoStackTraces.SettingChanged += OnStackTraceSettingChanged;

			PerfRunner.Create();
			CameraMenuSmoothing.Init();
			NotepadRebuild.Init();
			NotepadChestRedraw.Init();
			Log.LogInfo($"{PluginName} {PluginVersion} loaded. Optimizations {(EnableOptimizations.Value ? "ON" : "OFF")}.");
		}

		private void BindConfig()
		{
			EnableOptimizations = Config.Bind("General", "EnableOptimizations", true,
				"Master switch for all optimizations. Set to false to compare against vanilla.");

			PrewarmPadding = Config.Bind("Streaming", "PrewarmPadding", 6f,
				new ConfigDescription(
					"World units around the camera view in which buildings/stations/props are loaded in the background before they scroll on screen. " +
					"Vanilla is 1. Larger = fewer hitches when walking, slightly more memory and per-frame bookkeeping.",
					new AcceptableValueRange<float>(1f, 30f)));

			OffscreenPreload = Config.Bind("Streaming", "OffscreenPreload", true,
				"Spawn static decor/terrain pieces just outside the view (inside the prewarm band) spread over several frames, " +
				"and keep them alive until they leave the band. Prevents spawn bursts and edge thrashing. Only affects off-screen objects.");

			OffscreenBudgetMs = Config.Bind("Streaming", "OffscreenBudgetMs", 1.0f,
				new ConfigDescription("Max milliseconds per frame spent spawning off-screen objects ahead of time.",
					new AcceptableValueRange<float>(0.1f, 8f)));

			OffscreenMaxPerFrame = Config.Bind("Streaming", "OffscreenMaxPerFrame", 24,
				new ConfigDescription("Hard cap on off-screen objects pre-spawned per frame.",
					new AcceptableValueRange<int>(1, 500)));

			PreloadIncludesSceneObjects = Config.Bind("Streaming", "PreloadIncludesSceneObjects", false,
				"Also pre-spawn generic scene objects (ChunkableObjectComponent). These may carry scripts/sounds, so this is off by default.");

			SmoothPlayerMotion = Config.Bind("Motion", "SmoothPlayerMotion", true,
				"Enable Rigidbody interpolation on the player so movement is smooth at frame rates above the 50 Hz physics tick " +
				"(fixes judder while walking). Runtime only; takes effect after a restart.");

			SmoothCameraOnMenuOpen = Config.Bind("Motion", "SmoothCameraOnMenuOpen", true,
				"Opening a menu (e.g. a chest) while walking makes one long frame, and the trailing camera covers that whole time in one " +
				"step so it looks like it teleports. For the first frames after a menu opens, advance the camera by a normal frame's time.");

			DisableBugReporter = Config.Bind("Menus", "DisableBugReporter", true,
				"Opening the pause menu makes the game capture and PNG-encode a full screenshot for its bug reporter first (~0.5 s). " +
				"Skip that and disable the Shift+F5 bug report window.");

			PrefetchMenus = Config.Bind("Menus", "PrefetchMenus", true,
				"After the world has loaded, load all UI window prefabs in the background so menus don't hit the disk the first time they open.");

			SkipNotepadRebuild = Config.Bind("Menus", "SkipNotepadRebuild", true,
				"For the \"No More Running Back\" Workshop mod: its queue next to chests is built twice every time a chest opens (a second hitch). " +
				"Skip the repeat when the queue was just built. Does nothing without that mod.");

			BatchNotepadChestRedraw = Config.Bind("Menus", "BatchNotepadChestRedraw", true,
				"For the \"No More Running Back\" Workshop mod: its chest lock feature redraws a chest window one storage at a time, and the game " +
				"re-lays out the whole window after each one (~0.25 s freeze in the yard). Lay the window out once at the end instead. " +
				"Does nothing without that mod.");

			PreloadWorld = Config.Bind("Preload", "PreloadWorld", true,
				"When an area loads, load every building/prop/station prefab it uses and pre-create a few instances into the game's " +
				"pools (nearest first), so walking into unvisited parts of the map doesn't hitch. Uses more memory.");

			PreloadBudgetMs = Config.Bind("Preload", "PreloadBudgetMs", 2f,
				new ConfigDescription("Max milliseconds per frame the preloader may use during gameplay (it uses much more while the loading screen is up).",
					new AcceptableValueRange<float>(0.5f, 16f)));

			PreloadInstancesPerPrefab = Config.Bind("Preload", "PreloadInstancesPerPrefab", 4,
				new ConfigDescription("Idle instances to pre-create per prefab (capped by how often the area uses it). 0 = only load prefabs.",
					new AcceptableValueRange<int>(0, 32)));

			HoldLoadingScreen = Config.Bind("Preload", "HoldLoadingScreen", true,
				"Keep the loading screen up until the area's objects and the menus are loaded, instead of loading them during " +
				"the first seconds of play (which hitches).");

			MaxHoldSeconds = Config.Bind("Preload", "MaxHoldSeconds", 20f,
				new ConfigDescription("Longest time the loading screen is kept up for this. The rest then loads during play.",
					new AcceptableValueRange<float>(1f, 120f)));

			PrepareManagers = Config.Bind("Preload", "PrepareManagers", true,
				"Behind the loading screen, set up the game managers that are otherwise searched for or created the first time they are " +
				"used (for example the first time a worker starts a craft), which freezes the game for tens of milliseconds.");

			PrecreateWindows = Config.Bind("Menus", "PrecreateWindows", true,
				"Behind the loading screen, create the character/inventory window and fill the item cell pools that chest and inventory " +
				"windows use, instead of creating them the first time a menu opens.");

			PrewarmMovingObjects = Config.Bind("Streaming", "PrewarmMovingObjects", true,
				"Use the PrewarmPadding band for moving world objects (NPCs, workers, animals) too, so they load before they walk into view.");

			SmoothWorldUpdates = Config.Bind("World", "SmoothWorldUpdates", true,
				"After a slow frame the game runs all missed world logic steps (crafting, conveyors, NPCs) at once, which slows the next " +
				"frame too. Run at most one extra step per frame instead; the rest follows in the next frames. No game time is lost.");

			CollectGarbageWhenHidden = Config.Bind("World", "CollectGarbageWhenHidden", true,
				"Run the garbage collector while the screen is black (door fades, end of a loading screen), so its ~20 ms pause " +
				"happens less often during play.");

			PrewarmBudgetMs = Config.Bind("Streaming", "PrewarmBudgetMs", 2f,
				new ConfigDescription("Milliseconds per frame for loading buildings, stations, trees and NPCs that enter the band around the " +
					"screen. Objects over budget start a frame later. 0 = the game's behaviour (all at once).",
					new AcceptableValueRange<float>(0f, 16f)));

			AsyncPrefabWarmup = Config.Bind("Streaming", "AsyncPrefabWarmup", true,
				"Load a decor prefab from disk asynchronously before the game needs it, instead of the game's synchronous (frame-freezing) load.");

			DisableInfoStackTraces = Config.Bind("Logging", "DisableInfoStackTraces", true,
				"Don't capture a stack trace for every Info/Warning log line the game writes. Errors keep their stack traces.");

			SkipCleanupOnLocalTeleport = Config.Bind("Streaming", "SkipCleanupOnLocalTeleport", true,
				"Doors/stairs that stay in the same area make the game run UnloadUnusedAssets + a full GC behind the fade " +
				"(0.5-0.8 s freeze). Skip it for same-area teleports. Cross-area teleports, sleeping and loading keep the vanilla cleanup.");

			SkipBackLightOnTerrain = Config.Bind("Lighting", "SkipBackLightOnTerrain", true,
				"The game's second sun light (the back light) points upward, so it never lights the ground, yet the game still draws " +
				"the ground an extra time for it every frame. Skip that extra pass for the ground only. The picture stays exactly the same. " +
				"Only used with BackLight = Normal.");

			BackLight = Config.Bind("Lighting", "BackLight", BackLightMode.Soft,
				"The game's back light adds a faint bluish rim to tree leaves and some walls, but draws every object once more (about a fifth of " +
				"the frame time outdoors). Normal: as in the game. Soft: the same bluish light, blended into the normal lighting pass; " +
				"softer and more even instead of crisp rims. Off: no back light. This is the only setting that changes the picture.");
		}

		private void OnStackTraceSettingChanged(object sender, EventArgs e)
		{
			ApplyStackTraceSettings();
		}

		private static void ApplyStackTraceSettings()
		{
			var mode = DisableInfoStackTraces.Value ? StackTraceLogType.None : StackTraceLogType.ScriptOnly;
			Application.SetStackTraceLogType(LogType.Log, mode);
			Application.SetStackTraceLogType(LogType.Warning, mode);
		}
	}
}
