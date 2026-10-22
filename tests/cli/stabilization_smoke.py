"""Scripted endpoint: one batch narrative, bounded source and verified patch delivery."""
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
    return {"message": message, "action": "tool" if calls else "done", "tool_calls": list(calls)}


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
            response("BATCH_NARRATION_ONCE", call("common.resources_read", target="large.txt", startLine=1257, lineCount=2),
                     *(call("common.resources_read", target=f"small{i}.txt") for i in range(4))),
            response("Attempt whole replacement from a partial read.", call("files.replace", relativePath="large.txt", text="UNSAFE")),
            response("Patch only the observed anchor.", call("files.patch", relativePath="large.txt", oldText="TARGET_OLD", newText="TARGET_NEW")),
            response("Use the delivered after-source.", call("files.patch", relativePath="large.txt", oldText="TARGET_NEW", newText="TARGET_FINAL")),
            response("Verified patches complete."),
        ]
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                reply = replies[len(requests) - 1] if len(requests) <= len(replies) else response("Unexpected extra step.")
                payload = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(reply)}}]}).encode()
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
                "--max-iterations", "6", "--max-tool-steps", "9", "--tool-result-role", "tool", "--jsonl"],
                cwd=REPO, env=env, text=True, capture_output=True, timeout=60)
            approvals = 0
            while run.returncode == 3 and approvals < 2:
                pending = json.loads(run.stdout.splitlines()[-1])["data"]
                assert pending["pendingTool"] == "files.patch", pending
                run = subprocess.run(["dotnet", str(CLI), "approve", "--workspace", str(workspace),
                    "--session", pending["SessionId"], "--pending", pending["pendingId"], "--jsonl"],
                    cwd=REPO, env=env, text=True, capture_output=True, timeout=60)
                approvals += 1
            assert run.returncode == 0, (run.stdout, run.stderr)
            assert approvals == 2, approvals
            assert len(requests) in (5, 6), (len(requests), run.stdout)
            if len(requests) == 6:
                assert "title" in json.dumps(requests[-1]).lower(), requests[-1]
            batch = requests[1]["messages"]
            assert sum("BATCH_NARRATION_ONCE" in str(m.get("content", "")) for m in batch) == 1, batch
            assert sum(m.get("role") == "tool" for m in batch) == 5, batch
            failures = [json.loads(m["content"]) for m in requests[2]["messages"] if m.get("role") == "tool"]
            assert failures[-1]["data"]["code"] == "source_observation_required", failures
            for index, marker in [(3, "TARGET_NEW"), (4, "TARGET_FINAL")]:
                bodies = [m["content"] for m in requests[index]["messages"] if "CURRENT_RESOURCE_VIEW" in str(m.get("content", ""))]
                assert any(marker in body for body in bodies), (marker, requests[index])
                assert all('"complete":false' in body for body in bodies), bodies
            assert target.read_bytes() == original.replace("TARGET_OLD", "TARGET_FINAL").encode(), target.read_bytes()
            assert "rna://" not in json.dumps(requests), "Runtime references leaked"
            final = json.loads(run.stdout.splitlines()[-1])["data"]
            assert final["ToolCounts"]["WriteOk"] == 2 and final["ToolCounts"]["WriteUnknown"] == 0, final
            print("PASS five-call narrative, bounded tail read, whole-write refusal, patch → delivered after-source → patch")
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    main()
