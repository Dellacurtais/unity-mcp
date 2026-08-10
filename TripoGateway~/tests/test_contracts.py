from __future__ import annotations

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from tripo_gateway.config import GatewaySettings, normalize_api_base_url
from tripo_gateway.models import JobCreateRequest
from tripo_gateway.security import canonical_json, redact, sha256_json


FIXTURES = Path(__file__).parent / "fixtures"


def valid_request() -> dict:
    return {
        "project_key": "project:test",
        "operation": "text_to_model",
        "plan_hash": "a" * 64,
        "payload": {"prompt": "synthetic cube", "model": "v3.1-20260211"},
        "budget": {"estimated_credits": 100, "cap_credits": 120},
        "disclosure": {
            "external_send_approved": True,
            "kinds": ["text"],
            "source_hashes": [],
        },
        "auto_download": True,
    }


def test_contract_rejects_extra_fields_and_invalid_budget() -> None:
    payload = valid_request()
    payload["unexpected"] = True
    with pytest.raises(ValidationError):
        JobCreateRequest.model_validate(payload)


def test_contract_requires_operation_specific_minimums_and_disclosure() -> None:
    payload = valid_request()
    payload["operation"] = "convert"
    payload["payload"] = {"input": "task_source"}
    payload["disclosure"]["kinds"] = ["task_reference"]
    with pytest.raises(ValidationError, match="supported format"):
        JobCreateRequest.model_validate(payload)

    payload["payload"]["format"] = "FBX"
    assert JobCreateRequest.model_validate(payload).operation == "convert"

    payload = valid_request()
    payload["budget"] = {"estimated_credits": 0, "cap_credits": 0}
    with pytest.raises(ValidationError, match="positive"):
        JobCreateRequest.model_validate(payload)

    payload = valid_request()
    payload["budget"] = {"estimated_credits": 121, "cap_credits": 120}
    with pytest.raises(ValidationError):
        JobCreateRequest.model_validate(payload)


def test_canonical_hash_is_deterministic_and_secrets_are_redacted() -> None:
    left = {"b": [2, 1], "a": {"x": "ç"}}
    right = {"a": {"x": "ç"}, "b": [2, 1]}
    assert canonical_json(left) == canonical_json(right)
    assert sha256_json(left) == sha256_json(right)
    assert redact({"api_key": "secret", "nested": {"token": "secret", "ok": 1}}) == {
        "api_key": "[REDACTED]",
        "nested": {"token": "[REDACTED]", "ok": 1},
    }


def test_official_base_url_is_enforced(tmp_path: Path) -> None:
    assert normalize_api_base_url("https://openapi.tripo3d.ai/v3") == "https://openapi.tripo3d.ai/v3"
    with pytest.raises(ValueError):
        normalize_api_base_url("http://openapi.tripo3d.ai/v3")
    with pytest.raises(ValueError):
        normalize_api_base_url("https://example.com/v3")
    with pytest.raises(ValueError):
        GatewaySettings(data_dir=tmp_path, gateway_token="")


@pytest.mark.parametrize("name", ["balance.json", "task_success.json", "task_failed.json", "rate_limit.json"])
def test_phase_zero_fixtures_are_valid_json_objects(name: str) -> None:
    value = json.loads((FIXTURES / name).read_text("utf-8"))
    assert isinstance(value, dict)
    assert "code" in value
