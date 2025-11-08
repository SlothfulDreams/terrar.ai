using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Actions
{
	public sealed class MineAction : AgentAction
	{
		private readonly Point _tile;

		public MineAction(Point tile)
		{
			_tile = tile;
		}

		public override string Name => "mine";

		public override AgentActionResult Execute(AgentActionContext context)
		{
			if (!ServerAuthority.IsServer)
			{
				return AgentActionResult.Failure("MineAction must run on the server.");
			}

			var tile = Framing.GetTileSafely(_tile.X, _tile.Y);
			if (!tile.HasTile)
			{
				return AgentActionResult.Success($"Tile {_tile.X},{_tile.Y} already empty.");
			}

			WorldGen.KillTile(_tile.X, _tile.Y, false, false, true);

			if (Main.netMode == NetmodeID.Server)
			{
				NetMessage.SendTileSquare(-1, _tile.X, _tile.Y, 1);
			}

			tile = Framing.GetTileSafely(_tile.X, _tile.Y);
			if (!tile.HasTile)
			{
				return AgentActionResult.Success($"Mined tile at {_tile.X},{_tile.Y}");
			}

			return AgentActionResult.Pending("Mining...");
		}
	}
}
