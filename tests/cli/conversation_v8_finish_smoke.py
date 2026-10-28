"""Premature v8 finish is rejected after a verified write without replaying it."""

from conversation_transport import conversation_content

import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def execute(name, arguments):
    return {"action": "execute", "reason": "Create the requested artifact.",
            "tool_calls": [{"name": name, "arguments": arguments}], "message": ""}


def finish(request):
    ids = []
    for message in request["messages"]:
        content = message.get("content") or ""
        if isinstance(content, str) and content.startswith("TOOL_RESULT:\n"):
            ids.append(json.loads(content.split("\n", 1)[1])["tool_call_id"])
    ids = list(dict.fromkeys(ids))
    assert len(ids) == 2, ids
    return {"action": "finish", "outcome": "completed", "reason": "Both requested artifacts exist.",
            "results": [{"summary": "Data and chart created.", "evidence": ids,
                         "assessment": "satisfied"}],
            "message": "Data and chart created."}


def run_writes(mode):
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    errors = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if "response_format" not in request:
                    response = "Test title"
                else:
                    requests.append(request)
                    index = len(requests)
                    if index == 1:
                        response = execute("core.write", {
                            'target': "data.csv", 'content': "month,value\nJan,1\n"})
                    elif index == 2:
                        response = {"action": "finish", "outcome": "completed", "reason": "Empty answer.",
                                    "results": [], "message": " \t\n"}
                    elif index == 3:
                        response = {"action": "finish", "outcome": "partial", "reason": "Contradictory claim.",
                                    "results": [], "message": "No achieved work was reported."}
                    elif index == 4:
                        response = {"action": "finish", "outcome": "completed", "reason": "Premature claim.",
                                    "results": [], "message": "Everything is finished."}
                    elif index == 5:
                        response = execute("core.write", {
                            'target': "chart.json", 'content': '{"series":[1]}'})
                    elif index == 6:
                        response = finish(request)
                    else:
                        raise AssertionError("Unexpected model retry or extra request")
                    response = json.dumps(response)
                body = json.dumps({"choices": [{"message": {
                    "role": "assistant", "content": conversation_content(request, response)}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as error:
                errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with TemporaryDirectory(prefix="rna-v8-finish-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="test")
            result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Create data.csv and chart.json.", "--auto-confirm", "on",
                "--response-mode", mode, "--jsonl"], cwd=REPO, env=env, text=True, capture_output=True,
                timeout=30, check=False)
            assert result.returncode == 0 and not errors, (result.stdout, result.stderr, errors)
            final = json.loads(result.stdout.splitlines()[-1])["data"]
            assert final["reason"] == "model_finish" and final["ToolCounts"]["WriteOk"] == 2, final
            assert final["modelOutcome"] == "completed", final
            assert len(requests) == 6, len(requests)
            assert (workspace / "data.csv").read_text() == "month,value\nJan,1\n"
            assert (workspace / "chart.json").read_text() == '{"series":[1]}'
            assert "empty_finish_message" in json.dumps(requests[2]), "No empty-answer correction"
            assert "contradictory_outcome" in json.dumps(requests[3]), "No outcome correction"
            third_messages = requests[4]["messages"]
            assert any(message.get("role") == "developer" and
                       "missing_finish_results" in (message.get("content") or "")
                       for message in third_messages), "The model did not receive finish feedback"
            assert "Everything is finished." not in json.dumps(third_messages), \
                "The rejected finish leaked into accepted history"
            print(f"PASS v8 finish {mode}: empty answer, contradictory outcome and empty results rejected; writes executed once")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def run_direct(mode, outcome):
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            assessment = "partially_satisfied" if outcome == "partial" else "not_satisfied"
            response = {"action": "finish", "reason": "The requested facts are partly unavailable.",
                        "outcome": outcome, "results": [{"summary": "Known facts and limits.",
                        "assessment": assessment, "evidence": []}], "message": "Answer with explicit limits."}
            content = conversation_content(request, response) if "response_format" in request else "Fixture title"
            body = json.dumps({"choices": [{"message": {"role": "assistant", "content": content}}]}).encode()
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
        with TemporaryDirectory(prefix="rna-v8-outcome-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}", RNA_MODEL="scripted", RNA_API_KEY="test")
            result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Answer using the available facts.", "--response-mode", mode, "--jsonl"],
                cwd=REPO, env=env, text=True, capture_output=True, timeout=30, check=False)
            assert result.returncode == 5, (result.returncode, result.stdout, result.stderr)
            final = json.loads(result.stdout.splitlines()[-1])["data"]
            assert final["lifecycle"] == "Completed" and final["health"] == "Clean", final
            assert final["modelOutcome"] == outcome and final["acceptance"] == "notrequested", final
            assert final["ToolCounts"]["WriteOk"] == 0 and final["IterationsUsed"] == 1, final
            print(f"PASS v8 finish {mode}: {outcome} remains distinct from runtime Completed/Clean; exit 5")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    for mode in ("json_object", "json_schema"):
        run_writes(mode)
        for outcome in ("partial", "unable_to_complete"):
            run_direct(mode, outcome)
