"""Check that CLI capabilities reflect browser absence and never advertise Office."""

import json
import os
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with TemporaryDirectory(prefix="rna-capability-") as folder:
        root = Path(folder)
        workspace = root / "workspace"
        workspace.mkdir()
        env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                   RNA_BROWSER_EXECUTABLE="/missing/chromium")

        def cli(*args):
            result = subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                    cwd=REPO, env=env, text=True, capture_output=True,
                                    timeout=20, check=False)
            return result, json.loads(result.stdout) if result.stdout.strip() else None

        listed, output = cli("env", "--workspace", str(workspace))
        assert listed.returncode == 0, listed.stderr
        capabilities = output["data"]["capabilities"]
        assert "common.resources_find" in capabilities and "common.resources_read" in capabilities
        assert "files.create" in capabilities and "files.patch" in capabilities
        assert "web.verify" not in capabilities, capabilities
        assert not any("excel" in tool.lower() or "vba" in tool.lower() or
                       "outlook" in tool.lower() or "powerpoint" in tool.lower() or
                       "word" in tool.lower() for tool in capabilities), capabilities

        (workspace / "index.html").write_text("<!doctype html><title>Offline</title>", encoding="utf-8")
        unavailable, result = cli("verify", "--workspace", str(workspace))
        assert unavailable.returncode == 4 and result["data"]["status"] == "not-run", result

        required, output = cli("run", "--workspace", str(workspace), "--message", "Create index.html",
                               "--require-web-verify")
        assert required.returncode == 4 and output is None, (required.stdout, required.stderr)
        sessions, result = cli("sessions", "--workspace", str(workspace))
        assert sessions.returncode == 0 and result["data"]["sessions"] == [], result
        print("PASS CLI capability snapshot: no Office/browser tool; required browser rejected before model/session")


if __name__ == "__main__":
    main()
