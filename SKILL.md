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
   - `E:\git\windbg-bridge\artifacts\publish\windbg-bridge\Release\windbg-bridge.exe launch [-- <optional WinDbg args>]`
3. Obtain the pipe name from the WinDbg bridge panel, or reuse the `pipeName` returned by `launch`.
4. Use the CLI to talk to the pipe:
   - `E:\git\windbg-bridge\artifacts\publish\windbg-bridge\Release\windbg-bridge.exe`
5. Start with `status` to confirm the bridge is running and the pipe is correct.
6. Launch an instance _in the background_ with the `listen` option. It will block until the WinDbg user queues a prompt with `ask ...` or `!ask ...`. It allows the user to communicate with you directly from Windbg.
7. Use `history` for lightweight command discovery.
8. Use `output` to retrieve the captured output for a specific history id, optionally capped with `--max-chars`.
9. Use `execute` to send exactly one WinDbg command per CLI invocation.
10. The CLI waits indefinitely unless you pass `--timeout <seconds>`.

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

Use this first to confirm the bridge is live.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 status
```

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

### Execute

`execute` sends one WinDbg command. Agent-triggered commands are submitted as typed commands so they should appear in WinDbg like normal user input.

```powershell
windbg-bridge.exe --pipe windbg-bridge-123 execute "!clrstack"
```

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
- History is sourced from WinDbg command and output events, so it includes both user commands and agent commands.
- Thread decoration is derived from the prompt prefix embedded in captured output, for example `0:001>` from `0:001> k`.

## Recommended agent behavior

1. Call `status` before assuming the bridge is available.
2. Prefer `history` plus `output --id ... --max-chars ...` over repeatedly executing broad commands just to rediscover prior output.
3. Keep output requests bounded when exploring large dumps or verbose commands.
4. Treat `NoSession` as a debugger-state issue, not a transport failure.
5. Run `listen` as a background or monitored command instead of waiting synchronously in the foreground.
6. After `listen` completes, read the queued prompt from stdout, respond, and immediately launch `listen` again.
7. If history already contains the command you need, prefer reading its output over rerunning it.
