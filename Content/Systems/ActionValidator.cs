using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems
{
	public sealed class ActionValidator
	{
		private readonly Dictionary<int, int> _allowedBlockMap = new()
		{
			{ 1, TileID.Dirt }, // Friendly mapping from plan (1=dirt)
			{ 2, TileID.Stone }, // 2=stone
			{ 9, TileID.WoodBlock } // 9=wood
		};

		public Vector2 ClampPixelPosition(float x, float y)
		{
			var maxX = Main.maxTilesX * 16f;
			var maxY = Main.maxTilesY * 16f;

			return new Vector2(
				MathHelper.Clamp(x, 16f, maxX - 16f),
				MathHelper.Clamp(y, 16f, maxY - 16f));
		}

		public Point ClampTilePosition(int tileX, int tileY)
		{
			var maxX = Main.maxTilesX - 1;
			var maxY = Main.maxTilesY - 1;

			return new Point(
				Utils.Clamp(tileX, 1, maxX - 1),
				Utils.Clamp(tileY, 1, maxY - 1));
		}

		public int ValidateBlockType(int requestedType)
		{
			if (_allowedBlockMap.TryGetValue(requestedType, out var mapped))
			{
				return mapped;
			}

			if (requestedType >= 0 && requestedType < TileLoader.TileCount && _allowedBlockMap.ContainsValue(requestedType))
			{
				return requestedType;
			}

			throw new ActionParserException($"Unsupported blockType '{requestedType}'. Allowed: 1 (dirt), 2 (stone), 9 (wood).");
		}

		public void EnsureServerOrThrow()
		{
			if (!ServerAuthority.IsServer)
			{
				throw new InvalidOperationException("Actions can only be queued on the server or single-player host.");
			}
		}
	}
}
