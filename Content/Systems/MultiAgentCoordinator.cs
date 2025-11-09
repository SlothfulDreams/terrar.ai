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

        // Shared hellevator center X position (tile coordinate)
        private static int? _activeHellevatorCenterX = null;
        private static int? _hellevatorClaimingAgent = null;

        public override void OnWorldUnload()
        {
            // Clear all claims when world unloads
            _claimedTrees.Clear();
            _activeHellevatorCenterX = null;
            _hellevatorClaimingAgent = null;
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

        /// <summary>
        /// Claims a hellevator for an agent. First agent to call this sets the shared center X.
        /// Returns true if claim succeeded, false if already claimed by another agent.
        /// </summary>
        public static bool ClaimHellevator(int centerX, int agentWhoAmI)
        {
            if (_activeHellevatorCenterX.HasValue && _hellevatorClaimingAgent.HasValue)
            {
                // Hellevator already claimed - check if it's the same agent
                if (_hellevatorClaimingAgent.Value == agentWhoAmI)
                {
                    return true; // Same agent, already claimed
                }
                return false; // Already claimed by another agent
            }

            // First agent to claim sets the center
            _activeHellevatorCenterX = centerX;
            _hellevatorClaimingAgent = agentWhoAmI;
            return true;
        }

        /// <summary>
        /// Gets the shared hellevator center X position, or null if no hellevator is active.
        /// </summary>
        public static int? GetHellevatorCenter()
        {
            return _activeHellevatorCenterX;
        }

        /// <summary>
        /// Releases a hellevator claim. Only the claiming agent can release it.
        /// </summary>
        public static void ReleaseHellevator(int agentWhoAmI)
        {
            if (_hellevatorClaimingAgent.HasValue && _hellevatorClaimingAgent.Value == agentWhoAmI)
            {
                _activeHellevatorCenterX = null;
                _hellevatorClaimingAgent = null;
            }
        }

        /// <summary>
        /// Checks if a hellevator is currently being dug.
        /// </summary>
        public static bool IsHellevatorActive()
        {
            return _activeHellevatorCenterX.HasValue;
        }
    }
}

