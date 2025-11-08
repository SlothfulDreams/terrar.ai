using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems
{
    public class WorldContext
    {
        private readonly NPC _agent;
        private const int SCAN_RADIUS = 25; // 25 tiles = ~400 pixels
        private const float MAX_REACH = 80f; // 5 tiles

        public WorldContext(NPC agent)
        {
            _agent = agent;
        }

        public string GetContextSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== YOUR SITUATION ===");
            sb.AppendLine($"Position: {GetPositionString()}");
            sb.AppendLine($"Nearby Resources: {GetResourceSummary()}");
            sb.AppendLine($"Nearby Blocks: {GetBlockSummary()}");
            sb.AppendLine($"Nearby Players: {GetPlayerNames()}");
            return sb.ToString();
        }

        private string GetPositionString()
        {
            int tileX = (int)(_agent.Center.X / 16f);
            int tileY = (int)(_agent.Center.Y / 16f);
            return $"tile({tileX},{tileY})";
        }

        private string GetResourceSummary()
        {
            var resources = ScanResources();
            if (resources.Count == 0)
            {
                return "none";
            }

            var grouped = resources.GroupBy(r => r.Type)
                                  .OrderBy(g => g.Min(r => r.Distance))
                                  .Take(8);

            var summaries = new List<string>();
            foreach (var group in grouped)
            {
                int count = group.Count();
                int reachable = group.Count(r => r.Distance <= MAX_REACH);
                string suffix = reachable > 0 ? $" ({reachable} reachable)" : "";
                summaries.Add($"{count} {group.Key}{suffix}");
            }

            return string.Join(", ", summaries);
        }

        private string GetBlockSummary()
        {
            var blocks = ScanBlocks();
            if (blocks.Count == 0)
            {
                return "none";
            }

            var sorted = blocks.OrderByDescending(kvp => kvp.Value)
                              .Take(5)
                              .Select(kvp => kvp.Key);

            return string.Join(", ", sorted);
        }

        private string GetPlayerNames()
        {
            var players = new List<string>();
            foreach (var player in Main.player)
            {
                if (player == null || !player.active || player.dead)
                {
                    continue;
                }

                float distance = Vector2.Distance(_agent.Center, player.Center);
                if (distance <= SCAN_RADIUS * 16f)
                {
                    players.Add(player.name);
                }
            }

            return players.Count > 0 ? string.Join(", ", players) : "none";
        }

        private List<ResourceInfo> ScanResources()
        {
            var resources = new List<ResourceInfo>();
            int agentTileX = (int)(_agent.Center.X / 16f);
            int agentTileY = (int)(_agent.Center.Y / 16f);

            for (int y = -SCAN_RADIUS; y <= SCAN_RADIUS; y += 2)
            {
                for (int x = -SCAN_RADIUS; x <= SCAN_RADIUS; x += 2)
                {
                    int checkX = agentTileX + x;
                    int checkY = agentTileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (!tile.HasTile)
                    {
                        continue;
                    }

                    var tileName = TileID.Search.GetName(tile.TileType);
                    if (!IsResourceTile(tileName))
                    {
                        continue;
                    }

                    float tileCenterX = checkX * 16f + 8f;
                    float tileCenterY = checkY * 16f + 8f;
                    float distance = Vector2.Distance(_agent.Center, new Vector2(tileCenterX, tileCenterY));

                    resources.Add(new ResourceInfo
                    {
                        Type = SimplifyResourceName(tileName),
                        TileX = checkX,
                        TileY = checkY,
                        Distance = distance
                    });
                }
            }

            return resources;
        }

        private Dictionary<string, int> ScanBlocks()
        {
            var blocks = new Dictionary<string, int>();
            int agentTileX = (int)(_agent.Center.X / 16f);
            int agentTileY = (int)(_agent.Center.Y / 16f);

            for (int y = -SCAN_RADIUS; y <= SCAN_RADIUS; y += 3)
            {
                for (int x = -SCAN_RADIUS; x <= SCAN_RADIUS; x += 3)
                {
                    int checkX = agentTileX + x;
                    int checkY = agentTileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (!tile.HasTile)
                    {
                        continue;
                    }

                    var tileName = TileID.Search.GetName(tile.TileType);
                    string simplified = SimplifyBlockName(tileName);

                    if (!blocks.ContainsKey(simplified))
                    {
                        blocks[simplified] = 0;
                    }
                    blocks[simplified]++;
                }
            }

            return blocks;
        }

        private bool IsResourceTile(string tileName)
        {
            return tileName.Contains("Ore") ||
                   tileName.Contains("Tree") ||
                   tileName.Contains("Gem") ||
                   tileName.Contains("Crystal") ||
                   tileName.Contains("Wood") ||
                   tileName.Contains("Gold") ||
                   tileName.Contains("Silver") ||
                   tileName.Contains("Copper") ||
                   tileName.Contains("Iron") ||
                   tileName.Contains("Platinum") ||
                   tileName.Contains("Tungsten") ||
                   tileName.Contains("Lead") ||
                   tileName.Contains("Tin");
        }

        private string SimplifyResourceName(string tileName)
        {
            if (tileName.Contains("Tree"))
            {
                return "trees";
            }
            if (tileName.Contains("Copper"))
            {
                return "copper_ore";
            }
            if (tileName.Contains("Iron"))
            {
                return "iron_ore";
            }
            if (tileName.Contains("Gold"))
            {
                return "gold_ore";
            }
            if (tileName.Contains("Silver"))
            {
                return "silver_ore";
            }
            if (tileName.Contains("Platinum"))
            {
                return "platinum_ore";
            }
            if (tileName.Contains("Tungsten"))
            {
                return "tungsten_ore";
            }
            if (tileName.Contains("Lead"))
            {
                return "lead_ore";
            }
            if (tileName.Contains("Tin"))
            {
                return "tin_ore";
            }

            return tileName.ToLower().Replace(" ", "_");
        }

        private string SimplifyBlockName(string tileName)
        {
            return tileName.ToLower().Replace(" ", "_");
        }

        public Point? FindNearest(string resourceType, Vector2 fromPosition)
        {
            var resources = ScanResources();
            var matching = resources.Where(r => r.Type.Equals(resourceType, StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(r => r.Distance)
                                   .FirstOrDefault();

            if (matching != null)
            {
                return new Point(matching.TileX, matching.TileY);
            }

            return null;
        }

        private class ResourceInfo
        {
            public string Type { get; set; } = string.Empty;
            public int TileX { get; set; }
            public int TileY { get; set; }
            public float Distance { get; set; }
        }
    }
}

