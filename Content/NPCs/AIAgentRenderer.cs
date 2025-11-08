using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;

namespace TerrarAI.Content.NPCs
{
    public sealed class AIAgentRenderer
    {
        private Player? _appearanceClone;

        public void ClonePlayerAppearance(Player sourcePlayer)
        {
            _appearanceClone = new Player();

            _appearanceClone.skinVariant = sourcePlayer.skinVariant;
            _appearanceClone.hair = sourcePlayer.hair;
            _appearanceClone.hairDye = sourcePlayer.hairDye;
            _appearanceClone.hairColor = sourcePlayer.hairColor;
            _appearanceClone.skinColor = sourcePlayer.skinColor;
            _appearanceClone.eyeColor = sourcePlayer.eyeColor;
            _appearanceClone.shirtColor = sourcePlayer.shirtColor;
            _appearanceClone.underShirtColor = sourcePlayer.underShirtColor;
            _appearanceClone.pantsColor = sourcePlayer.pantsColor;
            _appearanceClone.shoeColor = sourcePlayer.shoeColor;

            for (int i = 0; i < sourcePlayer.armor.Length; i++)
            {
                _appearanceClone.armor[i] = sourcePlayer.armor[i].Clone();
            }
            for (int i = 0; i < sourcePlayer.dye.Length; i++)
            {
                _appearanceClone.dye[i] = sourcePlayer.dye[i].Clone();
            }

            _appearanceClone.Male = sourcePlayer.Male;
        }

        public bool HasAppearance => _appearanceClone != null;

        public void DrawAsPlayer(NPC npc, SpriteBatch spriteBatch, Vector2 screenPos, Color lightColor)
        {
            if (_appearanceClone == null) return;

            _appearanceClone.position = npc.position;
            _appearanceClone.direction = npc.direction;
            _appearanceClone.velocity = npc.velocity;
            _appearanceClone.fullRotation = 0f;
            _appearanceClone.fullRotationOrigin = Vector2.Zero;

            if (Math.Abs(npc.velocity.X) > 0.1f)
            {
                _appearanceClone.legFrame.Y = (int)((Main.GameUpdateCount / 7) % 20) * 56;
            }
            else
            {
                _appearanceClone.legFrame.Y = 0;
            }
            _appearanceClone.bodyFrame.Y = _appearanceClone.legFrame.Y;
            _appearanceClone.headFrame.Y = 0;

            Main.PlayerRenderer.DrawPlayer(Main.Camera, _appearanceClone, npc.position, 0f, Vector2.Zero, 0f);
        }

        public void DrawStatusText(NPC npc, AgentState state, string statusMessage, SpriteBatch spriteBatch, Vector2 screenPos)
        {
            var stateText = state.ToString();
            var messageText = string.IsNullOrWhiteSpace(statusMessage) ? "Ready" : statusMessage;

            var combined = $"{stateText}: {messageText}";
            var font = FontAssets.MouseText.Value;
            var measurement = font.MeasureString(combined);
            var drawPosition = npc.Top - screenPos - new Vector2(measurement.X * 0.5f, 24f);

            var color = state switch
            {
                AgentState.Planning => Color.CornflowerBlue,
                AgentState.Executing => Color.LimeGreen,
                AgentState.Replanning => Color.Orange,
                AgentState.Completed => Color.LightGray,
                _ => Color.White
            };

            Utils.DrawBorderString(spriteBatch, combined, drawPosition, color, 0.9f);
        }
    }
}

