# TerrarAI

An AI-powered agent mod for Terraria tModLoader that lets you spawn intelligent NPCs capable of understanding and executing natural language commands.

https://github.com/user-attachments/assets/67f0fc44-abfc-4884-83d4-9293c090899d

## Features

- Spawn AI agents that can understand natural language instructions
- Command agents to perform tasks like gathering resources, building structures, and mining
- AI-powered planning system using xAI (Grok) for complex task breakdown
- Real-time agent status tracking and feedback
- Chat-based command interface


## Installation

1. Install tModLoader
2. Place this mod in your tModLoader mods folder
3. Enable the mod in the Mods menu
4. Configure your xAI API key in the mod settings (required for AI functionality)

## Getting Started

### Step 1: Create an Agent

1. Press **T** or **Enter** to open Terraria's chat
2. Type one of the following:
   ```
   /create
   ```
   Or with a custom name:
   ```
   /create Builder
   /create Miner Bot
   ```
3. Press **Enter** to execute the command
4. An AI agent will appear near your character
5. You should see a chat message: "[Agent] Planning: [your spawned agent]"

**Note:** Only the server host can create agents (in singleplayer, you are the host).

### Step 2: Command Your Agent

Once you have created an agent, give it commands using chat:

1. Press **T** or **Enter** to open chat
2. Type your command:
   ```
   /action Go gather some wood
   /action Build a 10 block tall tower
   /action Mine some iron ore
   ```
3. Press **Enter** to send

You'll see chat messages from the agent as it plans and executes your commands.

## Command Reference

### Chat Commands

All commands are typed in Terraria's chat (press T or Enter first):

| Command | Description | Example |
|---------|-------------|---------|
| `/create [name]` | Creates a new AI agent near you | `/create Miner` |
| `/action <task>` | Sends a task to the nearest agent | `/action Gather wood` |
| `/remove [all]` | Removes the nearest agent or all agents | `/remove all` |
| `/testxai` | Test your xAI API connection | `/testxai` |

## Common Workflow

```
1. Press T → Type /create → Press Enter
   ↓
2. Agent spawns and confirms in chat
   ↓
3. Press T → Type /action Gather some wood → Press Enter
   ↓
4. Agent plans and executes (you'll see status updates in chat)
```

## Agent Chat Messages

The agent will send you chat messages to keep you informed:

**When planning starts:**
```
[Agent] Planning: "Go gather some wood"
```

**When planning completes:**
```
[Agent] Planning complete! Executing 5 action(s).
```

**If planning fails:**
```
[Agent] Planning failed: [error message]
[Agent] Tip: [helpful troubleshooting suggestion]
```

## Troubleshooting

### Testing Your API Connection

Before spawning agents, test your xAI API connection:
```
/testxai
```

**Expected responses:**
- ✅ "xAI replied: [response]" - Everything is working
- ❌ "Set an xAI API key in Mod Configuration first" - API key not configured
- ❌ "xAI request failed: [error]" - Connection or authentication issue

### Agent Gets Stuck in Planning

**If the agent stays in "Planning" state:**

1. **Check your API connection** - Run `/testxai` to verify
2. **Enable verbose logging:**
   - Go to **Workshop → Manage Mods → Mod Configuration**
   - Find **TerrarAI**
   - Set **EnableVerboseLogging** to `true`
   - Save and reload
3. **Check the logs** at `~/.local/share/Terraria/tModLoader/Logs/client.log`
   - Look for `[XAIClient]` entries
   - You'll see exact API requests and responses

**The agent will automatically timeout after 90 seconds** (configurable in settings).

### "No agent nearby" Error

**Cause:** No agent within 960 pixels of you when you ran `/action`

**Solution:**
- Create an agent first with `/create`
- Move closer to your existing agent
- The command targets the nearest agent within range

### Agent Not Responding

**Possible causes:**
- xAI API key not configured or invalid
- Network connectivity issues
- Agent is busy with another task
- API timeout (check timeout settings)

**Solutions:**
- Run `/testxai` to verify API connectivity
- Check mod settings for API configuration
- Enable verbose logging to see detailed error messages
- Increase timeout values in config if needed

## Configuration Options

Configure in **Workshop → Manage Mods → Mod Configuration → TerrarAI**:

### xAI Settings
- **ApiKey** - Your xAI API key (required)
- **Model** - AI model to use (default: grok-beta)
- **Temperature** - Response randomness (0.0-2.0, default: 0.7)
- **BaseEndpoint** - API endpoint URL

### Debugging
- **EnableVerboseLogging** - Log detailed API requests/responses (default: false)

### Timeouts
- **RequestTimeoutSeconds** - HTTP request timeout (10-300s, default: 60s)
- **MaxPlanningSeconds** - Max planning duration (30-300s, default: 90s)

## Agent States

Your agent can be in one of these states (shown above the agent):

- **Idle** - Waiting for commands
- **Planning** - Using AI to break down your command into steps
- **Executing** - Performing the planned actions
- **Replanning** - Adjusting the plan based on new information
- **Completed** - Task finished successfully

## Technical Details

### AI System

- Uses xAI (Grok) API for natural language understanding
- Breaks down complex commands into executable actions
- Action types: Move, Mine, Place, Say
- Supports multi-step task planning and execution
- Automatic timeout protection prevents infinite hangs
- Detailed error reporting with troubleshooting hints

### Agent Capabilities

Agents can:
- Understand natural language instructions
- Navigate the terrain
- Mine blocks and gather resources
- Place blocks and build structures
- Communicate via chat
- Adapt plans when obstacles are encountered
- Report progress and errors in chat

## License

[Add your license information here]

## Credits

Developed using tModLoader and powered by xAI (Grok) for intelligent command processing.
