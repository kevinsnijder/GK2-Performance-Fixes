using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>How the game's back light is drawn.</summary>
	internal enum BackLightMode
	{
		Normal,
		Soft,
		Off
	}

	/// <summary>
	/// The back light is a second sun pointing up. It draws every object once more, about a fifth of the frame outdoors.
	/// Soft blends it into the normal lighting instead; Off turns it off.
	/// </summary>
	internal static class BackLightFixes
	{
		private static bool disabledByMod;
		private static bool failed;

		private static BackLightMode Mode
		{
			get
			{
				return Plugin.EnableOptimizations.Value ? Plugin.BackLight.Value : BackLightMode.Normal;
			}
		}

		/// <summary>Applies the chosen back light mode.</summary>
		internal static void LateTick()
		{
			if (failed)
			{
				return;
			}
			try
			{
				var system = BackLight.System();
				var light = system != null ? system.BackLight : null;
				if (light == null)
				{
					return;
				}
				var mode = Mode;
				if (mode == BackLightMode.Off && light.enabled)
				{
					system.DisableBackLight();
					disabledByMod = true;
				}
				else if (mode != BackLightMode.Off && disabledByMod)
				{
					system.EnableBackLight();
					disabledByMod = false;
				}
				var renderMode = mode == BackLightMode.Soft ? LightRenderMode.ForceVertex : LightRenderMode.Auto;
				if (light.renderMode != renderMode)
				{
					light.renderMode = renderMode;
				}
			}
			catch (Exception ex)
			{
				failed = true;
				Plugin.Log.LogWarning($"Switched off the BackLight setting: {ex.GetBaseException().Message}");
			}
		}
	}

	/// <summary>
	/// The back light points up, so it never lights the ground, yet the ground is drawn once more for it.
	/// The ground moves to a spare layer that everything treats like its own, except the back light.
	/// </summary>
	internal static class TerrainBackLight
	{
		private const int SOURCE_LAYER = 0;
		private const string TERRAIN_SHADER = "Custom/Lazy Terrain";
		private const float MIN_UPWARD = 0.8f;

		private static readonly int[] SpareCandidates = { 13, 25 };
		private static readonly HashSet<GameObject> Moved = new HashSet<GameObject>();
		private static readonly Camera[] CameraBuffer = new Camera[16];

		private static int spareLayer = -1;
		private static bool validated;
		private static bool failed;
		private static int syncedFrame = -1;

		internal static bool Enabled
		{
			get
			{
				return !failed && PatchGroups.IsActive(Features.TERRAIN_BACK_LIGHT)
					&& Plugin.EnableOptimizations.Value && Plugin.SkipBackLightOnTerrain.Value && Plugin.BackLight.Value == BackLightMode.Normal;
			}
		}

		/// <summary>Moves a ground piece that the game just set up onto the spare layer.</summary>
		internal static void Assign(LazyTerrainMesh mesh)
		{
			if (!Enabled || mesh == null)
			{
				return;
			}
			if (!validated)
			{
				Validate();
				if (failed)
				{
					return;
				}
			}
			var go = mesh.gameObject;
			if (Moved.Contains(go) || go.layer != SOURCE_LAYER || go.GetComponent<Collider>() != null)
			{
				return;
			}
			var renderer = go.GetComponent<MeshRenderer>();
			if (renderer == null || renderer.sharedMaterial == null || renderer.sharedMaterial.shader == null || renderer.sharedMaterial.shader.name != TERRAIN_SHADER)
			{
				return;
			}
			go.layer = spareLayer;
			Moved.Add(go);
		}

		/// <summary>Picks a layer nothing else uses. Checked again when an area loads.</summary>
		internal static void Validate()
		{
			if (!Enabled)
			{
				return;
			}
			validated = true;
			var used = new HashSet<int>();
			foreach (var renderer in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
			{
				if (!Moved.Contains(renderer.gameObject))
				{
					used.Add(renderer.gameObject.layer);
				}
			}
			if (spareLayer >= 0 && !used.Contains(spareLayer))
			{
				return;
			}
			Restore();
			foreach (var candidate in SpareCandidates)
			{
				if (!used.Contains(candidate))
				{
					spareLayer = candidate;
					return;
				}
			}
			Fail("no unused layer left");
		}

		/// <summary>
		/// Before a frame renders, gives every camera and light the same setting for the spare layer as for the ground's layer.
		/// Only the back light skips it. When the fix is off, the ground goes back to its own layer.
		/// </summary>
		internal static void SyncMasks()
		{
			if (syncedFrame == Time.frameCount)
			{
				return;
			}
			syncedFrame = Time.frameCount;
			if (!Enabled)
			{
				if (Moved.Count > 0)
				{
					Restore();
				}
				return;
			}
			if (spareLayer < 0 || Moved.Count == 0)
			{
				return;
			}
			var backLight = BackLight.Find();
			var skipGround = backLight != null && backLight.type == LightType.Directional && backLight.transform.forward.y >= MIN_UPWARD;
			var count = Camera.GetAllCameras(CameraBuffer);
			for (var i = 0; i < count; i++)
			{
				var camera = CameraBuffer[i];
				camera.cullingMask = Mirror(camera.cullingMask, false);
			}
			foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
			{
				var mask = Mirror(light.cullingMask, skipGround && light == backLight);
				if (mask != light.cullingMask)
				{
					light.cullingMask = mask;
				}
			}
		}

		private static int Mirror(int mask, bool exclude)
		{
			var bit = 1 << spareLayer;
			var include = !exclude && (mask & (1 << SOURCE_LAYER)) != 0;
			return include ? mask | bit : mask & ~bit;
		}

		/// <summary>Puts every moved ground piece back on its own layer.</summary>
		internal static void Restore()
		{
			foreach (var go in Moved)
			{
				if (go != null)
				{
					go.layer = SOURCE_LAYER;
				}
			}
			Moved.Clear();
		}

		internal static void Fail(string reason)
		{
			if (failed)
			{
				return;
			}
			failed = true;
			Restore();
			Plugin.Log.LogWarning($"Switched off {Features.TERRAIN_BACK_LIGHT}: {reason}");
		}

		internal static void Reset()
		{
			validated = false;
		}
	}

	[PatchGroup(Features.TERRAIN_BACK_LIGHT)]
	[HarmonyPatch(typeof(LazyTerrainMesh), nameof(LazyTerrainMesh.DrawFromData))]
	internal static class LazyTerrainMeshDrawPatch
	{
		private static void Postfix(LazyTerrainMesh __instance)
		{
			try
			{
				TerrainBackLight.Assign(__instance);
			}
			catch (Exception ex)
			{
				TerrainBackLight.Fail(ex.GetBaseException().Message);
			}
		}
	}

	/// <summary>Finds the game's back light without the game's own slow search.</summary>
	internal static class BackLight
	{
		private static readonly AccessTools.FieldRef<LightsSystem> Instance = AccessTools.StaticFieldRefAccess<LightsSystem>(AccessTools.Field(typeof(LightsSystem), "instance"));

		internal static LightsSystem System()
		{
			var system = Instance();
			return system != null ? system : null;
		}

		internal static Light Find()
		{
			var system = System();
			if (system == null)
			{
				return null;
			}
			var light = system.BackLight;
			return light != null && light.isActiveAndEnabled ? light : null;
		}
	}
}
