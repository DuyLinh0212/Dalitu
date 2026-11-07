from __future__ import annotations

import asyncio
import logging
from contextlib import asynccontextmanager, suppress

import httpx
from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from app.api import router
from app.config import Settings, get_settings
from app.model_registry import ModelRegistry
from app.public_feed import router as public_feed_router
from app.r2_store import R2ConfigurationError
from app.ranking_cache import UpstashRankingCache

logger = logging.getLogger("uvicorn.error")


def create_app(
    settings: Settings | None = None,
    registry: ModelRegistry | None = None,
) -> FastAPI:
    resolved_settings = settings or get_settings()
    resolved_registry = registry or ModelRegistry(resolved_settings)

    @asynccontextmanager
    async def lifespan(_app: FastAPI):
        async def refresh_models():
            if resolved_registry.loaded is None:
                logger.info("Loading the initial recommendation model in the background.")
                # Use the same lock as later refreshes and model training. Storage
                # must not hold the HTTP listener (including /health) closed.
                await asyncio.to_thread(resolved_registry.refresh)
                if resolved_registry.loaded is None:
                    logger.warning("Initial model is not ready: %s", resolved_registry.load_error)
                else:
                    logger.info("Initial recommendation model is ready: %s",
                                resolved_registry.loaded.metadata.get("modelVersion"))
            delay = 2
            while True:
                await asyncio.sleep(delay)
                await asyncio.to_thread(resolved_registry.refresh)
                delay = 10 if resolved_registry.loaded is not None else min(delay * 2, 60)

        logger.info("Starting Recommendation HTTP without waiting for model storage.")
        task = asyncio.create_task(refresh_models())
        async with httpx.AsyncClient(timeout=30) as content_client:
            ranking_cache = UpstashRankingCache(
                endpoint=resolved_settings.upstash_redis_rest_url,
                token=resolved_settings.upstash_redis_rest_token,
                environment=resolved_settings.app_env,
            )
            _app.state.content_client = content_client
            _app.state.ranking_cache = ranking_cache
            try:
                yield
            finally:
                task.cancel()
                with suppress(asyncio.CancelledError):
                    await task
                await asyncio.to_thread(resolved_registry.statistics.flush)
                ranking_cache.close()

    app = FastAPI(
        title=resolved_settings.app_name,
        version="0.1.0",
        description=(
            "Collaborative Filtering and direct web/mobile recommendation feeds for HuTube. "
            "Supports User-Based and Item-Based CF."
        ),
        lifespan=lifespan,
    )
    app.state.settings = resolved_settings
    app.state.registry = resolved_registry
    app.add_middleware(
        CORSMiddleware,
        allow_origins=resolved_settings.cors_allowed_origins,
        allow_methods=["GET"],
        allow_headers=["Authorization", "X-HuTube-Client"],
    )

    @app.exception_handler(R2ConfigurationError)
    async def missing_model_storage(request: Request, exc: R2ConfigurationError):
        logger.error("Model request %s cannot use R2; missing settings: %s",
                     request.url.path, ", ".join(exc.missing_settings))
        return JSONResponse(status_code=503, content={"detail": {
            "code": "MODEL_STORAGE_NOT_CONFIGURED",
            "message": str(exc) + ". Configure them on the Recommendation Service in Render.",
            "retryable": False,
            "missingSettings": exc.missing_settings,
        }})

    @app.exception_handler(Exception)
    async def unexpected_model_error(request: Request, exc: Exception):
        logger.error("Unhandled Recommendation Service error at %s", request.url.path,
                     exc_info=(type(exc), exc, exc.__traceback__))
        return JSONResponse(status_code=500, content={"detail": {
            "code": "MODEL_SERVICE_ERROR",
            "message": "Recommendation Service failed; inspect its error logs.",
            "retryable": False,
            "errorType": type(exc).__name__,
        }})

    app.include_router(router)
    app.include_router(public_feed_router)
    return app


app = create_app()
