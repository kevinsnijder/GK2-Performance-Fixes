using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GK2Performance
{
	/// <summary>
	/// Access to the game's object pools, read from private fields.
	/// The public getters search the scene or even create a pool.
	/// </summary>
	internal static class GamePools
	{
		private static readonly AccessTools.FieldRef<BakedChunkableObjectPool> BakedInstanceRef =
			AccessTools.StaticFieldRefAccess<BakedChunkableObjectPool>(AccessTools.Field(typeof(LazySingleton<BakedChunkableObjectPool>), "instance"));

		private static readonly AccessTools.FieldRef<ConstructorPartPool> ConstructorInstanceRef =
			AccessTools.StaticFieldRefAccess<ConstructorPartPool>(AccessTools.Field(typeof(LazySingleton<ConstructorPartPool>), "instance"));

		private static readonly AccessTools.FieldRef<WgoPartPool> WgoInstanceRef =
			AccessTools.StaticFieldRefAccess<WgoPartPool>(AccessTools.Field(typeof(LazySingleton<WgoPartPool>), "instance"));

		private static readonly AccessTools.FieldRef<BakedChunkableObjectPool, Dictionary<string, Pool>> BakedPoolsRef =
			AccessTools.FieldRefAccess<BakedChunkableObjectPool, Dictionary<string, Pool>>("poolsDict");

		private static readonly AccessTools.FieldRef<ConstructorPartPool, Dictionary<string, Pool>> ConstructorPoolsRef =
			AccessTools.FieldRefAccess<ConstructorPartPool, Dictionary<string, Pool>>("poolsDict");

		private static readonly AccessTools.FieldRef<WgoPartPool, Dictionary<string, Stack<WgoPart>>> WgoPoolsRef =
			AccessTools.FieldRefAccess<WgoPartPool, Dictionary<string, Stack<WgoPart>>>("pools");

		private static readonly AccessTools.FieldRef<WgoPartPool, Dictionary<string, AsyncOperationHandle<GameObject>>> WgoHandlesRef =
			AccessTools.FieldRefAccess<WgoPartPool, Dictionary<string, AsyncOperationHandle<GameObject>>>("loadedHandles");

		private static readonly Func<BakedChunkableObjectPool, string, UniTask<Pool>> BakedCreateAsync =
			AccessTools.MethodDelegate<Func<BakedChunkableObjectPool, string, UniTask<Pool>>>(
				AccessTools.Method(typeof(BakedChunkableObjectPool), "CreatePoolByIdAsync"));

		private static readonly Func<ConstructorPartPool, string, UniTask<Pool>> ConstructorCreateAsync =
			AccessTools.MethodDelegate<Func<ConstructorPartPool, string, UniTask<Pool>>>(
				AccessTools.Method(typeof(ConstructorPartPool), "CreatePoolByIdAsync"));

		private static readonly Func<WgoPartPool, string, AsyncOperationHandle<GameObject>> WgoLoadAsync =
			AccessTools.MethodDelegate<Func<WgoPartPool, string, AsyncOperationHandle<GameObject>>>(
				AccessTools.Method(typeof(WgoPartPool), "LoadPrefabAsync", new[] { typeof(string) }));

		internal static BakedChunkableObjectPool Baked
		{
			get
			{
				var pool = BakedInstanceRef();
				return pool != null ? pool : null;
			}
		}

		internal static ConstructorPartPool Constructor
		{
			get
			{
				var pool = ConstructorInstanceRef();
				return pool != null ? pool : null;
			}
		}

		internal static WgoPartPool Wgo
		{
			get
			{
				var pool = WgoInstanceRef();
				return pool != null ? pool : null;
			}
		}

		internal static Dictionary<string, Pool> BakedPools()
		{
			var owner = Baked;
			return owner != null ? BakedPoolsRef(owner) : null;
		}

		internal static Dictionary<string, Pool> ConstructorPools()
		{
			var owner = Constructor;
			return owner != null ? ConstructorPoolsRef(owner) : null;
		}

		internal static Dictionary<string, Stack<WgoPart>> WgoPools()
		{
			var owner = Wgo;
			return owner != null ? WgoPoolsRef(owner) : null;
		}

		internal static Dictionary<string, AsyncOperationHandle<GameObject>> WgoHandles()
		{
			var owner = Wgo;
			return owner != null ? WgoHandlesRef(owner) : null;
		}

		/// <summary>Starts the game's own async pool creation.</summary>
		internal static void StartBakedPoolAsync(string path)
		{
			BakedCreateAsync(Baked, path).Forget();
		}

		internal static void StartConstructorPoolAsync(string path)
		{
			ConstructorCreateAsync(Constructor, path).Forget();
		}

		/// <summary>Starts the game's own async prefab load for a world object.</summary>
		internal static void StartWgoPrefabLoad(string key)
		{
			WgoLoadAsync(Wgo, key);
		}
	}
}
