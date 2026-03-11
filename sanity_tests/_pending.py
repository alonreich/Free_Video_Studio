from __future__ import annotations
import pytest

def pending_test(requirement_id: str, description: str) -> None:
    pytest.skip(f"[{requirement_id}] {description} (pending integration harness)")
