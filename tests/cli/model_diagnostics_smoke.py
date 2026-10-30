"""Exercise diagnostic CLI capture/redaction through the production HTTP client; no build."""
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
    requests = []
    secret = 'local-test-key-"\\-private'

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            raw = self.rfile.read(int(self.headers["Content-Length"]))
            requests.append(raw)
            request = json.loads(raw)
            marker = None
            for message in request["messages"]:
                content = message.get("content") or ""
                if message["role"] == "tool" and message["tool_call_id"] == "T2":
                    marker = json.loads(content)["marker"]
                elif content.startswith("TOOL_RESULT ("):
                    marker = json.loads(content.split("\n", 1)[1])["data"]["marker"]
            answer = json.dumps({"target": "ModuleB", "marker": marker, "tool_call_id": "T2"})
            error = len(requests) > 9
            response = json.dumps({"error": {"message": secret}} if error else
                {"choices": [{"finish_reason": "stop", "message": {"role": "assistant", "content": answer}}]}).encode()
            self.send_response(500 if error else 200)
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
        with TemporaryDirectory(prefix="rna-diagnostics-cli-") as temp:
            root = Path(temp)
            env = dict(os.environ, RNA_API_KEY=secret)

            def run(output, *extra):
                return subprocess.run(["dotnet", str(CLI), "diagnostics", "--base-url",
                    f"http://127.0.0.1:{server.server_port}", "--model", "synthetic", "--synthetic",
                    "--output", str(output), "--seed", "cli-regression", *extra], cwd=REPO,
                    env=env, text=True, capture_output=True, timeout=30)

            first = run(root / "pass")
            assert first.returncode == 0, (first.stdout, first.stderr)
            report = json.loads((root / "pass/report.json").read_text())
            assert len(report["Results"]) == 9
            assert report["NativeGeneration"] == report["V8Parser"] == report["Sse"] == "NotRun"
            for raw, row in zip(requests, report["Results"]):
                assert (root / "pass" / row["RequestFile"]).read_bytes() == raw
                assert row["Result"]["Verdict"] == "Pass"
                assert row["Result"]["ExecutionKind"] == "Synthetic"
            assert run(root / "pass").returncode != 0
            assert len(requests) == 9, "Existing output rejected before network"
            failure = run(root / "failure", "--cases", "C5")
            assert failure.returncode == 1, (failure.stdout, failure.stderr)
            row = json.loads((root / "failure/report.json").read_text())["Results"][0]
            assert row["Result"]["Verdict"] == "Inconclusive" and row["Result"]["HttpStatusCode"] == 500
            assert row["ResponseRedacted"]
            for path in root.rglob("*"):
                if path.is_file():
                    text = path.read_text()
                    assert secret not in text and json.dumps(secret)[1:-1] not in text, path
                    if path.name == "report.json":
                        assert secret not in json.dumps(json.loads(text), ensure_ascii=False)
            assert len(requests) == 10, "No retry or automatic fallback"
            print("PASS diagnostics CLI: nine probes, exact bytes, explicit source, safe exports, no overwrite/retry")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    main()
