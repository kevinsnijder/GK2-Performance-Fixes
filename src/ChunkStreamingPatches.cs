using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Loads world objects before they scroll into view. The game's "prewarm" band around the camera is widened, and
	/// static decor inside it is spawned early on a time budget and kept until it leaves the band.
	/// Only off-screen objects are affected; everything on screen is handled by the game as usual.
	/// </summary>
	internal static class ChunkStreaming
	{
		private static readonly AccessTools.FieldRef<ChunkManager, List<ChunkManagerLayer>> LayersRef =
			AccessTools.FieldRefAccess<ChunkManager, List<ChunkManagerLayer>>("layers");

		private static readonly AccessTools.FieldRef<ChunkManager, BurstablePlane[]> PlanesRef =
			AccessTools.FieldRefAccess<ChunkManager, BurstablePlane[]>("cachedBurstablePlanes");

		private const float VANILLA_PREWARM_PADDING = 1f;
		private const float VANILLA_EXPAND_FACTOR = 1.05f;
		private const int STALE_AFTER_PASSES = 60;
		private const int TRACKED_SAFETY_CAP = 50000;

		private sealed class Entry
		{
			public int LastPass;
			public bool HeldByMod;
		}

		private static readonly Dictionary<IChunkableObject, Entry> Tracked = new Dictionary<IChunkableObject, Entry>();
		private static readonly List<IChunkableObject> SweepBuffer = new List<IChunkableObject>();

		private static readonly Stopwatch BudgetWatch = new Stopwatch();
		private static int budgetFrame = -1;
		private static int preShownThisFrame;
		private static float lastAppliedPadding = -1f;
		private static float lastAppliedMovingPadding = -1f;

		private static BurstablePlane[] planes;
		private static int pass;
		private static int lastPassFrame = -1;

		/// <summary>Set when something unexpected went wrong; streaming then stays off and the game handles everything itself.</summary>
		internal static bool Broken { get; private set; }

		internal static void Fail(System.Exception ex)
		{
			if (Broken)
			{
				return;
			}
			Broken = true;
			Plugin.Log.LogWarning($"Streaming switched off: {ex.GetBaseException().Message}");
			try
			{
				ReleaseAllHeld();
				PrewarmThrottle.Clear();
			}
			catch (System.Exception)
			{
				Tracked.Clear();
			}
		}

		/// <summary>True while the world is active (the chunk manager processed a camera update in the last couple of frames).</summary>
		internal static bool RanRecently
		{
			get
			{
				return lastPassFrame >= 0 && Time.frameCount - lastPassFrame <= 2;
			}
		}

		private static bool Active
		{
			get
			{
				return Plugin.EnableOptimizations.Value && Plugin.OffscreenPreload.Value;
			}
		}

		internal static float BandPadding
		{
			get
			{
				return Mathf.Max(VANILLA_PREWARM_PADDING, Plugin.PrewarmPadding.Value);
			}
		}

		internal static void BeginPass(ChunkManager manager)
		{
			ApplyLayerSettings(manager);
			planes = PlanesRef(manager);
			pass++;

			var frame = Time.frameCount;
			lastPassFrame = frame;
			if (frame != budgetFrame)
			{
				budgetFrame = frame;
				preShownThisFrame = 0;
				BudgetWatch.Reset();
			}
		}

		/// <summary>Sets the prewarm band on the static layers and, when enabled, on moving world objects such as NPCs.</summary>
		private static void ApplyLayerSettings(ChunkManager manager)
		{
			var padding = Plugin.EnableOptimizations.Value ? BandPadding : VANILLA_PREWARM_PADDING;
			var movingPadding = Plugin.EnableOptimizations.Value && Plugin.PrewarmMovingObjects.Value ? padding : VANILLA_PREWARM_PADDING;
			if (Mathf.Approximately(padding, lastAppliedPadding) && Mathf.Approximately(movingPadding, lastAppliedMovingPadding))
			{
				return;
			}

			var layers = LayersRef(manager);
			if (layers == null)
			{
				return;
			}
			foreach (var layer in layers)
			{
				if (layer == null)
				{
					continue;
				}
				if (!layer.IsDynamic)
				{
					layer.prewarmPlanePadding = padding;
				}
				else if (layer.layerType == ChunkManagerLayerType.DynamicWgo)
				{
					layer.prewarmPlanePadding = movingPadding;
				}
			}
			lastAppliedPadding = padding;
			lastAppliedMovingPadding = movingPadding;
		}

		internal static void ResetLayerCache()
		{
			lastAppliedPadding = -1f;
			lastAppliedMovingPadding = -1f;
		}

		internal static void Clear()
		{
			Tracked.Clear();
			PrewarmThrottle.Clear();
		}

		internal static void Forget(IChunkableObject obj)
		{
			Tracked.Remove(obj);
		}

		internal static bool IsEligible(IChunkableObject obj)
		{
			if (obj is BakedChunkableObjectComponentData || obj is ConstructorPart || obj is LazyTerrainMeshData)
			{
				return true;
			}
			return Plugin.PreloadIncludesSceneObjects.Value && obj is ChunkableObjectComponent;
		}

		private static bool IsIgnoredByChunking(IChunkableObject obj)
		{
			var flag = obj.IgnoreMultiFlag;
			return flag != null && flag.ResultFlag;
		}

		/// <summary>
		/// Called before the game shows or hides an object. Keeps already spawned objects inside the band, and spawns new
		/// ones there while the frame budget allows. Returns true when a spawn is about to happen, so the caller can time it.
		/// The game only sends "prewarm" for objects inside the band, so those skip the band test.
		/// </summary>
		internal static bool Resolve(IChunkableObject obj, ref ChunkVisibilityState state)
		{
			if (IsIgnoredByChunking(obj))
			{
				return false;
			}

			Entry entry;
			if (state == ChunkVisibilityState.Visible)
			{
				if (Tracked.TryGetValue(obj, out entry))
				{
					entry.LastPass = pass;
					entry.HeldByMod = false;
				}
				else if (Tracked.Count < TRACKED_SAFETY_CAP)
				{
					Tracked[obj] = new Entry { LastPass = pass, HeldByMod = false };
				}
				return false;
			}

			var inBand = (state == ChunkVisibilityState.Prewarm && Mathf.Approximately(lastAppliedPadding, BandPadding))
				|| IsInBand(obj, BandPadding);
			if (Tracked.TryGetValue(obj, out entry))
			{
				if (inBand)
				{
					entry.LastPass = pass;
					entry.HeldByMod = true;
					state = ChunkVisibilityState.Visible;
				}
				return false;
			}

			if (!inBand || Tracked.Count >= TRACKED_SAFETY_CAP || !HasBudget() || !PrefabWarmer.IsReady(obj))
			{
				return false;
			}

			Tracked[obj] = new Entry { LastPass = pass, HeldByMod = true };
			state = ChunkVisibilityState.Visible;
			preShownThisFrame++;
			return true;
		}

		/// <summary>True when the object is within the given distance around the camera view (same bounds test as the game).</summary>
		internal static bool IsInBand(IChunkableObject obj, float padding)
		{
			if (planes == null || planes.Length < 6)
			{
				return false;
			}

			var bounds = obj.GetChunkableData();
			var min = bounds.Min;
			var max = bounds.Max;
			var growX = (max.x - min.x) * (VANILLA_EXPAND_FACTOR - 1f) * 0.5f;
			var growZ = (max.z - min.z) * (VANILLA_EXPAND_FACTOR - 1f) * 0.5f;
			min.x -= growX;
			max.x += growX;
			min.z -= growZ;
			max.z += growZ;

			for (var i = 0; i < 6; i++)
			{
				var plane = planes[i];
				var corner = math.select(min, max, plane.normal > 0f);
				if (math.dot(plane.normal, corner) + plane.distance <= -padding)
				{
					return false;
				}
			}
			return true;
		}

		internal static void BeginBudgetedWork()
		{
			BudgetWatch.Start();
		}

		internal static void EndBudgetedWork()
		{
			BudgetWatch.Stop();
		}

		private static bool HasBudget()
		{
			if (preShownThisFrame >= Plugin.OffscreenMaxPerFrame.Value)
			{
				return false;
			}
			return BudgetWatch.Elapsed.TotalMilliseconds < Plugin.OffscreenBudgetMs.Value;
		}

		/// <summary>
		/// Runs once a second. Forgets destroyed objects, and hides objects the mod kept that the game no longer updates,
		/// which is the state the game would have them in.
		/// </summary>
		internal static void Sweep()
		{
			if (Tracked.Count == 0)
			{
				return;
			}

			var chunkManagerRunning = Time.frameCount - lastPassFrame <= 2;
			SweepBuffer.Clear();
			foreach (var kv in Tracked)
			{
				var obj = kv.Key;
				if (IsDeadUnityObject(obj))
				{
					SweepBuffer.Add(obj);
					continue;
				}
				if (chunkManagerRunning && pass - kv.Value.LastPass > STALE_AFTER_PASSES)
				{
					SweepBuffer.Add(obj);
				}
			}

			var releaseHeld = Active;
			foreach (var obj in SweepBuffer)
			{
				Entry entry;
				if (!Tracked.TryGetValue(obj, out entry))
				{
					continue;
				}
				Tracked.Remove(obj);
				if (releaseHeld && entry.HeldByMod && !IsDeadUnityObject(obj) && !IsIgnoredByChunking(obj))
				{
					obj.UpdateChunkVisibility(false);
				}
			}
		}

		/// <summary>When optimizations are switched off at runtime, hand every held object back to vanilla (hidden).</summary>
		internal static void ReleaseAllHeld()
		{
			SweepBuffer.Clear();
			foreach (var kv in Tracked)
			{
				if (kv.Value.HeldByMod)
				{
					SweepBuffer.Add(kv.Key);
				}
			}
			foreach (var obj in SweepBuffer)
			{
				Tracked.Remove(obj);
				if (!IsDeadUnityObject(obj) && !IsIgnoredByChunking(obj))
				{
					obj.UpdateChunkVisibility(false);
				}
			}
			Tracked.Clear();
		}

		internal static bool IsDeadUnityObject(IChunkableObject obj)
		{
			if (obj == null)
			{
				return true;
			}
			var unityObj = obj as Object;
			return !ReferenceEquals(unityObj, null) && unityObj == null;
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ChunkManager), "ProcessChunkVisibility")]
	internal static class ProcessChunkVisibilityPatch
	{
		private static void Prefix(ChunkManager __instance)
		{
			if (ChunkStreaming.Broken)
			{
				return;
			}
			try
			{
				ChunkStreaming.BeginPass(__instance);
			}
			catch (System.Exception ex)
			{
				ChunkStreaming.Fail(ex);
			}
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ChunkManager), "DispatchChunkVisibilityState")]
	internal static class DispatchChunkVisibilityStatePatch
	{
		private const int NO_WORK = 0;
		private const int OFFSCREEN_WORK = 1;
		private const int PREWARM_WORK = 2;

		private static void Prefix(IChunkableObject chunkableObject, ref ChunkVisibilityState state, out int __state)
		{
			__state = NO_WORK;
			if (ChunkStreaming.Broken || !Plugin.EnableOptimizations.Value || chunkableObject == null)
			{
				return;
			}
			try
			{
				var requested = state;
				if (PrewarmThrottle.Resolve(chunkableObject, ref state))
				{
					__state = PREWARM_WORK;
					PrewarmThrottle.BeginWork();
					return;
				}
				if (state != requested || !Plugin.OffscreenPreload.Value || !ChunkStreaming.IsEligible(chunkableObject))
				{
					return;
				}
				if (ChunkStreaming.Resolve(chunkableObject, ref state))
				{
					__state = OFFSCREEN_WORK;
					ChunkStreaming.BeginBudgetedWork();
				}
			}
			catch (System.Exception ex)
			{
				ChunkStreaming.Fail(ex);
			}
		}

		private static void Postfix(int __state)
		{
			if (__state == OFFSCREEN_WORK)
			{
				ChunkStreaming.EndBudgetedWork();
			}
			else if (__state == PREWARM_WORK)
			{
				PrewarmThrottle.EndWork();
			}
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ChunkManager), nameof(ChunkManager.ClearAll))]
	internal static class ChunkManagerClearAllPatch
	{
		private static void Prefix()
		{
			ChunkStreaming.Clear();
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ChunkManager), "TryInit")]
	internal static class ChunkManagerTryInitPatch
	{
		/// <summary>New layers start with the game's band size, so apply ours again.</summary>
		private static void Postfix()
		{
			ChunkStreaming.ResetLayerCache();
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(BakedChunkableObjectComponentData), nameof(BakedChunkableObjectComponentData.UpdateChunkVisibility))]
	internal static class BakedHiddenPatch
	{
		/// <summary>Forgets an object whenever it gets hidden, also when the game hides it directly (e.g. on unload).</summary>
		private static void Postfix(BakedChunkableObjectComponentData __instance, bool isVisible)
		{
			if (!isVisible)
			{
				ChunkStreaming.Forget(__instance);
			}
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ConstructorPart), nameof(ConstructorPart.UpdateChunkVisibility))]
	internal static class ConstructorPartHiddenPatch
	{
		/// <summary>Also forgets a part that failed to spawn (no view), so the game retries it as usual.</summary>
		private static void Postfix(ConstructorPart __instance, bool isVisible)
		{
			if (!isVisible || __instance.constructorPartChildData?.view == null)
			{
				ChunkStreaming.Forget(__instance);
			}
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(LazyTerrainMeshData), nameof(LazyTerrainMeshData.UpdateChunkVisibility))]
	internal static class TerrainHiddenPatch
	{
		private static void Postfix(LazyTerrainMeshData __instance, bool isVisible)
		{
			if (!isVisible)
			{
				ChunkStreaming.Forget(__instance);
			}
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(ChunkableObjectComponent), nameof(ChunkableObjectComponent.UpdateChunkVisibility))]
	internal static class SceneObjectHiddenPatch
	{
		private static void Postfix(ChunkableObjectComponent __instance, bool isVisible)
		{
			if (!isVisible)
			{
				ChunkStreaming.Forget(__instance);
			}
		}
	}
}
