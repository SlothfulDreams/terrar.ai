using System;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems
{
	public static class ServerAuthority
	{
		public static bool IsServer => Main.netMode != NetmodeID.MultiplayerClient;

		public static bool EnsureServer(Action<string> reply, string? denyMessage = null)
		{
			if (IsServer)
			{
				return true;
			}

			reply(denyMessage ?? "Only the server or a single-player instance can perform this action.");
			return false;
		}

		public static void QueueMainThread(Action action)
		{
			if (action == null)
			{
				return;
			}

			if (Main.dedServ)
			{
				Main.QueueMainThreadAction(action);
			}
			else
			{
				action();
			}
		}
	}
}
