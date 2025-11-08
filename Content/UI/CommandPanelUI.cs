using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.UI;
using TerrarAIMod = TerrarAI.TerrarAI;

namespace TerrarAI.Content.UI
{
    public class CommandPanelUI : UIState
    {
        private const string DefaultStatus = "Type a task for the nearest TerrarAI agent.";

        private UIPanel _panel = null!;
        private CommandInputField _inputField = null!;
        private UIText _statusText = null!;
        private UITextPanel<string> _submitButton = null!;

        public event Action<string>? OnSubmit;

        public override void OnInitialize()
        {
            _panel = new UIPanel();
            _panel.Width.Set(420f, 0f);
            _panel.Height.Set(220f, 0f);
            _panel.HAlign = 0.5f;
            _panel.VAlign = 0.2f;
            _panel.SetPadding(16f);
            Append(_panel);

            var title = new UIText("TerrarAI Command");
            title.HAlign = 0.5f;
            _panel.Append(title);

            _inputField = new CommandInputField("Example: Go gather some wood", 240);
            _inputField.Top.Set(48f, 0f);
            _inputField.Width.Set(-24f, 1f);
            _inputField.Height.Set(52f, 0f);
            _panel.Append(_inputField);

            _submitButton = new UITextPanel<string>("Send", 0.9f, true);
            _submitButton.Width.Set(120f, 0f);
            _submitButton.Height.Set(36f, 0f);
            _submitButton.Top.Set(116f, 0f);
            _submitButton.HAlign = 1f;
            _submitButton.OnLeftClick += (_, _) => Submit();
            _panel.Append(_submitButton);

            _statusText = new UIText(DefaultStatus);
            _statusText.Top.Set(164f, 0f);
            _statusText.Width.Set(-24f, 1f);
            _statusText.TextColor = Color.LightGray;
            _panel.Append(_statusText);
        }

        public void FocusInput() => _inputField.Focus();

        public void UnfocusInput() => _inputField.Unfocus();

        public void ClearInput() => _inputField.SetText(string.Empty);

        public void ShowDefaultStatus() => SetStatus(DefaultStatus, Color.LightGray);

        public void SetStatus(string text, Color color)
        {
            _statusText.SetText(text);
            _statusText.TextColor = color;
        }

        public string CurrentText => _inputField.Text;

        private void Submit()
        {
            SoundEngine.PlaySound(SoundID.MenuTick);
            OnSubmit?.Invoke(_inputField.Text);
        }

        public override void Draw(SpriteBatch spriteBatch)
        {
            base.Draw(spriteBatch);
            Main.LocalPlayer.mouseInterface = true;
        }

        private sealed class CommandInputField : UIPanel
        {
            private readonly UIText _textDisplay;
            private readonly string _placeholder;
            private readonly int _maxLength;

            private string _text = string.Empty;
            private bool _focused;

            internal CommandInputField(string placeholder, int maxLength)
            {
                _placeholder = placeholder;
                _maxLength = Math.Max(1, maxLength);

                SetPadding(8f);
                BackgroundColor = new Color(33, 43, 79) * 0.9f;
                BorderColor = new Color(89, 116, 213);

                _textDisplay = new UIText(string.Empty);
                _textDisplay.Left.Set(4f, 0f);
                _textDisplay.Top.Set(6f, 0f);
                _textDisplay.IgnoresMouseInteraction = true;
                Append(_textDisplay);

                UpdateDisplayedText();
            }

            internal string Text => _text;

            internal void SetText(string value)
            {
                _text = value ?? string.Empty;
                if (_text.Length > _maxLength)
                {
                    _text = _text[.._maxLength];
                }

                UpdateDisplayedText();
            }

            internal void Focus()
            {
                if (_focused)
                {
                    return;
                }

                _focused = true;
                Main.clrInput();
                PlayerInput.WritingText = true;
                Main.instance?.HandleIME();
                TerrarAIMod.LogInfo("CommandInputField focused.");
            }

            internal void Unfocus()
            {
                if (!_focused)
                {
                    return;
                }

                _focused = false;
                PlayerInput.WritingText = false;
                Main.blockInput = false;
                UpdateDisplayedText();
                TerrarAIMod.LogInfo("CommandInputField unfocused.");
            }

            public override void LeftClick(UIMouseEvent evt)
            {
                base.LeftClick(evt);
                Focus();
            }

            public override void Update(GameTime gameTime)
            {
                base.Update(gameTime);

                if (_focused)
                {
                    Main.LocalPlayer.mouseInterface = true;
                    PlayerInput.WritingText = true;
                    Main.blockInput = true;
                    Main.instance?.HandleIME();

                    var incoming = Main.GetInputText(_text) ?? string.Empty;
                    if (incoming.Length > _maxLength)
                    {
                        incoming = incoming[.._maxLength];
                    }

                    if (!incoming.Equals(_text, StringComparison.Ordinal))
                    {
                        _text = incoming;
                    }
                }
                else if (ContainsPoint(Main.MouseScreen))
                {
                    Main.LocalPlayer.mouseInterface = true;
                }

                UpdateDisplayedText();
            }

            private void UpdateDisplayedText()
            {
                if (_focused)
                {
                    var caret = Main.GameUpdateCount % 30 < 15 ? "|" : string.Empty;
                    var text = string.IsNullOrEmpty(_text) ? caret : $"{_text}{caret}";
                    _textDisplay.SetText(text);
                    _textDisplay.TextColor = Color.White;
                    return;
                }

                if (string.IsNullOrEmpty(_text))
                {
                    _textDisplay.SetText(_placeholder);
                    _textDisplay.TextColor = Color.Gray;
                    return;
                }

                _textDisplay.SetText(_text);
                _textDisplay.TextColor = Color.White;
            }
        }
    }
}
