"""Optional real-Chromium smoke for the independent workspace CLI.

Build RNAssistant.Cli first, then run this file with Python 3. No Python packages
or user browser profile are used; the CLI verifier owns its isolated profile.
"""

import json
import os
import subprocess
import tempfile
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def verify(workspace, state, browser=None):
    env = dict(os.environ, RNA_STATE_ROOT=str(state))
    if browser is not None:
        env["RNA_BROWSER_EXECUTABLE"] = browser
    completed = subprocess.run(
        ["dotnet", str(CLI), "verify", "--workspace", str(workspace), "--jsonl"],
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


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with tempfile.TemporaryDirectory(prefix="rna-web-smoke-") as folder:
        root = Path(folder)
        workspace = root / "work"
        workspace.mkdir()
        state = root / "state"
        (workspace / "index.html").write_text(
            '<!doctype html><link rel="stylesheet" href="styles.css">'
            '<button id="go">Go</button><script src="app.js"></script>', encoding="utf-8"
        )
        (workspace / "styles.css").write_text("body {color:black}", encoding="utf-8")
        script = workspace / "app.js"
        script.write_text('document.getElementById("go").textContent="Ready";', encoding="utf-8")
        code, result = verify(workspace, state)
        assert code == 0 and result["status"] == "passed", result
        assert result["checkedFiles"] == ["app.js", "index.html", "styles.css"]

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
        print("PASS workspace CLI web verifier: passed, immediate/delayed error, DOM hint, missing asset, not-run")


if __name__ == "__main__":
    main()
