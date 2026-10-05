"""Exact-snapshot functional checks: real pointer events and negative acceptance."""
import json
import shutil
import tempfile
from pathlib import Path

from web_verifier_smoke import REPO, record, verify


def main():
    checks = REPO / "tests/cli/counter_checks.json"
    with tempfile.TemporaryDirectory(prefix="rna-web-checks-") as folder:
        root = Path(folder)
        workspace, state = root / "work", root / "state"
        shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", workspace)
        code, smoke = verify(workspace, state)
        assert code == 0 and smoke["checks"] == [], smoke
        code, broken = verify(workspace, state, checks=checks)
        assert code == 5 and broken["status"] == "failed", broken
        assert any("increment" in error for error in broken["errors"]), broken
        assert broken["checks"][2]["status"] == "Failed", broken
        assert all(step["status"] == "NotRun" for step in broken["checks"][3:]), broken
        saved_broken = record(workspace, state, broken["verificationId"])
        assert saved_broken["CheckResults"] == broken["checks"], saved_broken
        assert saved_broken["Checks"] == json.loads(checks.read_text()) | {
            "steps": [dict(step, expected=step.get("expected")) for step in json.loads(checks.read_text())["steps"]]
        }, saved_broken

        script = workspace / "app.js"
        script.write_text(script.read_text().replace("count += increment", "count += 1"))
        code, passed = verify(workspace, state, checks=checks)
        assert code == 0 and passed["status"] == "passed", passed
        assert len(passed["checks"]) == 9 and all(step["status"] == "Passed" for step in passed["checks"]), passed
        assert passed["snapshotSha256"] != broken["snapshotSha256"], passed
        saved_pass = record(workspace, state, passed["verificationId"])
        assert saved_pass["CheckResults"] == passed["checks"] and saved_pass["Checks"] == saved_broken["Checks"]

        # Historical verification must exercise old bytes after a successful repair.
        code, historical = verify(workspace, state, snapshot=broken["snapshotId"], checks=checks)
        assert code == 5 and historical["historical"], historical
        assert historical["snapshotSha256"] == broken["snapshotSha256"], historical
        assert "count += 1" in script.read_text()
        assert record(workspace, state, broken["verificationId"]) == saved_broken

        # A no-op handler has no JS exception, but must still fail text equality.
        script.write_text(script.read_text().replace("count += 1", "count += 0"))
        code, noop = verify(workspace, state, checks=checks)
        assert code == 5 and noop["checks"][2]["actual"] == "0", noop
        assert "Text differs" in noop["checks"][2]["error"], noop

        code, unavailable = verify(workspace, state, "/missing/chromium", checks=checks)
        assert code == 4 and all(step["status"] == "NotRun" for step in unavailable["checks"]), unavailable
        (workspace / "other.html").write_text("<p>Unrelated page</p>")
        code, wrong_entry = verify(workspace, state, entry="other.html", checks=checks)
        assert code == 5 and wrong_entry["browser"] is None and "Entry must match" in wrong_entry["errors"][0], wrong_entry

        # Duplicate selectors cannot silently choose a passing element.
        entry = workspace / "index.html"
        entry.write_text(entry.read_text().replace('<output id="count">0</output>',
            '<output id="count">0</output><output id="count">0</output>'))
        code, duplicate = verify(workspace, state, checks=checks)
        assert code == 5 and "exactly one" in duplicate["checks"][0]["error"], duplicate
        print("PASS exact functional checks: smoke false-positive, click exception/repair, no-op, historical bytes, missing browser, wrong entry, duplicate selector")


if __name__ == "__main__":
    main()
