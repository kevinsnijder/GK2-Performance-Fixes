using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// The player moves with the physics tick (50 Hz) while the screen draws faster, which makes walking judder.
	/// Turns on interpolation for the player's physics body.
	/// </summary>
	internal static class PlayerMotion
	{
		private static readonly AccessTools.FieldRef<PlayerPhysicalBody, Rigidbody> RbRef =
			AccessTools.FieldRefAccess<PlayerPhysicalBody, Rigidbody>("rb");

		private static PlayerPhysicalBody appliedTo;

		internal static void OnPlayerFixedUpdate(PlayerPhysicalBody body)
		{
			if (ReferenceEquals(body, appliedTo))
			{
				return;
			}
			appliedTo = body;
			if (!Plugin.EnableOptimizations.Value || !Plugin.SmoothPlayerMotion.Value)
			{
				return;
			}
			var rb = RbRef(body);
			if (rb != null && rb.interpolation == RigidbodyInterpolation.None)
			{
				rb.interpolation = RigidbodyInterpolation.Interpolate;
			}
		}
	}

	[HarmonyPatch(typeof(PlayerPhysicalBody), "FixedUpdate")]
	internal static class PlayerFixedUpdatePatch
	{
		/// <summary>Applies the setting once to each new player body.</summary>
		private static void Postfix(PlayerPhysicalBody __instance)
		{
			PlayerMotion.OnPlayerFixedUpdate(__instance);
		}
	}
}
