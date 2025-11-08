using Microsoft.Xna.Framework;
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

    public override AgentActionResult Execute(AgentActionContext context)
    {
        // Send completion message to chat
        if (Main.netMode != Terraria.ID.NetmodeID.Server && !string.IsNullOrWhiteSpace(_message))
        {
            Main.NewText($"[{context.Agent.GivenName}] {_message}", Color.LimeGreen);
        }

        return AgentActionResult.Success(_message);
    }

    public override void Reset()
    {
        // Nothing to reset
    }
}
