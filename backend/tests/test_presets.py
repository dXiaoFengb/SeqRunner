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
    assert task.timeout_seconds == 600
    assert task.success_log is None
    assert task.failure_log is None


def test_preset_keeps_custom_name_but_names_default_task() -> None:
    default_named = apply_preset("maa", r"C:\\tools\\MAA\\MAA.exe", task_id="task", name="")
    custom_named = apply_preset("maa", r"C:\\tools\\MAA\\MAA.exe", task_id="task", name="早班 MAA")
    assert default_named.name == "MAA"
    assert custom_named.name == "早班 MAA"


def test_maa_and_zzz_rules_match_local_reference_evidence() -> None:
    maa = apply_preset("maa", r"C:\\tools\\MAA\\MAA.exe", task_id="task", name="")
    zzz = apply_preset("zzz_onedragon", r"C:\\tools\\ZZZ\\OneDragon-RuntimeLauncher.exe", task_id="task", name="")
    assert maa.log_path.endswith(r"debug\gui.log")
    assert [rule.pattern for rule in maa.success_log_rules] == ["任务已全部完成！"]
    assert "MaaAssistantArknights GUI exited" in [rule.pattern for rule in maa.failure_log_rules]
    assert zzz.arguments == ["--onedragon"]
    assert zzz.log_path.endswith(r".log\log.txt")
    assert [rule.pattern for rule in zzz.success_log_rules] == ["指令[ 一条龙 ] 执行成功"]
    assert [rule.pattern for rule in zzz.failure_log_rules] == [
        "未找到有效的实例",
        "请先结束其他运行中的功能 再启动",
        "运行应用 one_dragon 失败",
        "指令[ 一条龙 ] 执行失败",
    ]


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
    assert next(item for item in public_presets() if item["id"] == "maa")["requiresLogRules"]
    assert not next(item for item in public_presets() if item["id"] == "maaend")["requiresLogRules"]
    assert CompletionSource.SUCCESS_LOG.value == "success_log"
