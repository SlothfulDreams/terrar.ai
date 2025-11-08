using Microsoft.Xna.Framework;
using Terraria;

namespace TerrarAI.Content.Actions
{
    public abstract class AgentAction
    {
        private bool _hasEntered;
        private int _tickCount;

        public bool Cancelled { get; private set; }

        public abstract string Name { get; }

        /// <summary>
        /// Maximum number of ticks this action can execute before timing out.
        /// Default is 600 ticks (10 seconds). Override to customize per action type.
        /// </summary>
        protected virtual int MaxExecutionTicks => 600;

        public void Cancel()
        {
            if (!Cancelled)
            {
                Cancelled = true;
                OnCancel();
            }
        }

        internal AgentActionResult Tick(AgentActionContext context)
        {
            if (Cancelled)
            {
                return AgentActionResult.Failure("Action was cancelled");
            }

            // Increment tick counter and check for timeout
            _tickCount++;
            if (_tickCount > MaxExecutionTicks)
            {
                return AgentActionResult.Failure(
                    $"{Name} action timed out after {MaxExecutionTicks / 60f:F1}s. Target may be unreachable or task impossible.");
            }

            if (!_hasEntered)
            {
                OnEnter(context);
                _hasEntered = true;
            }

            var result = OnTick(context);

            if (result.Status != AgentActionStatus.Pending)
            {
                OnExit(context, result);
                _hasEntered = false;
            }

            return result;
        }

        public virtual void Reset()
        {
            _hasEntered = false;
            Cancelled = false;
            _tickCount = 0;
        }

        protected virtual void OnCancel()
        {
        }

        /// <summary>
        /// Gets the required range in pixels for this action to execute.
        /// Return 0 if no range validation is needed.
        /// </summary>
        public virtual float GetRequiredRange() => 0f;

        /// <summary>
        /// Gets the target tile for tile-based actions (mining, placing).
        /// Return null if action is not tile-based.
        /// </summary>
        public virtual Point? GetTargetTile() => null;

        /// <summary>
        /// Gets the target position for position-based actions (combat, gathering).
        /// Return null if action is not position-based.
        /// </summary>
        public virtual Vector2? GetTargetPosition() => null;

        protected virtual void OnEnter(AgentActionContext context)
        {
        }

        protected abstract AgentActionResult OnTick(AgentActionContext context);

        protected virtual void OnExit(AgentActionContext context, AgentActionResult result)
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
