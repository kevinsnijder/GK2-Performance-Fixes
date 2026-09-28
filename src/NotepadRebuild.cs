using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// For the "No More Running Back" Workshop mod (GK2Notepad). Its queue next to chests is built when a chest opens
	/// and built again two frames later, which only repeats the same work. The repeat is cancelled when the queue was
	/// just built. That mod switches itself off when its code is patched, so its fields are only read and written.
	/// </summary>
	internal static class NotepadRebuild
	{
		private const string PANEL_TYPE = "GK2Notepad.StorageTodoPanel";
		private const string NOTEPAD_ASSEMBLY = "GK2Notepad.Core";
		private const int RELAYOUT_DELAY_FRAMES = 2;
		private const BindingFlags INSTANCE = BindingFlags.Instance | BindingFlags.NonPublic;

		private static bool resolved;
		private static bool unavailable;
		private static IDictionary panels;
		private static FieldInfo rebuildAtField;
		private static FieldInfo framedField;
		private static FieldInfo sectionField;
		private static EventInfo rebuiltEvent;

		private static readonly Dictionary<UnityEngine.Object, int> lastRebuiltFrame = new Dictionary<UnityEngine.Object, int>();
		private static readonly Dictionary<UnityEngine.Object, int> firstFramedFrame = new Dictionary<UnityEngine.Object, int>();
		private static readonly List<UnityEngine.Object> stale = new List<UnityEngine.Object>();

		internal static void Init()
		{
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
				var panelType = assembly.GetType(PANEL_TYPE, true);
				panels = (IDictionary)panelType.GetField("panels", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
				rebuildAtField = panelType.GetField("rebuildAt", INSTANCE);
				framedField = panelType.GetField("framed", INSTANCE);
				sectionField = panelType.GetField("section", INSTANCE);
				rebuiltEvent = sectionField.FieldType.GetEvent("Rebuilt");
				if (panels == null || rebuildAtField?.FieldType != typeof(int) || framedField?.FieldType != typeof(bool) || rebuiltEvent == null)
				{
					throw new MissingMemberException("No More Running Back's chest queue changed");
				}
				resolved = true;
			}
			catch (Exception ex)
			{
				unavailable = true;
				Plugin.Log.LogWarning($"No More Running Back queue speed-up disabled: {ex.Message}");
			}
		}

		/// <summary>Called every frame from Update, between the frame a rebuild is scheduled and the frame it runs.</summary>
		internal static void Tick()
		{
			if (!resolved || !Plugin.EnableOptimizations.Value || !Plugin.SkipNotepadRebuild.Value || panels.Count == 0)
			{
				return;
			}
			try
			{
				foreach (DictionaryEntry entry in panels)
				{
					var panel = entry.Value as MonoBehaviour;
					if (panel != null)
					{
						Check(panel);
					}
				}
			}
			catch (Exception ex)
			{
				unavailable = true;
				resolved = false;
				Plugin.Log.LogWarning($"No More Running Back queue speed-up disabled: {ex.Message}");
			}
		}

		/// <summary>
		/// Cancels a scheduled rebuild when the queue was already built in the frame that scheduled it.
		/// The rebuild scheduled on the first open of a chest window is always kept.
		/// </summary>
		private static void Check(MonoBehaviour panel)
		{
			var section = sectionField.GetValue(panel) as MonoBehaviour;
			if (section == null)
			{
				return;
			}
			if (!lastRebuiltFrame.ContainsKey(section))
			{
				Watch(section);
			}
			if (!firstFramedFrame.ContainsKey(panel) && (bool)framedField.GetValue(panel))
			{
				firstFramedFrame[panel] = Time.frameCount;
			}
			var rebuildAt = (int)rebuildAtField.GetValue(panel);
			if (rebuildAt < 0 || !firstFramedFrame.TryGetValue(panel, out var framedAt) || framedAt >= rebuildAt - RELAYOUT_DELAY_FRAMES)
			{
				return;
			}
			if (lastRebuiltFrame[section] >= rebuildAt - RELAYOUT_DELAY_FRAMES)
			{
				rebuildAtField.SetValue(panel, -1);
			}
		}

		/// <summary>Records the frame of every queue build.</summary>
		private static void Watch(MonoBehaviour section)
		{
			PruneDestroyed();
			lastRebuiltFrame[section] = -1;
			rebuiltEvent.AddEventHandler(section, new Action(() =>
			{
				lastRebuiltFrame[section] = Time.frameCount;
			}));
		}

		private static void PruneDestroyed()
		{
			Prune(lastRebuiltFrame);
			Prune(firstFramedFrame);
		}

		private static void Prune(Dictionary<UnityEngine.Object, int> frames)
		{
			stale.Clear();
			foreach (var key in frames.Keys)
			{
				if (key == null)
				{
					stale.Add(key);
				}
			}
			foreach (var key in stale)
			{
				frames.Remove(key);
			}
		}
	}
}
