using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// After a slow frame the game runs all missed world logic steps at once, so the next frame is slow too.
	/// Here a frame runs at most one extra step; the rest follows in the next frames. No game time is lost.
	/// </summary>
	internal static class UpdateCatchUp
	{
		private const float TYPICAL_DT_SMOOTHING = 0.05f;
		private const float MAX_TYPICAL_DT = 0.25f;
		private const float EPSILON = 0.0001f;

		private static readonly FieldInfo ScheduledUpdatesField = AccessTools.Field(typeof(UpdateManager), "scheduledUpdates");

		private static readonly Dictionary<ScheduledUpdate, float> Carried = new Dictionary<ScheduledUpdate, float>();

		private static float typicalDt = 1f / 60f;
		private static bool failed;

		internal static bool IsAvailable
		{
			get
			{
				return ScheduledUpdatesField != null && ScheduledUpdatesField.FieldType == typeof(List<ScheduledUpdate>);
			}
		}

		/// <summary>Runs before the game's world update and moves surplus step time into the carry-over.</summary>
		internal static void BeforeUpdate(UpdateManager manager)
		{
			var dt = Time.deltaTime;
			if (dt > 0f)
			{
				typicalDt = Mathf.Min(MAX_TYPICAL_DT, Mathf.Lerp(typicalDt, dt, TYPICAL_DT_SMOOTHING));
			}
			if (failed || !manager.IsActive)
			{
				return;
			}
			if (!Plugin.EnableOptimizations.Value || !Plugin.SmoothWorldUpdates.Value)
			{
				ReleaseCarried();
				return;
			}
			try
			{
				var updates = ScheduledUpdatesField.GetValue(manager) as List<ScheduledUpdate>;
				if (updates == null)
				{
					return;
				}
				var multiplier = manager.TimeMultiplier;
				var added = dt * multiplier;
				foreach (var scheduled in updates)
				{
					Limit(scheduled, added, typicalDt * multiplier);
				}
			}
			catch (Exception ex)
			{
				failed = true;
				ReleaseCarried();
				Plugin.Log.LogWarning($"World update smoothing disabled: {ex.Message}");
			}
		}

		/// <summary>Lets this frame run its usual steps plus one; the rest waits.</summary>
		private static void Limit(ScheduledUpdate scheduled, float added, float typicalAdded)
		{
			var interval = scheduled.updateInterval;
			if (interval <= 0f)
			{
				return;
			}
			float carried;
			Carried.TryGetValue(scheduled, out carried);
			var pending = scheduled.accumulatedTime + carried;
			var maxSteps = Mathf.Ceil(typicalAdded / interval) + 1f;
			var allowed = maxSteps * interval - added - EPSILON;
			if (pending > allowed)
			{
				scheduled.accumulatedTime = allowed;
				Carried[scheduled] = pending - allowed;
			}
			else
			{
				scheduled.accumulatedTime = pending;
				if (carried > 0f)
				{
					Carried.Remove(scheduled);
				}
			}
		}

		/// <summary>Hands all carried-over time back to the game when the feature is switched off.</summary>
		private static void ReleaseCarried()
		{
			if (Carried.Count == 0)
			{
				return;
			}
			foreach (var pair in Carried)
			{
				pair.Key.accumulatedTime += pair.Value;
			}
			Carried.Clear();
		}
	}

	[PatchGroup(Features.WORLD_UPDATES, typeof(UpdateCatchUp))]
	[HarmonyPatch(typeof(UpdateManager), "Update")]
	internal static class UpdateManagerUpdatePatch
	{
		private static bool Prepare()
		{
			return UpdateCatchUp.IsAvailable;
		}

		private static void Prefix(UpdateManager __instance)
		{
			UpdateCatchUp.BeforeUpdate(__instance);
		}
	}
}
