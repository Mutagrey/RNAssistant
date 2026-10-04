"""Check explicit CLI response format selection and persisted run metadata."""

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

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            content = json.dumps({"message": "Ready.", "action": "done", "tool_calls": []})
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": content}}]}).encode()
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
        with TemporaryDirectory(prefix="rna-response-mode-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")

            def cli(*args):
                return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                      cwd=REPO, env=env, text=True, capture_output=True,
                                      timeout=30, check=False)

            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", "invalid")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)

            for mode in ("json_schema", "json_object"):
                run = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", mode)
                assert run.returncode == 0, (run.stdout, run.stderr)
                result = json.loads(run.stdout.splitlines()[-1])["data"]
                assert result["reason"] == "model_done", result
                assert requests[-1]["response_format"]["type"] == mode, requests[-1]
                inspect = cli("inspect", "--workspace", str(workspace),
                              "--session", result["SessionId"])
                assert inspect.returncode == 0, inspect.stderr
                saved = json.loads(inspect.stdout)["data"]["modelConfiguration"]
                assert saved["AgentResponseMode"] == mode, saved
            assert len(requests) == 2, len(requests)
            print("PASS explicit CLI response modes and durable model metadata")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
