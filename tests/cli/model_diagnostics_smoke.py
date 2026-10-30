"""Exercise diagnostic CLI capture/redaction through the production HTTP client; no build."""
import json
import os
import re
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def main():
    requests = []
    responses = []
    force_error = threading.Event()
    secret = 'local-test-key-"\\-private'

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            raw = self.rfile.read(int(self.headers["Content-Length"]))
            requests.append(raw)
            request = json.loads(raw)
            marker = None
            latest_id = "T2"
            tool_results = {}
            for message in request["messages"]:
                content = message.get("content") or ""
                for call in message.get("tool_calls", []):
                    if json.loads(call["function"]["arguments"]).get("target") == "ModuleB":
                        latest_id = call["id"]
                if message["role"] == "tool":
                    tool_results[message["tool_call_id"]] = json.loads(content)["marker"]
                elif content.startswith("TOOL_RESULT ("):
                    value = json.loads(content.split("\n", 1)[1])
                    latest_id = value["tool_call_id"]
                    tool_results[latest_id] = value["data"]["marker"]
            marker = tool_results.get(latest_id)
            answer = json.dumps({"target": "ModuleB", "marker": marker, "tool_call_id": latest_id})
            last_user = next(m["content"] for m in reversed(request["messages"]) if m["role"] == "user")
            if "{" in last_user:
                answer = json.dumps(json.loads(last_user[last_user.index("{"):]))
            role_marker = next((re.search(r"ROLE_MARKER=([A-F0-9]{24})", m.get("content") or "")
                                for m in request["messages"] if "ROLE_MARKER=" in (m.get("content") or "")), None)
            if role_marker:
                answer = role_marker.group(1)
            message = {"role": "assistant", "content": answer}
            finish = "stop"
            if request.get("tools"):
                value = re.search(r"value=([A-F0-9]{24})", last_user).group(1)
                message = {"role": "assistant", "content": None, "tool_calls": [{"id": "provider_call_1",
                    "type": "function", "function": {"name": "diagnostic_echo", "arguments": json.dumps({"value": value})}}]}
                finish = "tool_calls"
            status, content_type = 200, "application/json"
            response = json.dumps({"choices": [{"finish_reason": finish, "message": message}]}).encode()
            if "invalid_diagnostic_type" in json.dumps(request.get("response_format")):
                status = 400
                response = b'{"error":{"code":"invalid_json_schema","message":"schema is invalid"}}'
            elif request["stream"]:
                content_type = "text/event-stream"
                def delta(text, reason):
                    return "data: " + json.dumps({"choices": [{"delta": {"content": text}, "finish_reason": reason}]}) + "\r\n\r\n"
                response = (delta(answer[:len(answer)//2], None) + delta(answer[len(answer)//2:], "stop") + "data: [DONE]\r\n\r\n").encode()
            if force_error.is_set():
                status, content_type = 500, "application/json"
                response = json.dumps({"error": {"message": secret}}).encode()
            responses.append(response)
            self.send_response(status)
            self.send_header("Content-Type", content_type)
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
            assert len(report["Results"]) == 23
            assert report["NativeGeneration"] == report["V8Parser"] == report["Sse"] == "Pass"
            assert report["TargetCriterion"] == "Pass" and report["FinishResultField"] == "criterion"
            for raw, response, row in zip(requests, responses, report["Results"]):
                assert (root / "pass" / row["RequestFile"]).read_bytes() == raw
                captured = (root / "pass" / row["ResponseFile"]).read_bytes()
                assert response.startswith(captured)
                if row["Result"]["ResponseBodyComplete"]:
                    assert captured == response
                assert row["Result"]["Verdict"] == "Pass"
                assert row["Result"]["ExecutionKind"] == "Synthetic"
            assert run(root / "pass").returncode != 0
            assert len(requests) == 23, "Existing output rejected before network"
            assert run(root / "sse", "--cases", "C4").returncode == 0
            control_report = json.loads((root / "sse/report.json").read_text())
            assert [r["Result"]["ScenarioId"] for r in control_report["Results"]] == ["C4-control", "C4"]
            assert control_report["Sse"] == "Pass" and control_report["NativeGeneration"] == "NotRun"
            force_error.set()
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
            assert len(requests) == 26, "No retry or automatic fallback"
            print("PASS diagnostics CLI: 23 probes, native/V8/SSE verdicts, exact bytes, safe exports, no overwrite/retry")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    main()
