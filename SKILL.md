---
name: windbg-bridge
description: "Connect to a live WinDbg session via the named-pipe bridge. Use the pipe name provided by the user, or use `windbg-bridge.exe launch -- [WinDbg args]` to locate Store-installed WinDbg, launch it, start the bridge, and return the pipe name."
argument-hint: '[pipe name] [debugging goal]'
license: MIT
---

# WinDbg Bridge

This skill connects you to a live WinDbg debugging session through a named-pipe bridge. The bridge can be started manually from the WinDbg tool window or automatically by using the bridge CLI to launch WinDbg for you.

With the bridge active, you can:
- **Execute WinDbg commands** — they appear in the WinDbg UI so the user sees what you're doing
- **Read the user's command history** — see what the user ran manually, including output
- **Wait for user prompts** — block on `listen` until the user queues the next prompt from WinDbg
- **Collaborate in real time** — you and the user share the same debugging session

## Bridge workflow

1. Make sure the WinDbg extension is installed.
2. If WinDbg is not already running with the bridge enabled, launch it with the bridge CLI:
   - `%LOCALAPPDATA%\DBG\UIExtensions\windbg-bridge\windbg-bridge.exe launch [-- <optional WinDbg args>]`
3. Obtain the pipe name from the WinDbg bridge panel, or reuse the `pipeName` returned by `launch`.
4. Use the CLI to talk to the pipe:
   - `%LOCALAPPDATA%\DBG\UIExtensions\windbg-bridge\windbg-bridge.exe`
5. Start with `status` to confirm the bridge is running and to see the debugger state (`session.runningState` tells you whether the target is running, broken in, or absent).
6. Launch an instance _in the background_ with the `listen` option. It will block until the WinDbg user queues a prompt with `ask ...` or `!ask ...`. It allows the user to communicate with you directly from Windbg.
7. Use `history` for lightweight command discovery.
8. Use `output` to retrieve the captured output for a specific history id, optionally capped with `--max-chars`.
9. Use `execute` to send exactly one WinDbg command per CLI invocation.
10. When the target is running (after `g`), use `wait` to block until it breaks in, `break` to force a break-in, and `console` to read everything printed while it ran.
11. The CLI waits indefinitely unless you pass `--timeout <seconds>`.

### Launch WinDbg and auto-enable the bridge

Use the bridge CLI when you want the agent to launch WinDbg on its own. It resolves the Store-installed WinDbg location, injects `bridgestart <pipe-name>`, waits for the bridge to come up, and prints JSON with `pipeName`, `pipePath`, `processId`, and `winDbgPath`.

```powershell
windbg-bridge.exe launch
```

Launch with your own deterministic pipe name:

```powershell
windbg-bridge.exe launch --pipe windbg-bridge-demo
```

Launch and forward extra WinDbg arguments after `--`:

```powershell
windbg-bridge.exe launch -- -z C:\dumps\app.dmp
```

Use the bare pipe name, for example `windbg-bridge-demo`. Use only letters, digits, `.`, `_`, and `-`. If you pass a WinDbg `-c` argument, the CLI prepends `bridgestart <pipe-name>` to it automatically.

Inside WinDbg, the user can also manage the bridge directly:

```text
!startbridge
!startbridge windbg-bridge-demo
!stopbridge
```

## Commands

### Status

Use this first to confirm the bridge is live. It never touches the engine, so it is always safe to call — even while the target is running.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 status
```

The response includes a `session` object describing the debugger state:

- `runningState` — `NoTarget`, `Running`, or `Stopped`. Check this before `execute`: commands can only be delivered while `Stopped`.
- `targetType` — e.g. `LiveUser`, `DumpUser`, `NoTarget`.
- `connectionState` — e.g. `NoSession`, `LocalDebugging`.
- `prompt` — the current WinDbg prompt (e.g. `0:001>`) when the target is broken in; tells you the current process/thread.

It also reports `consoleLength`, the total number of characters captured in the console transcript (see `console` below).

### History

`history` returns lightweight metadata only:

- `id`
- `source`
- `command`
- `thread` when available

It intentionally does **not** include full command output.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 history --count 20
```

### Output

Use `output` when you need the text for a specific history entry. Prefer `--max-chars` unless you truly need the full output.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 output --id 42 --max-chars 4000
```

To keep the **last** N characters instead of the first (useful for commands like `!dumpheap -stat` where the summary is at the end):

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 output --id 42 --max-chars 4000 --tail
```

### Execute

`execute` sends one WinDbg command. Agent-triggered commands are submitted as typed commands so they should appear in WinDbg like normal user input.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 execute "!clrstack"
```

Use `--max-chars` to cap the streamed output. Add `--tail` to keep the last N characters instead of the first (useful for verbose commands like `!dumpheap -stat` where the summary is at the end):

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 execute --max-chars 4000 --tail "!dumpheap -stat"
```

`execute` is rejected with a clear error while the target is running — commands cannot be delivered to a running debuggee. Use `break` or `wait` first, then retry.

### Break

Forces a running target to break in, like clicking **Break** in the WinDbg UI. Returns the new debugger state. Safe to call when the target is already broken in (no-op).

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 break
```

Use `--seconds <n>` to change how long to wait for the break-in to take effect (default 10).

### Wait

Blocks until the target breaks in (a breakpoint without `gc`, an exception, process exit, or a `break` from another connection). Returns the debugger state; check `session.runningState` — `Stopped` means it broke in, `Running` means the wait timed out and you can simply call `wait` again.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 wait --seconds 60
```

The default server-side wait is 300 seconds.

### Console

Reads the console transcript. Unlike `output`, this captures **everything**, including output produced while the target runs: breakpoint command strings (`bu foo ".echo HIT; gc"`), module loads, and exception notices. This is the only way to observe a live trace without breaking in.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 console
```

By default it returns the last 10000 characters. For incremental reading (tailing a trace), note `consoleLength` from `status` (or the `next --since` hint printed on stderr), then poll:

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 console --since 12345
```

Each call prints `console-length: <n>; next --since <n>` on stderr; pass that offset to the next call to read only new output. The transcript keeps roughly the last 2 million characters; older text is trimmed (reflected by `truncated`).

### Tracing a running target (recipe)

To trace calls with auto-continue breakpoints:

```powershell
# 1. Set the breakpoint with an echo + continue command string
windbg-bridge.exe --pipe P execute "bu mymodule!MyFunc \".echo HIT MyFunc; k 5; gc\""
# 2. Note the current console offset
windbg-bridge.exe --pipe P status
# 3. Resume the target (returns immediately)
windbg-bridge.exe --pipe P execute "g"
# 4. Poll the trace while it runs
windbg-bridge.exe --pipe P console --since <offset>
# 5. When done, break back in and continue debugging
windbg-bridge.exe --pipe P break
```

Or, if you expect a real (non-continuing) breakpoint to hit, replace step 4 with `wait`.

### Listen

`listen` blocks until the WinDbg user queues a prompt with `ask` or `!ask`, then prints that prompt to stdout and exits.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 listen
```

For agent-driven workflows, start `listen` as a background or monitored command and let it keep running until the user submits the next prompt. When it completes, read stdout, respond to the user, then launch `listen` again for the next turn.

In WinDbg, queue the next prompt with either of these:

```text
ask why is this thread deadlocked
!ask what should we inspect next
```

The user will probably ask you about what they're currently seeing in Windbg, so it's good practice to check the history to get the context on what the user is asking.

## Important behaviors

- If WinDbg is not attached to a target, `execute` can fail with: `Invalid request for current state (NoSession)`.
- While the target is **running**, `execute` is rejected immediately with an explanatory error (the command is *not* queued). Use `break` or `wait` first.
- `execute "g"` returns as soon as the target resumes; it does not wait for the next break-in. Follow it with `wait` (blocking) or poll `console --since` (non-blocking trace reading).
- Output emitted while no agent/user command is active (breakpoint command strings, module loads, exceptions) is not attached to any history entry — it is only visible through `console`.
- History is sourced from WinDbg command and output events, so it includes both user commands and agent commands.
- Thread decoration is derived from the prompt prefix embedded in captured output, for example `0:001>` from `0:001> k`.

## Recommended agent behavior

1. Call `status` before assuming the bridge is available, and re-check it whenever a command fails or times out — `session.runningState` distinguishes "target is running" from "bridge is dead".
2. Prefer `history` plus `output --id ... --max-chars ...` over repeatedly executing broad commands just to rediscover prior output.
3. Keep output requests bounded when exploring large dumps or verbose commands.
4. Treat `NoSession` as a debugger-state issue, not a transport failure.
5. Never wait for a human to click **Break** — use the `break` command to interrupt a running target yourself.
6. Run `listen` as a background or monitored command instead of waiting synchronously in the foreground.
7. After `listen` completes, read the queued prompt from stdout, respond, and immediately launch `listen` again.
8. If history already contains the command you need, prefer reading its output over rerunning it.
