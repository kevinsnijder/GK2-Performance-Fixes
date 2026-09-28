using System.Collections.Generic;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GK2Performance
{
	/// <summary>
	/// The game loads a decor prefab from disk with a blocking call the first time it is needed. Before the mod spawns
	/// such an object early, it loads the prefab in the background first, so the game's blocking load returns at once.
	/// The mod's handle is released once the game holds its own.
	/// </summary>
	internal static class PrefabWarmer
	{
		private const float HANDLE_TIMEOUT_SECONDS = 60f;
		private const int MAX_IN_FLIGHT = 32;

		private struct WarmEntry
		{
			public AsyncOperationHandle<GameObject> Handle;
			public float StartTime;
			public bool IsBaked;
		}

		private static readonly Dictionary<string, WarmEntry> Warming = new Dictionary<string, WarmEntry>();
		private static readonly HashSet<string> Failed = new HashSet<string>();
		private static readonly List<string> SweepBuffer = new List<string>();
		private static int inFlight;

		/// <summary>True when pre-spawning this object right now won't trigger a blocking disk load.</summary>
		internal static bool IsReady(IChunkableObject obj)
		{
			if (!Plugin.AsyncPrefabWarmup.Value)
			{
				return true;
			}

			var baked = obj as BakedChunkableObjectComponentData;
			if (baked != null)
			{
				return IsPathReady(baked.pathToObject, true);
			}

			var part = obj as ConstructorPart;
			if (part != null)
			{
				var data = part.constructorPartChildData;
				if (data == null || data.view != null)
				{
					return true;
				}
				return IsPathReady(data.pathToObject, false);
			}

			return true;
		}

		/// <summary>True when the prefab is in memory. Otherwise starts a background load (if none is running) and returns false.</summary>
		private static bool IsPathReady(string path, bool isBaked)
		{
			if (string.IsNullOrEmpty(path) || Failed.Contains(path))
			{
				return true;
			}

			var pools = GetPools(isBaked);
			if (pools == null)
			{
				return false;
			}
			if (pools.ContainsKey(path))
			{
				return true;
			}

			WarmEntry entry;
			if (Warming.TryGetValue(path, out entry))
			{
				return entry.Handle.IsValid() && entry.Handle.IsDone;
			}

			if (inFlight >= MAX_IN_FLIGHT)
			{
				return false;
			}

			var handle = Addressables.LoadAssetAsync<GameObject>(path);
			Warming[path] = new WarmEntry { Handle = handle, StartTime = Time.realtimeSinceStartup, IsBaked = isBaked };
			inFlight++;
			return false;
		}

		private static Dictionary<string, Pool> GetPools(bool isBaked)
		{
			return isBaked ? GamePools.BakedPools() : GamePools.ConstructorPools();
		}

		/// <summary>Release our handles once the game has its own pool (or after a timeout). Runs once per frame.</summary>
		internal static void Sweep()
		{
			if (Warming.Count == 0)
			{
				return;
			}

			var now = Time.realtimeSinceStartup;
			inFlight = 0;
			SweepBuffer.Clear();
			foreach (var kv in Warming)
			{
				var entry = kv.Value;
				if (!entry.Handle.IsValid())
				{
					SweepBuffer.Add(kv.Key);
					continue;
				}
				if (!entry.Handle.IsDone)
				{
					inFlight++;
					continue;
				}
				if (entry.Handle.Status != AsyncOperationStatus.Succeeded)
				{
					Failed.Add(kv.Key);
					SweepBuffer.Add(kv.Key);
					continue;
				}
				var pools = GetPools(entry.IsBaked);
				var gameOwnsIt = pools != null && pools.ContainsKey(kv.Key);
				if (gameOwnsIt || now - entry.StartTime > HANDLE_TIMEOUT_SECONDS)
				{
					SweepBuffer.Add(kv.Key);
				}
			}

			foreach (var key in SweepBuffer)
			{
				var handle = Warming[key].Handle;
				Warming.Remove(key);
				if (handle.IsValid())
				{
					Addressables.Release(handle);
				}
			}
		}

		/// <summary>Releases every handle the mod still holds.</summary>
		internal static void ReleaseAll()
		{
			foreach (var entry in Warming.Values)
			{
				if (entry.Handle.IsValid())
				{
					Addressables.Release(entry.Handle);
				}
			}
			Warming.Clear();
			inFlight = 0;
		}
	}
}
