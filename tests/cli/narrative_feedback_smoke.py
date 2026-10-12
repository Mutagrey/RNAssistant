"""Transient no-call feedback through production CLI and controlled HTTP.

This checks request delivery and repair, not real-model quality or Office delivery.
"""
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call


def run_case(root, result_role, response_mode):
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    for name in ("first.txt", "second.txt"):
        (workspace / name).write_text(name, encoding="utf-8")
    narrative = {"message": "I will read the next file.", "action": "continue", "tool_calls": []}
    responses = [call("common.resources_read", target="first.txt"), narrative,
                 {}, narrative, call("common.resources_read", target="second.txt"), DONE]
    requests, errors = [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions", self.path
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                assert len(requests) <= len(responses), "Unexpected request or replay"
                body = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": json.dumps(responses[len(requests) - 1])}}]}).encode()
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
    env = {key: value for key, value in os.environ.items() if not key.startswith("RNA_")}
    env.update(RNA_STATE_ROOT=str(root / "state"), RNA_MODEL="scripted", RNA_API_KEY="test",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}")
    try:
        result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--message", "Read first.txt and second.txt.", "--instruction-role", "system",
            "--tool-result-role", result_role, "--response-mode", response_mode,
            "--context-tokens", "32768", "--min-reads", "2", "--min-writes", "0",
            "--max-iterations", "8", "--max-tool-steps", "4", "--jsonl"],
            cwd=REPO, env=env, text=True, capture_output=True, timeout=30)
        assert result.returncode == 0, (result.stdout, result.stderr, errors)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_done" and final["completeFileReads"] == 2, final
        assert len(requests) == len(responses) and not errors, (len(requests), errors)
        for index, request in enumerate(requests):
            feedback = [message for message in request["messages"]
                        if (message.get("content") or "").startswith("RUNTIME_FEEDBACK:\n")]
            assert len(feedback) == (1 if index in (2, 3, 4) else 0), (index, feedback)
            if feedback:
                assert feedback[0]["role"] == "system" and "tool_call_id" not in feedback[0]
                assert "No tools were called in that step" in feedback[0]["content"]
                assert "Another continue remains allowed" in feedback[0]["content"]
                if index != 3:
                    assert request["messages"][-1] == feedback[0], "Feedback must follow accepted history"
        original = requests[2]["messages"]
        repaired = requests[3]["messages"]
        assert repaired[:-1] == original and repaired[-1]["content"].startswith("FORMAT_REPAIR:"), repaired
        if response_mode == "json_schema":
            actions = requests[4]["response_format"]["json_schema"]["schema"]["properties"]["action"]["enum"]
            assert "continue" in actions, actions
        for name in ("first.txt", "second.txt"):
            assert (workspace / name).read_text(encoding="utf-8") == name
        print(f"PASS narrative feedback: {result_role}/{response_mode}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-narrative-feedback-") as temporary:
        for result_role in ("user", "tool"):
            for response_mode in ("json_schema", "json_object"):
                run_case(Path(temporary) / f"{result_role}-{response_mode}", result_role, response_mode)
