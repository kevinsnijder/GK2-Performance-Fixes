using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GK2Performance
{
	internal static class BugReporterSwitch
	{
		/// <summary>True when the bug reporter should be disabled.</summary>
		internal static bool IsOn()
		{
			return Plugin.EnableOptimizations.Value && Plugin.DisableBugReporter.Value;
		}
	}

	[PatchGroup(Features.BUG_REPORTER)]
	[HarmonyPatch(typeof(BugReportScreenshot), nameof(BugReportScreenshot.Capture))]
	internal static class BugReportScreenshotPatch
	{
		/// <summary>Skips the ~0.5 s screenshot the game takes for its bug reporter when the pause menu opens.</summary>
		private static bool Prefix(Action onDone)
		{
			if (!BugReporterSwitch.IsOn())
			{
				return true;
			}
			onDone?.Invoke();
			return false;
		}
	}

	[PatchGroup(Features.BUG_REPORTER)]
	[HarmonyPatch(typeof(UIGamePauseWindow), "CreateBugReportButton")]
	internal static class PauseBugReportButtonPatch
	{
		/// <summary>Hides the pause menu's "Report a bug" button, which does nothing without the bug reporter.</summary>
		private static bool Prefix()
		{
			return !BugReporterSwitch.IsOn();
		}
	}

	[PatchGroup(Features.BUG_REPORTER)]
	[HarmonyPatch(typeof(BugReporter), nameof(BugReporter.TryOpen))]
	internal static class BugReporterOpenPatch
	{
		/// <summary>Disables the Shift+F5 bug report window.</summary>
		private static bool Prefix()
		{
			return !BugReporterSwitch.IsOn();
		}
	}

	/// <summary>
	/// The game creates the character window the first time it opens, which freezes for a moment.
	/// It is created behind the loading screen instead.
	/// </summary>
	internal static class WindowPrecreate
	{
		private const string CHARACTER_WINDOW = "CharacterWindow";
		private const int CREATES_PER_FRAME = 40;

		private static readonly KeyValuePair<Type, int>[] PoolTargets =
		{
			new KeyValuePair<Type, int>(typeof(UIItemCell), 480),
			new KeyValuePair<Type, int>(typeof(InventoryWidget), 24),
			new KeyValuePair<Type, int>(typeof(BigItemInventoryWidget), 6),
			new KeyValuePair<Type, int>(typeof(BagInventoryWidget), 6)
		};

		private static readonly FieldInfo PoolsCacheField = AccessTools.Field(typeof(UIPrefabsPooler), "poolsCache");
		private static readonly FieldInfo PoolField = AccessTools.Field(typeof(UIPrefabPool), "pool");

		private static bool windowPending;
		private static bool cellsPending;

		internal static bool IsBusy
		{
			get
			{
				return windowPending || cellsPending;
			}
		}

		internal static void Begin()
		{
			var enabled = Plugin.EnableOptimizations.Value && Plugin.PrecreateWindows.Value;
			windowPending = enabled;
			cellsPending = enabled;
		}

		/// <summary>Runs every frame. Creates the window once its prefab is loaded, and fills the item cell pools.</summary>
		internal static void Tick()
		{
			if (cellsPending)
			{
				cellsPending = FillPools();
			}
			if (!windowPending || !UiWindowPrefetch.IsLoaded(CHARACTER_WINDOW))
			{
				return;
			}
			windowPending = false;
			try
			{
				LazyUI.GetWindow<CharacterWindow>();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Could not create the character window early: {ex.Message}");
			}
		}

		/// <summary>
		/// Chest and inventory windows take item cells from pools that start almost empty, so the first big chest
		/// creates hundreds at once. The pools are filled a few per frame. Returns true while not done.
		/// </summary>
		private static bool FillPools()
		{
			try
			{
				var pooler = UIPrefabsPooler.Instance;
				if (pooler == null || PoolsCacheField == null || PoolField == null)
				{
					return false;
				}
				var cache = PoolsCacheField.GetValue(pooler) as IDictionary<Type, UIPrefabPool>;
				if (cache == null)
				{
					return false;
				}
				var created = 0;
				foreach (var target in PoolTargets)
				{
					UIPrefabPool prefabPool;
					if (!cache.TryGetValue(target.Key, out prefabPool) || prefabPool == null)
					{
						continue;
					}
					var pool = PoolField.GetValue(prefabPool) as Pool;
					while (pool != null && pool.Objects.Count < target.Value)
					{
						if (created >= CREATES_PER_FRAME)
						{
							return true;
						}
						pool.AddObjectToPool();
						created++;
					}
				}
				return false;
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Could not fill the menu pools: {ex.Message}");
				return false;
			}
		}
	}

	/// <summary>
	/// The game loads each menu from disk the first time it opens, which lags.
	/// After the world has loaded, all menus are loaded in the background and kept in memory.
	/// </summary>
	internal static class UiWindowPrefetch
	{
		private const string BIG_PREFIX = "Assets/AddressableAssets/UIElements/WindowsBig/";
		private const string SMALL_PREFIX = "Assets/AddressableAssets/UIElements/WindowsSmall/";
		private const int STARTS_PER_FRAME = 2;
		private const int STARTS_PER_FRAME_LOADING_SCREEN = 16;
		private const float BUSY_TIMEOUT_SECONDS = 30f;

		private static readonly Queue<string> Pending = new Queue<string>();
		private static readonly List<AsyncOperationHandle<GameObject>> Held = new List<AsyncOperationHandle<GameObject>>();
		private static bool started;
		private static float startedAt;

		private static bool Enabled
		{
			get
			{
				return Plugin.EnableOptimizations.Value && Plugin.PrefetchMenus.Value;
			}
		}

		internal static bool IsBusy
		{
			get
			{
				if (!started || !Enabled || Time.realtimeSinceStartup - startedAt > BUSY_TIMEOUT_SECONDS)
				{
					return false;
				}
				if (Pending.Count > 0)
				{
					return true;
				}
				foreach (var handle in Held)
				{
					if (handle.IsValid() && !handle.IsDone)
					{
						return true;
					}
				}
				return false;
			}
		}

		/// <summary>Builds the list of menus once the first area has loaded.</summary>
		internal static void Begin()
		{
			if (started || !Enabled)
			{
				return;
			}
			started = true;
			startedAt = Time.realtimeSinceStartup;
			BuildQueue();
		}

		/// <summary>Runs every frame. Starts a few loads and keeps the handles, so the menus stay loaded.</summary>
		internal static void Tick(bool worldReady)
		{
			if (!Enabled)
			{
				return;
			}
			if (!started)
			{
				if (worldReady)
				{
					Begin();
				}
				return;
			}
			var starts = LoadingScreenHold.IsHolding ? STARTS_PER_FRAME_LOADING_SCREEN : STARTS_PER_FRAME;
			for (var i = 0; i < starts && Pending.Count > 0; i++)
			{
				Held.Add(Addressables.LoadAssetAsync<GameObject>(Pending.Dequeue()));
			}
		}

		/// <summary>Queues the menu prefab of every window class in the game.</summary>
		private static void BuildQueue()
		{
			try
			{
				var names = new HashSet<string>();
				foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					var assemblyName = assembly.GetName().Name;
					if (assemblyName != "Assembly-CSharp" && assemblyName != "LazyBearTechnology")
					{
						continue;
					}
					Type[] types;
					try
					{
						types = assembly.GetTypes();
					}
					catch (System.Reflection.ReflectionTypeLoadException ex)
					{
						types = ex.Types.Where(t => t != null).ToArray();
					}
					foreach (var type in types)
					{
						if (!type.IsAbstract && typeof(LazyWidgetBase).IsAssignableFrom(type))
						{
							names.Add(type.Name);
						}
					}
				}
				foreach (var name in names)
				{
					TryEnqueue($"{BIG_PREFIX}{name}_Big.prefab");
					TryEnqueue($"{SMALL_PREFIX}{name}_Small.prefab");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Menu prefetch skipped: {ex.Message}");
			}
		}

		/// <summary>True when the prefab is loaded, or not queued at all.</summary>
		internal static bool IsLoaded(string windowName)
		{
			foreach (var handle in Held)
			{
				if (handle.IsValid() && !handle.IsDone && handle.DebugName != null && handle.DebugName.Contains(windowName))
				{
					return false;
				}
			}
			return !Pending.Any(key => key.Contains(windowName));
		}

		/// <summary>Queues a prefab only when it exists in the game's asset catalog.</summary>
		private static void TryEnqueue(string key)
		{
			foreach (var locator in Addressables.ResourceLocators)
			{
				if (locator.Locate(key, typeof(GameObject), out _))
				{
					Pending.Enqueue(key);
					return;
				}
			}
		}
	}
}
