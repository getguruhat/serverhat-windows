ServerHat starts, stops, restarts and opens the local web servers you run while you work (`npm run dev`, a Node script, `python -m http.server`), from the Windows notification area. It has a local API, so Claude Code and Codex can set servers up for you.

**Install** (Windows 10 1903 or later, or Windows 11)
1. Download `ServerHat-0.2.1-Windows-Setup.exe` below and run it. No administrator rights needed.
2. If Windows SmartScreen says it protected your PC, choose **More info › Run anyway**. The installer isn't code-signed yet.
3. Click the globe in the notification area (it may be behind the **^** arrow) and add a server, or import a `.claude\launch.json`.

**What's in ServerHat for Windows 0.2.1**
This is the first Windows release. It shares its name and version with ServerHat for macOS.
- Every server with its status: running, stopped, crashed, failed or port in use, with Start/Stop, Restart, Open in browser, View logs, Edit and Remove
- Tells you when a server crashes (with its last line of output) and which process holds a port that's already in use
- Stop ends the whole process tree, so `npm run dev` leaves nothing behind; quitting ServerHat stops the servers it started
- Finds `npm`, `node`, `python` and other commands on your PATH, including `.cmd` shims
- The same `servers.json` and core local API as ServerHat for macOS, so the same scripts work on both

**Not on Windows yet:** command approval, the MCP server, notifications, project import, HTTP health checks, restart after a crash and export are macOS-only for now. Without approval, anything holding the API token can start a command right away, so only give it to agents and scripts you trust.

Free and open source (MIT). No account, no analytics.
