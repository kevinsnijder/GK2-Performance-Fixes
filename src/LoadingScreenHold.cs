using System;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// The game hides the loading screen while the mod is still loading the area's objects and menus in the background,
	/// which made the first seconds of play hitch. The loading screen now stays up until that work is done, with a time limit.
	/// </summary>
	internal static class LoadingScreenHold
	{
		private static UILoadingOverlay heldOverlay;
		private static float heldSince;
		private static bool releasing;

		internal static bool IsHolding
		{
			get
			{
				return heldOverlay != null;
			}
		}

		private static bool Enabled
		{
			get
			{
				return Plugin.EnableOptimizations.Value && Plugin.HoldLoadingScreen.Value;
			}
		}

		private static bool HasPendingWork()
		{
			var preloading = PatchGroups.IsActive(Features.PRELOAD_WORLD) && WorldPreloader.IsBusy;
			return preloading || UiWindowPrefetch.IsBusy || WindowPrecreate.IsBusy;
		}

		/// <summary>Returns false when the hide is postponed until the background work is done. Any error lets the game hide it.</summary>
		internal static bool OnHide(UILoadingOverlay overlay)
		{
			try
			{
				if (releasing || overlay == null || !overlay.IsShown || !Enabled)
				{
					return true;
				}
				if (heldOverlay != null)
				{
					return false;
				}
				SingletonPrimer.Run();
				UiWindowPrefetch.Begin();
				WindowPrecreate.Begin();
				if (!HasPendingWork())
				{
					return true;
				}
				heldOverlay = overlay;
				heldSince = Time.realtimeSinceStartup;
				return false;
			}
			catch (Exception ex)
			{
				heldOverlay = null;
				Plugin.Log.LogWarning($"Loading screen hold skipped: {ex.GetBaseException().Message}");
				return true;
			}
		}

		/// <summary>A new loading screen replaces a held one.</summary>
		internal static void OnDraw()
		{
			heldOverlay = null;
		}

		/// <summary>Called every frame. Hides the held loading screen once the work is done or the time limit is reached.</summary>
		internal static void Tick()
		{
			if (heldOverlay == null)
			{
				return;
			}
			var elapsed = Time.realtimeSinceStartup - heldSince;
			var timedOut = elapsed > Plugin.MaxHoldSeconds.Value;
			if (!heldOverlay.IsShown)
			{
				heldOverlay = null;
				return;
			}
			var pending = false;
			try
			{
				pending = HasPendingWork();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Loading screen hold ended early: {ex.GetBaseException().Message}");
			}
			if (Enabled && !timedOut && pending)
			{
				return;
			}
			Plugin.Log.LogInfo(timedOut
				? $"Loading screen kept up {elapsed:0.0} s (limit reached); the rest loads during play."
				: $"Loading screen kept up {elapsed:0.0} s while the area and menus loaded.");
			Release();
		}

		private static void Release()
		{
			var overlay = heldOverlay;
			heldOverlay = null;
			HiddenGarbageCollection.Run(HiddenGarbageCollection.LOADING_SCREEN_BUDGET_MS);
			releasing = true;
			try
			{
				overlay.Hide();
			}
			finally
			{
				releasing = false;
			}
		}
	}

	[PatchGroup(Features.HOLD_LOADING_SCREEN, typeof(LoadingScreenHold))]
	[HarmonyPatch(typeof(UILoadingOverlay), nameof(UILoadingOverlay.Hide))]
	internal static class LoadingOverlayHidePatch
	{
		private static bool Prefix(UILoadingOverlay __instance)
		{
			return LoadingScreenHold.OnHide(__instance);
		}
	}

	[PatchGroup(Features.HOLD_LOADING_SCREEN, typeof(LoadingScreenHold))]
	[HarmonyPatch(typeof(UILoadingOverlay), nameof(UILoadingOverlay.Draw))]
	internal static class LoadingOverlayDrawPatch
	{
		private static void Prefix()
		{
			LoadingScreenHold.OnDraw();
		}
	}
}
