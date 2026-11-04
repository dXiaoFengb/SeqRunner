import pytest

from app.update_rules import UpdateRuleReader, UpdateRules


def rules() -> UpdateRules:
    return UpdateRules("check", "download", "apply", "restart", "failed", "ready")


def test_explicit_rules_handle_split_lines_and_do_not_presume_generic_exit() -> None:
    reader = UpdateRuleReader(rules())
    assert reader.feed("che") == []
    events = reader.feed("ck\ndownload\nrestart\nprocess exited\nready\n")
    assert [event.kind for event in events] == ["update_detected", "downloading", "restart_requested", "update_applied"]


def test_failure_has_priority_and_reset_drops_partial_lines() -> None:
    reader = UpdateRuleReader(rules())
    assert reader.feed("ready failed\n")[0].kind == "update_failed"
    reader.feed("rest")
    reader.reset()
    assert reader.feed("art\n") == []


def test_rules_reject_empty_and_duplicate_signals() -> None:
    with pytest.raises(ValueError):
        UpdateRules("", "download", "apply", "restart", "failed", "ready")
    with pytest.raises(ValueError):
        UpdateRules("same", "download", "apply", "restart", "failed", "same")
