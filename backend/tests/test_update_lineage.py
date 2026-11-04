from app.processes import OwnedProcess
from app.update_lineage import ProcessSnapshot, UpdateLineage


def test_only_observed_descendants_with_matching_path_are_candidates(tmp_path) -> None:
    exe = str(tmp_path / "MAA.exe")
    lineage = UpdateLineage(OwnedProcess(10, 100), exe)
    processes = [
        ProcessSnapshot(10, 100, 1, exe),
        ProcessSnapshot(11, 101, 10, str(tmp_path / "updater.exe")),
        ProcessSnapshot(12, 102, 11, exe),
        ProcessSnapshot(13, 102, 20, exe),
    ]
    assert lineage.observe(processes) == [OwnedProcess(12, 102)]


def test_reused_or_missing_parent_does_not_prove_ownership(tmp_path) -> None:
    exe = str(tmp_path / "MAA.exe")
    lineage = UpdateLineage(OwnedProcess(10, 100), exe)
    assert lineage.observe([ProcessSnapshot(10, 200, 1, exe), ProcessSnapshot(11, 201, 10, exe)]) == []
    assert lineage.observe([ProcessSnapshot(12, 202, 10, exe)]) == []


def test_multiple_verified_candidates_remain_ambiguous(tmp_path) -> None:
    exe = str(tmp_path / "MAA.exe")
    lineage = UpdateLineage(OwnedProcess(10, 100), exe)
    processes = [ProcessSnapshot(10, 100, 1, exe), ProcessSnapshot(11, 101, 10, exe), ProcessSnapshot(12, 102, 10, exe)]
    assert lineage.observe(processes) == [OwnedProcess(11, 101), OwnedProcess(12, 102)]


def test_parent_seen_alive_before_snapshot_can_prove_older_child(tmp_path) -> None:
    exe = str(tmp_path / "fixture.exe")
    root = OwnedProcess(10, 100)
    lineage = UpdateLineage(root, exe)
    child = ProcessSnapshot(11, 101, 10, exe)
    assert lineage.observe([child], {root: 102}) == [OwnedProcess(11, 101)]


def test_parent_proof_does_not_extend_to_newer_child(tmp_path) -> None:
    exe = str(tmp_path / "fixture.exe")
    root = OwnedProcess(10, 100)
    lineage = UpdateLineage(root, exe)
    assert lineage.observe([ProcessSnapshot(11, 103, 10, exe)], {root: 102}) == []


def test_updater_requires_verified_chain_full_path_and_creation_time(tmp_path) -> None:
    exe = str(tmp_path / 'MAA.exe')
    updater = str(tmp_path / 'MAA.Updater.exe')
    lineage = UpdateLineage(OwnedProcess(10, 100), exe)
    observations = [
        ProcessSnapshot(10, 100, 1, exe),
        ProcessSnapshot(11, 101, 10, updater),
        ProcessSnapshot(12, 102, 99, updater),
        ProcessSnapshot(13, 103, 10, str(tmp_path / 'other' / 'MAA.Updater.exe')),
    ]
    lineage.observe(observations)
    assert lineage.observed_executables(observations, updater, 100.5) == [OwnedProcess(11, 101)]
    assert lineage.observed_executables(observations, updater, 101.5) == []
