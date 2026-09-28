using System;
using System.Collections.Generic;
using System.Linq;
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

	[HarmonyPatch(typeof(BugReportScreenshot), nameof(BugReportScreenshot.Capture))]
	internal static class BugReportScreenshotPatch
	{
		/// <summary>Skips the ~0.5 s screenshot the game captures for its bug reporter every time the pause menu opens.</summary>
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

	[HarmonyPatch(typeof(UIGamePauseWindow), "CreateBugReportButton")]
	internal static class PauseBugReportButtonPatch
	{
		/// <summary>Hides the pause menu's "Report a bug" button, which would do nothing without the bug reporter.</summary>
		private static bool Prefix()
		{
			return !BugReporterSwitch.IsOn();
		}
	}

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
	/// The game loads each menu from disk the first time it opens, which lags. After the world has loaded,
	/// all menus are loaded in the background (a couple per frame) and kept in memory.
	/// </summary>
	internal static class UiWindowPrefetch
	{
		private const string BIG_PREFIX = "Assets/AddressableAssets/UIElements/WindowsBig/";
		private const string SMALL_PREFIX = "Assets/AddressableAssets/UIElements/WindowsSmall/";
		private const int STARTS_PER_FRAME = 2;

		private static readonly Queue<string> Pending = new Queue<string>();
		private static readonly List<AsyncOperationHandle<GameObject>> Held = new List<AsyncOperationHandle<GameObject>>();
		private static bool started;

		/// <summary>Called every frame. Starts a couple of loads per frame; the handles are kept so the menus stay loaded.</summary>
		internal static void Tick(bool worldReady)
		{
			if (!Plugin.EnableOptimizations.Value || !Plugin.PrefetchMenus.Value)
			{
				return;
			}
			if (!started)
			{
				if (!worldReady)
				{
					return;
				}
				started = true;
				BuildQueue();
				return;
			}
			for (var i = 0; i < STARTS_PER_FRAME && Pending.Count > 0; i++)
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
