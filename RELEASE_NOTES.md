ServerHat starts, stops, restarts and opens the local web servers you run while you work (`npm run dev`, a Node script, `python -m http.server`), from the Windows notification area. Its built-in MCP server lets Claude Code and Codex run them for you, with your approval if you turn that on.

**Install** (Windows 10 1903 or later, or Windows 11)
1. Download `ServerHat-0.4.0-Windows-Setup.exe` below and run it. No administrator rights needed.
2. If Windows SmartScreen says it protected your PC, choose **More info › Run anyway**. The installer isn't code-signed yet.
3. Click the globe in the notification area (it may be behind the **^** arrow) and add a server, or import a `.claude\launch.json`.

**New in 0.4.0: choose whether agents need your approval**
- A new setting, **Approve servers added by Claude or ChatGPT**, in the gear menu and the right-click menu. It's off by default: servers an agent adds or changes can run right away. Turn it on and anything an agent, script or `servers.json` edit adds or changes waits for you to click **Allow**, as before. Turning it on counts your current servers as allowed.

**Since 0.3.0:** `ServerHat.exe --mcp` gives Claude Code, Codex and other MCP clients tools to list, add, start, stop, restart and remove your servers and read their logs. Choose **Copy for Claude Code** or **Copy for Codex** in Settings and paste the command into PowerShell.

**Also in ServerHat for Windows**
- Optional command approval: when it's on, anything an agent, script or `servers.json` edit adds or changes waits for you to click **Allow**
- Every server with its status: running, stopped, crashed, failed or port in use, with Start/Stop, Restart, Open in browser, View logs, Edit and Remove
- Tells you when a server crashes (with its last line of output) and which process holds a port that's already in use
- Stop ends the whole process tree, so `npm run dev` leaves nothing behind; quitting ServerHat stops the servers it started
- Finds `npm`, `node`, `python` and other commands on your PATH, including `.cmd` shims
- The same `servers.json` and core local API as ServerHat for macOS, so the same scripts work on both

**Not on Windows yet:** project import, crash notifications, HTTP health checks, restart after a crash and export are macOS-only for now.

Free and open source (MIT). No account, no analytics.
