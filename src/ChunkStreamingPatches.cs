using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Loads world objects before they scroll into view. Decor near the screen is spawned early on a time budget
	/// and kept until it is further away. Only off-screen objects are affected.
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
		private const float VIEW_DISTANCE_TOLERANCE = 0.001f;
		private const float VIEW_NORMAL_TOLERANCE = 0.00001f;

		private sealed class Entry
		{
			public int LastPass;
			public bool HeldByMod;
			public bool Removed;
		}

		private enum Outcome
		{
			Stable,
			Retry,
			Spawn
		}

		/// <summary>One object's decision from the previous pass, so it can be repeated while the camera stands still.</summary>
		private struct Decision
		{
			public IChunkableObject Obj;
			public ChunkVisibilityState Requested;
			public ChunkVisibilityState Result;
			public Entry Entry;
			public int View;
		}

		private static readonly Dictionary<IChunkableObject, Entry> Tracked = new Dictionary<IChunkableObject, Entry>();
		private static readonly List<IChunkableObject> SweepBuffer = new List<IChunkableObject>();

		private static Decision[] decisions = new Decision[1024];
		private static int decisionIndex;
		private static int view;
		private static bool viewChecked;
		private static readonly BurstablePlane[] ViewPlanes = new BurstablePlane[6];
		private static float viewPadding = -1f;
		private static float viewAppliedPadding = -1f;

		private static readonly Stopwatch BudgetWatch = new Stopwatch();
		private static int budgetFrame = -1;
		private static int preShownThisFrame;
		private static float lastAppliedPadding = -1f;
		private static float lastAppliedMovingPadding = -1f;

		private static BurstablePlane[] planes;
		private static int pass;
		private static int lastPassFrame = -1;

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
				ClearTracked();
			}
		}

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
			decisionIndex = 0;
			viewChecked = false;

			var frame = Time.frameCount;
			lastPassFrame = frame;
			if (frame != budgetFrame)
			{
				budgetFrame = frame;
				preShownThisFrame = 0;
				BudgetWatch.Reset();
			}
		}

		/// <summary>
		/// Starts a new view when the camera or band size changed. Runs on the first object of a pass, after the game
		/// has updated the camera. Changes under a millimetre are ignored, because a still camera can flicker by a rounding step.
		/// </summary>
		private static void UpdateView()
		{
			var padding = BandPadding;
			var changed = planes == null || planes.Length < 6 || padding != viewPadding || lastAppliedPadding != viewAppliedPadding;
			for (var i = 0; !changed && i < 6; i++)
			{
				var plane = planes[i];
				var start = ViewPlanes[i];
				changed = Mathf.Abs(plane.distance - start.distance) > VIEW_DISTANCE_TOLERANCE
					|| Mathf.Abs(plane.normal.x - start.normal.x) > VIEW_NORMAL_TOLERANCE
					|| Mathf.Abs(plane.normal.y - start.normal.y) > VIEW_NORMAL_TOLERANCE
					|| Mathf.Abs(plane.normal.z - start.normal.z) > VIEW_NORMAL_TOLERANCE;
			}
			if (!changed)
			{
				return;
			}
			view++;
			viewPadding = padding;
			viewAppliedPadding = lastAppliedPadding;
			if (planes != null && planes.Length >= 6)
			{
				System.Array.Copy(planes, ViewPlanes, 6);
			}
		}

		/// <summary>Sets the size of the band around the screen, for decor and, when enabled, for NPCs.</summary>
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
			ClearTracked();
			PrewarmThrottle.Clear();
		}

		private static void ClearTracked()
		{
			foreach (var entry in Tracked.Values)
			{
				entry.Removed = true;
			}
			Tracked.Clear();
			System.Array.Clear(decisions, 0, decisions.Length);
		}

		internal static void Forget(IChunkableObject obj)
		{
			Entry entry;
			if (Tracked.TryGetValue(obj, out entry))
			{
				entry.Removed = true;
				Tracked.Remove(obj);
			}
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
		/// Called before the game shows or hides an object. Keeps spawned objects in the band and spawns new ones there
		/// within the frame budget. Returns true when a spawn starts. While the camera stands still, last frame's decision is reused.
		/// </summary>
		internal static bool Resolve(IChunkableObject obj, ref ChunkVisibilityState state)
		{
			if (!viewChecked)
			{
				viewChecked = true;
				UpdateView();
			}
			var index = decisionIndex++;
			if (IsIgnoredByChunking(obj))
			{
				return false;
			}
			if (TryRepeat(index, obj, ref state))
			{
				return false;
			}

			var requested = state;
			Entry entry;
			var outcome = Decide(obj, ref state, out entry);
			if (outcome == Outcome.Stable)
			{
				Remember(index, obj, requested, state, entry);
			}
			return outcome == Outcome.Spawn;
		}

		/// <summary>Works out what to do with one object. Objects the game marks as "prewarm" are already inside the band.</summary>
		private static Outcome Decide(IChunkableObject obj, ref ChunkVisibilityState state, out Entry entry)
		{
			if (state == ChunkVisibilityState.Visible)
			{
				if (Tracked.TryGetValue(obj, out entry))
				{
					entry.LastPass = pass;
					entry.HeldByMod = false;
					return Outcome.Stable;
				}
				if (Tracked.Count >= TRACKED_SAFETY_CAP)
				{
					return Outcome.Retry;
				}
				entry = new Entry { LastPass = pass, HeldByMod = false };
				Tracked[obj] = entry;
				return Outcome.Stable;
			}

			var inBand = (state == ChunkVisibilityState.Prewarm && Mathf.Approximately(lastAppliedPadding, BandPadding))
				|| IsInBand(obj, BandPadding);
			if (Tracked.TryGetValue(obj, out entry))
			{
				if (!inBand)
				{
					entry = null;
					return Outcome.Retry;
				}
				entry.LastPass = pass;
				entry.HeldByMod = true;
				state = ChunkVisibilityState.Visible;
				return Outcome.Stable;
			}

			if (!inBand)
			{
				return Outcome.Stable;
			}
			if (Tracked.Count >= TRACKED_SAFETY_CAP || !HasBudget() || !PrefabWarmer.IsReady(obj))
			{
				return Outcome.Retry;
			}

			Tracked[obj] = new Entry { LastPass = pass, HeldByMod = true };
			state = ChunkVisibilityState.Visible;
			preShownThisFrame++;
			return Outcome.Spawn;
		}

		/// <summary>Reuses last pass's decision when nothing changed and a kept object is still kept.</summary>
		private static bool TryRepeat(int index, IChunkableObject obj, ref ChunkVisibilityState state)
		{
			if (index >= decisions.Length)
			{
				return false;
			}
			var decision = decisions[index];
			if (decision.View != view || decision.Requested != state || !ReferenceEquals(decision.Obj, obj))
			{
				return false;
			}
			var entry = decision.Entry;
			if (entry != null)
			{
				if (entry.Removed)
				{
					return false;
				}
				entry.LastPass = pass;
				entry.HeldByMod = state != ChunkVisibilityState.Visible;
			}
			state = decision.Result;
			return true;
		}

		private static void Remember(int index, IChunkableObject obj, ChunkVisibilityState requested, ChunkVisibilityState result, Entry entry)
		{
			if (index >= decisions.Length)
			{
				if (decisions.Length >= TRACKED_SAFETY_CAP)
				{
					return;
				}
				System.Array.Resize(ref decisions, decisions.Length * 2);
			}
			decisions[index] = new Decision { Obj = obj, Requested = requested, Result = result, Entry = entry, View = view };
		}

		/// <summary>
		/// True when the object is within the given distance around the camera view, using the game's own test.
		/// Plain floats, because the vector version is much slower here.
		/// </summary>
		internal static bool IsInBand(IChunkableObject obj, float padding)
		{
			if (planes == null || planes.Length < 6)
			{
				return false;
			}

			var bounds = obj.GetChunkableData();
			var center = bounds.center;
			var size = bounds.size;
			var halfX = size.x * 0.5f;
			var halfY = size.y * 0.5f;
			var halfZ = size.z * 0.5f;
			var minX = center.x - halfX;
			var maxX = center.x + halfX;
			var minY = center.y - halfY;
			var maxY = center.y + halfY;
			var minZ = center.z - halfZ;
			var maxZ = center.z + halfZ;
			var growX = (maxX - minX) * (VANILLA_EXPAND_FACTOR - 1f) * 0.5f;
			var growZ = (maxZ - minZ) * (VANILLA_EXPAND_FACTOR - 1f) * 0.5f;
			minX -= growX;
			maxX += growX;
			minZ -= growZ;
			maxZ += growZ;

			for (var i = 0; i < 6; i++)
			{
				var normal = planes[i].normal;
				var x = normal.x > 0f ? maxX : minX;
				var y = normal.y > 0f ? maxY : minY;
				var z = normal.z > 0f ? maxZ : minZ;
				if (normal.x * x + normal.y * y + normal.z * z + planes[i].distance <= -padding)
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

		/// <summary>Runs once a second. Forgets destroyed objects and hides kept objects the game no longer updates.</summary>
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
				entry.Removed = true;
				Tracked.Remove(obj);
				if (releaseHeld && entry.HeldByMod && !IsDeadUnityObject(obj) && !IsIgnoredByChunking(obj))
				{
					obj.UpdateChunkVisibility(false);
				}
			}
		}

		/// <summary>When the fix is switched off, hides every object the mod kept.</summary>
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
				Forget(obj);
				if (!IsDeadUnityObject(obj) && !IsIgnoredByChunking(obj))
				{
					obj.UpdateChunkVisibility(false);
				}
			}
			ClearTracked();
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
		/// <summary>New layers start with the game's band size, so set ours again.</summary>
		private static void Postfix()
		{
			ChunkStreaming.ResetLayerCache();
		}
	}

	[PatchGroup(Features.STREAMING, typeof(ChunkStreaming), typeof(PrewarmThrottle), typeof(PrefabWarmer), typeof(GamePools))]
	[HarmonyPatch(typeof(BakedChunkableObjectComponentData), nameof(BakedChunkableObjectComponentData.UpdateChunkVisibility))]
	internal static class BakedHiddenPatch
	{
		/// <summary>Forgets an object whenever it is hidden, also when the game hides it directly.</summary>
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
		/// <summary>Also forgets a part that failed to spawn, so the game retries it as usual.</summary>
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
