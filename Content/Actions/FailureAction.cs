using TerrarAI.Content.Systems;

namespace TerrarAI.Content.Actions
{
    /// <summary>
    /// Simple action that immediately reports a failure back to the planner.
    /// Useful when a requested natural-language target cannot be resolved.
    /// </summary>
    public sealed class FailureAction : AgentAction
    {
        private readonly string _message;

        public FailureAction(string message)
        {
            _message = string.IsNullOrWhiteSpace(message)
                ? "Requested action could not be performed."
                : message;
        }

        public override string Name => "failure";

        protected override AgentActionResult OnTick(AgentActionContext context)
        {
            return AgentActionResult.Failure(_message);
        }
    }
}
