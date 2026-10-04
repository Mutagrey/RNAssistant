"""Kill a CLI model wait and verify explicit, non-replaying session recovery."""

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
            body = self.rfile.read(int(self.headers["Content-Length"]))
            requests.append(json.loads(body))
            if len(requests) == 1:
                entered.set()
                release.wait(10)
            response = json.dumps({"choices": [{"message": {"role": "assistant", "content":
                json.dumps({"message": "Waiting for an explicit new task.",
                            "action": "needs_input", "tool_calls": []})}}]}).encode()
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
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")

            def cli(*args):
                return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                      cwd=REPO, env=env, text=True, capture_output=True,
                                      timeout=20, check=False)

            process = subprocess.Popen(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Create index.html", "--expect-files", "index.html", "--jsonl"],
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
            assert len(requests) == 1, "resume must not call the model"

            again = cli("resume", "--workspace", str(workspace), "--session", session_id)
            assert again.returncode == 3 and json.loads(again.stdout)["type"] == "run.interrupted"
            assert len(requests) == 1, "repeat resume must not replay"

            continued = cli("run", "--workspace", str(workspace), "--session", session_id,
                            "--message", "Continue only after my new input")
            assert continued.returncode == 3, (continued.stdout, continued.stderr)
            result = json.loads(continued.stdout.splitlines()[-1])
            assert result["data"]["reason"] == "model_needs_input", result
            assert len(requests) == 2, "one new model request after explicit input"
            assert not (workspace / "index.html").exists(), "the interrupted tool was replayed"
            print("PASS interrupted CLI model wait: durable unknown, idempotent resume, explicit new input")
    finally:
        release.set()
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
