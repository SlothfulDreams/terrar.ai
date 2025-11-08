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
        private readonly Player? _commander;
        private readonly Point? _lockedMineTarget;
        private readonly string? _lockReason;
        private const int SCAN_RADIUS = 25; // 25 tiles = ~400 pixels
        private const int RESOURCE_SCAN_RADIUS = 60; // Extended search for meaningful resources
        private const float MAX_REACH = 80f; // 5 tiles
        private const float ITEM_STABLE_SPEED_THRESHOLD = 0.6f; // px per frame

        public WorldContext(NPC agent, Player? commander = null, Point? lockedMineTarget = null, string? lockReason = null)
        {
            _agent = agent;
            _commander = commander;
            _lockedMineTarget = lockedMineTarget;
            _lockReason = lockReason;
        }

        public string GetContextSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== YOUR SITUATION ===");
            sb.AppendLine($"Position: {GetPositionString()}");
            
            var trees = GetTreeSummary();
            if (!string.IsNullOrEmpty(trees))
            {
                sb.AppendLine($"Trees:\n{trees}");
            }
            
            var ores = GetResourceSummary();
            if (!string.IsNullOrEmpty(ores))
            {
                sb.AppendLine($"Ores:\n{ores}");
            }
            
            sb.AppendLine($"Nearby Blocks: {GetBlockSummary()}");
            sb.AppendLine($"Nearby Items: {GetItemsSummary()}");
            sb.AppendLine($"Commander Target: {GetCommanderSummary()}");
            sb.AppendLine($"Nearby Players: {GetNearbyPlayersSummary()}");
            return sb.ToString();
        }

        private string GetPositionString()
        {
            int tileX = (int)(_agent.Center.X / 16f);
            int tileY = (int)(_agent.Center.Y / 16f);
            return $"tile({tileX},{tileY})";
        }

        private string GetTreeSummary()
        {
            int agentTileX = (int)(_agent.Center.X / 16f);
            int agentTileY = (int)(_agent.Center.Y / 16f);
            var centerTile = new Point(agentTileX, agentTileY);
            var treeBases = TreeHelper.FindTreeBasesNear(centerTile, RESOURCE_SCAN_RADIUS);
            
            if (treeBases.Count == 0)
            {
                return string.Empty;
            }
            
            // If actively chopping, show current target
            if (_lockedMineTarget.HasValue && _lockReason == "tree")
            {
                return $"Active: chop tile({_lockedMineTarget.Value.X},{_lockedMineTarget.Value.Y})";
            }
            
            // Show nearest 5 tree bases (tile coords only, no distance)
            var nearest = treeBases.OrderBy(t => TileDistance(t)).Take(5);
            var lines = nearest.Select(t => $"Tree: tile({t.X},{t.Y})");
            return string.Join("\n", lines);
        }
        
        private int TileDistance(Point tile)
        {
            int agentTileX = (int)(_agent.Center.X / 16f);
            int agentTileY = (int)(_agent.Center.Y / 16f);
            return Math.Abs(tile.X - agentTileX) + Math.Abs(tile.Y - agentTileY);
        }
        
        private string GetResourceSummary()
        {
            var resources = ScanResources();
            // Exclude trees - they're shown separately
            resources = resources.Where(r => r.Type != "trees").ToList();
            
            if (resources.Count == 0)
            {
                return "none";
            }

            // If there's a locked target (ore), show it prominently
            if (_lockedMineTarget.HasValue && _lockReason != "tree")
            {
                var lockedResource = resources.FirstOrDefault(r => r.TileX == _lockedMineTarget.Value.X && r.TileY == _lockedMineTarget.Value.Y);
                if (lockedResource != null)
                {
                    string lockedInfo = $"Active: mine tile({lockedResource.TileX},{lockedResource.TileY})";

                    // Filter out other resources of the same type
                    var otherResources = resources.Where(r => r.Type != lockedResource.Type || (r.TileX == lockedResource.TileX && r.TileY == lockedResource.TileY)).ToList();

                    if (otherResources.Count == 0 || otherResources.All(r => r.TileX == lockedResource.TileX && r.TileY == lockedResource.TileY))
                    {
                        return lockedInfo;
                    }

                    // Show locked target first, then other resource types
                    var grouped = otherResources.Where(r => r.Type != lockedResource.Type)
                                               .GroupBy(r => r.Type)
                                               .OrderBy(g => g.Min(r => TileDistance(new Point(r.TileX, r.TileY))))
                                               .Take(5);

                    var summaries = new List<string> { lockedInfo };
                    foreach (var group in grouped)
                    {
                        var nearest = group.OrderBy(r => TileDistance(new Point(r.TileX, r.TileY))).First();
                        summaries.Add($"{group.Key}: tile({nearest.TileX},{nearest.TileY})");
                    }

                    return string.Join("\n", summaries);
                }
            }

            // No lock - show resources normally (tile coords only, no distances)
            var groupedNormal = resources.GroupBy(r => r.Type)
                                  .OrderBy(g => g.Min(r => TileDistance(new Point(r.TileX, r.TileY))))
                                  .Take(8);

            var summariesNormal = new List<string>();
            foreach (var group in groupedNormal)
            {
                var nearest = group.OrderBy(r => TileDistance(new Point(r.TileX, r.TileY))).Take(3)
                                   .Select(r => $"tile({r.TileX},{r.TileY})");

                string nearestInfo = string.Join(", ", nearest);
                summariesNormal.Add($"{group.Key}: {nearestInfo}");
            }

            return string.Join("\n", summariesNormal);
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

        private string GetItemsSummary()
        {
            var items = ScanItems();
            if (items.Count == 0)
            {
                return "none (auto-collected)";
            }

            // Group by item name and sum stacks
            var grouped = items.GroupBy(i => i.Name)
                              .Select(g => new
                              {
                                  Name = g.Key,
                                  TotalStack = g.Sum(i => i.Stack),
                                  MinDistance = g.Min(i => i.Distance)
                              })
                              .OrderBy(g => g.MinDistance)
                              .Take(8);

            var summaries = grouped.Select(g => $"{g.Name} x{g.TotalStack} ({g.MinDistance:F0}px)");
            return string.Join(", ", summaries);
        }

        private string GetCommanderSummary()
        {
            if (_commander == null || !_commander.active || _commander.dead)
            {
                return "none (no active commander)";
            }

            return $"{_commander.name} {FormatRelativePosition(_commander.Center)}";
        }

        private string GetNearbyPlayersSummary()
        {
            var summaries = new List<string>();
            foreach (var player in Main.player)
            {
                if (player == null || !player.active || player.dead)
                {
                    continue;
                }

                if (_commander != null && player.whoAmI == _commander.whoAmI)
                {
                    continue;
                }

                float distance = Vector2.Distance(_agent.Center, player.Center);
                if (distance <= SCAN_RADIUS * 16f)
                {
                    summaries.Add($"{player.name} {FormatRelativePosition(player.Center)}");
                }
            }

            return summaries.Count > 0 ? string.Join("; ", summaries) : "none within scan radius";
        }

        private List<ItemInfo> ScanItems()
        {
            var items = new List<ItemInfo>();

            for (int i = 0; i < Main.maxItems; i++)
            {
                Item item = Main.item[i];
                if (item == null || !item.active || item.IsAir)
                {
                    continue;
                }

                if (!IsItemStable(item))
                {
                    continue;
                }

                float distance = Vector2.Distance(_agent.Center, item.position);

                // Only show items within scan radius
                if (distance <= SCAN_RADIUS * 16f)
                {
                    items.Add(new ItemInfo
                    {
                        Name = item.Name,
                        Stack = item.stack,
                        Distance = distance
                    });
                }
            }

            return items;
        }

        private bool IsItemStable(Item item)
        {
            if (item.noGrabDelay > 0)
            {
                return false;
            }

            if (item.velocity.LengthSquared() > ITEM_STABLE_SPEED_THRESHOLD * ITEM_STABLE_SPEED_THRESHOLD)
            {
                return false;
            }

            return true;
        }

        private List<ResourceInfo> ScanResources()
        {
            var resources = new List<ResourceInfo>();
            int agentTileX = (int)(_agent.Center.X / 16f);
            int agentTileY = (int)(_agent.Center.Y / 16f);

            for (int y = -RESOURCE_SCAN_RADIUS; y <= RESOURCE_SCAN_RADIUS; y += 2)
            {
                for (int x = -RESOURCE_SCAN_RADIUS; x <= RESOURCE_SCAN_RADIUS; x += 2)
                {
                    int checkX = agentTileX + x;
                    int checkY = agentTileY + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (!tile.HasTile)
                    {
                        continue;
                    }

                    if (!TryGetResourceType(tile.TileType, out string resourceType))
                    {
                        continue;
                    }

                    float tileCenterX = checkX * 16f + 8f;
                    float tileCenterY = checkY * 16f + 8f;
                    float distance = Vector2.Distance(_agent.Center, new Vector2(tileCenterX, tileCenterY));

                    resources.Add(new ResourceInfo
                    {
                        Type = resourceType,
                        TileX = checkX,
                        TileY = checkY,
                        Distance = distance,
                        Reachable = distance <= MAX_REACH
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

        private bool TryGetResourceType(ushort tileType, out string resourceType)
        {
            if (TileID.Sets.IsATreeTrunk[tileType])
            {
                resourceType = "trees";
                return true;
            }

            if (Main.tileOreFinderPriority[tileType] > 0)
            {
                resourceType = TileID.Search.GetName(tileType).ToLower().Replace(" ", "_");
                return true;
            }

            resourceType = string.Empty;
            return false;
        }

        private string SimplifyBlockName(string tileName)
        {
            return tileName.ToLower().Replace(" ", "_");
        }

        private string FormatRelativePosition(Vector2 target)
        {
            int tileX = (int)(target.X / 16f);
            int tileY = (int)(target.Y / 16f);
            var delta = target - _agent.Center;
            string direction = DescribeDirection(delta);
            return $"tile({tileX},{tileY}) [{direction}]";
        }

        private string DescribeDirection(Vector2 delta)
        {
            int tilesX = (int)Math.Round(delta.X / 16f);
            int tilesY = (int)Math.Round(delta.Y / 16f);

            string horizontal = tilesX == 0
                ? "same X"
                : $"{Math.Abs(tilesX)} tile{(Math.Abs(tilesX) == 1 ? "" : "s")} {(tilesX > 0 ? "right" : "left")}";

            string vertical = tilesY == 0
                ? "same Y"
                : $"{Math.Abs(tilesY)} tile{(Math.Abs(tilesY) == 1 ? "" : "s")} {(tilesY > 0 ? "below" : "above")}";

            return $"{horizontal}, {vertical}";
        }

        private class ResourceInfo
        {
            public string Type { get; set; } = string.Empty;
            public int TileX { get; set; }
            public int TileY { get; set; }
            public float Distance { get; set; }
            public bool Reachable { get; set; }
        }

        private class ItemInfo
        {
            public string Name { get; set; } = string.Empty;
            public int Stack { get; set; }
            public float Distance { get; set; }
        }
    }
}
