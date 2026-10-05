"""Scripted HTTP model exercises the production runtime's functional acceptance.

This checks orchestration/guards, not real-model reasoning quality.
"""
import json
import os
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from web_verifier_smoke import CLI, REPO, record


def run_case(root, change_after_verify):
    workspace, state, checks = root / "work", root / "state", root / "checks.json"
    shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", workspace)
    shutil.copyfile(REPO / "tests/cli/counter_checks.json", checks)
    requests = []

    def call(name, arguments):
        return {"message": "Run the accepted counter checks.", "action": "tool",
                "tool_calls": [{"name": name, "arguments": arguments}]}

    responses = [
        call("web.verify", {"entryPath": "index.html"}),
        call("common.resources_read", {"target": "app.js"}),
        call("files.patch", {"relativePath": "app.js", "oldText": "count += increment", "newText": "count += 1"}),
        call("common.resources_read", {"target": "app.js"}),
        call("web.verify", {"entryPath": "index.html"}),
        {"message": "Verified repair.", "action": "done", "tool_calls": []},
    ]

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_POST(self):
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            index = len(requests) - 1
            assert self.path == "/v1/chat/completions" and index < len(responses)
            if index == 1:
                checks.write_text("{}")  # The admitted contract must not be reread.
            if index == 5 and change_after_verify:
                script = workspace / "app.js"
                script.write_text(script.read_text().replace("count += 1", "count += 0"))
            body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(responses[index])}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                   RNA_MODEL="scripted", RNA_API_KEY="test")
        run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--task-file", str(REPO / "tests/cli/counter_repair_task.md"), "--web-checks", str(checks),
            "--min-reads", "1", "--min-writes", "1", "--expect-files", "index.html,styles.css,app.js",
            "--max-iterations", "8", "--max-tool-steps", "8", "--jsonl"],
            cwd=REPO, env=env, capture_output=True, text=True, timeout=90, check=False)
        assert run.stdout.strip(), run.stderr
        result = json.loads(run.stdout.splitlines()[-1])["data"]
        assert run.returncode == (5 if change_after_verify else 0), (run.stdout, run.stderr)
        assert result["acceptance"] == ("failed" if change_after_verify else "passed"), result
        assert result["webSnapshotVerified"] == (not change_after_verify), result
        assert result["webCheckCount"] == 9 and len(requests) == 6, result
        assert "rna://" not in json.dumps(requests), "Runtime references leaked into the model context"
        saved = record(workspace, state)["Items"]
        assert len(saved) == 2 and {item["State"] for item in saved} == {"Passed", "Failed"}, saved
        assert all(len(item["Checks"]["steps"]) == 9 for item in saved), saved
        passed = next(item for item in saved if item["State"] == "Passed")
        assert all(step["status"] == "Passed" for step in passed["CheckResults"]), passed
        assert passed["Origin"]["RunId"] == result["RunId"], passed
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def main():
    with tempfile.TemporaryDirectory(prefix="rna-checks-runtime-") as folder:
        root = Path(folder)
        for name, changed in [("repair", False), ("stale", True)]:
            (root / name).mkdir()
            run_case(root / name, changed)
    print("PASS scripted production runtime: verify/read/repair/read/verify/done, frozen checks, stale final evidence rejected")


if __name__ == "__main__":
    main()
