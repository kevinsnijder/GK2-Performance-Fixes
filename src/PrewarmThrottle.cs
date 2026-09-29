using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Buildings, trees and NPCs start loading when they near the screen, all in the same frame, which hitches while walking.
	/// Each frame gets a small time budget; the rest starts a frame later. Objects that just left keep their visuals a bit longer.
	/// </summary>
	internal static class PrewarmThrottle
	{
		private const float KEEP_EXTRA_PADDING = 3f;

		private static readonly AccessTools.FieldRef<Wgo, ChunkVisibilityState> WgoStateRef =
			AccessTools.FieldRefAccess<Wgo, ChunkVisibilityState>("chunkVisibilityState");

		private static readonly AccessTools.FieldRef<Wso, ChunkVisibilityState> WsoStateRef =
			AccessTools.FieldRefAccess<Wso, ChunkVisibilityState>("chunkVisibilityState");

		private static readonly Stopwatch Watch = new Stopwatch();
		private static readonly HashSet<IChunkableObject> Kept = new HashSet<IChunkableObject>();
		private static readonly List<IChunkableObject> SweepBuffer = new List<IChunkableObject>();
		private static int budgetFrame = -1;
		private static int startedThisFrame;

		private static bool Enabled
		{
			get
			{
				return Plugin.EnableOptimizations.Value && Plugin.PrewarmBudgetMs.Value > 0f;
			}
		}

		private static bool TryGetState(IChunkableObject obj, out ChunkVisibilityState current)
		{
			var wgo = obj as Wgo;
			if (!ReferenceEquals(wgo, null))
			{
				current = WgoStateRef(wgo);
				return true;
			}
			var wso = obj as Wso;
			if (!ReferenceEquals(wso, null))
			{
				current = WsoStateRef(wso);
				return true;
			}
			current = ChunkVisibilityState.OutOfRange;
			return false;
		}

		/// <summary>
		/// Called before the game changes an object's state. May postpone a load or keep an object loaded.
		/// Returns true when a load starts.
		/// </summary>
		internal static bool Resolve(IChunkableObject obj, ref ChunkVisibilityState state)
		{
			if (!Enabled)
			{
				return false;
			}
			ChunkVisibilityState current;
			if (!TryGetState(obj, out current))
			{
				return false;
			}
			if (state == ChunkVisibilityState.OutOfRange && current == ChunkVisibilityState.Prewarm)
			{
				if (ChunkStreaming.IsInBand(obj, ChunkStreaming.BandPadding + KEEP_EXTRA_PADDING))
				{
					state = ChunkVisibilityState.Prewarm;
					Kept.Add(obj);
				}
				return false;
			}
			if (state != ChunkVisibilityState.OutOfRange && Kept.Count > 0)
			{
				Kept.Remove(obj);
			}
			if (state != ChunkVisibilityState.Prewarm || current != ChunkVisibilityState.OutOfRange)
			{
				return false;
			}
			var frame = Time.frameCount;
			if (frame != budgetFrame)
			{
				budgetFrame = frame;
				startedThisFrame = 0;
				Watch.Reset();
			}
			if (startedThisFrame > 0 && Watch.Elapsed.TotalMilliseconds >= Plugin.PrewarmBudgetMs.Value)
			{
				state = ChunkVisibilityState.OutOfRange;
				return false;
			}
			startedThisFrame++;
			return true;
		}

		/// <summary>
		/// Runs once a second. Unloads kept objects once they are further away.
		/// Unloads all of them when the feature is switched off.
		/// </summary>
		internal static void Sweep()
		{
			var enabled = Enabled;
			if (Kept.Count == 0 || (enabled && !ChunkStreaming.RanRecently))
			{
				return;
			}
			SweepBuffer.Clear();
			foreach (var obj in Kept)
			{
				ChunkVisibilityState current;
				if (ChunkStreaming.IsDeadUnityObject(obj) || !TryGetState(obj, out current) || current != ChunkVisibilityState.Prewarm)
				{
					SweepBuffer.Add(obj);
					continue;
				}
				if (!enabled || !ChunkStreaming.IsInBand(obj, ChunkStreaming.BandPadding + KEEP_EXTRA_PADDING))
				{
					SweepBuffer.Add(obj);
					Unload(obj);
				}
			}
			foreach (var obj in SweepBuffer)
			{
				Kept.Remove(obj);
			}
		}

		/// <summary>Does what the game does for an object that is out of range.</summary>
		private static void Unload(IChunkableObject obj)
		{
			if (ChunkStreaming.IsDeadUnityObject(obj) || obj.IgnoreChunkVisibility)
			{
				return;
			}
			var receiver = obj as IChunkVisibilityStateReceiver;
			if (receiver != null)
			{
				receiver.UpdateChunkVisibilityState(ChunkVisibilityState.OutOfRange);
			}
			obj.UpdateChunkVisibility(false);
		}

		internal static void Clear()
		{
			Kept.Clear();
		}

		internal static void BeginWork()
		{
			Watch.Start();
		}

		internal static void EndWork()
		{
			Watch.Stop();
		}
	}
}
