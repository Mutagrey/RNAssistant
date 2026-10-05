"""Exercise current-turn file and browser results at the CLI model boundary."""

import gzip
import hashlib
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
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    request_bodies = []
    responses = [
        {"message": "Find workspace files.", "action": "tool", "tool_calls": [
            {"name": "common.resources_find", "arguments": {"directory": "", "query": "app"}}]},
        {"message": "Read the current source.", "action": "tool", "tool_calls": [
            {"name": "common.resources_read", "arguments": {"target": "app.js"}}]},
        {"message": "Check the browser error.", "action": "tool", "tool_calls": [
            {"name": "web.verify", "arguments": {"entryPath": "index.html"}}]},
        {"invalid_protocol": "Exercise repair from the frozen context."},
        {"message": "Browser error remains visible.", "action": "blocked", "tool_calls": []},
    ]

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            request_bodies.append(self.rfile.read(int(self.headers["Content-Length"])))
            requests.append(json.loads(request_bodies[-1]))
            index = len(requests) - 1
            assert index < len(responses), "Unexpected extra model request"
            if index == 2:
                # Change source after the model saw the exact read. The next
                # request must invalidate that body against frozen authority.
                (workspace / "app.js").write_text(
                    'throw new Error("CHANGED_AFTER_READ");', encoding="utf-8")
            if index == 3:
                (workspace / "app.js").write_text(
                    'throw new Error("REPAIR_LIVE_EDIT");', encoding="utf-8")
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": json.dumps(responses[index])}}]}).encode()
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
        with TemporaryDirectory(prefix="rna-tool-result-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            (workspace / "index.html").write_text(
                '<!doctype html><script src="app.js"></script>', encoding="utf-8")
            (workspace / "app.js").write_text(
                'throw new Error("TOOL_RESULT_VISIBLE");', encoding="utf-8")
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="TRACE_MUST_NOT_CONTAIN_THIS_KEY")
            run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Find and read app.js, then verify index.html", "--max-iterations", "4",
                "--max-tool-steps", "3", "--jsonl"], cwd=REPO, env=env,
                text=True, capture_output=True, timeout=90, check=False)
            assert run.returncode == 0, (run.stdout, run.stderr)
            assert len(requests) == 5, len(requests)
            find_results = [message["content"] for message in requests[1]["messages"]
                            if message.get("role") == "user" and message.get("content")]
            assert any("app.js" in value and '"status":"ok"' in value
                       for value in find_results), find_results
            file_results = [message["content"] for message in requests[2]["messages"]
                            if message.get("role") == "user" and message.get("content")]
            assert any("TOOL_RESULT_VISIBLE" in value and '"status":"ok"' in value
                       for value in file_results), file_results
            browser_results = [message["content"] for message in requests[3]["messages"]
                               if message.get("role") == "user" and message.get("content")]
            stale_receipts = [message["content"] for message in requests[3]["messages"]
                              if message.get("role") == "assistant" and message.get("content")]
            assert any('"state":"Superseded"' in value and '"bodyIncluded":false' in value
                       for value in stale_receipts), stale_receipts
            assert any("CHANGED_AFTER_READ" in value and '"status":"error"' in value
                       for value in browser_results), browser_results
            assert not any("TOOL_RESULT_VISIBLE" in value for value in browser_results), browser_results
            assert requests[4]["messages"][:-1] == requests[3]["messages"], "Repair changed its frozen base"
            assert "REPAIR_LIVE_EDIT" not in json.dumps(requests[4]), "Repair read live state"
            assert "rna://" not in json.dumps(requests), "Runtime identity leaked to the model"
            final = json.loads(run.stdout.splitlines()[-1])["data"]
            assert final["reason"] == "model_blocked", final
            inspected = subprocess.run(["dotnet", str(CLI), "inspect", "--workspace", str(workspace),
                "--session", final["SessionId"], "--jsonl"], cwd=REPO, env=env,
                text=True, capture_output=True, timeout=15, check=True)
            receipt = json.loads(inspected.stdout.splitlines()[-1])["data"]["contextReceipt"]
            assert receipt["ExcludedSuperseded"] > 0, receipt
            assert receipt["SnapshotId"] and receipt["ResourceGenerations"], receipt

            events = [json.loads(line)
                      for path in (root / "state/chats").rglob("*.events.jsonl")
                      for line in path.read_text(encoding="utf-8").splitlines()]
            verification_ids = set(re.findall(
                r'rna://workspace-web/[^/"\\]+/verification/([0-9a-f]{32})', json.dumps(events)))
            assert len(verification_ids) == 1, "Tool result did not retain its exact verification resource"
            saved_result = subprocess.run([
                "dotnet", str(CLI), "verification", "--workspace", str(workspace),
                "--id", verification_ids.pop(), "--jsonl"], cwd=REPO, env=env,
                text=True, capture_output=True, timeout=15, check=True)
            verification = json.loads(saved_result.stdout)["data"]
            assert verification["State"] == "Failed" and verification["SnapshotId"], verification
            assert verification["Origin"]["SessionId"] == final["SessionId"], verification
            assert verification["Origin"]["RunId"] and verification["Origin"]["ToolCallId"], verification
            assert any("CHANGED_AFTER_READ" in error for error in verification["Errors"]), verification

            def payload(event):
                reference = event["Payload"]
                assert reference["Encryption"] == "none", reference
                sha = reference["Sha256"]
                body = (root / "state/chat-blobs" / sha[:2] / (sha + ".blob")).read_bytes()
                if body.startswith(b"RNACAS01"):
                    body = gzip.decompress(body[28:])
                assert len(body) == reference["ByteLength"], reference
                assert hashlib.sha256(body).hexdigest() == sha, reference
                assert env["RNA_API_KEY"].encode() not in body, "API key leaked to trace payload"
                return body

            prepared = [event for event in events if event["Type"] == "llm.request"]
            assert len(prepared) == len(requests), "Every wire attempt must have exactly one request trace"
            for index, event in enumerate(prepared):
                assert payload(event) == request_bodies[index], "Saved request differs from dispatched UTF-8 bytes"
                assert event["Data"]["ContextSnapshotId"], event
                assert event["RunId"] == final["RunId"], event
            rejected = [event for event in events if event["Type"] == "agent.response.rejected"]
            assert len(rejected) == 1, rejected
            assert json.loads(payload(rejected[0])) == responses[3], "Rejected model body was lost"
            assert rejected[0]["Data"]["Error"], rejected
            assert rejected[0]["Data"]["RequestId"] == prepared[3]["Data"]["RequestId"], rejected
            assert rejected[0]["Data"]["ModelAttemptId"] == prepared[3]["Data"]["ModelAttemptId"], rejected
            for event in prepared[3:] + rejected:
                assert event["Data"]["ContextSnapshotId"] == receipt["SnapshotId"], event
            assert prepared[3]["Data"]["StepId"] == prepared[4]["Data"]["StepId"], prepared[3:]
            for key in ("RequestId", "ModelAttemptId"):
                assert prepared[3]["Data"][key] != prepared[4]["Data"][key], "Repair reused an attempt identity"
            received = [event for event in events if event["Type"] == "llm.response"]
            accepted = [event for event in events if event["Type"] == "model.response.accepted"]
            assert len(received) == 5 and len(accepted) == 4, (len(received), len(accepted))
            for index, event in enumerate(received):
                assert event["Data"]["RequestId"] == prepared[index]["Data"]["RequestId"], event
                # Streaming trace stores the assembled LlmCompletionResult;
                # raw provider frames have their separate assistant.chunk records.
                assert requests[index]["stream"] is True, requests[index]
                content = json.loads(payload(event))["Content"]
                assert json.loads(content) == responses[index], "Assembled response content was lost"
            assert env["RNA_API_KEY"] not in json.dumps(events), "API key leaked to trace metadata"
            print("PASS shared compiler and trace: exact wire bytes, rejected body, correlated repair, "
                  "stale receipts and durable verification origin")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
