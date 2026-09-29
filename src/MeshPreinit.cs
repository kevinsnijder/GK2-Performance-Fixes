using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GK2Performance
{
	/// <summary>
	/// Most world objects set up their 3D sprite meshes the first time they become active (Object3DMesh.Start), which adds
	/// up to tens of milliseconds when a whole area appears at once. For the copies the mod creates behind the loading
	/// screen, that one-time setup runs right away, the same way it would run on first use.
	/// </summary>
	internal static class MeshPreinit
	{
		private static readonly Action<Object3DMesh> TryInit = CreateTryInit();
		private static readonly List<Object3DMesh> Buffer = new List<Object3DMesh>();

		private static Action<Object3DMesh> CreateTryInit()
		{
			var method = AccessTools.Method(typeof(Object3DMesh), "TryInit", Type.EmptyTypes);
			return method != null ? AccessTools.MethodDelegate<Action<Object3DMesh>>(method) : null;
		}

		internal static void Run(Component root)
		{
			if (TryInit == null || root == null)
			{
				return;
			}
			try
			{
				Buffer.Clear();
				root.GetComponentsInChildren(true, Buffer);
				foreach (var mesh in Buffer)
				{
					if (mesh != null)
					{
						TryInit(mesh);
					}
				}
			}
			catch (Exception)
			{
				Buffer.Clear();
				return;
			}
			Buffer.Clear();
		}
	}
}
