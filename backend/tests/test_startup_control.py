from dataclasses import replace

import pytest

from app.processes import OwnedProcess
from app.startup_control import ControlSelector, ControlSnapshot, select_startup_control


def test_selector_requires_window_and_control_identifier() -> None:
    with pytest.raises(ValueError):
        ControlSelector(window_title="窗口")
    with pytest.raises(ValueError):
        ControlSelector(window_title="", control_name="开始")


def test_selection_requires_unique_owned_enabled_invokable_button() -> None:
    owner = OwnedProcess(12, 100)
    control = ControlSnapshot(owner, "目标", "start", "开始", "Button", True, True, True)
    selector = ControlSelector("目标", "start", "开始")
    assert select_startup_control(owner, selector, [control]) == control
    with pytest.raises(RuntimeError):
        select_startup_control(owner, selector, [control, control])
    with pytest.raises(RuntimeError):
        select_startup_control(OwnedProcess(12, 101), selector, [control])
    for changed in (replace(control, enabled=False), replace(control, visible=False),
                    replace(control, invoke_supported=False), replace(control, control_type="Text")):
        with pytest.raises(RuntimeError):
            select_startup_control(owner, selector, [changed])
