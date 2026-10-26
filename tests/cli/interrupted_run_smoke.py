"""Kill after completed reads; verify retained context and non-replaying recovery."""

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


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    entered = threading.Event()
    release = threading.Event()
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if "response_format" not in body:
                content = "Test title"
                response = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": content}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)
                return
            requests.append(body)
            if len(requests) == 2:
                entered.set()
                release.wait(10)
            content = {"action": "finish", "outcome": "completed", "reason": "No source change is authorized by this request.",
                       "results": [], "message": "Waiting for an explicit new task."}
            if len(requests) == 1:
                content = {"action": "execute", "reason": "Inspect both source files.",
                           "tool_calls": [{"name": "common.resources_read", "arguments": {"target": name}}
                                          for name in ("seed.txt", "second.txt")],
                           "message": "Read the existing sources."}
            if len(requests) == 4:
                content = {"action": "execute", "reason": "Inspect available files.",
                           "tool_calls": [{"name": "common.resources_find",
                                           "arguments": {"query": "index.html"}}],
                           "message": "Inspect available files."}
            response = json.dumps({"choices": [{"message": {"role": "assistant", "content":
                conversation_content(body, content)}}]}).encode()
            try:
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)
            except BrokenPipeError:
                pass

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    try:
        with TemporaryDirectory(prefix="rna-interrupted-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            (workspace / "seed.txt").write_text("FIRST_COMPLETED_READ_SENTINEL\n")
            (workspace / "second.txt").write_text("SECOND_COMPLETED_READ_SENTINEL\n")
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")

            def cli(*args):
                return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                      cwd=REPO, env=env, text=True, capture_output=True,
                                      timeout=20, check=False)

            process = subprocess.Popen(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Create index.html from the two sources", "--expect-files", "index.html",
                "--tool-result-role", "tool", "--jsonl"],
                cwd=REPO, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            assert entered.wait(10), "CLI did not reach the model endpoint"
            process.kill()
            process.communicate(timeout=10)
            release.set()

            listing = cli("sessions", "--workspace", str(workspace))
            assert listing.returncode == 0, listing.stderr
            sessions = json.loads(listing.stdout)["data"]["sessions"]
            assert len(sessions) == 1, sessions
            session_id = sessions[0]["Id"]

            recovered = cli("resume", "--workspace", str(workspace), "--session", session_id)
            assert recovered.returncode == 3, recovered.stderr
            recovery = json.loads(recovered.stdout)
            assert recovery["type"] == "run.interrupted", recovery
            assert recovery["data"]["possibleEffect"] is False, recovery
            assert recovery["data"]["acceptance"] == "unknown", recovery
            assert len(requests) == 2, "resume must not call the model"

            again = cli("resume", "--workspace", str(workspace), "--session", session_id)
            assert again.returncode == 3 and json.loads(again.stdout)["type"] == "run.interrupted"
            assert len(requests) == 2, "repeat resume must not replay"

            continued = cli("run", "--workspace", str(workspace), "--session", session_id,
                            "--message", "Continue only after my new input", "--tool-result-role", "tool")
            assert continued.returncode == 0, (continued.stdout, continued.stderr)
            result = json.loads(continued.stdout.splitlines()[-1])
            assert result["data"]["reason"] == "model_finish", result
            assert len(requests) == 3, "one new model request after explicit input"
            for marker in ("FIRST_COMPLETED_READ_SENTINEL", "SECOND_COMPLETED_READ_SENTINEL"):
                assert marker in json.dumps(requests[-1]), "Completed batch read lost after restart: " + marker
            assert not (workspace / "index.html").exists(), "the interrupted tool was replayed"

            # Revalidation must preserve unchanged sources and suppress stale bytes.
            (workspace / "seed.txt").write_text("EXTERNALLY_CHANGED_SOURCE\n")

            invalid = cli("run", "--workspace", str(workspace), "--session", session_id,
                          "--message", "Check budget", "--max-iterations", "0")
            assert invalid.returncode == 2 and len(requests) == 3, invalid
            limited = cli("run", "--workspace", str(workspace), "--session", session_id,
                          "--message", "Check budget", "--max-iterations", "1",
                          "--max-tool-steps", "1")
            assert limited.returncode == 5, (limited.stdout, limited.stderr)
            assert json.loads(limited.stdout.splitlines()[-1])["data"]["reason"] == "iteration_limit"
            assert len(requests) == 4, "one model request within the explicit iteration limit"
            assert "FIRST_COMPLETED_READ_SENTINEL" not in json.dumps(requests[-1]), "Stale source bytes survived revalidation"
            assert "SECOND_COMPLETED_READ_SENTINEL" in json.dumps(requests[-1]), "Unchanged source was lost with the stale sibling"
            print("PASS interrupted CLI: completed batch reads survive restart, idempotent resume without replay, explicit input and run limit")
    finally:
        release.set()
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
