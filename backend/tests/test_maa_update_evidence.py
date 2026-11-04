from app.maa_update_evidence import MaaUpdateEvidence


def test_restart_and_update_sequence_survives_fragmented_lines() -> None:
    parser = MaaUpdateEvidence()
    assert parser.feed("[INF][VersionUpdateDialogViewModel] New version found: fixture\n")[0].kind == "update_detected"
    assert parser.feed("[INF][Bootstrapper] Shutdown and restart with custom Start") == []
    events = parser.feed("Info, call by `ShutdownAndRestartWithArgs`\n[INF][Bootstrapper] MaaAssistantArknights GUI exited\n")
    assert [event.kind for event in events] == ["restart_requested", "expected_exit"]
    assert parser.feed("[INF][Bootstrapper] Pending update package detected, applying before full startup\n")[0].kind == "applying"
    assert parser.feed("[INF][Bootstrapper] Delegated pending update completed successfully\n")[0].kind == "update_applied"


def test_unrelated_update_and_exit_are_not_reclassified() -> None:
    parser = MaaUpdateEvidence()
    assert parser.feed("[INF][ResourceUpdater] New version found: resource update\n") == []
    assert parser.feed("[INF][Bootstrapper] MaaAssistantArknights GUI exited\n")[0].kind == "unexpected_exit"


def test_partial_line_buffer_is_bounded() -> None:
    parser = MaaUpdateEvidence()
    assert parser.feed("x" * 100_000) == []
    assert len(parser._pending) == 65536


def test_new_observation_does_not_inherit_an_old_restart() -> None:
    parser = MaaUpdateEvidence()
    parser.feed("[INF][VersionUpdateDialogViewModel] New version found: fixture\n")
    parser.feed("[INF][Bootstrapper] Shutdown and restart with custom StartInfo\n")
    parser.reset()
    assert parser.feed("[INF][Bootstrapper] MaaAssistantArknights GUI exited\n")[0].kind == "unexpected_exit"


def test_restart_without_update_evidence_is_not_classified_as_update() -> None:
    parser = MaaUpdateEvidence()
    assert parser.feed("[INF][Bootstrapper] Shutdown and restart with custom StartInfo\n") == []
    assert parser.feed("[INF][Bootstrapper] MaaAssistantArknights GUI exited\n")[0].kind == "unexpected_exit"


def test_restarted_gui_is_observation_not_update_completion() -> None:
    from app.update_supervisor import UpdateSupervisor

    parser = MaaUpdateEvidence()
    monitor = UpdateSupervisor()
    for event in parser.feed('[VersionUpdateDialogViewModel] New version found: fixture\n'
                             '[Bootstrapper] Shutdown and restart with custom StartInfo\n'
                             '[Bootstrapper] MaaAssistantArknights GUI started\n'):
        monitor.observe_event(event, 1.0)
    assert monitor.stage == 'restarting'
    assert not monitor.update_applied
    assert '界面启动' in monitor.message


def test_updater_log_observations_are_not_success_proof() -> None:
    from app.maa_update_evidence import MaaUpdaterLogEvidence

    parser = MaaUpdaterLogEvidence()
    assert parser.feed('Wrote success status ') == []
    kinds = [event.kind for event in parser.feed('file: fixture\nRelaunch succeeded.\nUpdate failed: fixture\n')]
    assert kinds == ['updater_success_observed', 'updater_relaunch_observed', 'updater_failure_observed']
    parser.feed('Relaunch suc')
    parser.clear_partial()
    assert parser.feed('ceeded.\n') == []
