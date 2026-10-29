"""Scripted endpoint: one batch narrative, bounded source and verified patch delivery."""
from conversation_transport import conversation_content, reply_to_title
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def response(message, *calls):
    return ({"action": "execute", "reason": "Perform the checked step.", "tool_calls": list(calls), "message": message}
            if calls else {"finish_from_request": True, "message": message})


def call(name, **arguments):
    return {"name": name, "arguments": arguments}


def main():
    with TemporaryDirectory(prefix="rna-stabilization-") as folder:
        root = Path(folder)
        workspace = root / "workspace"
        workspace.mkdir()
        original = ("".join(f"line {i:04d} untouched\r\n" for i in range(1000)) +
                    ("x" * 8192 + "\r\n") * 256 + "TARGET_OLD\n尾😀\r\n")
        target = workspace / "large.txt"
        target.write_bytes(original.encode())
        for i in range(4):
            (workspace / f"small{i}.txt").write_text(f"small source {i}")
        replies = [
            response("BATCH_NARRATION_ONCE", call("core.read", target="large.txt", startLine=1257, lineCount=2),
                     *(call("core.read", target=f"small{i}.txt") for i in range(4))),
            response("Attempt whole replacement from a partial read.", call("core.write", target="large.txt", content="UNSAFE")),
            response("Patch related observed targets together.", call("core.patch", targets=[{'target': 'large.txt', 'edits': [{'before': 'TARGET_OLD', 'after': 'TARGET_NEW'}]}, {'target': 'small0.txt', 'edits': [{'before': 'small source 0', 'after': 'small updated 0'}]}])),
            response("Read the changed tail before the dependent patch.", call("core.read", target="large.txt", startLine=1257, lineCount=2)),
            response("Patch the newly observed source.", call("core.patch", targets=[{'target': 'large.txt', 'edits': [{'before': 'TARGET_NEW', 'after': 'TARGET_FINAL'}]}])),
            response("Verified patches complete."),
        ]
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if reply_to_title(self, request): return
                requests.append(request)
                reply = replies[len(requests) - 1] if len(requests) <= len(replies) else response("Unexpected extra step.")
                payload = json.dumps({"choices": [{"message": {"role": "assistant", "content": conversation_content(requests[-1], reply)}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                   RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}", RNA_MODEL="scripted", RNA_API_KEY="fixture")
        try:
            run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Read five files, reject whole replacement from partial source, then patch the tail twice.",
                "--max-iterations", "7", "--max-tool-steps", "10", "--tool-result-role", "tool", "--jsonl"],
                cwd=REPO, env=env, text=True, capture_output=True, timeout=60)
            approvals = 0
            while run.returncode == 3 and approvals < 2:
                pending = json.loads(run.stdout.splitlines()[-1])["data"]
                assert pending["pendingTool"] == "core.patch", pending
                assert [row["target"] for row in pending["patchPreview"]["targets"]] == (["large.txt", "small0.txt"] if approvals == 0 else ["large.txt"]), pending
                run = subprocess.run(["dotnet", str(CLI), "approve", "--workspace", str(workspace),
                    "--session", pending["SessionId"], "--pending", pending["pendingId"], "--jsonl"],
                    cwd=REPO, env=env, text=True, capture_output=True, timeout=60)
                approvals += 1
            assert run.returncode == 0, (run.stdout, run.stderr)
            assert approvals == 2, approvals
            assert len(requests) in (6, 7), (len(requests), run.stdout)
            if len(requests) == 7:
                assert "title" in json.dumps(requests[-1]).lower(), requests[-1]
            batch = requests[1]["messages"]
            assert sum("BATCH_NARRATION_ONCE" in str(m.get("content", "")) for m in batch) == 1, batch
            assert sum(m.get("role") == "tool" for m in batch) == 5, batch
            failures = [json.loads(m["content"]) for m in requests[2]["messages"] if m.get("role") == "tool"]
            assert failures[-1]["data"]["code"] == "source_observation_required", failures
            assert any("TARGET_NEW" in str(m.get("content", "")) for m in requests[4]["messages"]), requests[4]
            delivered = [m["content"] for m in requests[3]["messages"] if "CURRENT_RESOURCE_VIEW" in str(m.get("content", ""))]
            assert any("small0.txt" in body and "small updated 0" in body for body in delivered), delivered
            assert target.read_bytes() == original.replace("TARGET_OLD", "TARGET_FINAL").encode(), target.read_bytes()
            assert (workspace / "small0.txt").read_text() == "small updated 0"
            assert "rna://" not in json.dumps(requests), "Runtime references leaked"
            final = json.loads(run.stdout.splitlines()[-1])["data"]
            assert final["ToolCounts"]["WriteOk"] == 2 and final["ToolCounts"]["WriteUnknown"] == 0, final
            print("PASS bounded read, whole-write refusal, batch approval across restart, per-target after-source, dependent read/patch")
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    main()
