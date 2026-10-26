"""A separate CLI process reads live model tokens without changing the run or event stream."""
import json
import os
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from conversation_transport import conversation_content
from read_acceptance_context_smoke import CLI, REPO


def run_case(root):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    first_sent, release = threading.Event(), threading.Event()
    errors = []
    first_marker, second_marker = "LIVE_PREVIEW_short", "LATER_PREVIEW_" + "b" * 300

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if "response_format" not in request:
                    body = json.dumps({"choices": [{"message": {"role": "assistant", "content": "Test title"}}]}).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                    return
                assert request["stream"] is True
                response = {"action": "finish", "outcome": "completed", "reason": "Completed.", "results": [],
                            "message": first_marker + second_marker}
                content = conversation_content(request, response)
                split = content.index(second_marker)
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.end_headers()

                def emit(delta, finish=None):
                    chunk = {"choices": [{"delta": delta, "finish_reason": finish}]}
                    self.wfile.write(("data: " + json.dumps(chunk) + "\n\n").encode())
                    self.wfile.flush()

                emit({"role": "assistant", "reasoning_content": "LIVE_REASONING_", "content": content[:split]})
                first_sent.set()
                assert release.wait(15), "Fixture did not release the model response"
                emit({"content": content[split:]})
                emit({}, "stop")
                self.wfile.write(b"data: [DONE]\n\n")
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as error:
                errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_MODEL="scripted", RNA_API_KEY="test",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}")
    for key in ("RNA_THINKING", "RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE"):
        env.pop(key, None)

    def command(action, *args):
        return ["dotnet", str(CLI), *action, "--workspace", str(workspace), "--jsonl", *args]

    def cli(action, *args):
        result = subprocess.run(command(action, *args), cwd=REPO, env=env, text=True,
                                capture_output=True, timeout=10)
        assert result.returncode == 0, (result.returncode, result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    runner = None
    try:
        session = cli(["session", "create"])["Id"]
        runner = subprocess.Popen(command(["run"], "--session", session, "--message", "Answer without tools.",
                                          "--max-iterations", "1"), cwd=REPO, env=env,
                                  text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        assert first_sent.wait(10), "No first model chunk"
        source = next(state.rglob("*.events.jsonl"))
        before = source.read_bytes()
        deadline = time.monotonic() + 10
        preview = None
        while time.monotonic() < deadline:
            snapshot = cli(["inspect"], "--session", session)
            preview = snapshot.get("modelProgress")
            if preview and first_marker in (preview.get("Message") or ""):
                break
            time.sleep(0.1)
        assert preview and first_marker in preview["Message"], (snapshot, errors)
        assert preview["Reasoning"] == "LIVE_REASONING_", preview
        assert preview["RunId"] == snapshot["RunId"] and preview["Sequence"] > 0
        other = cli(["session", "create"])["Id"]
        assert cli(["inspect"], "--session", other).get("modelProgress") is None
        repeated = cli(["inspect"], "--session", session)
        assert repeated["modelProgress"] == preview and repeated["Cursor"] == snapshot["Cursor"]
        assert source.read_bytes() == before, "Preview reads must not append events"
        assert runner.poll() is None, "Preview read interrupted the owning run"
        release.set()
        out, err = runner.communicate(timeout=12)
        assert runner.returncode == 0, (runner.returncode, out, err, errors)
        final = cli(["inspect"], "--session", session)
        assert final["Status"] != "running" and final.get("modelProgress") is None
        assert first_marker + second_marker == json.loads(out.splitlines()[-1])["data"]["AssistantMessage"]
        assert not errors, errors
        print("PASS model preview IPC: separate process sees bounded live tokens; reads are inert; terminal clears preview")
    finally:
        release.set()
        if runner is not None and runner.poll() is None:
            runner.kill()
            runner.communicate(timeout=5)
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-model-preview-") as folder:
        run_case(Path(folder))
