using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LazyBearTechnology;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Many of the game's managers are found the first time they are used, by searching every object in the game
	/// (tens of milliseconds), and some are created at that moment. A few settings files are loaded from disk on first use.
	/// Behind the loading screen, managers that already exist are linked up front, a few that the game creates on
	/// demand are created, and those settings are loaded; each exactly the way the game would do it on first use.
	/// </summary>
	internal static class SingletonPrimer
	{
		private static readonly string[] CreatedOnDemand =
		{
			"UICraftHintWidgetForceUpdateCanvasesScheduler",
			"FlowScriptAssetCache",
			"AStarScanScheduler"
		};

		private static readonly string[] SettingsAssets =
		{
			"FishingSettings",
			"LazyTerrainSurfaceConfiguration"
		};

		private static List<Type> singletonTypes;

		internal static void Run()
		{
			if (!Plugin.EnableOptimizations.Value || !Plugin.PrepareManagers.Value)
			{
				return;
			}
			try
			{
				LinkExisting();
				foreach (var name in CreatedOnDemand)
				{
					GetInstance(FindSingletonType(name));
				}
				foreach (var name in SettingsAssets)
				{
					GetInstance(FindSingletonType(name));
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Manager setup skipped: {ex.GetBaseException().Message}");
			}
		}

		/// <summary>Links every scene manager that exists but is not linked yet, like the game's first-use search does.</summary>
		private static void LinkExisting()
		{
			var missing = SingletonTypes().Where(t => typeof(MonoBehaviour).IsAssignableFrom(t) && !HasInstance(t)).ToList();
			if (missing.Count == 0)
			{
				return;
			}
			var found = new Dictionary<Type, MonoBehaviour>();
			foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
			{
				if (behaviour == null || !behaviour.gameObject.scene.IsValid())
				{
					continue;
				}
				var type = behaviour.GetType();
				if (missing.Contains(type) && !found.ContainsKey(type))
				{
					found[type] = behaviour;
				}
			}
			foreach (var pair in found)
			{
				var setReference = BaseOf(pair.Key).GetMethod("SetReference", BindingFlags.Public | BindingFlags.Static);
				if (setReference != null)
				{
					setReference.Invoke(null, new object[] { pair.Value });
				}
			}
		}

		private static void GetInstance(Type type)
		{
			if (type == null || HasInstance(type))
			{
				return;
			}
			var property = BaseOf(type).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
			if (property != null)
			{
				property.GetValue(null, null);
			}
		}

		private static bool HasInstance(Type type)
		{
			var field = BaseOf(type).GetField("instance", BindingFlags.NonPublic | BindingFlags.Static);
			var value = field != null ? field.GetValue(null) as UnityEngine.Object : null;
			return value != null;
		}

		private static Type BaseOf(Type type)
		{
			var current = type.BaseType;
			while (current != null && !(current.IsGenericType && IsSingletonBase(current.GetGenericTypeDefinition())))
			{
				current = current.BaseType;
			}
			return current;
		}

		private static bool IsSingletonBase(Type definition)
		{
			return definition == typeof(LazySingleton<>) || definition == typeof(LazySingletonSO<>) || definition == typeof(LazySingletonSerializedSO<>);
		}

		private static Type FindSingletonType(string name)
		{
			return SingletonTypes().FirstOrDefault(t => t.Name == name);
		}

		private static List<Type> SingletonTypes()
		{
			if (singletonTypes != null)
			{
				return singletonTypes;
			}
			singletonTypes = new List<Type>();
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				var name = assembly.GetName().Name;
				if (name != "Assembly-CSharp" && name != "LazyBearTechnology")
				{
					continue;
				}
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types.Where(t => t != null).ToArray();
				}
				singletonTypes.AddRange(types.Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && BaseOf(t) != null));
			}
			return singletonTypes;
		}
	}
}
