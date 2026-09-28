using UnityEngine;
using UnityEngine.SceneManagement;

namespace GK2Performance
{
	/// <summary>Drives the per-frame work of all features.</summary>
	internal class PerfRunner : MonoBehaviour
	{
		private const float SWEEP_INTERVAL_SECONDS = 1f;

		private float sweepTimer;
		private bool wasActive;

		internal static void Create()
		{
			var go = new GameObject("GK2Performance");
			go.hideFlags = HideFlags.HideAndDontSave;
			DontDestroyOnLoad(go);
			go.AddComponent<PerfRunner>();
		}

		private void OnEnable()
		{
			SceneManager.sceneUnloaded += OnSceneUnloaded;
			wasActive = IsStreamingActive();
		}

		private void OnDisable()
		{
			SceneManager.sceneUnloaded -= OnSceneUnloaded;
		}

		private void OnSceneUnloaded(Scene scene)
		{
			ChunkStreaming.Clear();
		}

		private static bool IsStreamingActive()
		{
			return Plugin.EnableOptimizations.Value && Plugin.OffscreenPreload.Value;
		}

		/// <summary>Runs each feature's per-frame work. Features switched off at runtime hand their objects back to the game.</summary>
		private void Update()
		{
			var active = IsStreamingActive();
			if (wasActive && !active)
			{
				ChunkStreaming.ReleaseAllHeld();
			}
			wasActive = active;

			CameraMenuSmoothing.Tick();
			NotepadRebuild.Tick();
			PrefabWarmer.Sweep();
			WorldPreloader.Tick();
			UiWindowPrefetch.Tick(ChunkStreaming.RanRecently && !WorldPreloader.IsBusy);

			sweepTimer += Time.unscaledDeltaTime;
			if (sweepTimer >= SWEEP_INTERVAL_SECONDS)
			{
				sweepTimer = 0f;
				ChunkStreaming.Sweep();
			}
		}

		/// <summary>Runs after every script's Update, so it sees what they changed this frame.</summary>
		private void LateUpdate()
		{
			NotepadChestRedraw.LateTick();
		}

		private void OnDestroy()
		{
			PrefabWarmer.ReleaseAll();
		}
	}
}
