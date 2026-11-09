# MobySync

**MobySync** is a lightweight, high-performance Docker container auto-updater designed for home labs and production environments. It monitors your running containers, intelligently pulls new images, and safely replaces containers while respecting dependency chains and providing automated rollbacks on failure.

Built with .NET 10, MobySync is designed to be a "set-and-forget" solution for keeping your Docker stack up-to-date with minimal manual intervention.

---

## Key Features

- **Safety First:** Automatic rollback to the previous container state if an update fails or if the new container crashes within 10 seconds of starting.
- **Dependency Awareness:** Respects container dependencies (via labels) to ensure services update in the correct order (e.g., Database before Web App).
- **Flexible Scheduling:** Define exactly when updates should occur using Cron-like hour/minute settings and Timezone support.
- **Registry Support:** Seamlessly handles private registries using your existing Docker `config.json`.
- **Smart Tag Tracking:** Track specific tags (`latest`, `stable`, etc.) per container.
- **Rich Notifications:** Native support for Discord webhooks and generic JSON webhooks for startup, start-of-cycle, and detailed summaries.
- **Housekeeping:** Optional automatic pruning of old images after a successful update cycle.
- **Manual Triggers:** REST API endpoint to force an update cycle instantly, protected by API Key authentication.
- **Fine-grained Control:** Exclude specific containers or override tags globally or per-service.

---

## How It Works

1. **Discovery:** MobySync scans for all running containers, excluding itself and any containers specified in `EXCLUDED_CONTAINERS`.
2. **Dependency Mapping:** It builds a dependency graph using the `com.mobysync.depends-on` label to determine the safe update order.
3. **Image Verification:** For each monitored container, it checks for a newer version of the image on the registry.
4. **The Update Cycle:**
   - Stops and renames the old container to a backup name.
   - Pulls the new image (respecting credentials).
   - Creates and starts a new container with the exact same configuration.
   - **Health Check:** Waits 10 seconds to ensure the new container doesn't crash.
5. **Finalization:** On success, the backup container is removed. On failure, MobySync automatically performs a rollback of the current group to ensure system stability.

---

## Configuration

### Environment Variables

| Variable | Description | Default |
| :--- | :--- | :--- |
| `API_KEY` | **(Required)** Secret key for API authentication | - |
| `UPDATE_HOUR` | Hour of the day to run updates (0-23) | `23` |
| `UPDATE_MINUTE` | Minute of the hour to run updates (0-59) | `30` |
| `TZ` | Timezone for the update schedule (e.g., `Europe/London`) | `UTC` |
| `PRUNE_IMAGES` | Set to `true` to remove old images after updates | `false` |
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
By default, MobySync excludes itself. You can add more containers to the exclusion list using `EXCLUDED_CONTAINERS=container1,container2`.

---

## API Reference

### Trigger Manual Update
Instantly starts an update cycle.

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
