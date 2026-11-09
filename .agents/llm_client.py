"""Shared client for the Token Harbor (DeepSeek) and NVIDIA (Llama) helper scripts.

Reads credentials from environment variables first, then from .agents/secrets.env.
Never prints key values.
"""

from __future__ import annotations

import asyncio
import os
import random
from pathlib import Path

from openai import APIConnectionError, APIStatusError, AsyncOpenAI

SECRETS_FILE = Path(__file__).resolve().parent / "secrets.env"

DEFAULT_TOKEN_HARBOR_BASE_URL = "https://tokenharbor.ai/v1"
DEFAULT_NVIDIA_BASE_URL = "https://integrate.api.nvidia.com/v1"
DEFAULT_DEEPSEEK_MODEL = "deepseek-v4-flash:free"
DEFAULT_NVIDIA_MODEL = "nvidia/nemotron-3.5-super-vl-120b-a12b"

# NVIDIA reasoning models (nemotron-*) put the answer in `content` only after
# burning max_tokens on `reasoning_content`, so they need a generous budget.
MIN_TOKENS_WITH_REASONING = 6000


def load_secrets() -> dict[str, str]:
    """Environment variables win; secrets.env fills in the gaps."""
    values: dict[str, str] = {}
    if SECRETS_FILE.exists():
        for raw in SECRETS_FILE.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip().strip('"').strip("'")
    for key in list(values) + [
        "TOKEN_HARBOR_KEYS",
        "TOKEN_HARBOR_BASE_URL",
        "DEEPSEEK_MODEL",
        "NVIDIA_API_KEY",
        "NVIDIA_BASE_URL",
        "NVIDIA_MODEL",
    ]:
        env_value = os.environ.get(key)
        if env_value:
            values[key] = env_value.strip()
    return values


def token_harbor_keys(secrets: dict[str, str]) -> list[str]:
    keys = [k.strip() for k in secrets.get("TOKEN_HARBOR_KEYS", "").split(",") if k.strip()]
    if not keys:
        raise SystemExit(
            f"Không tìm thấy TOKEN_HARBOR_KEYS. Copy .agents/secrets.env.example "
            f"thành .agents/secrets.env và điền key thật."
        )
    return keys


class KeyRotationError(RuntimeError):
    pass


class AsyncKeyRotator:
    """Round-robin over multiple API keys, rotating on rate limit / server errors."""

    def __init__(self, keys: list[str], base_url: str, max_attempts: int | None = None):
        self._base_url = base_url
        self._clients = [AsyncOpenAI(base_url=base_url, api_key=key) for key in keys]
        self._cooldown = [0.0] * len(keys)
        self._index = 0
        self._lock = asyncio.Lock()
        self._max_attempts = max_attempts or len(keys) * 3

    @property
    def key_count(self) -> int:
        return len(self._clients)

    async def _next_client(self):
        async with self._lock:
            now = asyncio.get_running_loop().time()
            for offset in range(len(self._clients)):
                idx = (self._index + offset) % len(self._clients)
                if self._cooldown[idx] <= now:
                    self._index = (idx + 1) % len(self._clients)
                    return self._clients[idx], idx
            # every key is cooling down: take the one that frees up earliest
            idx = min(range(len(self._clients)), key=lambda i: self._cooldown[i])
            self._index = (idx + 1) % len(self._clients)
            return self._clients[idx], idx

    async def execute(self, coro_factory, *, label: str = "request"):
        """coro_factory(client) -> awaitable. Retries with the next key on 429/5xx."""
        last_error: Exception | None = None
        for attempt in range(1, self._max_attempts + 1):
            client, key_idx = await self._next_client()
            try:
                return await coro_factory(client)
            except APIStatusError as exc:
                last_error = exc
                status = exc.status_code
                if status == 429 or status >= 500:
                    backoff = min(30.0, 1.5 * (2 ** (attempt - 1))) + random.uniform(0, 0.5)
                    async with self._lock:
                        self._cooldown[key_idx] = (
                            asyncio.get_running_loop().time() + backoff
                        )
                    print(
                        f"[rotate] key #{key_idx} bị {status} khi {label} "
                        f"-> nghỉ {backoff:.1f}s, thử key khác ({attempt}/{self._max_attempts})",
                        flush=True,
                    )
                    await asyncio.sleep(backoff)
                    continue
                raise
            except APIConnectionError as exc:
                last_error = exc
                print(
                    f"[rotate] lỗi mạng với key #{key_idx} khi {label}: "
                    f"{str(exc)[:80]} -> thử lại ({attempt}/{self._max_attempts})",
                    flush=True,
                )
                await asyncio.sleep(1.0 + random.uniform(0, 0.5))
        raise KeyRotationError(
            f"Hết lượt thử cho {label}: {type(last_error).__name__}: {last_error}"
        )


async def chat(client: AsyncOpenAI, model: str, messages: list[dict], **kwargs) -> str:
    response = await client.chat.completions.create(
        model=model, messages=messages, stream=False, **kwargs
    )
    choice = response.choices[0]
    msg = choice.message
    content = msg.content or ""
    if not content and getattr(msg, "reasoning_content", None):
        # reasoning model ran out of budget before emitting the answer
        raise KeyRotationError(
            f"Model {model} chỉ trả về reasoning, chưa có câu trả lời "
            f"(hết max_tokens={kwargs.get('max_tokens')}). Tăng --max-tokens."
        )
    return content.strip()


def nvidia_client(secrets: dict[str, str]) -> AsyncOpenAI:
    api_key = secrets.get("NVIDIA_API_KEY", "")
    if not api_key:
        raise SystemExit("Thiếu NVIDIA_API_KEY trong biến môi trường hoặc .agents/secrets.env")
    return AsyncOpenAI(
        base_url=secrets.get("NVIDIA_BASE_URL", DEFAULT_NVIDIA_BASE_URL), api_key=api_key
    )
