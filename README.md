# local-deploy

A distributed mobile app build and deployment system built on .NET 9. It automates building apps via [EAS CLI](https://docs.expo.dev/eas/) and submitting them to the appropriate store (e.g. TestFlight for iOS), coordinated through a central API server and one or more remote build agents.

## How It Works

```
Admin
  │
  │  POST /api/jobs  (profile + platform)
  ▼
Deploy-Server  ──────  Redis job queue
  │
  │  poll every 10s
  ▼
Deploy-Agent
  ├── eas build --local ...
  ├── eas submit --platform {platform} ...
  └── POST /api/agent/status  (running → completed/failed)
```

1. An admin POSTs a build job to the server with a build profile and platform.
2. The server enqueues the job in Redis.
3. A deploy-agent polls the server, dequeues the job, and runs `eas build --local`.
4. If the build produces an artifact (`.ipa` for iOS), the agent submits it via `eas submit` using the job's platform.
5. The agent continuously pushes logs and final status back to the server.
6. The admin can query job status and agent health via the REST API.

## Prerequisites

| Tool | Version |
|------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 9.0+ |
| [Redis](https://redis.io/) | Any recent |
| [EAS CLI](https://docs.expo.dev/eas/) | Latest (`npm i -g eas-cli`) |
| Expo account + EAS project configured | — |

## Project Structure

```
local-deploy/
├── deploy-server/          # ASP.NET Core REST API + Redis orchestration
│   ├── Program.cs          # App bootstrap, API endpoint definitions
│   ├── DeploymentService.cs# Job queue, status tracking, agent monitoring
│   ├── ApiKeyAuthHandler.cs# Custom API key authentication (Admin / Agent roles)
│   ├── Models.cs           # Shared DTOs
│   └── appsettings.json    # Redis connection string, API keys
├── deploy-agent/           # .NET Worker Service — runs on the build machine
│   ├── Worker.cs           # Polling loop, job execution, log streaming
│   ├── EasBuildRunner.cs   # Invokes `eas build` and `eas submit` processes
│   ├── DeployServerClient.cs# HTTP client for server communication
│   ├── Models.cs           # Shared DTOs
│   └── appsettings.json    # Server URL, project path, agent ID, keys
└── telegram-admin-webapp/  # Telegram WebApp admin panel (single HTML file)
    └── index.html          # Mobile-friendly UI for job management
```

## Getting Started

### 1. Start Redis

```bash
redis-server
```

### 2. Configure and run the server

Edit `deploy-server/appsettings.json`:

```json
{
  "Redis": "localhost:6379",
  "AdminApiKey": "your-strong-admin-key",
  "AgentApiKey": "your-strong-agent-key"
}
```

```bash
dotnet run --project deploy-server/
# Listens on http://localhost:5100
```

### 3. Configure and run the agent

Edit `deploy-agent/appsettings.json`:

```json
{
  "ServerUrl": "http://localhost:5100",
  "AgentApiKey": "your-strong-agent-key",
  "AgentId": "my-mac-m1",
  "ProjectPath": "/path/to/your/expo/project",
  "BuildOutputPath": "./build-output",
  "LogFilePath": "/tmp/deploy-agent.log",
  "PollIntervalSeconds": 10,
  "LogPushIntervalSeconds": 30
}
```

```bash
dotnet run --project deploy-agent/
```

### 4. Submit a build job

```bash
curl -X POST http://localhost:5100/api/jobs \
  -H "X-API-Key: your-strong-admin-key" \
  -H "Content-Type: application/json" \
  -d '{"profile": "production", "platform": "Ios"}'
```

## Admin WebApp

A single-file Telegram WebApp for managing jobs and agents from your phone — no build step required.

**Features:**
- View all jobs with color-coded status badges (pending, running, completed, failed, cancelled)
- Tap any job to expand logs, error messages, and timestamps
- Cancel pending jobs
- Monitor agent health (online/offline) and view recent agent logs
- Create new build jobs via a simple form
- Auto-refreshes every 15 seconds on the Jobs tab
- Adapts to Telegram dark/light theme

**Setup:**

1. Host `telegram-admin-webapp/index.html` as a static file (GitHub Pages, Railway, any static host)
2. Open the URL in a browser — enter your server URL and admin API key on first launch (saved to `localStorage`)
3. Or pass config via URL hash to skip the setup screen:
   ```
   https://your-host.com/index.html#url=https://your-server.railway.app&key=your-admin-key
   ```
4. In BotFather, set the hosted URL as your bot's **Menu Button** or **Web App URL**

## API Reference

All endpoints require the `X-API-Key` header. Admin endpoints use the admin key; agent endpoints use the agent key.

### Admin Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Health check (no auth required) |
| `POST` | `/api/jobs` | Enqueue a new build job |
| `GET` | `/api/jobs` | List recent jobs (queued + history) |
| `GET` | `/api/jobs/{jobId}` | Get details of a specific job |
| `DELETE` | `/api/jobs/{jobId}` | Cancel a pending job |
| `GET` | `/api/agents` | List all known agents and their status |
| `GET` | `/api/agent/{agentId}/logs` | Retrieve logs from a specific agent |

### Agent Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/agent/poll` | Dequeue the next available job |
| `PUT` | `/api/agent/status` | Update job status and attach logs |
| `POST` | `/api/agent/heartbeat` | Record agent liveness |
| `POST` | `/api/agent/logs` | Push agent log lines to the server |
| `GET` | `/api/agent/alive/{agentId}` | Check if a specific agent is alive |

## Configuration Reference

### deploy-server `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Redis` | `localhost:6379` | Redis connection string |
| `AdminApiKey` | `change-me-admin-key` | Key for admin API access |
| `AgentApiKey` | `change-me-agent-key` | Key for agent API access |

### deploy-agent `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `ServerUrl` | — | URL of the deploy-server |
| `AgentApiKey` | — | Must match server's `AgentApiKey` |
| `AgentId` | — | Unique identifier for this agent machine |
| `ProjectPath` | — | Absolute path to the Expo project |
| `BuildOutputPath` | `./build-output` | Where EAS writes build artifacts |
| `LogFilePath` | — | Path to the agent's log file |
| `PollIntervalSeconds` | `10` | How often the agent polls for jobs |
| `LogPushIntervalSeconds` | `30` | How often agent logs are sent to the server |

## Security Notes

- **Change default API keys** before any network-exposed deployment. The defaults (`change-me-admin-key`, `change-me-agent-key`) are placeholders only.
- API key comparison uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks.
- The server enforces role-based authorization: admin keys have full access; agent keys are limited to polling and status reporting endpoints.

## License

MIT © Max Statnykh
