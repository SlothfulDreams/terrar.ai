using TerrarAI;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems
{
    public enum ToolType
    {
        Pickaxe,
        Axe,
        Hammer,
        Weapon
    }

    public static class ToolSelector
    {
        /// <summary>
        /// Finds the best tool of a specific type from a player's inventory.
        /// </summary>
        public static Item? FindBestTool(Player? player, ToolType toolType, int minPower = 0)
        {
            if (TerrarAI_Config.Get().EnableCreativeMode)
            {
                return CreateCreativeTool(toolType);
            }

            if (player == null || player.inventory == null)
            {
                return null;
            }

            Item? bestTool = null;
            int bestPower = minPower;

            foreach (Item item in player.inventory)
            {
                if (item == null || item.IsAir)
                {
                    continue;
                }

                int power = toolType switch
                {
                    ToolType.Pickaxe => item.pick,
                    ToolType.Axe => item.axe,
                    ToolType.Hammer => item.hammer,
                    ToolType.Weapon => item.damage,
                    _ => 0
                };

                if (power > bestPower)
                {
                    bestTool = item;
                    bestPower = power;
                }
            }

            return bestTool;
        }

        /// <summary>
        /// Gets the minimum pickaxe power required to mine a specific tile type.
        /// </summary>
        public static int GetTileStrength(int tileType)
        {
            return tileType switch
            {
                // No pickaxe required
                TileID.Dirt => 0,
                TileID.Grass => 0,
                TileID.Stone => 0,
                TileID.Sand => 0,
                TileID.Mud => 0,
                TileID.Ash => 0,
                TileID.Silt => 0,
                TileID.Trees => 0,

                // Copper pickaxe or better (35%)
                TileID.Copper => 35,
                TileID.Tin => 35,
                TileID.Iron => 35,
                TileID.Lead => 35,

                // Iron/Lead pickaxe or better (40%)
                TileID.Silver => 40,
                TileID.Tungsten => 40,

                // Silver/Tungsten pickaxe or better (45%)
                TileID.Gold => 45,
                TileID.Platinum => 45,

                // Gold/Platinum pickaxe or better (50%)
                TileID.Meteorite => 50,

                // Nightmare/Deathbringer pickaxe or better (55%)
                TileID.Demonite => 55,
                TileID.Crimtane => 55,

                // Nightmare/Deathbringer pickaxe or better (65%)
                TileID.Ebonstone => 65,
                TileID.Crimstone => 65,
                TileID.Hellstone => 65,
                TileID.Obsidian => 65,

                // Molten pickaxe or better (100%)
                TileID.Cobalt => 100,
                TileID.Palladium => 100,

                // Cobalt/Palladium pickaxe or better (110%)
                TileID.Mythril => 110,
                TileID.Orichalcum => 110,

                // Mythril/Orichalcum pickaxe or better (150%)
                TileID.Adamantite => 150,
                TileID.Titanium => 150,

                // Adamantite/Titanium pickaxe or better (200%)
                TileID.Chlorophyte => 200,

                // Default: no requirement
                _ => 0
            };
        }

        /// <summary>
        /// Checks if a tool has sufficient power to mine a specific tile.
        /// </summary>
        public static bool CanMineTile(Item? tool, int tileType)
        {
            if (tool == null || tool.IsAir)
            {
                return false;
            }

            int requiredPower = GetTileStrength(tileType);
            return tool.pick >= requiredPower;
        }

        /// <summary>
        /// Gets a human-readable description of a tool's capabilities.
        /// </summary>
        public static string GetToolDescription(Item tool, ToolType toolType)
        {
            if (tool == null || tool.IsAir)
            {
                return "None";
            }

            return toolType switch
            {
                ToolType.Pickaxe => $"{tool.Name} ({tool.pick}% power)",
                ToolType.Axe => $"{tool.Name} ({tool.axe}% power)",
                ToolType.Hammer => $"{tool.Name} ({tool.hammer}% power)",
                ToolType.Weapon => $"{tool.Name} ({tool.damage} damage)",
                _ => tool.Name
            };
        }

        private static Item CreateCreativeTool(ToolType toolType)
        {
            var item = new Item();

            switch (toolType)
            {
                case ToolType.Pickaxe:
                    item.SetDefaults(ItemID.PickaxeAxe);
                    item.pick = 1000;
                    item.SetNameOverride("Creative Pickaxe");
                    break;
                case ToolType.Axe:
                    item.SetDefaults(ItemID.SpectrePickaxe);
                    item.axe = 500;
                    item.SetNameOverride("Creative Axe");
                    break;
                case ToolType.Hammer:
                    item.SetDefaults(ItemID.TheBreaker);
                    item.hammer = 500;
                    item.SetNameOverride("Creative Hammer");
                    break;
                case ToolType.Weapon:
                    item.SetDefaults(ItemID.TerraBlade);
                    item.damage = 999;
                    item.SetNameOverride("Creative Weapon");
                    break;
                default:
                    item.SetDefaults(ItemID.CopperPickaxe);
                    item.pick = 1000;
                    item.SetNameOverride("Creative Tool");
                    break;
            }

            item.rare = ItemRarityID.Red;
            item.accessory = false;
            return item;
        }
    }
}
