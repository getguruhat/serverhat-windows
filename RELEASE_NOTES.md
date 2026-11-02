ServerHat starts, stops, restarts and opens the local web servers you run while you work (`npm run dev`, a Node script, `python -m http.server`), from the Windows notification area. It has a local API, so Claude Code and Codex can set servers up for you.

**Install** (Windows 10 1903 or later, or Windows 11)
1. Download `ServerHat-0.2.3-Windows-Setup.exe` below and run it. No administrator rights needed.
2. If Windows SmartScreen says it protected your PC, choose **More info › Run anyway**. The installer isn't code-signed yet.
3. Click the globe in the notification area (it may be behind the **^** arrow) and add a server, or import a `.claude\launch.json`.

**New in 0.2.3: command approval, as on macOS**
- A server added through the API or by editing `servers.json`, or whose `command`, `args`, `cwd` or `env` changes, can't start until you allow it. Name, port, url and `startAtLogin` don't count.
- ServerHat shows a notification with the command and folder; clicking it opens **Allow**, **Remove** and **Later**. The menu shows the waiting server with its full command, folder and environment variable names, and **Allow** and **Remove**.
- Until then, `start` and `restart` answer `403`; with `?wait=<seconds>` they wait for you to click Allow. API responses include `"needsApproval": true`, exactly like the Mac app's.
- Servers you add or edit in ServerHat's own windows, or import from its menu, count as allowed, and so do the servers you already had before updating. What you've allowed is kept in the registry, not in `servers.json`.

**Also in ServerHat for Windows**
- Every server with its status: running, stopped, crashed, failed or port in use, with Start/Stop, Restart, Open in browser, View logs, Edit and Remove
- Tells you when a server crashes (with its last line of output) and which process holds a port that's already in use
- Stop ends the whole process tree, so `npm run dev` leaves nothing behind; quitting ServerHat stops the servers it started
- Finds `npm`, `node`, `python` and other commands on your PATH, including `.cmd` shims
- The same `servers.json` and core local API as ServerHat for macOS, so the same scripts work on both

**Not on Windows yet:** the MCP server, crash notifications, project import, HTTP health checks, restart after a crash and export are macOS-only for now.

Free and open source (MIT). No account, no analytics.
