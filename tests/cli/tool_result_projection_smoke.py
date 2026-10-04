"""Exercise current-turn file and browser results at the CLI model boundary."""

import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    responses = [
        {"message": "Find workspace files.", "action": "tool", "tool_calls": [
            {"name": "common.resources_find", "arguments": {"directory": "", "query": "app"}}]},
        {"message": "Read the current source.", "action": "tool", "tool_calls": [
            {"name": "common.resources_read", "arguments": {"target": "app.js"}}]},
        {"message": "Check the browser error.", "action": "tool", "tool_calls": [
            {"name": "web.verify", "arguments": {"entryPath": "index.html"}}]},
        {"invalid_protocol": "Exercise repair from the frozen context."},
        {"message": "Browser error remains visible.", "action": "blocked", "tool_calls": []},
    ]

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            index = len(requests) - 1
            assert index < len(responses), "Unexpected extra model request"
            if index == 2:
                # Change source after the model saw the exact read. The next
                # request must invalidate that body against frozen authority.
                (workspace / "app.js").write_text(
                    'throw new Error("CHANGED_AFTER_READ");', encoding="utf-8")
            if index == 3:
                (workspace / "app.js").write_text(
                    'throw new Error("REPAIR_LIVE_EDIT");', encoding="utf-8")
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": json.dumps(responses[index])}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with TemporaryDirectory(prefix="rna-tool-result-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            (workspace / "index.html").write_text(
                '<!doctype html><script src="app.js"></script>', encoding="utf-8")
            (workspace / "app.js").write_text(
                'throw new Error("TOOL_RESULT_VISIBLE");', encoding="utf-8")
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")
            run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Find and read app.js, then verify index.html", "--max-iterations", "4",
                "--max-tool-steps", "3", "--jsonl"], cwd=REPO, env=env,
                text=True, capture_output=True, timeout=90, check=False)
            assert run.returncode == 0, (run.stdout, run.stderr)
            assert len(requests) == 5, len(requests)
            find_results = [message["content"] for message in requests[1]["messages"]
                            if message.get("role") == "user" and message.get("content")]
            assert any("app.js" in value and '"status":"ok"' in value
                       for value in find_results), find_results
            file_results = [message["content"] for message in requests[2]["messages"]
                            if message.get("role") == "user" and message.get("content")]
            assert any("TOOL_RESULT_VISIBLE" in value and '"status":"ok"' in value
                       for value in file_results), file_results
            browser_results = [message["content"] for message in requests[3]["messages"]
                               if message.get("role") == "user" and message.get("content")]
            stale_receipts = [message["content"] for message in requests[3]["messages"]
                              if message.get("role") == "assistant" and message.get("content")]
            assert any('"state":"Superseded"' in value and '"bodyIncluded":false' in value
                       for value in stale_receipts), stale_receipts
            assert any("CHANGED_AFTER_READ" in value and '"status":"error"' in value
                       for value in browser_results), browser_results
            assert not any("TOOL_RESULT_VISIBLE" in value for value in browser_results), browser_results
            assert requests[4]["messages"][:-1] == requests[3]["messages"], "Repair changed its frozen base"
            assert "REPAIR_LIVE_EDIT" not in json.dumps(requests[4]), "Repair read live state"
            assert "rna://" not in json.dumps(requests), "Runtime identity leaked to the model"
            final = json.loads(run.stdout.splitlines()[-1])["data"]
            assert final["reason"] == "model_blocked", final
            inspected = subprocess.run(["dotnet", str(CLI), "inspect", "--workspace", str(workspace),
                "--session", final["SessionId"], "--jsonl"], cwd=REPO, env=env,
                text=True, capture_output=True, timeout=15, check=True)
            receipt = json.loads(inspected.stdout.splitlines()[-1])["data"]["contextReceipt"]
            assert receipt["ExcludedSuperseded"] > 0, receipt
            assert receipt["SnapshotId"] and receipt["ResourceGenerations"], receipt
            print("PASS shared compiler: bounded find, exact read, stale receipt, frozen repair and saved receipt")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
