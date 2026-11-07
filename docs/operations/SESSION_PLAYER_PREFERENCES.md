# Web session player preferences

The web player stores volume and mute preferences in the active Redis session hash. For example, 20% volume is `volume = 0.2`, with `muted = false`.

## Redis configuration

Configure the API with `UPSTASH_REDIS_REST_URL` and a read/write `UPSTASH_REDIS_REST_TOKEN`. The existing `SystemMonitoring:RedisRestUrl` and `SystemMonitoring:RedisRestToken` configuration keys are also supported. Keep these credentials on the backend.

Preferences use the hash `dalitu:auth:session:<sessionId>`, where the session ID has 32 hexadecimal characters without hyphens. The store reads `volume` and `muted` from Redis on each request. It writes only those fields, preserves authentication fields already in the hash, and sets the hash expiration to the session expiry.

This does not turn Redis into the authentication session store. The API continues to validate active sessions against PostgreSQL, and no database migration is needed for the preference fields.

## Request flow

- `GET /api/v1/auth/session/player-preferences` reads the current session hash and returns `{ "volume": 1, "muted": false }` when no valid preference is present.
- `PUT /api/v1/auth/session/player-preferences` accepts the same JSON shape. Volume must be between 0 and 1, inclusive. Both endpoints derive user/session IDs from the authenticated session and disable HTTP response caching.
- The browser updates its own in-memory UI state immediately and saves after 400 ms without further slider changes. That browser state is client-side; the API does not keep a local copy.
- Refreshing the access token copies preferences to the replacement session ID, then removes only the audio fields from the old Redis hash. A new login starts with defaults; sessions on other devices have independent values.
- Automatic muting by the browser's autoplay fallback does not change the user's saved mute preference.

If Redis is disabled or unavailable, reads return the default preferences and writes are not persisted. The API does not fall back to an in-process copy. Playback remains available with the default volume and mute state.
