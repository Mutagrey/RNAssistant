"""A body restored by real automatic compaction retains the same-run read guard."""
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call


def run_case(root, role):
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    target = workspace / "app.js"
    original = "const marker = 'COMPACTED_READ_SOURCE';\n"
    target.write_text(original)
    requests, summaries, errors = [], [], []
    phase = {"step": 0, "patched": False, "reread": False}

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append(request)
                content = "\n".join(item.get("content") or "" for item in request["messages"])
                if "CONVERSATION_PREFIX (data to summarize" in content:
                    sources = [json.loads(line) for line in content.splitlines() if line.startswith('{"sourceId":')]
                    response = {"claims": [{"kind": "interpretation", "text": "Continue the file edit.",
                                             "sourceIds": [sources[0]["sourceId"]]}]}
                    summaries.append(response)
                elif phase["patched"]:
                    assert target.read_text() == original.replace("COMPACTED_READ_SOURCE", "VERIFIED_EDIT"), content
                    response = DONE if phase["reread"] else call("common.resources_read", target="app.js")
                    phase["reread"] = True
                elif summaries:
                    assert any("CURRENT_RESOURCE_VIEW" in (item.get("content") or "") and
                               "COMPACTED_READ_SOURCE" in item["content"] for item in request["messages"]), content
                    assert not any(item["role"] == "tool" and "COMPACTED_READ_SOURCE" in
                                   (item.get("content") or "") for item in request["messages"]), "Read must be archived"
                    response = call("files.patch", relativePath="app.js", oldText="COMPACTED_READ_SOURCE", newText="VERIFIED_EDIT")
                    phase["patched"] = True
                else:
                    phase["step"] += 1
                    if phase["step"] == 1:
                        response = call("common.resources_read", target="app.js")
                    else:
                        response = call("common.resources_find", query="probe-" + str(phase["step"]))
                        response["message"] = "Inspection: " + "bounded diagnostic context " * 90
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(response)}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as error:
                errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}", RNA_MODEL="scripted", RNA_API_KEY="test")
    for key in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(key, None)
    try:
        run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace), "--jsonl",
            "--message", "Inspect app.js, then edit its marker and read the final source.",
            "--context-tokens", "24576", "--max-iterations", "24", "--max-tool-steps", "24",
            "--min-reads", "1", "--min-writes", "1", "--expect-files", "app.js",
            "--tool-result-role", role, "--auto-confirm", "on"],
            cwd=REPO, env=env, text=True, capture_output=True, timeout=45)
        assert run.returncode == 0 and not errors, (run.stdout, run.stderr, errors)
        final = json.loads(run.stdout.splitlines()[-1])["data"]
        assert len(summaries) == 1 and phase["patched"] and phase["reread"], (phase, summaries)
        assert final["acceptance"] == "passed" and final["verifiedFileChanges"] == 1, final
        assert final["ToolCounts"]["WriteOk"] == 1 and final["ToolCounts"]["WriteUnknown"] == 0, final
        assert "source_observation_required" not in json.dumps(requests), "Compaction revoked a delivered read"
        assert "rna://" not in json.dumps(requests), "Runtime evidence leaked to the model"
        print("PASS compacted read guard:", role, "restored body authorizes one verified edit without another read")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-compacted-read-") as folder:
        for role in ("tool", "user"):
            run_case(Path(folder) / role, role)
