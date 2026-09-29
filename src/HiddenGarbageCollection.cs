using System;
using UnityEngine.Scripting;

namespace GK2Performance
{
	/// <summary>
	/// The garbage collector freezes a frame for about 20 ms every minute or two.
	/// While the screen is black (door fade, end of loading) it gets time to do that work, so it happens less during play.
	/// </summary>
	internal static class HiddenGarbageCollection
	{
		internal const float DOOR_BUDGET_MS = 30f;
		internal const float LOADING_SCREEN_BUDGET_MS = 200f;

		internal static void Run(float budgetMs)
		{
			if (!Plugin.EnableOptimizations.Value || !Plugin.CollectGarbageWhenHidden.Value)
			{
				return;
			}
			try
			{
				if (GarbageCollector.isIncremental)
				{
					GarbageCollector.CollectIncremental((ulong)(budgetMs * 1000000f));
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"Hidden garbage collection skipped: {ex.GetBaseException().Message}");
			}
		}
	}
}
