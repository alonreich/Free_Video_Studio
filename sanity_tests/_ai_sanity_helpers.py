from __future__ import annotations
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]

def read_source(rel_path: str) -> str:
    return (ROOT / rel_path).read_text(encoding="utf-8")

def assert_all_present(text: str, needles: list[str]) -> None:
    missing = [n for n in needles if n not in text]
    assert not missing, f"Missing expected snippets: {missing}"
