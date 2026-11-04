from app.maa_update_evidence import UpdateEvent
from app.processes import OwnedProcess
from app.update_supervisor import UpdateSupervisor


def test_update_never_completes_task_and_requires_confirmed_candidate() -> None:
    monitor = UpdateSupervisor(idle_seconds=10, total_seconds=60)
    monitor.observe_event(UpdateEvent("update_detected", "检测更新"), 0)
    monitor.observe_event(UpdateEvent("restart_requested", "请求重启"), 1)
    monitor.observe_event(UpdateEvent("expected_exit", "退出"), 2)
    assert monitor.choose_candidate([OwnedProcess(1, 3)]) is None
    monitor.observe_event(UpdateEvent("applying", "应用更新"), 3)
    monitor.observe_event(UpdateEvent("update_applied", "完成"), 4)
    assert monitor.status().stage == "awaiting_process"
    assert monitor.choose_candidate([OwnedProcess(1, 3)]) == OwnedProcess(1, 3)
    assert monitor.status().stage == "reattached"


def test_progress_renews_idle_but_not_total_deadline() -> None:
    monitor = UpdateSupervisor(idle_seconds=10, total_seconds=30)
    monitor.observe_event(UpdateEvent("update_detected", "检测更新"), 0)
    monitor.observe_event(UpdateEvent("restart_requested", "请求重启"), 0)
    for now in (8, 16, 24):
        monitor.progress(now)
        assert monitor.check_deadlines(now).stage != "needs_attention"
    assert monitor.check_deadlines(30).stage == "needs_attention"
    assert monitor.continue_waiting(31).stage == "restarting"
    assert monitor.check_deadlines(40).stage != "needs_attention"
    assert monitor.check_deadlines(41).stage == "needs_attention"


def test_ambiguous_candidates_are_never_adopted() -> None:
    monitor = UpdateSupervisor()
    monitor.observe_event(UpdateEvent("update_detected", "检测更新"), 0)
    monitor.observe_event(UpdateEvent("restart_requested", "请求重启"), 0)
    monitor.observe_event(UpdateEvent("update_applied", "完成"), 1)
    assert monitor.choose_candidate([OwnedProcess(10, 1), OwnedProcess(11, 1)]) is None
    assert monitor.status().stage == "needs_attention"


def test_unrelated_update_does_not_start_observation() -> None:
    monitor = UpdateSupervisor()
    monitor.observe_event(UpdateEvent("applying", "应用更新"), 0)
    assert monitor.status().stage == "normal"


def test_explicit_failure_stays_paused_despite_later_success() -> None:
    monitor = UpdateSupervisor()
    monitor.observe_event(UpdateEvent("update_detected", "检测更新"), 0)
    monitor.observe_event(UpdateEvent("restart_requested", "请求重启"), 1)
    monitor.observe_event(UpdateEvent("update_failed", "更新失败"), 2)
    monitor.observe_event(UpdateEvent("update_applied", "完成"), 3)
    assert monitor.status().stage == "needs_attention"
    assert monitor.choose_candidate([OwnedProcess(1, 3)]) is None


def test_second_update_starts_a_fresh_deadline_and_requires_new_success() -> None:
    monitor = UpdateSupervisor(idle_seconds=10, total_seconds=30)
    monitor.observe_event(UpdateEvent("update_detected", "检测更新"), 0)
    monitor.observe_event(UpdateEvent("restart_requested", "请求重启"), 1)
    monitor.observe_event(UpdateEvent("update_applied", "完成"), 2)
    assert monitor.choose_candidate([OwnedProcess(10, 2)]) is not None
    monitor.observe_event(UpdateEvent("update_detected", "再次检测更新"), 100)
    monitor.observe_event(UpdateEvent("restart_requested", "再次请求重启"), 101)
    assert monitor.choose_candidate([OwnedProcess(11, 101)]) is None
    assert monitor.check_deadlines(102).stage == "restarting"
