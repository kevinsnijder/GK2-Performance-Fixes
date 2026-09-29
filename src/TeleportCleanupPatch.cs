using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Doors and stairs make the game free unused memory (0.5-0.8 s freeze). When the door leads to the same area there
	/// is nothing new to free, so that step is skipped. Other area changes, sleeping and loading keep it.
	/// </summary>
	internal static class TeleportCleanup
	{
		private const float FLAG_LIFETIME_SECONDS = 30f;

		private static bool sameSceneTeleportPending;
		private static float flaggedAt;

		internal static void OnTeleport(TeleportDataBase teleportData)
		{
			sameSceneTeleportPending = false;
			if (teleportData == null)
			{
				return;
			}
			try
			{
				var destination = teleportData.GetDestinationSceneData();
				var currentSceneId = MainGame.PlayerData?.currentGameSceneId;
				var isCrossScene = destination != null && currentSceneId != destination.id;
				if (!isCrossScene)
				{
					sameSceneTeleportPending = true;
					flaggedAt = Time.realtimeSinceStartup;
				}
			}
			catch (System.Exception ex)
			{
				Plugin.Log.LogWarning($"Door cleanup skip not applied: {ex.GetBaseException().Message}");
			}
		}

		internal static bool ShouldSkip()
		{
			if (!sameSceneTeleportPending)
			{
				return false;
			}
			sameSceneTeleportPending = false;
			var fresh = Time.realtimeSinceStartup - flaggedAt < FLAG_LIFETIME_SECONDS;
			return fresh && Plugin.EnableOptimizations.Value && Plugin.SkipCleanupOnLocalTeleport.Value;
		}
	}

	[PatchGroup(Features.LOCAL_TELEPORT, typeof(TeleportCleanup))]
	[HarmonyPatch(typeof(PlayerController), nameof(PlayerController.Teleport))]
	internal static class TeleportPatch
	{
		/// <summary>Remembers whether this teleport stays in the same area.</summary>
		private static void Prefix(TeleportDataBase teleportData)
		{
			TeleportCleanup.OnTeleport(teleportData);
		}
	}

	[PatchGroup(Features.LOCAL_TELEPORT, typeof(TeleportCleanup))]
	[HarmonyPatch(typeof(MainGame), nameof(MainGame.HiddenOptimization))]
	internal static class HiddenOptimizationPatch
	{
		/// <summary>Replaces the slow memory cleanup after a same-area teleport with a quick garbage collection.</summary>
		private static bool Prefix(ref UniTask __result)
		{
			if (TeleportCleanup.ShouldSkip())
			{
				HiddenGarbageCollection.Run(HiddenGarbageCollection.DOOR_BUDGET_MS);
				__result = UniTask.CompletedTask;
				return false;
			}
			return true;
		}
	}
}
