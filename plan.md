# TERRAR.AI IMPLEMENTATION PLAN

## PROJECT OVERVIEW
Build AI-powered autonomous agents in Terraria using xAI API.
Players type natural language commands → AI agents execute tasks in-game
.

## CURRENT STATE
- **Phase 1 ✅ COMPLETE**: Config system, xAI client, `/testxai` command, server-only helpers all implemented and tested.
- **Phase 2 ✅ COMPLETE**: AgentAction hierarchy, parser + validator, move/mine/place/say behaviors fully implemented and compiling. Actions now include range validation, stability checks, and position verification during execution.
- **Phase 3 ✅ COMPLETE**: AIAgentNPC with full state machine, `/create` and `/action` commands working. Agents now render as full player character clones with all animations (skin, hair, armor, accessories). Planning phase includes verbose logging, HTTP timeouts, planning timeouts, and chat notifications for visibility.
- **Phase 4 ✅ ENHANCED**: Advanced context gathering implemented including:
  - **Tile Scanning**: Expanded from 3×3 to 21×21 grid (441 tiles) with absolute coordinates, distances, and reachability status
  - **Resource Discovery**: Dedicated `DescribeNearbyResources()` scans 31×31 grid for ores, trees, gems with tool requirement validation
  - **Inventory Context**: `DescribeInventory()` provides placeable blocks and collected resources
  - **Enhanced System Prompt**: Includes directional context, concrete coordinate conversion examples, and real-world positioning
  - **Tool Integration**: `ToolSelector` system validates pickaxe power vs tile strength (15+ tile types with 0-200% power requirements)
- **Phase 4 ⚠️ PARTIAL**: Memory system (`AgentMemory`, `PromptBuilder`) and observation reporting still outstanding
- **Action Enhancements** (Post-Phase 3):
  - **Mining Stability**: Added velocity zeroing, stability checks (velocity < 0.5), position validation every tick, and strong friction (0.5f)
  - **Realistic Mining Speed**: Reduced from 0.3-0.5s to 1-3s (damage reduced from 3-5/tick to 1-2/tick)
  - **Visible Animations**: Increased from 15 ticks to 30 ticks per swing (3-4 swings per mine vs 1.3 barely-visible)
  - **Drift Prevention**: Triple-layer protection (stability check + init zeroing + continuous dampening)
  - **Better Error Messages**: Context-rich failures with tile names, coordinates, and troubleshooting hints
- **Rendering System**: Agents use Terraria's native `Main.PlayerRenderer.DrawPlayer()` for full player character rendering including all animations. Separated rendering logic into `AIAgentRenderer.cs`. Uses vanilla fallback texture (`Terraria/Images/NPC_0`) since custom sprite not needed.
- **UI Changes**: Command UI (CommandPanelUI, CommandUISystem, CommandUIPlayer) was removed in favor of simpler chat-only interface. All commands now use standard Terraria chat (`T` or `Enter` key).
- Remaining phases (multi-agent coordination, final polish, advanced features) are still outstanding.

---

## PHASE 1: CORE INFRASTRUCTURE

### Goal
Set up foundational systems for AI agent communication.

### Tasks
1. Create AgentState enum (Idle, Planning, Executing, Replanning, Completed)
2. Build XAIClient class for async HTTP requests to xAI API
3. Create TerrarAI_Config for user settings (API key, model, temperature)
4. Update TerrarAI.cs with keybind registration (J key for command panel)
5. Add /testxai command to verify API connection
6. Establish server-authoritative flow helpers (NetmodeID checks, utility methods) so only the server issues xAI calls and mutates world state while clients display results.

### File Structure
```
Content/
  NPCs/
    AgentState.cs
  Systems/
    XAIClient.cs
Common/
  Commands/
    TestAPICommand.cs
TerrarAI_Config.cs
```

### Success Criteria
- Config menu shows xAI API settings
- /testxai command successfully calls xAI API
- Response displays in chat
- Multiplayer guardrails confirmed: server handles API + actions, clients only mirror via packets

---

## PHASE 2: ACTION SYSTEM

### Goal
Create action framework for agent behaviors.

### Tasks
1. Create AgentAction abstract base class with Execute() returning AgentActionResult and Reset() for reuse.
2. Define AgentActionResult + AgentActionStatus enum (Pending, Success, Failure) with optional message/payload for logging and replanning hooks.
3. Implement SayAction (display chat message)
4. Implement MoveAction (navigate to pixel coordinates)
5. Implement MineAction (break tiles using WorldGen.KillTile)
6. Implement PlaceBlockAction (place tiles using WorldGen.PlaceTile)
7. Build ActionParser (converts JSON string to validated action queue)
8. Add ActionValidator that clamps coordinates, converts pixel↔tile consistently, and rejects unknown block types or unsafe operations before queueing.

### File Structure
```
Content/
  Actions/
    AgentAction.cs
    SayAction.cs
    MoveAction.cs
    MineAction.cs
    PlaceBlockAction.cs
  Systems/
    ActionParser.cs
    ActionValidator.cs
```

### JSON Format Expected
```json
{
  "actions": [
    {"type": "move", "params": {"x": 1500, "y": 800}},
    {"type": "say", "params": {"text": "Moving!"}},
    {"type": "mine", "params": {"tileX": 100, "tileY": 50}},
    {"type": "place", "params": {"tileX": 100, "tileY": 50, "blockType": 1}}
  ]
}
```

### Action Lifecycle
```
AgentAction.Execute():
- Returns AgentActionResult each tick
- Status Pending keeps action on queue
- Status Success dequeues and logs optional message/payload
- Status Failure includes error string + payload consumed by Phase 5 replanning

AgentAction.Reset():
- Clears internal state so pooled instances can run new commands
```

### Success Criteria
- Actions execute when manually triggered
- ActionParser converts JSON to action objects correctly
- Actions modify game world (tiles break/place, NPC moves)
- Multiplayer sync works (NetMessage.SendData for tile changes)
- Invalid/malicious JSON is rejected with clear errors before affecting the world
- ActionResult failures propagate up to agent state machine for replanning

---

## PHASE 3: AI AGENT NPC

### Goal
Create the main agent NPC entity with state machine and xAI integration.

### Tasks
1. Create AIAgentNPC class extending ModNPC
2. Implement SetDefaults() (width, height, friendly, no damage)
3. Implement state machine in AI() method
4. Add ReceiveCommand() method to accept user input
5. Build BuildSystemPrompt() for xAI context
6. Build BuildUserPrompt() with command
7. Implement async xAI request handling (poll Task.IsCompleted)
8. Parse xAI response using ActionParser
9. Execute actions from queue frame-by-frame
10. Add PostDraw() visual state indicator above agent
11. Create /create command
12. Extract common movement logic into MovementHelper (ground checks, jump assist, adaptive tolerance)
13. Support creative-mode travel/tools (config toggle) so NPCs can snap to coordinates and place blocks without inventory limits
14. Detect hellevator/vertical-digging commands and enforce a 2x2 shaft (column clamping + auto-centering) regardless of model output

### File Structure
```
Content/
  NPCs/
    AIAgentNPC.cs
    AIAgentNPC.png (custom 20x30 sprite sheet, 19 horizontal frames)
Common/
  Commands/
    SpawnAgentCommand.cs
```

### State Machine Logic
```
AI() method per-frame logic:
- Idle: velocity.X *= 0.8 (friction), wait for command
- Planning: Check if pendingRequest.IsCompleted, transition when ready
- Executing: Execute currentAction, remove if complete, get next from queue
- Replanning: Send failure context to xAI, transition to Executing
- Completed: Display message, transition to Idle
```

### System Prompt Structure
```
You are an AI agent in Terraria at position (X, Y).

AVAILABLE ACTIONS:
- move(x, y): Move to pixel coordinates
- mine(tileX, tileY): Mine tile at grid position
- place(tileX, tileY, blockType): Place block (1=dirt, 2=stone, 9=wood)
- say(text): Display message in chat

CURRENT STATE:
- Position: (X, Y)
- Health: HP/MaxHP
- Nearby tiles: [description]
- Nearby entities: [description]

IMPORTANT:
- Tile coordinates = pixel coordinates / 16
- Respond ONLY with valid JSON
- Keep action lists short (1-5 actions)

Format: {"actions": [{"type": "action", "params": {...}}]}
```

### Success Criteria
- Agent spawns with /create command
- Agent receives commands via ReceiveCommand()
- Agent calls xAI API without blocking game thread
- Agent parses response and executes action queue
- State displays above agent head (colored text)
- Agent completes simple commands ("say hello", "move to 1000 500")
- Server remains authoritative for planning/execution; clients only send commands/display packets
- MovementHelper coordinates all navigation (ground checks + jumps) across actions
- Creative mode toggle allows instant movement/placement without using commander inventory
- Hellevator detection keeps the agent centered and mining a 2x2 column even if the LLM returns misaligned coordinates

---

## PHASE 4: ADVANCED PROMPTING

### Goal
Improve xAI context awareness and response quality.

### Tasks
1. Implement GetNearbyTilesDescription() in AIAgentNPC
2. Implement GetNearbyEntitiesDescription() in AIAgentNPC
3. Create AgentMemory class with conversation history
4. Add AddUserCommand(), AddAgentResponse(), AddObservation() methods
5. Create PromptBuilder class for context assembly
6. Update BuildSystemPrompt() to include world context
7. Update BuildUserPrompt() to include memory
8. Add failure detection in action execution
9. Implement replanning logic (send failure to xAI, get new plan)
10. Add observation reporting after action completion
11. Implement ModelRouter heuristics (word count + keywords) to choose between fast vs reasoning models
12. Add config knobs for router thresholds, reasoning model/temperature
13. Update XAIClient to log routing decisions (model, reason) for debugging
14. Standardize context strings (tiles/resources/players) to include tile coords, pixel coords, Δtile/Δpx, direction, reachability

### File Structure
```
Content/
  Systems/
    AgentMemory.cs
    PromptBuilder.cs
    ModelRouter.cs
```

### Context Gathering
```
GetNearbyTilesDescription():
- Scan 5 tile radius around agent
- List tile types found (Main.tile[x,y].TileType)
- Return string: "Stone, Dirt, Copper Ore at (x,y)"

GetNearbyEntitiesDescription():
- Scan Main.npc array
- Find NPCs within 300 pixels
- Return string: "Green Slime at distance 120, Blue Slime at 250"
```

### Memory System
```
AgentMemory maintains:
- List<string> conversationHistory (max 10 entries)
- AddUserCommand(string) adds "USER: command"
- AddAgentResponse(string) adds "AGENT: response"
- AddObservation(string) adds "OBSERVATION: result"
- GetHistoryString() returns formatted history for prompt
```

### Success Criteria
- Agent includes nearby tiles in decisions
- Agent includes nearby entities in decisions
- Agent maintains conversation history
- Agent can replan when actions fail
- Model router selects reasoning model for long/complex prompts
- Context strings always contain actionable coordinate + delta data so LLM can move/mine precisely
- Complex commands work ("mine 10 dirt then build platform")

---

## PHASE 5: MULTI-AGENT COORDINATION

### Goal
Enable multiple agents to work together without conflicts.

### Tasks
1. Create BuildTask class (tracks tile list, claimed tiles, assigned agents)
2. Create AgentCoordinator ModSystem (server-side singleton)
3. Implement CreateBuildTask(List<Point> targetTiles)
4. Implement AssignTileToAgent(taskId, agentWhoAmI)
5. Implement IsTaskComplete(taskId)
6. Add assignedTaskId field to AIAgentNPC
7. Add AssignToTask(taskId) method to AIAgentNPC
8. Implement tile claiming before placement
9. Create /agentbuild command (creates task, assigns all agents)
10. Add multiplayer sync via ModPacket

### File Structure
```
Content/
  Systems/
    AgentCoordinator.cs
    BuildTask.cs
Common/
  Commands/
    MultiAgentBuildCommand.cs
```

### Coordination Flow
```
1. /agentbuild creates BuildTask with 30 target tiles
2. Find all active AIAgentNPC instances
3. Call agent.AssignToTask(taskId) for each
4. Agent requests tile from coordinator
5. Coordinator returns unclaimed tile, marks as claimed
6. Agent places block at tile
7. Agent requests next tile
8. Repeat until no unclaimed tiles remain
```

### BuildTask Structure
```
class BuildTask:
- int TaskId
- List<Point> TargetTiles (all tiles to build)
- HashSet<Point> ClaimedTiles (tiles being worked on)
- HashSet<int> AssignedAgents (agent whoAmI list)
- Point? GetNextUnclaimedTile() (returns null if all claimed)
- void ClaimTile(Point tile)
- bool IsComplete() (returns ClaimedTiles.Count >= TargetTiles.Count)
```

### Success Criteria
- Multiple agents build without block conflicts
- Work distributed evenly across agents
- No duplicate tile placements
- /agentbuild coordinates 3+ agents successfully
- Works in multiplayer (server authority)

---

## PHASE 6: POLISH & ERROR HANDLING

### Goal
Make mod stable and user-friendly.

### Tasks
1. Add try-catch blocks to all critical sections
2. Handle xAI API failures gracefully (timeout, invalid response, network error)
3. Add loading indicator in chat during xAI calls
4. Validate API key format in TerrarAI_Config
5. Add null checks for xAI responses
6. Implement request timeout (5 seconds)
7. Add fallback behavior when API unavailable
8. Create debug logging system (optional chat messages)
9. Add agent activity status UI (list all active agents)
10. Update description.txt with usage instructions
11. Test multiplayer sync thoroughly

### Error Handling Points
```
XAIClient.SendPromptAsync():
- Catch HttpRequestException
- Catch TaskCanceledException (timeout)
- Catch JsonException (invalid response)
- Return error message string

ActionParser.ParseActions():
- Catch JsonException
- Catch NullReferenceException
- Return empty queue on failure

AIAgentNPC.HandleExecutingState():
- Catch any action execution exception
- Transition to Idle state
- Display error to user
```

### Success Criteria
- No crashes from API failures
- Clear error messages displayed to users
- Stable FPS with 5+ active agents
- Multiplayer sync reliable
- Config validation prevents invalid API keys
- Agent continues working after recoverable errors

---

## PHASE 7: ADVANCED FEATURES (OPTIONAL)

### Goal
Extend capabilities beyond basic implementation.

### Tasks
1. Implement CombatAction (target NPCs, attack with projectiles)
2. Implement CraftAction (use crafting stations, check recipes)
3. Implement InventoryManager (track agent inventory items)
4. Add A* pathfinding for complex navigation
5. Implement PotionAction (consume buff potions)
6. Add TeamFormation system (coordinated squad tactics)
7. Add voice command support (speech-to-text API)
8. Create agent personality system (custom prompts per agent)
9. Implement boss fighting strategies
10. Add autonomous exploration mode

### File Structure
```
Content/
  Actions/
    CombatAction.cs
    CraftAction.cs
    PotionAction.cs
  Systems/
    PathfindingManager.cs
    InventoryManager.cs
    VoiceCommandSystem.cs
    PersonalitySystem.cs
```

### Success Criteria
- Agents fight enemies autonomously
- Agents craft needed tools
- Pathfinding works on complex terrain
- Voice commands functional
- Personality variants create different behaviors

---

## KEY TECHNICAL CONCEPTS

### Async in Game Loop
```
Problem: xAI calls take 500-2000ms, game runs at 60 FPS
Solution:
- Start Task without await: pendingRequest = CallXAIAsync()
- Each frame: check if (pendingRequest.IsCompleted)
- When complete: process result, transition state
- Never block AI() method with await
```

### Tile vs Pixel Coordinates
```
- World uses pixel coordinates (NPC.position, NPC.Center)
- Tiles use grid coordinates (Main.tile[x,y])
- Conversion: tileX = (int)(pixelX / 16)
- Example: pixel 1600 = tile 100
```

### Multiplayer Sync
```
- Server authority: server runs xAI calls
- Clients receive results via ModPacket
- Tile changes: NetMessage.SendData after WorldGen operations
- Check Main.netMode before sending packets
- Use NetmodeID.SinglePlayer, .Server, .MultiplayerClient
```

### State Machine Pattern
```
Each frame AI() checks currentState:
- Idle: Apply friction, wait for input
- Planning: Poll async request, don't block
- Executing: Run one action per frame
- Replanning: Request new plan from xAI
- Completed: Display success, return to Idle

State transitions happen in same frame, no delays
```

---

## FILE STRUCTURE SUMMARY

```
TerrarAI/
├── TerrarAI.cs                    # Main mod class
├── TerrarAI_Config.cs             # User settings (API key, model, timeouts, verbose logging)
├── Content/
│   ├── NPCs/
│   │   ├── AIAgentNPC.cs          # ✅ Main agent class (includes AgentState enum, all context methods)
│   │   └── AIAgentRenderer.cs     # ✅ Separated rendering logic for player appearance clones
│   ├── Actions/
│   │   ├── AgentAction.cs         # ✅ Abstract base with range validation (GetRequiredRange, GetTargetTile, GetTargetPosition)
│   │   ├── MoveAction.cs          # ✅ Navigation with obstacle jumping, precise stopping
│   │   ├── MineAction.cs          # ✅ Tool-based mining with stability checks, realistic speed (1-3s), visible animations
│   │   ├── PlaceBlockAction.cs    # ✅ Tile placement with better error messages
│   │   ├── SayAction.cs           # ✅ Chat messages
│   │   ├── CombatAction.cs        # ❌ Phase 7 (not implemented)
│   │   └── CraftAction.cs         # ❌ Phase 7 (not implemented)
│   └── Systems/
│       ├── XAIClient.cs           # ✅ xAI API client with streaming support
│       ├── ActionParser.cs        # ✅ JSON to Action parser
│       ├── ActionValidator.cs     # ✅ Input sanitization/clamping
│       ├── ToolSelector.cs        # ✅ NEW: Tool management, tile strength validation (15+ tile types)
│       ├── ServerAuthority.cs     # ✅ Server-side validation helpers
│       ├── AgentMemory.cs         # ❌ Phase 4 (not implemented - conversation history)
│       ├── PromptBuilder.cs       # ❌ Phase 4 (not implemented - context assembly)
│       ├── AgentCoordinator.cs    # ❌ Phase 5 (not implemented - multi-agent manager)
│       └── BuildTask.cs           # ❌ Phase 5 (not implemented - shared construction task)
├── Common/
│   └── Commands/
│       ├── SpawnAgentCommand.cs   # ✅ /create
│       ├── AgentCommand.cs        # ✅ /action
│       ├── TestAPICommand.cs      # ✅ /testxai
│       └── MultiAgentBuildCommand.cs  # ❌ Phase 5 (not implemented - /agentbuild)
└── Tests/
    └── TerrarAI.Tests/            # ❌ Not implemented (xUnit/NUnit project with mocks)
```

**Legend:**
- ✅ = Implemented and working
- ❌ = Not implemented (future work)

---

## TESTING STRATEGY

Beyond in-game QA, add a lightweight `Tests/TerrarAI.Tests` project (xUnit/NUnit) with mocks for `IXAIClient`, `ActionParser`, and Agent actions so regressions are caught before loading into Terraria. Run `dotnet test` as part of the normal workflow.

### Phase 1 Tests
- Run /testxai, verify response in chat
- Check config menu shows xAI settings
- Verify API key saves correctly
- Unit-test XAIClient error handling and config serialization with mocked HttpClient

### Phase 2 Tests
- Create action manually, call Execute()
- Test ActionParser with sample JSON
- Verify WorldGen.KillTile removes tiles
- Verify WorldGen.PlaceTile adds tiles
- Add parser validation tests covering out-of-bounds coordinates and unsupported block types

### Phase 3 Tests
- Create agent with /create
- Send command via /action
- Verify state transitions (watch state display)
- Test simple commands: "say hello", "move to 1000 500"
- Verify agent renders as player character clone
- Check planning timeout notifications appear in chat
- Test verbose logging toggle

### Phase 4 Tests
- Command "describe surroundings", verify context used
- Send impossible command, verify replanning
- Test memory with follow-up commands
- Command "mine 10 dirt then say done"

### Phase 5 Tests
- Spawn 3 agents
- Run /agentbuild
- Verify parallel construction
- Check no duplicate placements
- Test in multiplayer server

### Phase 6 Tests
- Disconnect internet mid-command
- Enter invalid API key
- Spawn 10 agents, check FPS
- Send malformed command
- Test all error paths

---

## COMMON PITFALLS

1. **Blocking Game Thread**: Never await in AI(), use polling pattern
2. **Missing NetMessage**: Tile changes need SendData in multiplayer
3. **Tile Coordinate Confusion**: Always divide pixel pos by 16
4. **Null xAI Response**: Check for null before parsing JSON
5. **No Error Handling**: Wrap API calls in try-catch
6. **Infinite Action Loops**: Actions must eventually return true
7. **Memory Leaks**: Clear actionQueue when agent dies/despawns
8. **Missing Main.netMode Checks**: Different logic for single/multi
9. **Race Conditions**: Use server authority for coordination
10. **Skipping Validation**: Always run JSON through ActionValidator before queueing

---

## DEVELOPMENT WORKFLOW

1. Create files for current phase
2. Build mod: Workshop → Develop Mods → Build + Reload
3. Test features in-game immediately
4. Check logs: Documents/My Games/Terraria/tModLoader/Logs/
5. Fix errors before moving to next phase

---

## SUCCESS METRICS

**MVP (Phases 1-4)** ✅ COMPLETE
- Agent spawns and responds to natural language via chat commands
- xAI integration reliable with timeout handling and verbose logging
- Basic actions execute correctly (move, mine, place, say)
- Agents render as full player character clones with all animations
- Single-agent tasks complete successfully
- Planning visibility with chat notifications and configurable timeouts

**Complete (Phases 5-6)**
- Multi-agent coordination works
- Multiplayer stable
- Error handling robust
- Performance acceptable (60 FPS with 5+ agents)
- Advanced context awareness and replanning

**Advanced (Phase 7)**
- Combat, crafting, pathfinding functional
- Agent personality system implemented
- Complex multi-agent strategies execute
- Autonomous exploration mode
