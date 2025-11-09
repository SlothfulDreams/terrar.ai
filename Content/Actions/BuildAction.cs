using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;
using Terraria.ID;

namespace TerrarAI.Content.Actions
{
    public sealed class BuildAction : AgentAction
    {
        private readonly string _direction;
        private const float BUILD_SPEED = 0.8f;

        public BuildAction(string direction)
        {
            _direction = direction.ToLowerInvariant();
        }

        public override string Name => "build";

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            var npc = context.Agent;

            // Determine movement direction (left = -1, right = 1)
            int directionSign = _direction == "left" ? -1 : 1;

            // Face the correct direction
            npc.direction = directionSign;
            npc.spriteDirection = directionSign;

            // Calculate tile position directly below agent's feet
            int agentTileX = (int)(npc.position.X / 16f);
            int agentTileY = (int)((npc.position.Y + npc.height) / 16f);

            // SPAM place blocks - absolute priority!
            PlaceBlock(agentTileX, agentTileY);

            // Walk in the specified direction
            float targetSpeed = BUILD_SPEED * 3f; // Base speed is ~3, so 0.8x = 2.4
            npc.velocity.X = directionSign * targetSpeed;

            return AgentActionResult.Pending($"Building {_direction}...");
        }

        private void PlaceBlock(int tileX, int tileY)
        {
            // FORCE place stone block - overwrite anything in the way!
            WorldGen.PlaceTile(tileX, tileY, TileID.Stone, forced: true);

            if (Main.netMode == NetmodeID.Server)
            {
                NetMessage.SendData(MessageID.TileManipulation, -1, -1, null, 1, tileX, tileY, TileID.Stone);
            }
        }

        public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
        {
            var direction = ActionParameterReader.ReadString(parameters, "direction", required: true);
            return new BuildAction(direction);
        }
    }
}

