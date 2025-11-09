using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrarAI.Content.Systems
{
    /// <summary>
    /// Coordinates multi-agent activities by tracking resource claims.
    /// Prevents multiple agents from working on the same resource simultaneously.
    /// </summary>
    public sealed class MultiAgentCoordinator : ModSystem
    {
        // Maps tree base position -> agent NPC.whoAmI
        private static readonly Dictionary<Point, int> _claimedTrees = new();

        public override void OnWorldUnload()
        {
            // Clear all claims when world unloads
            _claimedTrees.Clear();
        }

        /// <summary>
        /// Attempts to claim a tree for an agent. Returns true if claim succeeded, false if already claimed.
        /// </summary>
        public static bool ClaimTree(Point treeBase, int agentWhoAmI)
        {
            if (_claimedTrees.ContainsKey(treeBase))
            {
                return false; // Already claimed by another agent
            }

            _claimedTrees[treeBase] = agentWhoAmI;
            return true;
        }

        /// <summary>
        /// Releases a tree claim.
        /// </summary>
        public static void ReleaseTree(Point treeBase)
        {
            _claimedTrees.Remove(treeBase);
        }

        /// <summary>
        /// Checks if a tree is currently claimed by any agent.
        /// </summary>
        public static bool IsTreeClaimed(Point treeBase)
        {
            return _claimedTrees.ContainsKey(treeBase);
        }

        /// <summary>
        /// Gets all currently claimed tree positions.
        /// </summary>
        public static HashSet<Point> GetClaimedTrees()
        {
            return new HashSet<Point>(_claimedTrees.Keys);
        }

        /// <summary>
        /// Releases all tree claims for a specific agent (e.g., when agent dies or is recalled).
        /// </summary>
        public static void ReleaseAllClaimsForAgent(int agentWhoAmI)
        {
            var toRemove = new List<Point>();
            foreach (var kvp in _claimedTrees)
            {
                if (kvp.Value == agentWhoAmI)
                {
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var treeBase in toRemove)
            {
                _claimedTrees.Remove(treeBase);
            }
        }

        /// <summary>
        /// Gets the agent who claimed a specific tree, or null if not claimed.
        /// </summary>
        public static int? GetClaimingAgent(Point treeBase)
        {
            return _claimedTrees.TryGetValue(treeBase, out int agentWhoAmI) ? agentWhoAmI : null;
        }
    }
}

