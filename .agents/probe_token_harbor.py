"""Probe Token Harbor: verify base URL, list models, confirm the DeepSeek model id.

Run:  python .agents/probe_token_harbor.py
Reads keys from .agents/secrets.env (or env vars). Prints key *labels* only, never values.
"""

from __future__ import annotations

import asyncio
import json
import sys

from openai import APIStatusError, AsyncOpenAI

from llm_client import (
    DEFAULT_DEEPSEEK_MODEL,
    DEFAULT_TOKEN_HARBOR_BASE_URL,
    load_secrets,
    token_harbor_keys,
)

CANDIDATE_BASE_URLS = [
    DEFAULT_TOKEN_HARBOR_BASE_URL,
    "https://api.token-harbor.com/v1",
    "https://tokenharbor.com/v1",
    "https://api.tokenharbor.ai/v1",
]


async def probe_base_url(base_url: str, keys: list[str]) -> dict:
    result = {"base_url": base_url, "ok": False, "models": [], "errors": []}
    for idx, key in enumerate(keys):
        client = AsyncOpenAI(base_url=base_url, api_key=key, timeout=20.0, max_retries=0)
        try:
            data = await client.models.list()
        except APIStatusError as exc:
            snippet = str(exc)[:160].replace(key, f"<key#{idx}>")
            result["errors"].append(f"key #{idx} [{exc.status_code}]: {snippet}")
            continue
        except Exception as exc:  # DNS / TLS / timeout
            result["errors"].append(f"key #{idx} [{type(exc).__name__}]: {str(exc)[:160]}")
            continue
        ids = sorted({getattr(m, "id", "") for m in getattr(data, "data", []) if getattr(m, "id", "")})
        result["ok"] = True
        result["models"] = ids
        result["working_key"] = idx
        return result
    return result


async def main() -> int:
    secrets = load_secrets()
    keys = token_harbor_keys(secrets)
    configured = secrets.get("TOKEN_HARBOR_BASE_URL", "")
    urls = ([configured] if configured else []) + [u for u in CANDIDATE_BASE_URLS if u != configured]

    print(f"Số key trong pool: {len(keys)}\n")
    results = []
    for url in urls:
        print(f"--- Đang thử {url}")
        res = await probe_base_url(url, keys)
        results.append(res)
        if res["ok"]:
            print(f"    OK với key #{res['working_key']}. Tổng số model: {len(res['models'])}")
            matches = [m for m in res["models"] if "deepseek" in m.lower()]
            print(f"    Model khớp 'deepseek': {json.dumps(matches, ensure_ascii=False)}")
            break
        for err in res["errors"]:
            print(f"    FAIL {err}")

    working = next((r for r in results if r["ok"]), None)
    if not working:
        print("\nKẾT LUẬN: không base URL nào trả về danh sách model.")
        print("Kiểm tra lại TOKEN_HARBOR_BASE_URL và key trong .agents/secrets.env.")
        return 1

    models = working["models"]
    guess = DEFAULT_DEEPSEEK_MODEL if DEFAULT_DEEPSEEK_MODEL in models else next(
        (m for m in models if "deepseek" in m.lower()), models[0] if models else ""
    )
    print("\n=== CHÉP CÁC GIÁ TRỊ NÀY VÀO .agents/secrets.env ===")
    print(f"TOKEN_HARBOR_BASE_URL={working['base_url']}")
    print(f"DEEPSEEK_MODEL={guess}")
    print("\nDanh sách model đầy đủ:")
    print(json.dumps(models, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
    raise SystemExit(asyncio.run(main()))
