# TerrarAI

### _Your Terraria world, now with agents that actually think._

https://github.com/user-attachments/assets/67f0fc44-abfc-4884-83d4-9293c090899d

[![HackPrinceton 2025](https://img.shields.io/badge/HackPrinceton-1st%20Place-gold?style=flat-square)](https://www.hackprinceton.com/)
[![xAI Track](https://img.shields.io/badge/xAI-Track%20Winner-000000?style=flat-square)](https://x.ai)
[![tModLoader](https://img.shields.io/badge/tModLoader-mod-E63946?style=flat-square)](https://tmodloader.net)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![xAI Grok](https://img.shields.io/badge/Powered%20by-Grok%204-0A0A0A?style=flat-square)](https://x.ai)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](LICENSE)

> **Spawn intelligent NPCs in Terraria. Tell them what to do in plain English.**
> They perceive the world, plan, act, fail, and replan — autonomously.

---

## 🗺 What It Does

| Step                         | What happens                                                                                                             |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| **1. Spawn agents**          | `/ai spawn 5 agents` — a router LLM parses your intent and spawns friendly NPCs next to your character                   |
| **2. Give a command**        | `/ai two agents mine copper ore` — the router extracts the task, the count, and picks the N closest agents               |
| **3. Agents perceive**       | Each agent scans nearby tiles, resources, and inventory, then packages it as world context for the LLM                   |
| **4. xAI plans in ReAct**    | Grok returns `{observation, thought, action}` JSON — one step at a time, with full conversation history                  |
| **5. Actions execute**       | Plans are parsed into typed actions (move, mine, chop, build, hellevator, say) that tick frame-by-frame                  |
| **6. Replan on failure**     | Unreachable target? Wrong pickaxe? Agent stagnated? The agent rebuilds its plan with the failure reason — up to 25 times |
| **7. Coordinate and finish** | A shared claim map stops agents from fighting over the same tree or hellevator shaft until the task completes            |

---

## ⚡ Quick Start

**Prerequisites:** [tModLoader](https://tmodloader.net) (via Steam) · an [xAI API key](https://console.x.ai)

```bash
# 1. Clone into your tModLoader mod sources folder
#    (usually ~/Documents/My Games/Terraria/tModLoader/ModSources)
git clone <repo-url> TerrarAI

# 2. Launch tModLoader → Workshop → Develop Mods → Build + Reload

# 3. Enable the mod: Workshop → Manage Mods → enable "TerrarAI"

# 4. Configure your key: Workshop → Manage Mods → Mod Configuration → TerrarAI
#    Paste your xAI API key into the ApiKey field
```

Then hop into a world and run `/testxai` to confirm the API is wired up. If you see `xAI replied: ...`, you're good.

### Minimum config

| Field                   | Default                       | Notes                                          |
| ----------------------- | ----------------------------- | ---------------------------------------------- |
| `ApiKey`                | _empty_ (reads `$XAI_TOKEN`)  | **Required** — Grok API token                  |
| `Model`                 | `grok-4-fast-non-reasoning`   | Fast model for simple tasks                    |
| `ReasoningModel`        | `grok-4-fast-reasoning`       | Used when the router detects complex tasks    |
| `BaseEndpoint`          | `https://api.x.ai/v1/chat/completions` | OpenAI-compatible endpoint            |
| `MaxPlanningSeconds`    | `90`                          | Kills stuck planning calls                     |
| `EnableVerboseLogging`  | `false`                       | Logs full prompts/responses to `client.log`   |

---

## 🎮 Example Commands

```bash
# Spawning
/ai spawn an agent
/ai spawn 5 agents
/ai create 10 agents

# Commanding
/ai gather some wood
/ai build a 10 block tall tower
/ai dig down
/ai mine copper ore

# Selection (N closest agents)
/ai two agents chop trees
/ai one agent mine iron
/ai all agents dig down        # default — broadcasts to everyone

# Recall + dismiss
/ai recall
/ai come back
/ai dismiss all agents
```

The router is LLM-based, not keyword-matched — it extracts `{action, command, agentCount}` from free-form English and routes accordingly.

---

## 🏗 Architecture

```
  ┌─────────────────────────────────────────────────────────────┐
  │                        PLAYER CHAT                          │
  │                  /ai two agents mine copper                 │
  └─────────────────────────┬───────────────────────────────────┘
                            │
                            ▼
  ┌─────────────────── ChatCoordinator ─────────────────────────┐
  │   Router LLM classifies intent →                            │
  │   {action: "command", command: "mine copper", agentCount: 2}│
  │   Picks the 2 closest agents, fans the task out             │
  └─────────────────────────┬───────────────────────────────────┘
                            │
                            ▼
  ┌────────────────────── AIAgentNPC ───────────────────────────┐
  │   Idle  →  Planning  →  Executing  →  Replanning  →  Done   │
  │                                                             │
  │  ┌───────────┐   ┌──────────────┐   ┌─────────────────┐     │
  │  │WorldContext│─▶│  XAIClient   │─▶│  ActionParser    │     │
  │  │  (tiles,  │   │   (async     │   │ (ReAct JSON →   │     │
  │  │ resources,│   │  chat call)  │   │  Action queue)  │     │
  │  │ inventory)│   └──────────────┘   └─────────────────┘     │
  │  └───────────┘                             │                │
  │                                            ▼                │
  │                                 ┌─────────────────────┐     │
  │                                 │   Action.Tick()     │     │
  │                                 │ Move · Mine · Chop  │     │
  │                                 │ Build · Hellevator  │     │
  │                                 │   Say · Complete    │     │
  │                                 └─────────────────────┘     │
  └─────────────────────────────────────────────────────────────┘
                            │
                            ▼
  ┌──────────────────── MultiAgentCoordinator ──────────────────┐
  │   Claims on trees + hellevator shafts keep agents off each  │
  │   other's work. Released on finish, recall, or death.       │
  └─────────────────────────────────────────────────────────────┘
```

---

## 🧠 How an Agent Thinks

```
   ┌─────────┐  new command    ┌──────────┐  plan ready   ┌──────────┐
   │  IDLE   │────────────────▶│ PLANNING │──────────────▶│EXECUTING │
   │follow   │                 │ xAI call │               │tick each │
   │player AI│◀──── done ──────│  pending │               │  action  │
   └─────────┘                 └──────────┘               └────┬─────┘
        ▲                           ▲                          │
        │                           │ < 25 cycles              │ action
        │ reached 25                │                          │ failed
        │ replans                   │                          ▼
   ┌────┴────┐  ◀──────────── ┌───────────┐ ◀──────────  ┌──────────┐
   │COMPLETED│                │REPLANNING │              │  failure │
   │  gives  │                │ rebuild w/│              │ detected │
   │   up    │                │  context  │              │(stagnate,│
   └─────────┘                └───────────┘              │ timeout) │
                                                         └──────────┘
```

Each Grok response is a single ReAct step — think once, act once, then loop:

```json
{
  "observation": "I see copper ore clusters at (120,80) and (125,82). My pickaxe power is 50%.",
  "thought": "Copper needs 35% power — my pickaxe works. I'll target the closest cluster.",
  "action": {
    "type": "mine",
    "params": { "tileX": 120, "tileY": 80 }
  },
  "complete": false
}
```

The last 20 `{role, content}` pairs are kept in the conversation history, so when an action fails the next planning call already knows _why_.

---

## 🔬 Design Foundations

### ReAct: Synergizing Reasoning and Acting in Language Models

> Yao, S. et al. _ReAct: Synergizing Reasoning and Acting in Language Models._
> ICLR 2023 — [arXiv:2210.03629](https://arxiv.org/abs/2210.03629)

Each agent interleaves **observation → thought → action** per step instead of planning the whole task up front. This lets Grok reason about the current state of the world (where the ore is, what tool the agent holds, what the last action did) before committing to the next move — crucial in a sandbox game where nothing about the terrain is known ahead of time.

### Generative Agents: Interactive Simulacra of Human Behavior

> Park, J. S. et al. _Generative Agents: Interactive Simulacra of Human Behavior._
> UIST 2023 — [arXiv:2304.03442](https://arxiv.org/abs/2304.03442)

The replan-on-failure cognitive loop — perceive, reflect on what went wrong, update the plan — is directly inspired by the generative-agents memory/reflection cycle, trimmed down for real-time gameplay constraints.

---

## 🛠 Tech Stack

| Layer          | Technology                                                              |
| -------------- | ----------------------------------------------------------------------- |
| **Runtime**    | tModLoader · XNA / FNA (Terraria rendering)                             |
| **Language**   | C# on .NET 8 (nullable refs, latest LangVersion)                        |
| **LLM**        | xAI Grok 4 Fast — non-reasoning + reasoning variants, dynamically routed |
| **API**        | OpenAI-compatible chat completions (`api.x.ai/v1/chat/completions`)     |
| **HTTP**       | `System.Net.Http.HttpClient`                                            |
| **JSON**       | `System.Text.Json`                                                      |
| **Deps**       | Zero external NuGet packages — Terraria + tModLoader APIs only          |

---

## 🗂 Project Structure

```
terrar.ai/
├── TerrarAI.cs                         # Mod entry point, bootstraps XAIClient
├── TerrarAI_Config.cs                  # ModConfig — API key, models, timeouts
│
├── Common/Commands/
│   ├── AgentCommand.cs                 # /ai chat command entry
│   └── TestAPICommand.cs               # /testxai connectivity check
│
├── Content/
│   ├── NPCs/
│   │   ├── AIAgentNPC.cs               # The agent — 5-state machine, planning loop
│   │   └── AIAgentRenderer.cs          # Status-text + sprite rendering
│   │
│   ├── Actions/
│   │   ├── AgentAction.cs              # Abstract base: OnEnter → Tick → OnExit
│   │   ├── ActionRegistry.cs           # JSON type → Action factory
│   │   ├── MoveAction.cs               # Navigate to a tile via MovementHelper
│   │   ├── MineAction.cs               # 3-phase mining with cluster expansion
│   │   ├── ChopAction.cs               # Tree felling with axe-power gate + claim
│   │   ├── BuildAction.cs              # Directional block placement
│   │   ├── HellevatorAction.cs         # Shared 2-wide vertical shaft digger
│   │   ├── SayAction.cs                # Chat output
│   │   ├── CompleteAction.cs           # Task-done marker
│   │   └── FailureAction.cs            # Task-failed marker
│   │
│   └── Systems/
│       ├── XAIClient.cs                # HTTP wrapper for xAI chat completions
│       ├── ChatCoordinator.cs          # LLM intent router (spawn / command / recall)
│       ├── ActionParser.cs             # ReAct JSON → AgentAction queue
│       ├── ActionValidator.cs          # Coordinate clamping, safety checks
│       ├── ModelRouter.cs              # Fast vs reasoning model selector
│       ├── WorldContext.cs             # Builds scene description for prompts
│       ├── ToolSelector.cs             # Ore → required pickaxe power map
│       ├── MovementHelper.cs           # Physics + jump/walk logic
│       ├── TreeHelper.cs               # Tree base detection
│       ├── MultiAgentCoordinator.cs    # Tree + hellevator claim registry
│       └── ServerAuthority.cs          # Netmode guards for multiplayer
│
├── build.txt                           # tModLoader mod metadata
├── description.txt                     # Mod description
└── icon.png                            # Workshop icon
```

---

## ⚙️ Key Features

- **LLM-based command router** — no keyword matching; Grok parses `/ai <free-form English>` and returns structured spawn/command/recall intents
- **ReAct planning loop** — observation + thought + action per step, with 20-turn conversation history preserved across replans
- **Multi-agent coordination** — tree + hellevator claim map prevents agents from tripping over each other
- **Smart model routing** — complexity score (word count + multi-step signals + custom keywords) picks fast vs reasoning model
- **Stagnation detection** — if an agent hasn't moved ≥4px in 5 seconds mid-action, it fails out and replans
- **Tool-power validation** — agents refuse to mine ore their pickaxe can't break (35% for copper → 200% for chlorophyte)
- **Replan ceiling** — up to 25 replan cycles per task before the agent gives up, preventing infinite loops
- **Server-authoritative** — all AI calls and world mutations run on the server; multiplayer-safe
- **Verbose logging mode** — dumps full prompts, responses, and state transitions to `client.log` for debugging
- **Creative mode toggle** — unlimited blocks + best-in-slot tools for testing builds

---

## 🧩 Full Configuration

Open **Workshop → Manage Mods → Mod Configuration → TerrarAI**:

| Group         | Field                    | Default                       | Range / Notes                              |
| ------------- | ------------------------ | ----------------------------- | ------------------------------------------ |
| **xAI**       | `ApiKey`                 | _empty_                       | Reads `$XAI_TOKEN` env var as fallback     |
|               | `Model`                  | `grok-4-fast-non-reasoning`   | Fast path                                  |
|               | `ReasoningModel`         | `grok-4-fast-reasoning`       | Complex-task path                          |
|               | `Temperature`            | `0.7`                         | 0.0 – 2.0                                  |
|               | `ReasoningTemperature`   | `0.4`                         | 0.0 – 2.0 — lower for reasoning model      |
|               | `BaseEndpoint`           | `https://api.x.ai/v1/chat/completions` |                                   |
| **Router**    | `EnableModelRouter`      | `false`                       | Auto-switch fast ↔ reasoning               |
|               | `RouterWordThreshold`    | `150`                         | Word count threshold                       |
|               | `RouterComplexKeywords`  | `build,structure,...`         | Comma-separated bonus keywords             |
| **Gameplay**  | `EnableCreativeMode`     | `false`                       | Unlimited blocks + top-tier tools          |
|               | `ShowAgentThoughts`      | `false`                       | Surface thought/observation in chat        |
| **Debug**     | `EnableVerboseLogging`   | `false`                       | Full prompt/response logs                  |
| **Timeouts**  | `RequestTimeoutSeconds`  | `60`                          | 10 – 300                                   |
|               | `MaxPlanningSeconds`     | `90`                          | 30 – 300                                   |

---

## 🩺 Troubleshooting

**Test your key first:** `/testxai` — expect `xAI replied: ...`. Anything else points at the fix:

- `Set an xAI API key in Mod Configuration first` → paste your key in the config
- `xAI request failed: 401` → key is wrong or expired
- Agent stuck in `Planning` → flip on `EnableVerboseLogging`, then tail `client.log`:
  - macOS/Linux: `~/.local/share/Terraria/tModLoader/Logs/client.log`
  - Windows: `%USERPROFILE%\Documents\My Games\Terraria\tModLoader\Logs\client.log`
  - Look for `[XAIClient]` entries — they show the exact request and response
- Agent keeps replanning forever → it'll hard-stop at 25 cycles and return to idle; usually means the target is unreachable (cave-walled, out-of-range, wrong tool tier)

---

_🏆 1st Place · xAI Track · HackPrinceton_
