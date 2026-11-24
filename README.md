# MobySync

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)

**MobySync** is a lightweight, high-performance Docker container auto-updater designed for home labs and production environments. It monitors your running containers, intelligently pulls new images, and safely replaces containers while respecting dependency chains and providing automated rollbacks on failure.

Built with .NET 10, MobySync is designed to be a "set-and-forget" solution for keeping your Docker stack up-to-date with minimal manual intervention.

---

## Key Features

- **Safety First:** Automatic reverse-order (LIFO) rollback of the entire dependency group if an update fails, fails its Docker `HEALTHCHECK`, or crashes/restarts during the startup stability window.
- **Dependency Awareness:** Respects container dependencies (via labels) to ensure services update in the correct order (e.g., Database before Web App).
- **Automated Self-Update:** Checks its own image at the end of each cycle and spawns an ephemeral `mobysync-self-updater` helper container to safely replace and health-check MobySync itself.
- **Flexible Scheduling:** Define exactly when updates should occur using Cron-like hour/minute settings and Timezone support.
- **Registry Support:** Queries Docker V2 / OCI registries directly and handles private registries using your existing Docker `config.json`.
- **Smart Tag Tracking:** Track specific tags (`latest`, `stable`, etc.) per container while automatically skipping pinned numeric versions and locally built images.
- **Rich Notifications:** Native support for Discord webhooks and generic JSON webhooks for startup, start-of-cycle, and detailed summaries.
- **Housekeeping:** Optional automatic pruning of old images after a successful update cycle.
- **REST API & Dry-Run Checks:** Endpoints to inspect live status and update history, run dry-run registry checks, trigger full update cycles, or update a single container/dependency group on demand, protected by API Key authentication.
- **Fine-grained Control:** Exclude specific containers or override tags globally or per-service.

---

## How It Works

1. **Discovery:** MobySync scans all running containers (excluding `EXCLUDED_CONTAINERS` and handling itself separately at the end of the cycle) and automatically skips pinned semantic version tags (e.g., `1.2.3`, `v2.0.1`) and locally built images.
2. **Dependency Mapping:** It partitions containers into isolated dependency groups and topologically sorts them via the `com.mobysync.depends-on` label to determine the safe update order.
3. **Direct Registry Check & Pre-Pulling:** For each group, MobySync queries the remote registry directly for the latest manifest digest (`Docker-Content-Digest`) and pulls all updated images (respecting credentials) *before* stopping any running containers — minimizing downtime and safely aborting the group if a pull fails.
4. **Container Replacement & Multi-Network Preservation:**
   - Cleans up any stale `<name>-backup` container, renames the running container to `<name>-backup`, and stops it.
   - Creates and starts the replacement container with the exact same configuration and all primary and secondary network attachments preserved.
   - **Health & Stability Verification:** If the container defines a Docker `HEALTHCHECK`, waits for it to report `healthy` (default `60s` timeout via `HEALTHCHECK_TIMEOUT_SECONDS`); otherwise monitors process stability (PID, exit codes, and restart counts) for the stability window (default `30s` via `STABILITY_WINDOW_SECONDS`).
5. **Finalization & Self-Update:** On group success, the `<name>-backup` containers are removed (and old images are pruned if `PRUNE_IMAGES=true`). On failure, MobySync automatically performs a reverse (LIFO) rollback of the current group. At the end of the cycle (when `SELF_UPDATE=true`), it checks its own image and spawns an ephemeral `mobysync-self-updater` container to safely swap MobySync itself.

---

## Configuration

### Environment Variables

| Variable | Description | Default |
| :--- | :--- | :--- |
| `API_KEY` | **(Required)** Secret key for API authentication | - |
| `UPDATE_HOUR` | Hour of the day to run updates (0-23) | `23` |
| `UPDATE_MINUTE` | Minute of the hour to run updates (0-59) | `30` |
| `TZ` | Timezone for the update schedule (e.g., `Europe/London`) | `UTC` |
| `STABILITY_WINDOW_SECONDS` | Seconds to monitor process stability (PID/restarts) for containers without a `HEALTHCHECK` | `30` |
| `HEALTHCHECK_TIMEOUT_SECONDS` | Maximum seconds to wait for containers with a Docker `HEALTHCHECK` to report `healthy` | `60` |
| `PRUNE_IMAGES` | Set to `true` to remove old images after updates | `false` |
| `SELF_UPDATE` | Set to `false` to disable MobySync from updating itself | `true` |
| `EXCLUDED_CONTAINERS`| Comma-separated list of container names to ignore | - |
| `IMAGE_TAG_OVERRIDES`| Global tag overrides (see [Advanced Configuration](#advanced-configuration)) | - |
| `DISCORD_WEBHOOK_URL`| URL for Discord notifications | - |
| `WEBHOOK_URL` | URL for generic JSON webhook notifications | - |

### Container Labels

By default, MobySync monitors **all running containers**. You can use labels to fine-tune update behavior:

| Label | Description | Example |
| :--- | :--- | :--- |
| `com.mobysync.target-tag` | The specific tag to track | `latest` or `1.2-stable` |
| `com.mobysync.depends-on` | Comma-separated list of container names this depends on | `db, redis` |

---

## Deployment

### Docker Compose

```yaml
services:
  mobysync:
    image: ghcr.io/sbairamis/mobysync:latest
    container_name: mobysync
    restart: unless-stopped
    ports:
      - "5080:5080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      # Optional: mount Docker credentials for private registries
      - ~/.docker/config.json:/app/creds/config.json:ro
    environment:
      - API_KEY=your_secure_random_key
      - UPDATE_HOUR=03
      - UPDATE_MINUTE=00
      - TZ=Europe/Athens
      - PRUNE_IMAGES=true
      - DISCORD_WEBHOOK_URL=https://discord.com/api/webhooks/...
      - EXCLUDED_CONTAINERS=portainer,nginx-proxy
```

---

## Advanced Configuration

### Tag Overrides
You can override tags globally or for specific docker-compose services using the `IMAGE_TAG_OVERRIDES` environment variable. 

> **Note:** Per-container labels (`com.mobysync.target-tag`) always take priority over these overrides. Containers with no match fall back to `latest`.

**Format:** `project:service:tag,image_name:tag`

- `my-stack:web:beta`: Overrides the `web` service in the `my-stack` project to use the `beta` tag.
- `nginx:alpine`: Overrides any container using the `nginx` image to use the `alpine` tag.

### Exclusions
You can exclude specific containers from being updated using `EXCLUDED_CONTAINERS=container1,container2`.

---

## API Reference

All `/api/*` endpoints require the `X-API-KEY: <your API_KEY>` header (`401 Unauthorized` if missing or invalid).

### Health Check
Unauthenticated endpoint used by container health checks to verify the service is running.

- **URL:** `GET /health`

**Responses:**

| Status | Meaning |
| :--- | :--- |
| `200 OK` | Service is healthy (`{"status":"healthy"}`) |

**Example:**
```bash
curl http://localhost:5080/health
```

### Get Status & History
Returns current coordinator state (`idle` or `running`), next scheduled run timestamp, the last `UpdateSummary`, and up to 20 recent history entries.

- **URL:** `GET /api/status`
- **Header:** `X-API-KEY: <your API_KEY>`

**Responses:**

| Status | Meaning |
| :--- | :--- |
| `200 OK` | Returns current status and update history |
| `401 Unauthorized` | Missing or invalid API key |

**Example:**
```bash
curl http://localhost:5080/api/status \
  -H "X-API-KEY: super-secret-key"
```

### Dry-Run Update Check
Checks all monitored containers against their registries and reports which have newer digests available without pulling images or restarting containers.

- **URL:** `POST /api/update/check`
- **Header:** `X-API-KEY: <your API_KEY>`

**Responses:**

| Status | Meaning |
| :--- | :--- |
| `200 OK` | Dry-run check completed |
| `401 Unauthorized` | Missing or invalid API key |

**Example:**
```bash
curl -X POST http://localhost:5080/api/update/check \
  -H "X-API-KEY: super-secret-key"
```

### Trigger Manual Update
Instantly starts an update cycle across all monitored containers.

- **URL:** `POST /api/update/trigger`
- **Header:** `X-API-KEY: <your API_KEY>`

**Responses:**

| Status | Meaning |
| :--- | :--- |
| `202 Accepted` | Cycle started |
| `409 Conflict` | A cycle is already running |
| `401 Unauthorized` | Missing or invalid API key |

**Example:**
```bash
curl -X POST http://localhost:5080/api/update/trigger \
  -H "X-API-KEY: super-secret-key"
```

### Trigger Targeted Update
Starts an update for a single monitored container (and, by default, its `com.mobysync.depends-on` dependency group; pass `?includeDependencies=false` to update only the named container).

- **URL:** `POST /api/update/trigger/{containerName}`
- **Header:** `X-API-KEY: <your API_KEY>`
- **Query (Optional):** `includeDependencies=true|false` (default `true`)

**Responses:**

| Status | Meaning |
| :--- | :--- |
| `202 Accepted` | Targeted update started |
| `404 Not Found` | Running monitored container was not found |
| `409 Conflict` | A cycle is already running |
| `401 Unauthorized` | Missing or invalid API key |

**Example:**
```bash
curl -X POST "http://localhost:5080/api/update/trigger/api?includeDependencies=true" \
  -H "X-API-KEY: super-secret-key"
```

---

## Notifications

### Discord
Provides rich embeds showing exactly what was updated, what was skipped, and any errors encountered.

### Generic Webhook
Sends a POST request with a JSON payload. Useful for integrating with Home Assistant, Gotify, or custom dashboards.

**Events sent:** `startup`, `update_started`, `update_cycle`.

---

## License

Distributed under the MIT License. See `LICENSE` for more information.
