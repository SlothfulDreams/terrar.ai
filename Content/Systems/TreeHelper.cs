using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Systems
{
    /// <summary>
    /// Helper class for detecting and managing tree structures in Terraria.
    /// </summary>
    public static class TreeHelper
    {
        /// <summary>
        /// Checks if a tile is part of a tree trunk.
        /// </summary>
        public static bool IsTreeTile(Point tilePos)
        {
            var tile = Framing.GetTileSafely(tilePos.X, tilePos.Y);
            return tile.HasTile && TileID.Sets.IsATreeTrunk[tile.TileType];
        }

        /// <summary>
        /// Finds the base (lowest) tile of a tree given any trunk tile.
        /// </summary>
        public static Point? FindTreeBase(Point treeTile)
        {
            if (!IsTreeTile(treeTile))
            {
                return null;
            }

            // Scan downward to find the base
            int baseY = treeTile.Y;
            for (int y = treeTile.Y + 1; y < Main.maxTilesY; y++)
            {
                var checkTile = Framing.GetTileSafely(treeTile.X, y);
                if (!checkTile.HasTile || !TileID.Sets.IsATreeTrunk[checkTile.TileType])
                {
                    // Found ground - base is one tile up
                    break;
                }
                baseY = y;
            }

            return new Point(treeTile.X, baseY);
        }

        /// <summary>
        /// Finds the center (main) trunk column of a tree.
        /// Trees can have multiple trunk columns (up to 3 tiles wide).
        /// According to Terraria mechanics: "cutting at the lowermost center tile will destroy the entire tree".
        /// This method finds that critical center tile.
        /// </summary>
        public static Point? FindTreeCenter(Point treeTile)
        {
            if (!IsTreeTile(treeTile))
            {
                return null;
            }

            // Step 1: Find the base Y coordinate of any trunk column
            var basePos = FindTreeBase(treeTile);
            if (!basePos.HasValue)
            {
                return null;
            }

            int baseY = basePos.Value.Y;
            int centerX = basePos.Value.X;
            int bestHeight = MeasureTrunkHeight(basePos.Value.X, baseY);

            // Trees can be up to 3 tiles wide; scan neighboring columns to find the tallest trunk column.
            for (int offset = -2; offset <= 2; offset++)
            {
                int columnX = basePos.Value.X + offset;
                if (!IsTreeTile(new Point(columnX, baseY)))
                {
                    continue;
                }

                int height = MeasureTrunkHeight(columnX, baseY);
                bool taller = height > bestHeight;
                bool sameHeightCloser = height == bestHeight &&
                    Math.Abs(columnX - treeTile.X) < Math.Abs(centerX - treeTile.X);

                if (taller || sameHeightCloser)
                {
                    bestHeight = height;
                    centerX = columnX;
                }
            }

            return new Point(centerX, baseY);
        }

        private static int MeasureTrunkHeight(int columnX, int baseY, int maxHeight = 80)
        {
            int height = 0;
            for (int y = baseY; y >= 0 && height < maxHeight; y--)
            {
                var checkTile = Framing.GetTileSafely(columnX, y);
                if (!checkTile.HasTile || !TileID.Sets.IsATreeTrunk[checkTile.TileType])
                {
                    break;
                }
                height++;
            }

            return height;
        }

        /// <summary>
        /// Gets all trunk tiles belonging to a tree (scanning from base upward).
        /// </summary>
        public static List<Point> GetAllTreeTiles(Point treeBase)
        {
            var tiles = new List<Point>();

            if (!IsTreeTile(treeBase))
            {
                return tiles;
            }

            // Scan upward from base to find all trunk tiles
            for (int y = treeBase.Y; y >= 0; y--)
            {
                var checkTile = Framing.GetTileSafely(treeBase.X, y);
                if (!checkTile.HasTile || !TileID.Sets.IsATreeTrunk[checkTile.TileType])
                {
                    // Reached top of tree
                    break;
                }
                tiles.Add(new Point(treeBase.X, y));
            }

            return tiles;
        }

        /// <summary>
        /// Checks if a tree still exists (has any trunk tiles remaining).
        /// </summary>
        public static bool DoesTreeExist(Point treeBase)
        {
            // Check if any trunk tiles remain at this X coordinate around the base
            for (int y = treeBase.Y - 10; y <= treeBase.Y + 2; y++)
            {
                if (y < 0 || y >= Main.maxTilesY)
                {
                    continue;
                }

                var tile = Framing.GetTileSafely(treeBase.X, y);
                if (tile.HasTile && TileID.Sets.IsATreeTrunk[tile.TileType])
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the next mineable trunk tile of a tree (bottom to top order).
        /// </summary>
        public static Point? GetNextTreeTileToMine(Point treeBase)
        {
            var allTiles = GetAllTreeTiles(treeBase);

            if (allTiles.Count == 0)
            {
                return null;
            }

            // Return the lowest trunk tile (bottom-up mining)
            // This is more stable as it prevents the tree from floating
            return allTiles[allTiles.Count - 1];
        }

        /// <summary>
        /// Gets a human-readable description of a tree's status.
        /// </summary>
        public static string GetTreeStatus(Point treeBase)
        {
            var tiles = GetAllTreeTiles(treeBase);
            if (tiles.Count == 0)
            {
                return "Tree fully chopped";
            }

            return $"Tree: {tiles.Count} trunk tile{(tiles.Count == 1 ? "" : "s")} remaining";
        }

        /// <summary>
        /// Finds all unique tree bases near a center tile within a radius.
        /// </summary>
        public static List<Point> FindTreeBasesNear(Point centerTile, int radius)
        {
            var bases = new HashSet<Point>();

            for (int y = -radius; y <= radius; y += 2)
            {
                for (int x = -radius; x <= radius; x += 2)
                {
                    int checkX = centerTile.X + x;
                    int checkY = centerTile.Y + y;
                    var tile = Framing.GetTileSafely(checkX, checkY);

                    if (tile.HasTile && TileID.Sets.IsATreeTrunk[tile.TileType])
                    {
                        var treeBase = FindTreeBase(new Point(checkX, checkY));
                        if (treeBase.HasValue)
                        {
                            bases.Add(treeBase.Value);
                        }
                    }
                }
            }

            return bases.ToList();
        }
    }
}
