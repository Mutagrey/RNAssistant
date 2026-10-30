"""Scripted public patch errors survive model delivery, presentation and restart."""
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


def call(name, **arguments):
    return {"name": name, "arguments": arguments}


def execute(*calls):
    return {"action": "execute", "reason": "Apply the observed edit.", "tool_calls": list(calls), "message": ""}


def main():
    with TemporaryDirectory(prefix="rna-patch-diagnostics-") as folder:
        root = Path(folder)
        workspace = root / "workspace"
        workspace.mkdir()
        original = {"a.txt": "old a", "b.txt": "head\nrepeat\nmiddle\nrepeat\nend", "c.txt": "old c"}
        for name, source in original.items():
            (workspace / name).write_text(source)
        targets = [{"target": name, "edits": [{"old": "repeat" if name == "b.txt" else source, "new": "updated"}]}
                   for name, source in original.items()]
        fixed = json.loads(json.dumps(targets))
        fixed[1]["edits"][0]["anchors_before"] = ["middle"]
        replies = [execute(*(call("core.read", target=name) for name in original)),
                   execute(call("core.patch", targets=targets)),
                   execute(call("core.patch", targets=fixed)),
                   {"finish_from_request": True, "message": "The corrected patch completed."}]
        requests = []

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if reply_to_title(self, request):
                    return
                requests.append(request)
                reply = replies[min(len(requests) - 1, len(replies) - 1)]
                payload = json.dumps({"choices": [{"message": {"role": "assistant", "content": conversation_content(request, reply)}}]}).encode()
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
        env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                   RNA_MODEL="scripted", RNA_API_KEY="fixture")

        def cli(command, *args):
            return subprocess.run(["dotnet", str(CLI), command, "--workspace", str(workspace), *args],
                                  cwd=REPO, env=env, text=True, capture_output=True, timeout=60)

        try:
            run = cli("run", "--message", "Read three files, report ambiguous edits, then apply the corrected patch.",
                      "--max-iterations", "7", "--max-tool-steps", "10", "--tool-result-role", "tool", "--jsonl")
            assert run.returncode == 3, (run.returncode, run.stdout[-3000:], run.stderr)
            pending = json.loads(run.stdout.splitlines()[-1])["data"]
            assert pending["pendingTool"] == "core.patch", pending
            failures = [json.loads(m["content"])["data"] for m in requests[2]["messages"]
                        if m.get("role") == "tool" and json.loads(m["content"]).get("data", {}).get("type") == "rnassistant.patch"]
            assert len(failures) == 1, failures
            batch = failures[0]
            assert batch["phase"] == "preparation" and not batch["writeStarted"] and not batch["partial"], batch
            assert [t["status"] for t in batch["targets"]] == ["NotStarted", "Rejected", "NotStarted"], batch
            diagnostic = batch["targets"][1]["diagnostic"]
            assert diagnostic["edit"] == 1 and diagnostic["code"] == "ambiguous_context" and diagnostic["nextAction"] == "refine_context", diagnostic
            assert [c["line"] for c in diagnostic["candidates"]] == [2, 4], diagnostic
            assert all((workspace / name).read_text() == source for name, source in original.items())
            resumed = cli("approve", "--session", pending["SessionId"], "--pending", pending["pendingId"], "--no-interactive")
            assert resumed.returncode == 0, (resumed.returncode, resumed.stdout[-3000:], resumed.stderr)
            for text in ["Edit 1: ambiguous_context", "Next: refine_context", "Candidate line 2: repeat",
                         "Candidate line 4: repeat", "Rejected: b.txt; write started: False"]:
                assert text in resumed.stdout, (text, resumed.stdout)
            inspected = cli("inspect", "--session", pending["SessionId"], "--jsonl")
            assert inspected.returncode == 0, inspected.stderr
            presentation = json.loads(inspected.stdout.splitlines()[-1])["data"]["Presentation"]
            patch_rows = [f for f in presentation["Files"] if f["ToolId"] == "core.patch"]
            assert len(patch_rows) == 2, patch_rows
            assert patch_rows[0]["Targets"][1]["diagnostic"] == diagnostic, patch_rows
            assert (workspace / "b.txt").read_text() == "head\nrepeat\nmiddle\nupdated\nend"
            assert "rna://" not in json.dumps(requests), "runtime identities leaked to the model"
            print("PASS patch diagnostics: actual model request, preflight target statuses, CLI text and restart presentation")
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    main()
