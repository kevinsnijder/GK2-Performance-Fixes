using System;
using System.Reflection;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// For No More Running Back: after a chest window opens or closes, it redraws the window one storage at a time,
	/// and the game lays out the whole window after each one. The mod does the same redraw with one layout at the end.
	/// </summary>
	internal static class NotepadChestRedraw
	{
		private const string CHEST_LOCK_TYPE = "GK2Notepad.ChestLock";
		private const string NOTEPAD_ASSEMBLY = "GK2Notepad.Core";
		private const BindingFlags INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;
		private const BindingFlags STATIC = BindingFlags.Static | BindingFlags.NonPublic;

		private static readonly FieldInfo LeftSideField = AccessTools.Field(typeof(UIBaseChestWindow), "leftMultiInventoryWidget");
		private static readonly FieldInfo RightSideField = AccessTools.Field(typeof(UIBaseChestWindow), "rightMultiInventoryWidget");
		private static readonly MethodInfo OnInventoryRedrawMethod = AccessTools.Method(typeof(MultiInventoryWidget), "OnInventoryRedraw");
		private static readonly FieldInfo OnRedrawField = AccessTools.Field(typeof(InventoryWidget), "onRedraw");

		private static bool resolved;
		private static bool unavailable;
		private static FieldInfo instanceField;
		private static FieldInfo refreshWindowField;
		private static FieldInfo applyRequestedField;

		private static bool chestWindowChanged;
		private static MonoBehaviour pendingFor;

		internal static void Init()
		{
			if (LeftSideField == null || RightSideField == null || OnInventoryRedrawMethod == null || OnRedrawField?.FieldType != typeof(Action))
			{
				Plugin.Log.LogWarning("No More Running Back chest speed-up disabled: chest window layout changed");
				return;
			}
			LazyWindowsStackController.OnWindowOpened += OnWindowChanged;
			LazyWindowsStackController.OnWindowClosed += OnWindowChanged;
			AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				TryResolve(assembly);
			}
		}

		private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
		{
			TryResolve(args.LoadedAssembly);
		}

		private static void TryResolve(Assembly assembly)
		{
			if (resolved || unavailable || assembly.GetName().Name != NOTEPAD_ASSEMBLY)
			{
				return;
			}
			try
			{
				var chestLockType = assembly.GetType(CHEST_LOCK_TYPE, true);
				instanceField = chestLockType.GetField("instance", STATIC);
				refreshWindowField = chestLockType.GetField("refreshWindow", INSTANCE);
				applyRequestedField = chestLockType.GetField("applyRequested", INSTANCE);
				if (instanceField == null || refreshWindowField?.FieldType != typeof(bool) || applyRequestedField?.FieldType != typeof(bool))
				{
					throw new MissingMemberException("No More Running Back's chest lock changed");
				}
				resolved = true;
			}
			catch (Exception ex)
			{
				unavailable = true;
				Plugin.Log.LogWarning($"No More Running Back chest speed-up disabled: {ex.Message}");
			}
		}

		private static void OnWindowChanged(LazyWidgetBase window)
		{
			if (window is UIBaseChestWindow)
			{
				chestWindowChanged = true;
			}
		}

		private static bool IsActive()
		{
			return resolved && Plugin.EnableOptimizations.Value && Plugin.BatchNotepadChestRedraw.Value;
		}

		/// <summary>
		/// Runs every frame after Update. Takes over the redraw No More Running Back asked for, and carries it out
		/// once that mod has re-applied its chest locks.
		/// </summary>
		internal static void LateTick()
		{
			if (pendingFor == null && !chestWindowChanged)
			{
				return;
			}
			try
			{
				if (pendingFor != null)
				{
					CheckPending();
				}
				if (chestWindowChanged)
				{
					chestWindowChanged = false;
					if (IsActive())
					{
						TakeOver();
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning($"No More Running Back chest speed-up disabled: {ex.Message}");
				RestorePending();
				unavailable = true;
				resolved = false;
			}
		}

		/// <summary>Clears the redraw request so No More Running Back skips its own one-by-one redraw.</summary>
		private static void TakeOver()
		{
			var chestLock = instanceField.GetValue(null) as MonoBehaviour;
			if (chestLock == null || !chestLock.isActiveAndEnabled || !(bool)refreshWindowField.GetValue(chestLock)
				|| !(bool)applyRequestedField.GetValue(chestLock))
			{
				return;
			}
			pendingFor = chestLock;
			refreshWindowField.SetValue(chestLock, false);
		}

		/// <summary>Waits while the chest locks are not re-applied yet, like No More Running Back would.</summary>
		private static void CheckPending()
		{
			var chestLock = pendingFor;
			if (!IsActive() || !chestLock.isActiveAndEnabled || instanceField.GetValue(null) as MonoBehaviour != chestLock)
			{
				RestorePending();
				return;
			}
			if ((bool)applyRequestedField.GetValue(chestLock))
			{
				return;
			}
			pendingFor = null;
			RedrawChestWindow(LazyWindowsStackController.ActiveWindow as UIBaseChestWindow);
		}

		/// <summary>Hands the redraw back to No More Running Back, as if it had never been taken over.</summary>
		private static void RestorePending()
		{
			var chestLock = pendingFor;
			pendingFor = null;
			if (chestLock != null)
			{
				refreshWindowField.SetValue(chestLock, true);
			}
		}

		/// <summary>Redraws the storages like No More Running Back does, then runs the window layout once per side.</summary>
		private static void RedrawChestWindow(UIBaseChestWindow window)
		{
			if (window == null)
			{
				return;
			}
			var sides = new[] { LeftSideField.GetValue(window) as MultiInventoryWidget, RightSideField.GetValue(window) as MultiInventoryWidget };
			var needsLayout = new bool[sides.Length];
			for (var i = 0; i < sides.Length; i++)
			{
				if (sides[i] == null)
				{
					continue;
				}
				foreach (var inventory in sides[i].DrawnInventories)
				{
					if (inventory != null && RedrawWithoutLayout(inventory, sides[i]))
					{
						needsLayout[i] = true;
					}
				}
			}
			for (var i = 0; i < sides.Length; i++)
			{
				if (needsLayout[i] && sides[i] != null)
				{
					OnInventoryRedrawMethod.Invoke(sides[i], null);
				}
			}
		}

		/// <summary>
		/// Redraws one storage with the window's layout handler taken out of its redraw event, so every other listener
		/// still runs. Returns whether the layout handler was taken out.
		/// </summary>
		private static bool RedrawWithoutLayout(InventoryWidget inventory, MultiInventoryWidget multi)
		{
			var original = OnRedrawField.GetValue(inventory) as Action;
			var stripped = original;
			Delegate layout = null;
			if (original != null)
			{
				foreach (var handler in original.GetInvocationList())
				{
					if (handler.Target == (object)multi && handler.Method == OnInventoryRedrawMethod)
					{
						stripped = (Action)Delegate.Remove(stripped, handler);
						layout = Delegate.Combine(layout, handler);
					}
				}
			}
			if (layout == null)
			{
				inventory.Redraw();
				return false;
			}
			OnRedrawField.SetValue(inventory, stripped);
			try
			{
				inventory.Redraw();
			}
			finally
			{
				var current = OnRedrawField.GetValue(inventory) as Action;
				OnRedrawField.SetValue(inventory, current == stripped ? original : Delegate.Combine(current, layout));
			}
			return true;
		}
	}
}
