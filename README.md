# MCPHub

**One app to install, run and update the MCPSharp servers — and one URL to point your AI client at.**

MCPSharp is a suite of MCP (Model Context Protocol) servers that let an AI assistant do real work: query your databases, drive a browser, manage GitHub and GitLab, administer remote machines, control Docker, read your mail. Each one is a separate program with its own version, config file and port.

Running a handful of them by hand gets old quickly. MCPHub does it for you.

![The Services page, listing every managed MCP server with its port, version and run state](docs/screenshots/01-services.png)

## Why you'd want it

- **Install with one click.** MCPHub fetches the right release from GitHub and unpacks it. No zips, no PATH, no guessing which build you need.
- **Keeps everything up to date.** It checks each server's latest release and shows you a coloured dot when one is behind.
- **Starts servers for you.** Tick *Auto* and a server comes up whenever MCPHub launches, quietly in the background.
- **One endpoint instead of fifteen.** The built-in proxy aggregates every running server behind a single URL, so your AI client needs one line of config no matter how many servers you run.
- **No runtime to install.** The self-contained builds bundle everything they need.

## Install

Download the latest release for your platform from the [Releases page](https://github.com/Wixely/MCPHub/releases), unzip it anywhere, and run `MCPHub`.

There's no installer and nothing is written outside your own user folders.

## First run

1. **Open MCPHub.** You land on the **Services** page, listing every server it knows about.
2. **Install what you want.** Click **Install** on a row. A progress bar replaces the buttons; when it finishes the version appears under *Installed*.
3. **Start it.** Click **Start**. The state dot goes amber (*Starting*) then green (*Running*).
4. **Tick *Auto*** on anything you want up automatically next time.
5. **Go to the Proxy page** and copy the client snippet into your AI client's config.

That's it. You don't need to know any server's port — the proxy handles routing.

## One endpoint for your AI client

The **Proxy** page is the important one. It runs a single MCP endpoint that forwards to every server you have running, namespacing each one's tools so nothing collides.

![The Proxy page, showing the single aggregated endpoint and the servers behind it](docs/screenshots/04-proxy.png)

Click **Copy snippet** and paste it into your client:

```json
{
  "mcpServers": {
    "mcphub": {
      "url": "http://127.0.0.1:5800/mcp"
    }
  }
}
```

Add or remove servers later and your client config never changes — the proxy picks them up as they start and stop.

## The pages

### Services

Everything MCPHub manages, one row each. The **search box** above the list narrows it as you type — by display name or product name, so both `mail` and `mailcal` find Mail & Calendar.

![Searching the services list narrows it to matching servers and shows a match count](docs/screenshots/08-services-search.png)

| Column | Meaning |
| --- | --- |
| **Port** | The port this server listens on, read from its own config file. `auto` until it's installed, because that's when the config arrives. |
| **Installed** | The version you have, or `—` if it isn't installed. |
| **Latest** | The newest release on GitHub. The dot is green when you're current, amber when an update is waiting. |
| **State** | Grey *Stopped*, amber *Starting*, green *Running*, red if it faulted. |
| **Auto** | Start this server automatically when MCPHub launches. |

The buttons:

| Button | Does |
| --- | --- |
| **Install** / **Update** / **Reinstall** | Label follows what's needed. Your config is preserved. |
| **Start** / **Restart** | Starts a stopped server. Once it's running the same button reads **Restart** and stops then starts it — which is what you want after editing a config. |
| **Stop** | Stops it without starting again. |
| **Config** | Opens that server's JSON in your editor. Servers that read more than one config file (Remote Admin) get a dropdown listing all of them. |
| **Logs** | Jumps to the Logs page filtered to this server. |

**Check for updates** refreshes the *Latest* column for everything at once. MCPHub remembers the answer, so it doesn't hit GitHub every time you open it.

![The Config button opens a menu when a server reads more than one config file](docs/screenshots/09-config-dropdown.png)

> **First time opening an extra config file?** Some servers ship their secondary files as `.example.json` templates so an update can never overwrite your real data. Picking one from the Config dropdown renames the template into place and opens it, pre-filled with the right shape.

### Diagnostics

Every connected server and the tool calls it exposes through the proxy. This is where you confirm your client really can see what you think it can — expand a server to list its calls, or filter by name across all of them.

![The Diagnostics page, listing connected servers and their aggregated tool calls](docs/screenshots/06-diagnostics.png)

### Logs

Output from any server, or from the proxy itself. Pick a source, filter the lines, and **Copy** or **Save** when you need to send someone the evidence.

![The Logs page showing proxy output](docs/screenshots/05-logs.png)

This is the first place to look when a server won't start.

### Agent

[DaggerAgent](https://github.com/Wixely/DaggerAgent) is an LLM agent that drives the whole suite through the proxy. Install it here and MCPHub wires it to the proxy for you. Run it as an interactive CLI, a web UI, or a background job poller.

![The Agent page](docs/screenshots/03-agent.png)

### Engine

[Slopworks](https://github.com/Wixely/Slopworks) runs a local vLLM server with an OpenAI-compatible API, so the agent can use a model on your own machine instead of a hosted one. MCPHub shows whether the container and API are healthy and can add the endpoint to DaggerAgent for you.

![The Engine page showing vLLM server status](docs/screenshots/02-engine.png)

### Settings

![The Settings page](docs/screenshots/07-settings.png)

| Setting | What it does |
| --- | --- |
| **Shared servers folder** | Where servers are installed. Change it to put them on another drive. |
| **Download self-contained builds** | On by default. Bundles the .NET runtime — bigger downloads, but nothing to install. Turn it off only if you already have the .NET runtime. |
| **Proxy port / bind** | The aggregated endpoint. `127.0.0.1` keeps it on this machine; change the bind address to reach it from your network. |
| **System tray** | Whether minimising and closing hide to the tray instead of exiting. |
| **GitHub token** | Optional. Lifts GitHub's 60-requests-per-hour limit on update checks. Stored encrypted (DPAPI on Windows). |

## The servers it manages

| Server | Port | What it's for |
| --- | --- | --- |
| [Azure DevOps](https://github.com/Wixely/AzureDevopsMCPSharp) | 5700 | Boards, repos, pipelines and wikis |
| [GitHub](https://github.com/Wixely/GithubMCPSharp) | 5701 | Repositories, issues, pull requests, Actions |
| [GitLab](https://github.com/Wixely/GitlabMCPSharp) | 5702 | Projects, merge requests, pipelines |
| [Home Assistant](https://github.com/Wixely/HomeAssistantMCPSharp) | 5703 | Smart-home entities and automations |
| [Playwright](https://github.com/Wixely/PlaywrightMCPSharp) | 5704 | Cross-browser automation |
| [Proxmox](https://github.com/Wixely/ProxmoxMCPSharp) | 5705 | Virtual machines and containers |
| [Remote Admin](https://github.com/Wixely/RemoteAdminMCPSharp) | 5706 | Windows (WinRM) and Linux (SSH) administration |
| [RouterOS](https://github.com/Wixely/RouterOSMCPSharp) | 5707 | MikroTik router management |
| [Paperless-ngx](https://github.com/Wixely/PaperlessNgxMCPSharp) | 5708 | Search and manage a document archive |
| [Chrome DevTools](https://github.com/Wixely/ChromeDevToolsMCPSharp) | 5709 | Drive Chrome via the DevTools Protocol |
| [Noteworthy](https://github.com/Wixely/NoteworthyMCPSharp) | 5711 | Create and edit MIDI music files |
| [SQL](https://github.com/Wixely/SQLMCPSharp) | 5712 | MSSQL, MySQL, Oracle and SQLite |
| [Redis](https://github.com/Wixely/RedisMCPSharp) | 5713 | Read, write, search and diagnose Redis |
| [Repo Detox](https://github.com/Wixely/RepoDetox) | 5714 | Clean secrets and history out of git repos |
| [ComfyUI](https://github.com/Wixely/ComfyUIMCPSharp) | 5715 | Image generation with live progress |
| [Portainer](https://github.com/Wixely/PortainerMCPSharp) | 5716 | Docker stacks and containers via Portainer |
| [Mail & Calendar](https://github.com/Wixely/MailCalMCPSharp) | 5717 | Outlook, Gmail and IMAP mail plus calendars |

These are each server's shipped default. MCPHub doesn't assume them — it reads the port from the
server's own config after install, so changing one there is all it takes and the Services list
follows.

Most servers need configuring before they're useful — a connection string, an API token, the address of the thing they talk to. Click **Config** on the row to open that server's JSON, then **Stop** and **Start** it to pick up the change. Each server's own README documents its settings.

> **A note on safety.** Servers that can change things ship **read-only by default**. Deleting a stack, dropping a table or restarting a machine stays blocked until you explicitly turn it on in that server's config. This is deliberate — check the server's README before you loosen it.

## Updating

**Check for updates** on the Services page refreshes every row. Where an update is waiting the button becomes **Update**; click it and MCPHub stops the server, replaces the binaries, keeps your config, and starts it again if it was running.

MCPHub updates itself the same way from the **Updates** page.

## Where things live

| | |
| --- | --- |
| Installed servers | The *Shared servers folder* from Settings |
| Your settings | Your user config folder, under `MCPHub` |
| Logs | Beside the installed servers, in `logs` |
| Single-instance lock | `mcphub.lock` in your user data folder |

Each server keeps its config in a `<ServerName>.json` next to its executable, so updating never overwrites your settings.

## Troubleshooting

**A server won't start.** Open **Logs**, pick that server, and read the last few lines. The usual causes are a missing setting in its config or a port already in use.

**MCPHub won't open and no window appears.** It is already running — check the notification area for the tray icon, since **Close to tray** keeps it alive after you close the window. Only one MCPHub may run at a time: it binds a fixed proxy port, starts each service on its own fixed port, and writes to a shared servers folder, so a second instance would fight the first for all three. A second launch exits immediately with code `2` and explains itself on standard error, which you will see if you started it from a terminal.

If you genuinely want another instance — testing a new build against an old one, say — pass `--allow-multiple-instances`. Nothing else is changed by that switch: both instances will still compete for the proxy port and the servers folder, so point the second one at a different port and folder in **Settings** first.

The lock is an exclusive lock the operating system holds on `mcphub.lock` for as long as the process lives. It is released even if MCPHub is killed, so a leftover `mcphub.lock` file never blocks a restart and should not be deleted by hand.

**Two servers fighting over a port.** Each server's port lives in its own JSON under `Server` → `Port`. Change one, then stop and start it.

**"Couldn't reach GitHub."** Update checks are rate-limited to 60 an hour without a token. Add a GitHub token in **Settings** to lift it — read-only public access is enough.

**My client can't see any tools.** Check the **Proxy** page shows the proxy running and at least one server *Connected*. A server has to be **Running** on the Services page before the proxy will pick it up.

**Nothing is listed as Connected.** Servers connect a moment after they start. If a server sits at *Starting* it's failing its health check — check its logs.

## Licence

MIT. See [LICENSE](LICENSE).
