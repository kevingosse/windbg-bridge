# windbg-bridge

**Let your AI coding agent debug alongside you in WinDbg.**

windbg-bridge connects a live WinDbg session to AI agents like Claude Code or Codex through a named pipe. The agent can run debugger commands, read your command history, and watch output in real time. Everything it does shows up in the WinDbg UI, so you always see exactly what's happening. 

```text
┌─────────────┐    named pipe    ┌────────────────────┐
│   WinDbg    │◄────────────────►│  windbg-bridge.exe │◄──── your AI agent
│ + extension │                  │       (CLI)        │
└─────────────┘                  └────────────────────┘
```

Two pieces make this work:

- **A WinDbg extension** that runs inside WinDbg and exposes the session over a named pipe.
- **A CLI** that agents call to talk to that pipe.

A ready-made [`SKILL.md`](SKILL.md) teaches your agent the whole workflow.

## Installation

1. Grab the latest zip from [Releases](https://github.com/kevingosse/windbg-bridge/releases) and extract it.
2. Run `install.ps1`.

The script installs the extension into `%LOCALAPPDATA%\DBG\UIExtensions` (where Store-installed WinDbg picks it up automatically), puts the CLI next to it, and offers to install the agent skill for Claude Code (`~/.claude/skills/`) and/or other assistants (`~/.agents/skills/`).

## Quick start

**Option A: the agent launches WinDbg for you.** Just ask your agent to debug something:

> "Launch notepad.exe under WinDbg and figure out why it's hanging."

With the skill installed, the agent runs `windbg-bridge.exe launch`, which finds your Store-installed WinDbg, starts it with the bridge enabled, and returns the pipe name. It can forward any WinDbg arguments too:

```powershell
windbg-bridge.exe launch -- -z C:\dumps\app.dmp
```

**Option B: start the bridge from a WinDbg session you already have open.** Click the bridge button in the ribbon, or run:

```text
!startbridge
```

Then give your agent the pipe name shown in the bridge panel: *"Connect to my WinDbg on windbg-bridge-123 and look at this access violation."*

## Talking to your agent from WinDbg

Once the agent is connected and listening, you don't need to switch windows to steer it. Queue your next question straight from the WinDbg command line:

```text
!ask why is thread 12 deadlocked
```

The agent receives the prompt, has full context from your command history, and prints its answer right back into the WinDbg console.

## What the agent can do

The CLI exposes a small, deliberate set of operations:

| Command | What it does |
|---------|-------------|
| `status` | Check bridge health and debugger state (running / broken in / no target) |
| `execute` | Run one WinDbg command, visible in the UI like typed input |
| `history` | List recent commands (yours and the agent's) without their output |
| `output` | Fetch the output of a specific history entry, size-capped |
| `console` | Read the full console transcript, including output printed while the target runs |
| `break` | Force a running target to break in |
| `wait` | Block until the target breaks in (breakpoint, exception, exit) |
| `listen` | Block until you queue a prompt with `ask` |
| `reply` | Print an answer into the WinDbg console |

The bridge stays usable **while the target is running**: the agent can set auto-continue breakpoints, resume with `g`, tail the trace through `console --since`, and break back in when it has seen enough, all autonomously.

## Building from source

The WinDbg interface DLLs are vendored in [`lib/`](lib), so all you need is the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
.\build.ps1           # build extension + CLI into artifacts/
.\build.ps1 -Deploy   # build and install into %LOCALAPPDATA%\DBG\UIExtensions
```