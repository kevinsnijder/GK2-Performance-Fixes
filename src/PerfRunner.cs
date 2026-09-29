using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GK2Performance
{
	/// <summary>Drives the per-frame work of all features. A feature that throws is switched off on its own.</summary>
	internal class PerfRunner : MonoBehaviour
	{
		private const float SWEEP_INTERVAL_SECONDS = 1f;

		private sealed class Job
		{
			public string Name;
			public string Group;
			public Action Run;
			public bool Failed;
		}

		private float sweepTimer;
		private bool wasActive;

		private Job[] updateJobs;
		private Job[] sweepJobs;
		private Job[] lateJobs;

		internal static void Create()
		{
			var go = new GameObject("GK2Performance");
			go.hideFlags = HideFlags.HideAndDontSave;
			DontDestroyOnLoad(go);
			go.AddComponent<PerfRunner>();
		}

		private void Awake()
		{
			updateJobs = new[]
			{
				NewJob("camera smoothing", null, CameraMenuSmoothing.Tick),
				NewJob("prefab warm-up", Features.STREAMING, PrefabWarmer.Sweep),
				NewJob("world preload", Features.PRELOAD_WORLD, WorldPreloader.Tick),
				NewJob("menu prefetch", null, TickMenus),
				NewJob("loading screen", Features.HOLD_LOADING_SCREEN, LoadingScreenHold.Tick)
			};
			sweepJobs = new[]
			{
				NewJob("off-screen decor", Features.STREAMING, ChunkStreaming.Sweep),
				NewJob("prewarm band", Features.STREAMING, PrewarmThrottle.Sweep)
			};
			lateJobs = new[]
			{
				NewJob("No More Running Back chest redraw", null, NotepadChestRedraw.LateTick),
				NewJob("back light", null, BackLightFixes.LateTick)
			};
		}

		private static Job NewJob(string name, string group, Action run)
		{
			return new Job { Name = name, Group = group, Run = run };
		}

		private static void TickMenus()
		{
			var worldRunning = PatchGroups.IsActive(Features.STREAMING) && ChunkStreaming.RanRecently;
			var preloading = PatchGroups.IsActive(Features.PRELOAD_WORLD) && WorldPreloader.IsBusy;
			UiWindowPrefetch.Tick(worldRunning && !preloading);
			WindowPrecreate.Tick();
		}

		private void OnEnable()
		{
			SceneManager.sceneUnloaded += OnSceneUnloaded;
			SceneManager.sceneLoaded += OnSceneLoaded;
			Camera.onPreCull += OnPreCull;
			wasActive = IsStreamingActive();
		}

		private void OnDisable()
		{
			SceneManager.sceneUnloaded -= OnSceneUnloaded;
			SceneManager.sceneLoaded -= OnSceneLoaded;
			Camera.onPreCull -= OnPreCull;
		}

		private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			try
			{
				TerrainBackLight.Reset();
				TerrainBackLight.Validate();
			}
			catch (Exception ex)
			{
				TerrainBackLight.Fail(ex.GetBaseException().Message);
			}
		}

		private static void OnPreCull(Camera camera)
		{
			try
			{
				TerrainBackLight.SyncMasks();
			}
			catch (Exception ex)
			{
				TerrainBackLight.Fail(ex.GetBaseException().Message);
			}
		}

		private void OnSceneUnloaded(Scene scene)
		{
			if (PatchGroups.IsActive(Features.STREAMING))
			{
				ChunkStreaming.Clear();
			}
		}

		private static bool IsStreamingActive()
		{
			return Plugin.EnableOptimizations.Value && Plugin.OffscreenPreload.Value;
		}

		private static void RunAll(Job[] jobs)
		{
			foreach (var job in jobs)
			{
				if (job.Failed || (job.Group != null && !PatchGroups.IsActive(job.Group)))
				{
					continue;
				}
				try
				{
					job.Run();
				}
				catch (Exception ex)
				{
					job.Failed = true;
					Plugin.Log.LogWarning($"Switched off {job.Name}: {ex.GetBaseException().Message}");
				}
			}
		}

		/// <summary>Runs each feature's per-frame work. A feature switched off while playing gives its objects back.</summary>
		private void Update()
		{
			var active = IsStreamingActive();
			if (wasActive && !active && PatchGroups.IsActive(Features.STREAMING))
			{
				ChunkStreaming.ReleaseAllHeld();
			}
			wasActive = active;

			RunAll(updateJobs);

			sweepTimer += Time.unscaledDeltaTime;
			if (sweepTimer >= SWEEP_INTERVAL_SECONDS)
			{
				sweepTimer = 0f;
				RunAll(sweepJobs);
			}
		}

		/// <summary>Runs after every script's Update, so it sees what they changed this frame.</summary>
		private void LateUpdate()
		{
			RunAll(lateJobs);
		}

		private void OnDestroy()
		{
			if (PatchGroups.IsActive(Features.STREAMING))
			{
				PrefabWarmer.ReleaseAll();
			}
		}
	}
}
