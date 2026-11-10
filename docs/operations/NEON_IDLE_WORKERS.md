# Neon idle behavior

The API keeps background work durable in PostgreSQL but wakes local workers when a payment, moderation event, notification delivery, backup, or recommendation job is committed. Timed plan, channel, payment, and backup work also wakes at its next due time. Empty queues reconcile on aligned 30-minute UTC boundaries; if an instance stops after committing a row but before signaling its worker, another reconciliation recovers it within approximately 30 minutes. Active jobs retain their existing processing and retry behavior.

Recommendation and database-backup workers acquire a dedicated PostgreSQL session lock only while recovering or processing work. Neon pooled URLs are converted to direct URLs for these session locks because transaction pooling does not preserve session state. The lock connection is closed when the queue is empty.

Use `/health/live` for frequent liveness probes. `/health` deliberately checks PostgreSQL and remains the deployment readiness check; frequent external requests to `/health` will keep Neon awake. Render's API blueprint uses `/health/live`, but the Render service follows `develop`, so changing `main` alone does not update an existing Render deployment.

The notification session monitor still checks PostgreSQL while a SignalR connection is open, so an open web or mobile session can keep Neon active. Database jobs, other clients, or repeated failed work can also wake the compute. Neon should be observed in its console after deployment rather than assuming a fixed monthly usage.
