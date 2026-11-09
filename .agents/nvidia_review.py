"""Cross-check a diff with NVIDIA Llama 3.3 70B: security + logic passes in parallel.

Usage:
    python .agents/nvidia_review.py --diff-file tmp/pr.diff
    git diff | python .agents/nvidia_review.py --mode security

Options: --mode both|security|logic (default both), --model, --max-tokens
Output sections are delimited so the calling agent can parse them.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from llm_client import (  # noqa: E402
    DEFAULT_NVIDIA_MODEL,
    chat,
    load_secrets,
    nvidia_client,
)

MAX_DIFF_CHARS = 60_000

PASSES = {
    "security": (
        "Bạn là chuyên gia bảo mật ứng dụng. Soi lỗ hổng BẢO MẬT trong diff dưới đây: "
        "SQL/NoSQL injection, thiếu kiểm tra AuthN/AuthR, lộ secret/key, XSS, SSRF, "
        "deserialization không an toàn, logging dữ liệu nhạy cảm. "
        "Trả lời bằng tiếng Việt, mỗi phát hiện theo mẫu: "
        "[MỨC ĐỘ: Blocking/Nên sửa/Nhẹ] file:dòng - vấn đề - cách sửa cụ thể. "
        "Nếu không thấy vấn đề, ghi rõ 'Không phát hiện lỗ hổng bảo mật.'"
    ),
    "logic": (
        "Bạn là reviewer code khó tính. Soi lỗi LOGIC trong diff dưới đây: "
        "corner case chưa xử lý, race condition, null/unhandled exception, memory leak, "
        "resource chưa dispose, transaction sai, contract event giữa các service lệch nhau, "
        "test thiếu bao phủ nhánh lỗi. "
        "Trả lời bằng tiếng Việt, mỗi phát hiện theo mẫu: "
        "[MỨC ĐỘ: Blocking/Nên sửa/Nhẹ] file:dòng - vấn đề - cách sửa cụ thể. "
        "Nếu không thấy vấn đề, ghi rõ 'Không phát hiện lỗi logic.'"
    ),
}


async def run(args) -> int:
    diff = (
        Path(args.diff_file).read_text(encoding="utf-8", errors="replace")
        if args.diff_file
        else sys.stdin.read()
    )
    diff = diff.strip()
    if not diff:
        print("Diff rỗng — không có gì để review.", file=sys.stderr)
        return 2
    if len(diff) > MAX_DIFF_CHARS:
        print(
            f"[warn] diff dài {len(diff)} ký tự, cắt còn {MAX_DIFF_CHARS}",
            file=sys.stderr,
        )
        diff = diff[:MAX_DIFF_CHARS]

    secrets = load_secrets()
    client = nvidia_client(secrets)
    model = args.model or secrets.get("NVIDIA_MODEL", DEFAULT_NVIDIA_MODEL)
    modes = ["security", "logic"] if args.mode == "both" else [args.mode]

    async def one(mode: str):
        text = await chat(
            client,
            model,
            [
                {"role": "system", "content": PASSES[mode]},
                {"role": "user", "content": f"```diff\n{diff}\n```"},
            ],
            max_tokens=args.max_tokens,
        )
        return mode, text

    results = await asyncio.gather(*(one(m) for m in modes), return_exceptions=True)

    exit_code = 0
    for item in results:
        if isinstance(item, Exception):
            print(f"=== REVIEW_ERROR ===\n{type(item).__name__}: {item}")
            exit_code = 1
            continue
        mode, text = item
        print(f"=== {mode.upper()}_FEEDBACK ===")
        print(text)
        print()
    return exit_code


def parse_args():
    parser = argparse.ArgumentParser(description="Review chéo bằng NVIDIA Llama")
    parser.add_argument("--diff-file", help="file chứa diff (nên dùng thay cho stdin)")
    parser.add_argument("--mode", choices=["both", "security", "logic"], default="both")
    parser.add_argument("--model", help="model id, mặc định lấy từ NVIDIA_MODEL")
    parser.add_argument("--max-tokens", type=int, default=8000)
    return parser.parse_args()


if __name__ == "__main__":
    raise SystemExit(asyncio.run(run(parse_args())))
