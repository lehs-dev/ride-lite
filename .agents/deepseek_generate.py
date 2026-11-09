"""Generate code with DeepSeek via Token Harbor, rotating across API keys.

Usage:
    python .agents/deepseek_generate.py --prompt-file tmp/prompt.md
    cat prompt.txt | python .agents/deepseek_generate.py

Options: --system "...", --max-tokens N, --temperature X
Prints the model output to stdout; diagnostics go to stderr.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from llm_client import (  # noqa: E402
    DEFAULT_DEEPSEEK_MODEL,
    AsyncKeyRotator,
    chat,
    load_secrets,
    token_harbor_keys,
)

DEFAULT_SYSTEM = (
    "Bạn là Senior Developer cho một solution .NET microservices (C#) kèm frontend JS thuần. "
    "Chỉ trả về mã nguồn hoàn chỉnh, đặt trong fenced code block có ghi tên file đích ở dòng đầu "
    "dưới dạng // path: relative/File.cs. Không giải thích dài dòng."
)


async def run(args) -> int:
    if args.prompt_file:
        prompt = Path(args.prompt_file).read_text(encoding="utf-8")
    else:
        prompt = sys.stdin.read()
    prompt = prompt.strip()
    if not prompt:
        print("Prompt rỗng.", file=sys.stderr)
        return 2

    secrets = load_secrets()
    rotator = AsyncKeyRotator(token_harbor_keys(secrets), secrets_base(secrets))
    model = args.model or secrets.get("DEEPSEEK_MODEL", DEFAULT_DEEPSEEK_MODEL)

    async def call(client):
        return await chat(
            client,
            model,
            [
                {"role": "system", "content": args.system},
                {"role": "user", "content": prompt},
            ],
            max_tokens=args.max_tokens,
            temperature=args.temperature,
        )

    try:
        output = await rotator.execute(call, label=f"deepseek generate ({model})")
    except Exception as exc:
        print(f"Lỗi: {exc}", file=sys.stderr)
        return 1

    sys.stdout.buffer.write(output.encode("utf-8"))
    sys.stdout.buffer.flush()
    return 0


def secrets_base(secrets) -> str:
    from llm_client import DEFAULT_TOKEN_HARBOR_BASE_URL

    return secrets.get("TOKEN_HARBOR_BASE_URL", DEFAULT_TOKEN_HARBOR_BASE_URL)


def parse_args():
    parser = argparse.ArgumentParser(description="Sinh code bằng DeepSeek/Token Harbor")
    parser.add_argument("--prompt-file", help="đường dẫn file chứa prompt (nên dùng thay cho stdin)")
    parser.add_argument("--model", help="model id, mặc định lấy từ DEEPSEEK_MODEL")
    parser.add_argument("--system", default=DEFAULT_SYSTEM)
    parser.add_argument("--max-tokens", type=int, default=8000)
    parser.add_argument("--temperature", type=float, default=0.2)
    return parser.parse_args()


if __name__ == "__main__":
    raise SystemExit(asyncio.run(run(parse_args())))
