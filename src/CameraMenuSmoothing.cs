using Cinemachine;
using LazyBearTechnology;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Opening a menu makes one long frame, and the trailing camera jumps to cover it. For the first frames after a menu
	/// opens, the camera moves by a normal frame's time instead.
	/// </summary>
	internal static class CameraMenuSmoothing
	{
		private const int FRAMES = 3;
		private const float MAX_NORMAL_DT = 0.1f;

		private static float typicalDt = 1f / 60f;
		private static int overrideUntil = -1;
		private static bool overriding;

		internal static void Init()
		{
			LazyWindowsStackController.OnWindowOpened += OnWindowOpened;
		}

		private static void OnWindowOpened(LazyWidgetBase window)
		{
			if (!Plugin.EnableOptimizations.Value || !Plugin.SmoothCameraOnMenuOpen.Value || !LazyWindowsStackController.HasAnyModalWindowOpened)
			{
				return;
			}
			if (overriding || CinemachineCore.UniformDeltaTimeOverride < 0f)
			{
				CinemachineCore.UniformDeltaTimeOverride = typicalDt;
				overriding = true;
				overrideUntil = Time.frameCount + FRAMES - 1;
			}
		}

		/// <summary>Called every frame from Update (before Cinemachine's LateUpdate).</summary>
		internal static void Tick()
		{
			if (overriding)
			{
				if (Time.frameCount <= overrideUntil)
				{
					return;
				}
				CinemachineCore.UniformDeltaTimeOverride = -1f;
				overriding = false;
			}
			var dt = Time.deltaTime;
			if (dt > 0f && dt < MAX_NORMAL_DT)
			{
				typicalDt = Mathf.Lerp(typicalDt, dt, 0.1f);
			}
		}
	}
}
