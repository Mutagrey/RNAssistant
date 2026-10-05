"""Optional real-Chromium smoke for the independent workspace CLI.

Build RNAssistant.Cli first, then run this file with Python 3. No Python packages
or user browser profile are used; the CLI verifier owns its isolated profile.
"""

import hashlib
import json
import os
import subprocess
import tempfile
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def verify(workspace, state, browser=None, snapshot=None, entry=None, checks=None):
    env = dict(os.environ, RNA_STATE_ROOT=str(state))
    if browser is not None:
        env["RNA_BROWSER_EXECUTABLE"] = browser
    args = ["dotnet", str(CLI), "verify", "--workspace", str(workspace), "--jsonl"]
    if snapshot:
        args += ["--snapshot", snapshot]
    if entry:
        args += ["--entry", entry]
    if checks:
        args += ["--web-checks", str(checks)]
    completed = subprocess.run(
        args,
        cwd=REPO,
        env=env,
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )
    if not completed.stdout.strip():
        raise AssertionError(completed.stderr)
    return completed.returncode, json.loads(completed.stdout)["data"]


def record(workspace, state, verification_id=None):
    args = ["dotnet", str(CLI), "verification" if verification_id else "verifications",
            "--workspace", str(workspace), "--jsonl"]
    if verification_id:
        args += ["--id", verification_id]
    completed = subprocess.run(
        args,
        cwd=REPO, env=dict(os.environ, RNA_STATE_ROOT=str(state)),
        capture_output=True, text=True, timeout=10, check=True,
    )
    return json.loads(completed.stdout)["data"]


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with tempfile.TemporaryDirectory(prefix="rna-web-smoke-") as folder:
        root = Path(folder)
        workspace = root / "work"
        workspace.mkdir()
        state = root / "state"
        (workspace / "index.html").write_bytes(
            ('\ufeff<!doctype html>\r\n<link rel="stylesheet" href="styles.css">'
             '<button id="go">Go</button><script src="app.js"></script>\r\n').encode("utf-8")
        )
        (workspace / "styles.css").write_text("body {color:black}", encoding="utf-8")
        script = workspace / "app.js"
        script.write_text('document.getElementById("go").textContent="Ready";', encoding="utf-8")
        code, result = verify(workspace, state)
        assert code == 0 and result["status"] == "passed", result
        assert result["checkedFiles"] == ["app.js", "index.html", "styles.css"]
        hashes = {name: hashlib.sha256((workspace / name).read_bytes()).hexdigest()
                  for name in result["checkedFiles"]}
        manifest = "\n".join(f"{name} {digest}" for name, digest in hashes.items())
        assert result["snapshotSha256"] == hashlib.sha256(manifest.encode("utf-8")).hexdigest(), result
        original = result
        assert not original["historical"] and original["snapshotId"] and original["verificationId"], original
        saved = record(workspace, state, original["verificationId"])
        assert saved["State"] == "Passed" and saved["SnapshotId"] == original["snapshotId"], saved
        listing = record(workspace, state)
        assert listing["Total"] == 1 and listing["NextOffset"] is None, listing
        assert listing["Items"][0]["Id"] == saved["Id"], listing

        # Another process reopens exact bytes while current paths are absent or changed.
        entry_bytes = (workspace / "index.html").read_bytes()
        script_bytes = script.read_bytes()
        script.write_text('throw new Error("CURRENT_SOURCE_MUST_NOT_RUN");', encoding="utf-8")
        script.rename(workspace / "renamed.js")
        (workspace / "index.html").unlink()
        code, result = verify(workspace, state, snapshot=original["snapshotId"])
        assert code == 0 and result["status"] == "passed" and result["historical"], result
        assert result["snapshotSha256"] == original["snapshotSha256"], result
        assert not script.exists() and not (workspace / "index.html").exists()
        assert "CURRENT_SOURCE_MUST_NOT_RUN" in (workspace / "renamed.js").read_text()
        historical = record(workspace, state, result["verificationId"])
        assert historical["Historical"] and historical["SnapshotId"] == original["snapshotId"], historical
        assert record(workspace, state, original["verificationId"]) == saved, "earlier result was rewritten"
        (workspace / "index.html").write_bytes(entry_bytes)
        script.write_bytes(script_bytes)

        # The live source still exists. Verification must fail when its retained
        # exact payload disappears instead of quietly serving the current file.
        digest = hashes["app.js"]
        payload = state / "chat-blobs" / digest[:2] / f"{digest}.blob"
        payload.unlink()
        code, result = verify(workspace, state)
        assert code == 5 and result["status"] == "failed", result
        assert result["browser"] is None, result
        assert any("Cannot read app.js" in error and "payload" in error for error in result["errors"]), result
        assert record(workspace, state, result["verificationId"])["State"] == "Failed"
        code, result = verify(workspace, state, snapshot=original["snapshotId"])
        assert code == 5 and result["status"] == "failed" and result["browser"] is None, result
        assert any("payload" in error for error in result["errors"]), result

        script.write_text('throw new Error("BROWSER_SMOKE_FAILURE");', encoding="utf-8")
        code, result = verify(workspace, state)
        assert code == 5 and result["status"] == "failed", result
        assert any("BROWSER_SMOKE_FAILURE" in error for error in result["errors"]), result

        script.write_text('setTimeout(() => { throw new Error("DELAYED_BROWSER_FAILURE"); }, 650);', encoding="utf-8")
        code, result = verify(workspace, state)
        assert code == 5 and result["status"] == "failed", result
        assert any("DELAYED_BROWSER_FAILURE" in error for error in result["errors"]), result

        script.write_text('document.getElementById("missing").textContent="Ready";', encoding="utf-8")
        code, result = verify(workspace, state)
        assert code == 5 and result["status"] == "failed", result
        assert any("#missing" in hint for hint in result["hints"]), result

        (workspace / "styles.css").unlink()
        code, result = verify(workspace, state)
        assert code == 5 and result["status"] == "failed", result
        assert any("styles.css" in error for error in result["errors"]), result

        (workspace / "styles.css").write_text("body {color:black}", encoding="utf-8")
        code, result = verify(workspace, state, "/missing/chromium")
        assert code == 4 and result["status"] == "not-run", result
        assert record(workspace, state, result["verificationId"])["State"] == "NotRun"
        # A retained semantic path must not be URL-decoded for a second time.
        (workspace / "literal%20.html").write_text("<!doctype html><h1>Exact path</h1>", encoding="utf-8")
        code, result = verify(workspace, state, "/missing/chromium", entry="literal%2520.html")
        assert code == 4 and result["entryPath"] == "literal%20.html" and result["snapshotId"], result
        code, reopened = verify(workspace, state, "/missing/chromium", snapshot=result["snapshotId"])
        assert code == 4 and reopened["entryPath"] == "literal%20.html", reopened
        print("PASS workspace CLI web verifier: durable results, historical restart, exact BOM/CRLF snapshot, missing retained payload, "
              "immediate/delayed error, DOM hint, missing asset, not-run")


if __name__ == "__main__":
    main()
