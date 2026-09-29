using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GK2Performance
{
	/// <summary>Feature names used for patch groups; they match the settings that switch them.</summary>
	internal static class Features
	{
		internal const string STREAMING = "Streaming";
		internal const string PRELOAD_WORLD = "PreloadWorld";
		internal const string HOLD_LOADING_SCREEN = "HoldLoadingScreen";
		internal const string PLAYER_MOTION = "SmoothPlayerMotion";
		internal const string LOCAL_TELEPORT = "SkipCleanupOnLocalTeleport";
		internal const string BUG_REPORTER = "DisableBugReporter";
		internal const string NOTEPAD_CHEST_REDRAW = "BatchNotepadChestRedraw";
		internal const string WORLD_UPDATES = "SmoothWorldUpdates";
	}

	/// <summary>Names the feature a Harmony patch class belongs to. Patches of one feature are applied or removed together.</summary>
	[AttributeUsage(AttributeTargets.Class)]
	internal sealed class PatchGroupAttribute : Attribute
	{
		public PatchGroupAttribute(string name, params Type[] uses)
		{
			Name = name;
			Uses = uses;
		}

		public string Name { get; }

		/// <summary>Classes the feature needs. They are set up before patching, so a missing game field turns the feature off up front.</summary>
		public Type[] Uses { get; }
	}

	/// <summary>
	/// Applies the patches feature by feature. When a game update breaks one feature's patch, only that feature is removed;
	/// the others keep working and the game runs as usual for the removed one.
	/// </summary>
	internal static class PatchGroups
	{
		private static readonly HashSet<string> Failed = new HashSet<string>();

		internal static bool IsActive(string name)
		{
			return !Failed.Contains(name);
		}

		internal static void ApplyAll(Harmony harmony)
		{
			var groups = typeof(Plugin).Assembly.GetTypes()
				.Where(t => t.IsDefined(typeof(HarmonyPatch), false))
				.GroupBy(GroupOf)
				.OrderBy(g => g.Key);
			foreach (var group in groups)
			{
				Apply(harmony, group.Key, group.ToList());
			}
		}

		private static string GroupOf(Type type)
		{
			var attribute = (PatchGroupAttribute)Attribute.GetCustomAttribute(type, typeof(PatchGroupAttribute));
			return attribute != null ? attribute.Name : type.Name;
		}

		private static void Apply(Harmony harmony, string name, List<Type> types)
		{
			try
			{
				foreach (var type in types)
				{
					var attribute = (PatchGroupAttribute)Attribute.GetCustomAttribute(type, typeof(PatchGroupAttribute));
					if (attribute == null)
					{
						continue;
					}
					foreach (var used in attribute.Uses)
					{
						RuntimeHelpers.RunClassConstructor(used.TypeHandle);
					}
				}
				foreach (var type in types)
				{
					harmony.CreateClassProcessor(type).Patch();
				}
			}
			catch (Exception ex)
			{
				Failed.Add(name);
				Remove(harmony, types);
				Plugin.Log.LogWarning($"Feature \"{name}\" is off: the game changed ({ex.GetBaseException().Message}). Everything else still works.");
			}
		}

		/// <summary>Removes every patch the given classes applied.</summary>
		private static void Remove(Harmony harmony, List<Type> types)
		{
			foreach (var original in harmony.GetPatchedMethods().ToList())
			{
				var info = Harmony.GetPatchInfo(original);
				if (info == null)
				{
					continue;
				}
				var ours = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
					.Where(p => p.owner == harmony.Id && types.Contains(p.PatchMethod.DeclaringType))
					.Select(p => p.PatchMethod)
					.ToList();
				foreach (var patch in ours)
				{
					harmony.Unpatch(original, patch);
				}
			}
		}
	}
}
