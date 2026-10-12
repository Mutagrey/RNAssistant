"""Terminal decisions and rejected no-call continuation through production CLI HTTP.

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
    question = {"message": "Which file should I read next?", "action": "needs_input", "tool_calls": []}
    responses = [call("common.resources_read", target="first.txt"), narrative,
                 question, call("common.resources_read", target="second.txt"), DONE]
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
        def run(message, session=None):
            args = ["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", message, "--instruction-role", "system", "--tool-result-role", result_role,
                "--response-mode", response_mode, "--context-tokens", "32768", "--max-iterations", "8",
                "--max-tool-steps", "4", "--jsonl"]
            if session:
                args += ["--session", session]
            return subprocess.run(args, cwd=REPO, env=env, text=True, capture_output=True, timeout=30)

        waiting = run("Read first.txt, then ask me which file to read next.")
        assert waiting.returncode == 3, (waiting.stdout, waiting.stderr, errors)
        question_result = json.loads(waiting.stdout.splitlines()[-1])["data"]
        assert question_result["reason"] == "model_needs_input" and question_result["action"] == "needs_input", question_result
        assert len(requests) == 3, "The question must stop model generation"
        result = run("Read second.txt.", question_result["SessionId"])
        assert result.returncode == 0, (result.stdout, result.stderr, errors)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_done" and final["ToolCounts"]["ReadOk"] == 1, final
        assert len(requests) == len(responses) and not errors, (len(requests), errors)
        for request in requests:
            assert not any((message.get("content") or "").startswith("RUNTIME_FEEDBACK:")
                           for message in request["messages"]), "Obsolete feedback must not be injected"
            if response_mode == "json_schema":
                schema = request["response_format"]["json_schema"]
                assert schema["name"] == "rnassistant_conversation_response_v7", schema["name"]
                assert schema["schema"]["properties"]["action"]["enum"] == ["tool", "done", "blocked", "needs_input"]
        original = requests[1]["messages"]
        repaired = requests[2]["messages"]
        assert repaired[:-1] == original and repaired[-1]["content"].startswith("FORMAT_REPAIR:"), repaired
        followup = json.dumps(requests[3]["messages"])
        assert "Which file should I read next?" in followup and "Read second.txt." in followup, followup
        assert "I will read the next file." not in followup, "Rejected continue cannot enter accepted history"
        for name in ("first.txt", "second.txt"):
            assert (workspace / name).read_text(encoding="utf-8") == name
        print(f"PASS turn decisions: {result_role}/{response_mode}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-turn-decisions-") as temporary:
        for result_role in ("user", "tool"):
            for response_mode in ("json_schema", "json_object"):
                run_case(Path(temporary) / f"{result_role}-{response_mode}", result_role, response_mode)
