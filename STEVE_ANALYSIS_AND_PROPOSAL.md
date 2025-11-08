# Steve Architecture Analysis & TerrarAI Improvement Proposal

## Key Differences: Steve vs TerrarAI

### 1. **Context Provision Strategy**

#### Steve (Simple & Aggregate):
```java
// WorldKnowledge.java - Lines 80-98
"Nearby Blocks: stone, dirt, oak_planks, iron_ore, cobblestone"
"Nearby Entities: 2 zombie, 1 creeper"
"Biome: plains"
```

**Approach:** Aggregate summaries, no coordinates, no distances
- Scans 16-block radius
- Groups by type, shows top 5
- ~5 lines of context total

#### TerrarAI (Detailed & Verbose):
```csharp
// AIAgentNPC.cs - Lines 1306-1400
"Trees: tile(234,567) pixels(3752,9080) Δtile(5,2) Δpx(80,32) dir[right,down] [144px]; 
        tile(230,567) pixels(3688,9080) Δtile(1,2) Δpx(16,32) dir[right,down] [REACHABLE];"
// ... 30+ more lines of detailed resources
```

**Approach:** Every resource with absolute coords, relative coords, direction, distance, reachability
- Scans 50-101 tile radius (much larger)
- Shows top 10 resource types × 3 instances each = 30+ entries
- ~100+ lines of context

**Impact:**
- ✅ Steve: Faster inference (less tokens)
- ✅ Steve: Clearer signal-to-noise
- ❌ TerrarAI: Token-heavy, slower
- ❌ TerrarAI: AI must manually parse coordinates

---

### 2. **Action Responsibility**

#### Steve (Smart Actions):
```java
// AI says: {"action": "mine", "parameters": {"block": "iron", "quantity": 8}}
// MineBlockAction INTERNALLY:
- Finds iron ore (scans world)
- Pathfinds to depth Y=64 (knows ore spawns)
- Searches in tunnel while mining forward
- Equips pickaxe automatically
- Mines until quantity reached
```

**Philosophy:** Actions are autonomous agents themselves

#### TerrarAI (Dumb Actions):
```csharp
// AI must say:
1. {"type": "move", "params": {"x": 3752, "y": 9080}}  // AI calculates this
2. {"type": "mine", "params": {"tileX": 234, "tileY": 567}}  // After arriving

// MineAction.cs:
- Assumes agent is already in range
- Just mines the specific tile
- Fails if not in range
```

**Philosophy:** AI does all the planning, actions just execute

**Impact:**
- ✅ Steve: One action = complete task
- ✅ Steve: Actions handle failure/retries internally
- ❌ TerrarAI: Two+ actions needed
- ❌ TerrarAI: AI must coordinate movement + action

---

### 3. **Pathfinding Integration**

#### Steve:
```java
// PathfindAction.java - Line 26
steve.getNavigation().moveTo(x, y, z, 1.0);
// Uses Minecraft's built-in pathfinding
// OR: BaritoneInterface.java (advanced pathfinding library)
```

#### TerrarAI:
```csharp
// MoveAction.cs - Lines 108-117
// Manual velocity control + jumping
float desiredVelocityX = MathHelper.Clamp(delta.X / 10f, -speed, speed);
npc.velocity.X = MathHelper.Lerp(npc.velocity.X, desiredVelocityX, 0.35f);
if (stuck || climbing) TryJump(...);
```

**Impact:**
- ✅ Steve: Real pathfinding (goes around obstacles)
- ❌ TerrarAI: Simple physics (gets stuck easily)

---

### 4. **Prompt Complexity**

#### Steve (40 lines system + 10 lines user):
```java
// PromptBuilder.java
System: "You are Minecraft AI. Respond ONLY with JSON. ACTIONS: attack, build, mine, follow, pathfind"
User: "Position: [100, 64, 200], Nearby: stone dirt iron, Biome: plains, Command: 'get iron'"
```

#### TerrarAI (200+ lines):
```csharp
// BuildSystemPrompt() - Lines 935-1030
- Coordinate system explanation (15 lines)
- Available tools with power levels (20 lines)
- Available actions with examples (30 lines)
- Current state (10 lines)
- Nearby resources with full coords (50 lines)
- Nearby tiles with full coords (50 lines)
- Important rules (20 lines)
- Concrete examples (30 lines)
```

**Impact:**
- ✅ Steve: Fast inference, cheap API costs
- ❌ TerrarAI: Slow inference, expensive

---

## Proposed Improvements for TerrarAI

### **Priority 1: Smart Actions (Auto-Movement)** ⭐⭐⭐

Make actions handle their own setup:

```csharp
// New: MineAction with auto-movement
public class MineAction : AgentAction
{
    private enum Phase { MovingToTarget, Mining, Complete }
    private Phase _phase = Phase.MovingToTarget;
    private MoveAction? _moveAction;
    
    protected override AgentActionResult OnTick(AgentActionContext context)
    {
        switch (_phase)
        {
            case Phase.MovingToTarget:
                // Check if in range
                if (IsInRange())
                {
                    _phase = Phase.Mining;
                    return AgentActionResult.Pending("In range, starting mining...");
                }
                
                // Auto-create move action if needed
                if (_moveAction == null)
                {
                    _moveAction = new MoveAction(GetTargetPosition());
                }
                
                var moveResult = _moveAction.Execute(context);
                if (moveResult.Status == AgentActionStatus.Success)
                {
                    _phase = Phase.Mining;
                }
                else if (moveResult.Status == AgentActionStatus.Failure)
                {
                    return AgentActionResult.Failure($"Could not reach target: {moveResult.Message}");
                }
                return moveResult;
                
            case Phase.Mining:
                // Existing mining logic
                return DoMining(context);
        }
    }
}
```

**Benefits:**
- AI says: `{"type": "mine", "params": {"tileX": 234, "tileY": 567}}`
- Action automatically moves then mines
- One action instead of two
- Internal retry/fallback logic

---

### **Priority 2: Simplified Context** ⭐⭐⭐

Create lightweight WorldKnowledge system:

```csharp
// New: WorldContext.cs
public class WorldContext
{
    private const int SCAN_RADIUS = 25; // tiles (400px)
    
    public string GetContextSummary(NPC agent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== YOUR SITUATION ===");
        sb.AppendLine($"Position: tile({tileX},{tileY})");
        sb.AppendLine($"Nearby Resources: {GetResourceSummary()}");  // "3 trees, 2 copper ore, 1 chest"
        sb.AppendLine($"Nearby Blocks: {GetBlockSummary()}");        // "dirt, stone, wood"
        sb.AppendLine($"Nearby Players: {GetPlayerNames()}");        // "Alice, Bob"
        return sb.ToString();
    }
    
    private string GetResourceSummary()
    {
        // Scan and group by type
        var resources = ScanResources(SCAN_RADIUS);
        var grouped = resources.GroupBy(r => r.Type)
                               .OrderBy(g => g.Min(r => r.Distance))
                               .Take(5);
        
        return string.Join(", ", grouped.Select(g => $"{g.Count()} {g.Key}"));
        // Output: "5 trees, 3 copper_ore, 2 iron_ore"
    }
}
```

**Changes to prompt:**
```
=== YOUR SITUATION ===
Position: tile(100,50)
Nearby Resources: 5 trees, 3 copper_ore, 2 iron_ore
Nearby Blocks: dirt, stone, wood_platform
Nearby Players: Alice

=== COMMAND ===
"chop nearest tree"

=== RESPONSE ===
```

**Benefits:**
- ~90% reduction in context size (200 lines → 20 lines)
- Faster inference
- Cheaper API costs
- AI focuses on intent, not coordinate math

---

### **Priority 3: Natural Language Action Parameters** ⭐⭐

Add smart parameter parsing:

```csharp
// New: ActionRegistry.cs enhancement
public static AgentAction Create(string type, JsonElement params, ActionValidator validator, WorldContext world)
{
    switch (type)
    {
        case "mine":
            // Support both explicit coords AND natural language
            if (params.TryGetProperty("target", out var target) && target.GetString() == "nearest_tree")
            {
                var nearestTree = world.FindNearest("tree", agent.Position);
                return new MineAction(nearestTree.Position);
            }
            // Existing tile coordinate parsing
            var tileX = ReadInt(params, "tileX");
            var tileY = ReadInt(params, "tileY");
            return new MineAction(new Point(tileX, tileY));
    }
}
```

**AI can now say:**
```json
{"type": "mine", "params": {"target": "nearest_tree"}}
```

Instead of:
```json
{"type": "move", "params": {"x": 3752, "y": 9080}},
{"type": "mine", "params": {"tileX": 234, "tileY": 567}}
```

---

### **Priority 4: Action Lifecycle with Internal State** ⭐

Adopt Steve's BaseAction pattern:

```csharp
// New: Enhanced AgentAction base class
public abstract class AgentAction
{
    protected bool Started { get; private set; }
    protected bool Cancelled { get; private set; }
    
    public AgentActionResult Execute(AgentActionContext context)
    {
        if (!Started)
        {
            Started = true;
            OnStart(context);
        }
        
        if (Cancelled)
        {
            return AgentActionResult.Failure("Cancelled");
        }
        
        return OnTick(context);
    }
    
    public void Cancel()
    {
        Cancelled = true;
        OnCancel();
    }
    
    protected abstract void OnStart(AgentActionContext context);
    protected abstract AgentActionResult OnTick(AgentActionContext context);
    protected virtual void OnCancel() { }
}
```

**Benefits:**
- Clear initialization vs execution phases
- Proper cancellation support
- Internal state management
- Matches Steve's proven pattern

---

## Implementation Plan

### **Phase 1: Smart Actions** (Week 1)
1. Add `Phase` enum to MineAction and PlaceBlockAction
2. Integrate auto-movement into both actions
3. Test: "mine tree at (234, 567)" works without separate move command

### **Phase 2: Simplified Context** (Week 1)
1. Create WorldContext.cs with aggregate scanning
2. Replace verbose DescribeNearbyResources with GetResourceSummary
3. Update BuildSystemPrompt to use simplified format
4. Measure token reduction and inference speedup

### **Phase 3: Natural Language Parameters** (Week 2)
1. Add "nearest_X" parameter support to ActionParser
2. Create FindNearest(type, position) helper in WorldContext
3. Update prompt to show natural language examples
4. Test: "mine nearest tree" works

### **Phase 4: Enhanced Base Class** (Week 2)
1. Refactor AgentAction with OnStart/OnTick/OnCancel
2. Migrate existing actions to new pattern
3. Add proper cancellation support

---

## Expected Benefits

### Performance:
- ⚡ **90% reduction in context size** (200 lines → 20 lines)
- ⚡ **50% faster inference** (less tokens to process)
- ⚡ **60% lower API costs** (smaller prompts)

### Reliability:
- ✅ **Actions succeed more often** (auto-movement)
- ✅ **Simpler prompts = better AI understanding**
- ✅ **Fewer multi-step failures** (atomic actions)

### User Experience:
- 😊 **Natural commands work** ("chop nearest tree")
- 😊 **Faster responses** (less planning needed)
- 😊 **More predictable behavior** (less AI guesswork)

---

## Key Architectural Shift

**Before (TerrarAI Current):**
```
AI = Smart Planner (does everything)
Actions = Dumb Executors (just follow orders)
```

**After (Steve-Inspired):**
```
AI = Intent Recognizer (high-level goals)
Actions = Smart Executors (figure out HOW)
```

This matches the **"Tell, Don't Ask"** principle: Tell actions what to achieve, don't micromanage how they do it.

