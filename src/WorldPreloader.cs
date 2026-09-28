using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GK2Performance
{
	/// <summary>
	/// When an area loads, loads every prefab it uses and puts a few ready-made copies into the game's own pools,
	/// nearest first, so walking into unvisited parts of the map doesn't hitch. Runs on a per-frame time budget.
	/// </summary>
	internal static class WorldPreloader
	{
		private enum Kind
		{
			Baked,
			Constructor,
			Wgo
		}

		private enum Stage
		{
			Load,
			Loading,
			Warm
		}

		private sealed class Item
		{
			public Kind Kind;
			public string Path;
			public int Uses;
			public float Distance;
			public Stage Stage;
			public float LoadStartedAt;
			public GameScene Scene;
		}

		private const float LOAD_TIMEOUT_SECONDS = 30f;
		private const int MAX_IN_FLIGHT = 6;
		private const int MAX_IN_FLIGHT_LOADING_SCREEN = 24;
		private const float LOADING_SCREEN_BUDGET_MS = 25f;

		private static readonly AccessTools.FieldRef<GameScene, List<ConstructorPart>> SceneConstructorPartsRef =
			AccessTools.FieldRefAccess<GameScene, List<ConstructorPart>>("constructorParts");

		private static readonly List<Item> Queue = new List<Item>();
		private static readonly Stopwatch Budget = new Stopwatch();
		private static readonly HashSet<string> Failed = new HashSet<string>();

		private static UILoadingOverlay loadingOverlay;
		private static float overlayLookupAt;

		internal static bool IsBusy
		{
			get
			{
				return Queue.Count > 0;
			}
		}

		/// <summary>Queues the preload for an area once the game has registered its prefabs.</summary>
		internal static void OnScenePathsRegistered(string id)
		{
			if (!Plugin.EnableOptimizations.Value || !Plugin.PreloadWorld.Value)
			{
				return;
			}
			var manager = LazySingleton<GameSceneManager>.Instance;
			var gameScene = manager != null ? manager.LoadedGameScenes.FirstOrDefault(s => s != null && s.Id == id) : null;
			if (gameScene == null)
			{
				return;
			}
			Enqueue(gameScene);
		}

		private static void Enqueue(GameScene gameScene)
		{
			var origin = GetCameraPosition(gameScene);
			var items = new Dictionary<string, Item>();

			if (gameScene.bakedChunkableObjectComponentDatas != null)
			{
				foreach (var data in gameScene.bakedChunkableObjectComponentDatas)
				{
					if (data != null)
					{
						Add(items, Kind.Baked, data.pathToObject, data.worldPos, origin);
					}
				}
			}

			var parts = SceneConstructorPartsRef(gameScene);
			if (parts != null)
			{
				foreach (var part in parts)
				{
					if (part != null && part.constructorPartChildData != null)
					{
						Add(items, Kind.Constructor, part.constructorPartChildData.pathToObject, part.transform.position, origin);
					}
				}
			}

			var sceneData = gameScene.GameSceneData;
			if (sceneData != null && sceneData.wgoDataList != null)
			{
				var keys = new HashSet<string>();
				foreach (var wgoData in sceneData.wgoDataList)
				{
					if (wgoData == null)
					{
						continue;
					}
					keys.Clear();
					WgoPartPool.CollectAddressableKeysForWgoData(wgoData, keys);
					foreach (var key in keys)
					{
						Add(items, Kind.Wgo, key, wgoData.Position, origin);
					}
				}
			}

			var added = items.Values.OrderBy(i => i.Distance).ToList();
			foreach (var item in added)
			{
				item.Scene = gameScene;
			}
			Queue.AddRange(added);
		}

		private static void Add(Dictionary<string, Item> items, Kind kind, string path, Vector3 position, Vector3 origin)
		{
			if (string.IsNullOrEmpty(path) || Failed.Contains(path))
			{
				return;
			}
			var distance = Vector3.Distance(new Vector3(position.x, 0f, position.z), origin);
			var key = $"{(int)kind}|{path}";
			Item item;
			if (items.TryGetValue(key, out item))
			{
				item.Uses++;
				if (distance < item.Distance)
				{
					item.Distance = distance;
				}
				return;
			}
			items[key] = new Item { Kind = kind, Path = path, Uses = 1, Distance = distance, Stage = Stage.Load };
		}

		private static Vector3 GetCameraPosition(GameScene gameScene)
		{
			var cam = Camera.main;
			var pos = cam != null ? cam.transform.position : gameScene.transform.position;
			return new Vector3(pos.x, 0f, pos.z);
		}

		/// <summary>Called every frame. Drops work for areas that were unloaded meanwhile, then continues within the budget.</summary>
		internal static void Tick()
		{
			if (Queue.Count == 0)
			{
				return;
			}
			if (!Plugin.EnableOptimizations.Value || !Plugin.PreloadWorld.Value)
			{
				Queue.Clear();
				return;
			}
			Queue.RemoveAll(i => i.Scene == null && i.Stage != Stage.Loading);

			var loadingScreen = IsLoadingScreenShown();
			var budgetMs = loadingScreen ? LOADING_SCREEN_BUDGET_MS : Plugin.PreloadBudgetMs.Value;
			var maxInFlight = loadingScreen ? MAX_IN_FLIGHT_LOADING_SCREEN : MAX_IN_FLIGHT;
			Budget.Reset();
			Budget.Start();

			var inFlight = 0;
			for (var i = 0; i < Queue.Count; i++)
			{
				if (Queue[i].Stage == Stage.Loading)
				{
					inFlight++;
				}
			}

			var index = 0;
			while (index < Queue.Count && Budget.Elapsed.TotalMilliseconds < budgetMs)
			{
				var item = Queue[index];
				var done = Step(item, ref inFlight, maxInFlight, budgetMs);
				if (done)
				{
					Queue.RemoveAt(index);
					continue;
				}
				index++;
			}
			Budget.Stop();
		}

		/// <summary>Advances one item; returns true when it is finished (or given up).</summary>
		private static bool Step(Item item, ref int inFlight, int maxInFlight, float budgetMs)
		{
			switch (item.Stage)
			{
				case Stage.Load:
					if (IsPrefabReady(item))
					{
						item.Stage = Stage.Warm;
						return WarmUp(item, budgetMs);
					}
					if (inFlight >= maxInFlight || !StartLoad(item))
					{
						return false;
					}
					inFlight++;
					item.Stage = Stage.Loading;
					item.LoadStartedAt = Time.realtimeSinceStartup;
					return false;

				case Stage.Loading:
					if (IsPrefabReady(item))
					{
						inFlight--;
						item.Stage = Stage.Warm;
						return WarmUp(item, budgetMs);
					}
					if (HasLoadFailed(item) || Time.realtimeSinceStartup - item.LoadStartedAt > LOAD_TIMEOUT_SECONDS)
					{
						inFlight--;
						Failed.Add(item.Path);
						return true;
					}
					return false;

				default:
					return WarmUp(item, budgetMs);
			}
		}

		private static bool IsPrefabReady(Item item)
		{
			switch (item.Kind)
			{
				case Kind.Baked:
				{
					var pools = GamePools.BakedPools();
					return pools != null && pools.ContainsKey(item.Path);
				}
				case Kind.Constructor:
				{
					var pools = GamePools.ConstructorPools();
					return pools != null && pools.ContainsKey(item.Path);
				}
				default:
				{
					var handles = GamePools.WgoHandles();
					AsyncOperationHandle<GameObject> handle;
					return handles != null && handles.TryGetValue(item.Path, out handle) && handle.IsValid() &&
						handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null;
				}
			}
		}

		private static bool HasLoadFailed(Item item)
		{
			if (item.Kind != Kind.Wgo)
			{
				return false;
			}
			var handles = GamePools.WgoHandles();
			AsyncOperationHandle<GameObject> handle;
			if (handles == null || !handles.TryGetValue(item.Path, out handle))
			{
				return false;
			}
			return !handle.IsValid() || (handle.IsDone && handle.Status != AsyncOperationStatus.Succeeded);
		}

		private static bool StartLoad(Item item)
		{
			switch (item.Kind)
			{
				case Kind.Baked:
					if (GamePools.Baked == null)
					{
						return false;
					}
					GamePools.StartBakedPoolAsync(item.Path);
					return true;
				case Kind.Constructor:
					if (GamePools.Constructor == null)
					{
						return false;
					}
					GamePools.StartConstructorPoolAsync(item.Path);
					return true;
				default:
					if (GamePools.Wgo == null)
					{
						return false;
					}
					GamePools.StartWgoPrefabLoad(item.Path);
					return true;
			}
		}

		/// <summary>Pre-creates idle instances one at a time within the budget. Returns true when the target is reached.</summary>
		private static bool WarmUp(Item item, float budgetMs)
		{
			var target = Mathf.Min(item.Uses, Plugin.PreloadInstancesPerPrefab.Value);
			while (Budget.Elapsed.TotalMilliseconds < budgetMs)
			{
				var idle = IdleCount(item);
				if (idle < 0 || idle >= target)
				{
					return true;
				}
				if (!CreateOne(item))
				{
					return true;
				}
			}
			return false;
		}

		private static int IdleCount(Item item)
		{
			switch (item.Kind)
			{
				case Kind.Baked:
				{
					var pools = GamePools.BakedPools();
					Pool pool;
					return pools != null && pools.TryGetValue(item.Path, out pool) ? pool.Objects.Count : -1;
				}
				case Kind.Constructor:
				{
					var pools = GamePools.ConstructorPools();
					Pool pool;
					return pools != null && pools.TryGetValue(item.Path, out pool) ? pool.Objects.Count : -1;
				}
				default:
				{
					var pools = GamePools.WgoPools();
					Stack<WgoPart> stack;
					return pools != null && pools.TryGetValue(item.Path, out stack) ? stack.Count : 0;
				}
			}
		}

		private static bool CreateOne(Item item)
		{
			switch (item.Kind)
			{
				case Kind.Baked:
				{
					var pools = GamePools.BakedPools();
					Pool pool;
					if (pools == null || !pools.TryGetValue(item.Path, out pool))
					{
						return false;
					}
					pool.AddObjectToPool();
					return true;
				}
				case Kind.Constructor:
				{
					var pools = GamePools.ConstructorPools();
					Pool pool;
					if (pools == null || !pools.TryGetValue(item.Path, out pool))
					{
						return false;
					}
					pool.AddObjectToPool();
					return true;
				}
				default:
					return CreateWgoPart(item.Path);
			}
		}

		/// <summary>Creates one idle part the same way the game's own pool warm-up does.</summary>
		private static bool CreateWgoPart(string key)
		{
			var owner = GamePools.Wgo;
			var handles = GamePools.WgoHandles();
			AsyncOperationHandle<GameObject> handle;
			if (owner == null || handles == null || !handles.TryGetValue(key, out handle) || !handle.IsValid() || handle.Result == null)
			{
				return false;
			}
			var prefabPart = handle.Result.GetComponent<WgoPart>();
			if (prefabPart == null)
			{
				return false;
			}
			var part = Object.Instantiate(prefabPart);
			part.CleanupChunkableComponents();
			part.PooledAddressableKey = key;
			owner.Release(key, part);
			return true;
		}

		private static bool IsLoadingScreenShown()
		{
			if (loadingOverlay == null && Time.realtimeSinceStartup - overlayLookupAt > 1f)
			{
				overlayLookupAt = Time.realtimeSinceStartup;
				loadingOverlay = LazyUI.Get<UILoadingOverlay>();
			}
			return loadingOverlay != null && loadingOverlay.IsShown;
		}
	}

	[HarmonyPatch(typeof(ScenePoolPathRegistry), nameof(ScenePoolPathRegistry.RegisterScenePaths))]
	internal static class RegisterScenePathsPatch
	{
		private static void Postfix(string sceneId)
		{
			WorldPreloader.OnScenePathsRegistered(sceneId);
		}
	}
}
