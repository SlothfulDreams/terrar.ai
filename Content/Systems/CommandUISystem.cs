using System.Collections.Generic;
using Microsoft.Xna.Framework;
using TerrarAI.Common.Players;
using TerrarAI.Content.UI;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;
using TerrarAIMod = TerrarAI.TerrarAI;

namespace TerrarAI.Content.Systems
{
    public class CommandUISystem : ModSystem
    {
        private UserInterface? _userInterface;
        private CommandPanelUI? _panel;
        private GameTime _lastUiGameTime = new();
        private bool _visible;

        public static CommandUISystem? Instance { get; private set; }

        public override void Load()
        {
			if (Main.dedServ)
			{
				return;
			}

			TerrarAIMod.LogInfo("CommandUISystem initializing on client.");

            Instance = this;

            _panel = new CommandPanelUI();
            _panel.OnSubmit += HandleSubmit;
            _panel.Activate();

            _userInterface = new UserInterface();
            _userInterface.SetState(_panel);
            Hide();
        }

        public override void Unload()
        {
            Instance = null;
            _userInterface = null;
            _panel = null;
        }

        public override void UpdateUI(GameTime gameTime)
        {
            _lastUiGameTime = gameTime;

            if (_visible)
            {
                _userInterface?.Update(gameTime);
                Main.blockInput = true;
            }
        }

        public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
        {
            if (!_visible || _userInterface == null || _panel == null)
            {
                return;
            }

            var inventoryIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Inventory"));
            if (inventoryIndex == -1)
            {
                inventoryIndex = layers.Count;
            }

            layers.Insert(inventoryIndex, new LegacyGameInterfaceLayer(
                "TerrarAI: Command Panel",
                delegate
                {
                    _userInterface.Draw(Main.spriteBatch, _lastUiGameTime);
                    return true;
                },
                InterfaceScaleType.UI));
        }

		public void ToggleUI()
		{
			if (_visible)
			{
				TerrarAIMod.LogInfo("CommandUISystem.ToggleUI -> Hide");
				Hide();
			}
			else
			{
				TerrarAIMod.LogInfo("CommandUISystem.ToggleUI -> Show");
				Show();
			}
		}

        public void Show()
        {
            if (_panel == null)
            {
                return;
            }

			_visible = true;
			Main.playerInventory = false;
			_panel.ClearInput();
			_panel.ShowDefaultStatus();
			_panel.FocusInput();
			TerrarAIMod.LogInfo("CommandUISystem.Show -> Panel focused.");
		}

		public void Hide()
		{
			_visible = false;
			Main.blockInput = false;
			_panel?.UnfocusInput();
			TerrarAIMod.LogInfo("CommandUISystem.Hide -> Panel closed.");
		}

		private void HandleSubmit(string rawText)
		{
			if (_panel == null)
            {
                return;
            }

			var player = Main.LocalPlayer;
			if (player == null || !player.active)
			{
				_panel.SetStatus("No active player detected.", Color.OrangeRed);
				TerrarAIMod.LogWarn("CommandUISystem.HandleSubmit: No active local player.");
				return;
			}

			TerrarAIMod.LogInfo($"CommandUISystem.HandleSubmit: Player {player.name} submitted \"{rawText}\"");
			var result = player.GetModPlayer<CommandUIPlayer>().TrySendCommand(rawText);
			_panel.SetStatus(result.Message, result.MessageColor);
			TerrarAIMod.LogInfo($"CommandUISystem.HandleSubmit result: success={result.Success}, message=\"{result.Message}\"");

			if (result.Success)
			{
				Hide();
			}
        }
    }
}
