using System;
using UnityEngine.Scripting;

namespace GK2Performance
{
	/// <summary>
	/// Every minute or two the game's garbage collector finishes a cycle, which freezes one frame for about 20 ms. At
	/// moments the screen is black anyway (a door fade, the end of a loading screen) the collector gets a short time
	/// budget to do that work, so the next pause during play comes later. What doesn't fit continues as usual.
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
