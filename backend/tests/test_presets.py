from __future__ import annotations

import pytest

from app.models import CompletionSource
from app.presets import PRESETS, apply_preset, public_presets


@pytest.mark.parametrize(
    ("preset_id", "executable"),
    [
        ("maa", r"C:\\tools\\MAA\\MAA.exe"),
        ("zzz_onedragon", r"C:\\tools\\ZZZ\\OneDragon-RuntimeLauncher.exe"),
    ],
)
def test_log_presets_keep_all_evidence_rules(preset_id: str, executable: str) -> None:
    task = apply_preset(preset_id, executable, task_id="task", name="")
    assert task.log_path
    assert task.success_log_rules
    assert task.failure_log_rules
    assert not task.allow_process_exit
    assert task.allow_manual_confirmation


@pytest.mark.parametrize(
    ("preset_id", "executable"),
    [
        ("maaend", r"C:\\tools\\MaaEnd.exe"),
        ("march7th", r"C:\\tools\\March7th Assistant.exe"),
        ("bettergi", r"C:\\tools\\BetterGI.exe"),
    ],
)
def test_manual_only_presets_do_not_claim_process_exit_success(preset_id: str, executable: str) -> None:
    task = apply_preset(preset_id, executable, task_id="task", name="")
    assert task.allow_manual_confirmation
    assert not task.allow_process_exit
    assert not task.has_success_log()


def test_preset_rejects_mismatched_executable() -> None:
    with pytest.raises(ValueError, match="不匹配"):
        apply_preset("maa", r"C:\\tools\\other.exe", task_id="task", name="MAA")


def test_public_metadata_has_all_requested_presets() -> None:
    assert {item["id"] for item in public_presets()} == set(PRESETS)
    assert CompletionSource.SUCCESS_LOG.value == "success_log"
