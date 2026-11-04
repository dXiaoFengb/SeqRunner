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
