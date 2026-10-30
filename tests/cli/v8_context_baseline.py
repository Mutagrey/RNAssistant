"""Capture exact production CLI HTTP bytes against a scripted, synthetic endpoint.

Does not qualify a real model or Office. Builds must be supplied separately.
"""
import argparse
import hashlib
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from conversation_transport import conversation_content

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def capture(output, role):
    output.mkdir(parents=True, exist_ok=False)
    read = {"action": "execute", "reason": "Inspect synthetic source.", "message": "Read.",
            "tool_calls": [{"name": "core.read", "arguments": {"target": "sample.txt"}}]}
    patch = {"action": "execute", "reason": "Change synthetic source.", "message": "Patch.",
             "tool_calls": [{"name": "core.patch", "arguments": {"targets": [{"target": "sample.txt",
                        "edits": [{"before": "BASELINE_OLD", "after": "BASELINE_NEW"}]}]}}]}
    script = [read, read, patch, read, {"finish_from_request": True}, read, {"finish_from_request": True}]
    requests = []
    calls = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            raw = self.rfile.read(int(self.headers["Content-Length"]))
            request = json.loads(raw)
            index = len(requests)
            requests.append(request)
            (output / f"{index:02d}.request.json").write_bytes(raw)
            if "response_format" in request:
                step = len(calls)
                calls.append(index)
                decision = script[step] if step < len(script) else {"finish_from_request": True}
                content = conversation_content(request, decision)
            else:
                content = "Synthetic baseline"
            response = json.dumps({"choices": [{"message": {"role": "assistant", "content": content},
                                                "finish_reason": "stop"}]}).encode()
            (output / f"{index:02d}.response.json").write_bytes(response)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with TemporaryDirectory(prefix="rna-v8-baseline-") as temp:
            root = Path(temp)
            workspace = root / "workspace"
            workspace.mkdir()
            (workspace / "sample.txt").write_text("BASELINE_OLD\n", encoding="utf-8")
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted-baseline", RNA_API_KEY="synthetic")
            session = []
            for turn in range(2):
                result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                    "--message", "Inspect, repeat read, patch and verify." if turn == 0 else "Check the saved change.",
                    "--tool-result-role", role, "--auto-confirm", "on", "--max-iterations", "10",
                    "--max-tool-steps", "10", "--jsonl", *session], env=env, cwd=REPO,
                    text=True, capture_output=True, timeout=60)
                (output / f"turn-{turn}.jsonl").write_text(result.stdout, encoding="utf-8")
                if result.returncode != 0:
                    raise RuntimeError(result.stderr + result.stdout)
                final = json.loads(result.stdout.splitlines()[-1])["data"]
                session = ["--session", final["SessionId"]]
            assert (workspace / "sample.txt").read_text() == "BASELINE_NEW\n"
            assert len(calls) == len(script), (calls, len(script))
        manifest = {"kind": "synthetic-http-baseline", "role": role,
            "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
            "conversationRequests": calls, "files": {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(output.glob("*.json"))}}
        (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        print(f"Captured {len(calls)} conversation requests: {output}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--role", choices=("user", "developer", "tool"), default="tool")
    args = parser.parse_args()
    capture(args.output.resolve(), args.role)
