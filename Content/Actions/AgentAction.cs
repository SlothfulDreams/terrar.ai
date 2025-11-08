using Terraria;

namespace TerrarAI.Content.Actions
{
    public abstract class AgentAction
    {
        public abstract string Name { get; }

        public abstract AgentActionResult Execute(AgentActionContext context);

        public virtual void Reset()
        {
        }
    }

    public readonly record struct AgentActionContext(NPC Agent, Player? Commander)
    {
        public static AgentActionContext From(NPC agent, Player? commander = null)
        {
            return new AgentActionContext(agent, commander);
        }
    }

    public readonly record struct AgentActionResult(AgentActionStatus Status, string? Message = null, object? Payload = null)
    {
        public static AgentActionResult Pending(string? message = null, object? payload = null) => new(AgentActionStatus.Pending, message, payload);

        public static AgentActionResult Success(string? message = null, object? payload = null) => new(AgentActionStatus.Success, message, payload);

        public static AgentActionResult Failure(string? message = null, object? payload = null) => new(AgentActionStatus.Failure, message, payload);
    }

    public enum AgentActionStatus
    {
        Pending,
        Success,
        Failure
    }
}
