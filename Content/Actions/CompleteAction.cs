using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using TerrarAI.Content.Systems;
using Terraria;

namespace TerrarAI.Content.Actions;

/// <summary>
/// Signals that the agent has completed its assigned task.
/// </summary>
public class CompleteAction : AgentAction
{
    private readonly string _message;

    public CompleteAction(string message)
    {
        _message = message ?? "Task completed.";
    }

    public override string Name => "Complete";

    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        // Only send chat message for failures (detected by keywords)
        if (Main.netMode != Terraria.ID.NetmodeID.Server && !string.IsNullOrWhiteSpace(_message))
        {
            bool isFailure = _message.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("no ", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("impossible", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                           _message.Contains("can't", StringComparison.OrdinalIgnoreCase);

            if (isFailure)
            {
                Main.NewText($"[{context.Agent.GivenName}] {_message}", Color.Orange);
            }
            // Success completions are silent (no chat spam)
        }

        return AgentActionResult.Success(_message);
    }

    public override void Reset()
    {
        base.Reset();
        // Nothing to reset
    }

    public static AgentAction CreateFromParameters(JsonElement parameters, ActionValidator validator)
    {
        var message = ActionParameterReader.ReadString(parameters, "message", required: false);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Task complete.";
        }

        return new CompleteAction(message);
    }
}
