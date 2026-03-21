# DeployKit

A distributed build and deployment system built on .NET 9. It runs arbitrary shell-command pipelines on remote build agents, coordinated through a central API server and configurable deployment profiles. Originally designed for mobile app builds via [EAS CLI](https://docs.expo.dev/eas/), but flexible enough for any multi-step build/deploy workflow.

## How It Works

```
Admin (Telegram WebApp)
  │
  │  1. Create a profile (steps, env vars, working dir)
  │  2. POST /api/jobs  { profileId }
  ▼
Deploy-Server  ──────  Redis (job queue + profiles)
  │
  │  poll every 10s
  ▼
Deploy-Agent
  ├── Run step 1 (from profile snapshot)
  ├── Run step 2 …
  ├── Run step N
  └── POST /api/agent/status  (running → completed/failed)
```

1. An admin creates a **profile** — a named configuration containing an ordered list of shell steps, environment variables (with secret support), and a working directory.
2. The admin submits a job referencing a profile ID. The server snapshots the profile and enqueues the job in Redis.
3. A deploy-agent polls the server, dequeues the job, and executes each step sequentially via `/bin/sh`.
4. Secret environment variable values are automatically redacted from all log output.
5. The agent streams logs and final status back to the server in real time (SSE).
6. The admin can monitor jobs, view live logs, and manage profiles via the REST API or Telegram WebApp.

## Prerequisites

| Tool | Version |
|------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 9.0+ |
| [Redis](https://redis.io/) | Any recent |

## Project Structure

```
local-deploy/
├── DeployKit.sln               # Solution file
├── deploy-server/                  # ASP.NET Core REST API + Redis orchestration
│   ├── Program.cs                  # App bootstrap, API endpoint definitions
│   ├── DeploymentService.cs        # Job queue, status tracking, agent monitoring
│   ├── ProfileService.cs           # Profile CRUD with secret masking
│   ├── ApiKeyAuthHandler.cs        # Custom API key authentication (Admin / Agent roles)
│   ├── Models.cs                   # Shared DTOs (jobs, profiles, env vars)
│   └── appsettings.json            # Redis connection string, API keys
├── deploy-server.Tests/            # Integration tests (WebApplicationFactory)
├── deploy-agent/                   # .NET Worker Service — runs on the build machine
│   ├── Worker.cs                   # Polling loop, job execution, log streaming
│   ├── StepRunner.cs               # Executes shell steps with secret redaction
│   ├── DeployServerClient.cs       # HTTP client for server communication
│   ├── Models.cs                   # Agent DTOs + settings
│   └── appsettings.json            # Server URL, agent ID, keys
├── deploy-agent.Tests/             # Unit tests (secret redaction, step runner)
└── telegram-admin-webapp/          # Telegram WebApp admin panel
    ├── index.html                  # Entry point
    ├── css/styles.css              # Themed styles
    └── js/
        ├── app.js                  # App initialization
        ├── api.js                  # Server API client
        ├── jobs.js                 # Job management UI
        ├── profiles.js             # Profile management UI
        ├── agents.js               # Agent monitoring UI
        ├── navigation.js           # Tab routing
        ├── helpers.js              # Shared utilities
        ├── state.js                # App state
        └── storage.js              # localStorage wrapper
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
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
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
  "PollIntervalSeconds": 10,
  "LogPushIntervalSeconds": 30
}
```

```bash
dotnet run --project deploy-agent/
```

### 4. Create a profile and submit a job

```bash
# Create a deployment profile
curl -X POST http://localhost:5100/api/profiles \
  -H "X-API-Key: your-strong-admin-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "ios-production",
    "workingDirectory": "/path/to/your/project",
    "envVars": [
      { "key": "APP_ENV", "value": "production", "isSecret": false },
      { "key": "API_TOKEN", "value": "secret-value", "isSecret": true }
    ],
    "steps": [
      "eas build --local --platform ios --profile production",
      "eas submit --platform ios --latest"
    ]
  }'

# Submit a build job using the profile ID from the response
curl -X POST http://localhost:5100/api/jobs \
  -H "X-API-Key: your-strong-admin-key" \
  -H "Content-Type: application/json" \
  -d '{"profileId": "PROFILE_ID_HERE"}'
```

## Admin WebApp

A modular Telegram WebApp for managing profiles, jobs, and agents from your phone — no build step required.

**Features:**
- **Profiles tab** — create, edit, and delete deployment profiles with steps, env vars, and secret management
- **Jobs tab** — view all jobs with color-coded status badges (pending, running, completed, failed, cancelled); tap to expand live logs; cancel pending jobs; create new jobs from existing profiles
- **Agents tab** — monitor agent health (online/offline) and view recent agent logs
- Auto-refreshes every 15 seconds on the Jobs tab
- Adapts to Telegram dark/light theme

**Setup:**

1. Host `telegram-admin-webapp/` as static files (GitHub Pages, Railway, any static host)
2. Open the URL in a browser — enter your server URL and admin API key on first launch (saved to `localStorage`)
3. Or pass config via URL hash to skip the setup screen:
   ```
   https://your-host.com/index.html#url=https://your-server.railway.app&key=your-admin-key
   ```
4. In BotFather, set the hosted URL as your bot's **Menu Button** or **Web App URL**

## API Reference

All endpoints require the `X-API-Key` header. Admin endpoints use the admin key; agent endpoints use the agent key.

### Admin — Jobs

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/health` | Health check (no auth required) |
| `POST` | `/api/jobs` | Enqueue a new job (`{ "profileId": "..." }`) |
| `GET` | `/api/jobs` | List recent jobs (queued + history) |
| `GET` | `/api/jobs/{jobId}` | Get details of a specific job |
| `GET` | `/api/jobs/{jobId}/stream` | SSE stream of job log deltas and status changes |
| `DELETE` | `/api/jobs/{jobId}` | Cancel a pending job |

### Admin — Profiles

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/profiles` | Create a new profile |
| `GET` | `/api/profiles` | List all profiles (secrets masked) |
| `GET` | `/api/profiles/{id}` | Get a specific profile (secrets masked) |
| `PUT` | `/api/profiles/{id}` | Update a profile (send `"***"` to preserve existing secrets) |
| `DELETE` | `/api/profiles/{id}` | Delete a profile (blocked if it has active jobs) |

### Admin — Agents

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/agents` | List all known agents and their status |
| `GET` | `/api/agent/{agentId}/logs` | Retrieve logs from a specific agent (`?lines=N`) |
| `GET` | `/api/agent/{agentId}/logs/stream` | SSE stream of agent log lines (`?from=N`) |

### Agent Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/agent/poll` | Dequeue the next available job |
| `PUT` | `/api/agent/status` | Update job status and attach logs (`?jobId=...`) |
| `POST` | `/api/agent/heartbeat` | Record agent liveness |
| `POST` | `/api/agent/logs` | Push agent log lines to the server |
| `GET` | `/api/agent/alive/{agentId}` | Check if a specific agent is alive (any auth) |

## Configuration Reference

### deploy-server `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection string |
| `AdminApiKey` | `change-me-admin-key` | Key for admin API access |
| `AgentApiKey` | `change-me-agent-key` | Key for agent API access |

### deploy-agent `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `ServerUrl` | — | URL of the deploy-server |
| `AgentApiKey` | — | Must match server's `AgentApiKey` |
| `AgentId` | Machine hostname | Unique identifier for this agent |
| `PollIntervalSeconds` | `10` | How often the agent polls for jobs |
| `LogPushIntervalSeconds` | `30` | How often agent logs are sent to the server |

> **Note:** Working directory, environment variables, and build steps are configured per-profile on the server, not on the agent.

## Running Tests

```bash
dotnet test DeployKit.sln
```

## Security Notes

- **Change default API keys** before any network-exposed deployment. The defaults (`change-me-admin-key`, `change-me-agent-key`) are placeholders only.
- API key comparison uses `CryptographicOperations.FixedTimeEquals` to prevent timing attacks.
- The server enforces role-based authorization: admin keys have full access; agent keys are limited to polling and status reporting endpoints.
- Environment variables marked as `isSecret` are automatically redacted from all agent log output (both literal values and common patterns like `KEY=value` or `"KEY":"value"`).
- Secret values in profile API responses are masked with `***`. When updating a profile, sending `***` as a secret's value preserves the existing stored value.

## License

MIT © Max Statnykh
