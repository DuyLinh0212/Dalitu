# EC2 deployment: API and Recommendation Service

This Compose file runs the .NET API, Python Recommendation Service, and Caddy reverse proxy. It expects PostgreSQL and R2 to be provided by the existing external services; it does not start PostgreSQL or either web app.

## Environment files

Create these files on the EC2 host and keep them out of Git:

- `.env.ec2-api` at the repository root: database connection string, JWT signing key/issuer/audience, auth web/admin URLs and allowed origins, recommendation service/admin tokens, storage settings, and any email/Google settings the API uses.
- `recommendation-service/.env.ec2`: `CORS_ALLOWED_ORIGINS`, matching `RECOMMENDER_SERVICE_TOKEN` and `RECOMMENDER_ADMIN_TOKEN`, the four R2 settings (`Storage__R2__AccountId`, `Storage__R2__AccessKeyId`, `Storage__R2__SecretAccessKey`, `Storage__R2__BucketName`), and optionally `UPSTASH_REDIS_REST_URL`/`UPSTASH_REDIS_REST_TOKEN` for the L2 ranking-ID cache. Copy only the Redis URL and REST token needed by this service; do not pass the complete API environment file into the container.
- `Caddyfile` routes `api.dalitu.app` to the API and `recommendation.dalitu.app` to Recommendation Service. Caddy obtains and renews public HTTPS certificates automatically after both DNS names point to this EC2 host and inbound TCP 80/443 is allowed in its AWS Security Group.

Use the supplied `Docs/HuTube.env` and `Docs/HuTube-recommendation-services.env` as the source. For the admin system monitor, also copy `UPSTASH_EMAIL`, `UPSTASH_API_KEY`, `UPSTASH_REDIS_DATABASE_ID`, `NEON_API_KEY`, `NEON_ORG_ID`, and `NEON_PROJECT_ID` into `.env.ec2-api`. These provider credentials are required for usage metrics; the Redis REST URL/token alone only covers Redis operations and health checks. Use read-only provider API keys where supported. Render credentials are no longer used by the monitor. Keep all secret values out of Git.

The API's `Recommendation__ServiceToken` and `Recommendation__AdminToken` must exactly match the corresponding Python service tokens. Set `CORS_ALLOWED_ORIGINS` to a JSON array of the actual browser client origins. The Compose file sets service-to-service URLs to Docker network names automatically.

After changing `.env.ec2-api`, recreate the API container so Docker loads the new environment:

```bash
sudo docker compose -f docker-compose.ec2.yml config --quiet
sudo docker compose -f docker-compose.ec2.yml up -d --no-deps --force-recreate api
```

The monitor shows host RAM from Linux `/proc/meminfo` and caches Upstash usage for five minutes. Neon monthly compute consumption depends on the account plan; a provider HTTP 403 leaves database size and connections available while monthly consumption remains unavailable. See [system monitoring integrations](SYSTEM_MONITORING.md) for the variable list and metric scope.

## PostgreSQL backups to Cloudflare R2

In Dalitu Admin, open **System → Backup & Recovery** (`/system/database-backups`) to set a daily or weekly schedule in Vietnam time (UTC+7), start a backup, filter execution history by date, and download completed dumps. `backup.view` grants history and download access; `backup.manage` grants schedule and run access.

The API worker runs on EC2, exports PostgreSQL in compressed custom format, and uploads it under `database-backups/YYYY/MM/` in the configured `Storage__R2__BucketName`. It uses the existing `Storage__R2__AccountId`, `Storage__R2__AccessKeyId`, and `Storage__R2__SecretAccessKey` values in `.env.ec2-api`; the API image includes PostgreSQL 18 client tools for `pg_dump`. Download links are signed and expire after five minutes. The dump contains PostgreSQL data and metadata; video and image files remain in their current storage services.

## Start

For automatic updates from verified `main` commits, install the [EC2 auto-deployment controller](EC2_AUTO_DEPLOY.md). It waits for all required CI jobs and both Vercel deployments, checks the new services, and restores previous images if activation fails.

From the repository root on EC2:

```bash
docker compose -f docker-compose.ec2.yml up -d --build
docker compose -f docker-compose.ec2.yml ps
docker compose -f docker-compose.ec2.yml logs --tail=100 api recommendation
```

The API and Recommendation Service bind to `127.0.0.1:5080` and `127.0.0.1:8000`. Caddy listens on host ports 80/443, redirects HTTP to HTTPS, and routes the two public hostnames. Add inbound TCP 80 and 443 to the EC2 Security Group; do not open 5080 or 8000. The web/mobile clients call the public Recommendation Service URL directly, so set their service base URL to `https://recommendation.dalitu.app/api/v1` and include their actual origins in `CORS_ALLOWED_ORIGINS`.

Local health checks on EC2:

```bash
curl -fsS http://127.0.0.1:5080/health
curl -fsS http://127.0.0.1:8000/health
```

`/health` confirms the processes respond; it does not confirm that recommendation artifacts are present or that R2 is configured. Check service logs and the model status endpoint for those details.
