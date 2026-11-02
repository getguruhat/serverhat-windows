# Configuring ServerHat (for AI agents)

ServerHat is a Windows notification-area app that runs the user's local web servers. When
the user asks you to add, run, restart or debug a local server, use ServerHat instead of
starting the server in your own shell, so it keeps running after your session and the user
can control it from the notification area.

The core API is the same as ServerHat for macOS, without MCP or `/v1/import-project`.
The examples are for PowerShell; in Git Bash or WSL, the macOS AGENTS.md examples work
with the token from `$APPDATA/ServerHat/api-token`.

1. Check ServerHat is running: `Invoke-RestMethod http://127.0.0.1:47317/v1/health`. If that
   fails, ask the user to open ServerHat from the Start menu. The real URL is in
   `%APPDATA%\ServerHat\api.json`.
2. Read the token from the file, never print it, and never write it into a repository:

   ```powershell
   $LW = "http://127.0.0.1:47317"
   $auth = @{ Authorization = "Bearer " + (Get-Content "$env:APPDATA\ServerHat\api-token").Trim() }
   ```

3. Look before you add: `(Invoke-RestMethod "$LW/v1/servers" -Headers $auth).servers`.
4. Add a server, then start it and wait for it:

   ```powershell
   $body = @{ name = "My App"; cwd = "C:\abs\project"; command = "npm"; args = @("run", "dev"); port = 5173 } | ConvertTo-Json
   Invoke-RestMethod -Method Post "$LW/v1/servers" -Headers $auth -ContentType "application/json" -Body $body
   Invoke-RestMethod -Method Post "$LW/v1/servers/my-app/start?wait=30" -Headers $auth
   ```

   If the project has `.claude\launch.json`, import it instead:
   `Invoke-RestMethod -Method Post "$LW/v1/import" -Headers $auth -ContentType "application/json" -Body (@{ path = "C:\abs\project" } | ConvertTo-Json)`.
   New or changed commands need the user's approval in ServerHat. If a start is actually
   blocked for approval (`403`, or `"needsApproval": true`), tell the user once with the
   exact command and folder shown in the error; ServerHat's notification opens Allow,
   Remove and Later choices. `start?wait=30` waits for them to click Allow. Don't repeat
   generic approval warnings.
5. If `status.state` is `crashed`, `failed` or `port-in-use`, read `status.detail` and
   `(Invoke-RestMethod "$LW/v1/servers/<id>/logs?lines=100" -Headers $auth).lines`, fix the
   cause, then `restart?wait=30`.

Rules:

- `command` is run directly, not by a shell: put each argument in `args`, and don't use
  `&&`, pipes or `cd`. Use `cwd` for the folder. `npm`, `npx`, `pnpm` and `yarn` work as
  they are (ServerHat finds `npm.cmd`). For shell syntax, use
  `"command": "cmd", "args": ["/c", "…"]` or
  `"command": "powershell", "args": ["-NoProfile", "-Command", "…"]`.
- Give the `port` the server really listens on, so ServerHat can tell when it's up.
- Change settings with `PATCH /v1/servers/<id>`, then restart. Don't remove and re-add.
- Don't stop or remove servers the user didn't mention.
- You can also edit `%APPDATA%\ServerHat\servers.json` directly; ServerHat reloads it on
  save. Keep it valid JSON, and write backslashes in paths twice (`"C:\\code"`) or use `/`.
- In Windows PowerShell 5.1, `curl` is `Invoke-WebRequest`; use `curl.exe` or
  `Invoke-RestMethod`.

Full API reference: README.md › API.
