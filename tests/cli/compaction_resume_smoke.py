"""Reduced-budget compaction, process loss, external edit and same-run resume.

Uses a controlled HTTP endpoint with structured compaction claims. It qualifies
production runtime invariants, not summarization quality of a real model.
"""
from conversation_transport import conversation_content

import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, read_progress


TASK = "TASK_KEEP_LOCAL: inspect app.js and guard.txt, preserve external changes, create once.txt once, and read the final app.js before finish."
OLD = "const marker = 'OLD_MARKER';\n"
EXTERNAL = "const marker = 'EXTERNAL_MARKER';\n"


def call(name, **arguments):
    return {"action": "execute", "reason": "Perform the checked file step.",
            "tool_calls": [{"name": name, "arguments": arguments}],
            "message": "Perform the checked file step."}


def finish(request, assessment="satisfied", message="Finished."):
    evidence = []
    for item in request["messages"]:
        content = item.get("content") or ""
        if item.get("role") == "tool" and item.get("tool_call_id"):
            evidence.append(item["tool_call_id"])
        elif isinstance(content, str) and content.startswith("TOOL_RESULT:\n"):
            evidence.append(json.loads(content.split("\n", 1)[1])["tool_call_id"])
    evidence = list(dict.fromkeys(evidence))
    return {"action": "finish", "outcome": "completed" if assessment == "satisfied" else "unable_to_complete", "reason": "Assess the available result evidence.",
            "results": [{"summary": message, "evidence": evidence,
                         "assessment": assessment}] if evidence else [],
            "message": message}


def run_case(root, recover):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir(parents=True)
    (workspace / "app.js").write_text(OLD)
    (workspace / "guard.txt").write_text("GUARD_KEEP")
    requests, errors, summaries = [], [], []
    entered, release = threading.Event(), threading.Event()
    phase = {"resumed": False, "before": 0, "after": 0}
    old_claim_sources = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                content = "\n".join(item.get("content") or "" for item in request["messages"])
                if "response_format" not in request and "CONVERSATION_PREFIX (data to summarize" not in content:
                    body = json.dumps({"choices": [{"message": {"role": "assistant",
                        "content": "Test title"}}]}).encode()
                    self.send_response(200); self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
                    return
                requests.append(request)
                if "CONVERSATION_PREFIX (data to summarize" in content:
                    sources = [json.loads(line) for line in content.splitlines() if line.startswith('{"sourceId":')]
                    assert sources, "Compaction must send exact admitted sources"
                    claims = []
                    for source in sources:
                        if source.get("userSource") or source.get("kind") == "constraint":
                            claims.append({"kind": "constraint", "text": "Preserve the local task and external edits.", "sourceIds": [source["sourceId"]]})
                        elif source.get("observationEligible") and "OLD_MARKER" in source["text"]:
                            claims.append({"kind": "observation", "text": "The app source contains OLD_MARKER.", "sourceIds": [source["sourceId"]]})
                            old_claim_sources.append(source)
                    if not claims:
                        claims = [{"kind": "interpretation", "text": "Continue the pending local task.", "sourceIds": [sources[0]["sourceId"]]}]
                    response = {"claims": claims}
                    summaries.append(response)
                elif not phase["resumed"]:
                    phase["before"] += 1
                    if summaries:
                        assert TASK in content, "Current user instruction was lost to compaction"
                        assert "STRUCTURED_CONTEXT_CLAIMS" in content, "Checkpoint must reach the real HTTP request"
                        assert "OLD_MARKER" in content and old_claim_sources, "Exercise an observed file claim, not only narration"
                        entered.set()
                        assert release.wait(30)
                        response = call("core.write", target="late.txt", content="MUST_NOT_DISPATCH")
                    elif phase["before"] == 1:
                        response = call("core.write", target="once.txt", content="CREATED_ONCE")
                    elif phase["before"] == 2:
                        response = {"action": "execute", "reason": "Read both exact sources.", "tool_calls": [
                            {"name": "core.read", "arguments": {"target": "app.js"}},
                            {"name": "core.read", "arguments": {"target": "guard.txt"}}],
                            "message": "Read both exact sources."}
                    else:
                        response = call("core.find", query="probe-" + str(phase["before"]))
                        # Deliberately grow accepted history within the reduced 24K
                        # profile. No synthetic checkpoint or edited event history.
                        response["message"] = "Inspection " + str(phase["before"]) + ": " + "bounded diagnostic context " * 90
                else:
                    phase["after"] += 1
                    if phase["after"] == 1:
                        assert TASK in content, "Task must survive a fresh process"
                        assert any(item["role"] == "tool" for item in request["messages"]) == recover, "Result role must survive compaction and process restart"
                        assert "OLD_MARKER" not in content, "Superseded claim/body reached the resumed model"
                        assert read_progress(request)["remainingCompleteFileReads"] == 1
                        assert "app.js" in read_progress(request)["expectedFilesWithoutCurrentRead"]
                        assert "STRUCTURED_CONTEXT_CLAIMS" in content
                        response = call("core.patch", targets=[{'target': 'app.js', 'edits': [{'before': "const marker = 'OLD_MARKER';", 'after': "const marker = 'UNSAFE';"}]}])
                    elif phase["after"] == 2:
                        assert (workspace / "app.js").read_text() == EXTERNAL, "Stale observation overwrote the user's edit"
                        assert "source_changed" in content
                        response = call("core.read", target="app.js") if recover else finish(request)
                    elif not recover:
                        assert phase["after"] == 3 and "completionFeedback" in content
                        response = finish(request, "not_satisfied",
                            "The external edit requires review before I can finish.")
                    elif phase["after"] == 3:
                        assert "EXTERNAL_MARKER" in content
                        response = call("core.patch", targets=[{'target': 'app.js', 'edits': [{'before': "const marker = 'EXTERNAL_MARKER';", 'after': "const marker = 'VERIFIED_MARKER';"}]}])
                    elif phase["after"] == 4:
                        assert read_progress(request)["remainingCompleteFileReads"] == 1
                        response = call("core.read", target="app.js")
                    else:
                        assert phase["after"] == 5 and read_progress(request)["remainingCompleteFileReads"] == 0
                        response = finish(request)
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": conversation_content(request, response)}}]}).encode()
                self.send_response(200); self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as error:
                errors.append(str(error)); self.send_error(500)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
               RNA_MODEL="scripted", RNA_API_KEY="test", RNA_BROWSER_EXECUTABLE="/missing/browser")
    for key in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(key, None)
    role = "tool" if recover else "user"

    def command(action, *args):
        return ["dotnet", str(CLI), *action.split(), "--workspace", str(workspace), "--jsonl", *args]

    def cli(action, *args, code=0):
        result = subprocess.run(command(action, *args), cwd=REPO, env=env, text=True, capture_output=True, timeout=15)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    process = None
    try:
        session = cli("session create")["Id"]
        process = subprocess.Popen(command("run", "--session", session, "--message", TASK,
            "--context-tokens", "24576", "--max-iterations", "24", "--max-tool-steps", "24",
            "--min-reads", "2", "--min-writes", "1", "--expect-files", "app.js,guard.txt,once.txt",
            "--tool-result-role", role, "--auto-confirm", "on"), cwd=REPO, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        assert entered.wait(20), (phase, summaries, errors, process.communicate(timeout=2) if process.poll() is not None else "compaction did not reach the barrier")
        before = cli("inspect", "--session", session)
        assert before["ContextCheckpointId"] and before["Status"] == "running"
        assert before["IterationsUsed"] == phase["before"] and before["ToolStepsUsed"] == phase["before"]
        process.kill(); process.communicate(timeout=5); process = None
        (workspace / "app.js").write_text(EXTERNAL)
        release.set()
        phase["resumed"] = True
        stopped = cli("inspect", "--session", session)
        assert stopped["ContextCheckpointId"] == before["ContextCheckpointId"]
        final = cli("resume", "--session", session, "--continue", "--run", stopped["RunId"], "--cursor", stopped["Cursor"],
                    "--context-tokens", "24576", code=0 if recover else 5)
        assert final["RunId"] == stopped["RunId"] and final["reason"] == "model_finish", final
        assert final["acceptance"] == ("passed" if recover else "failed")
        assert final["completeFileReads"] == (2 if recover else 1)
        assert final["verifiedFileChanges"] == (2 if recover else 1)
        assert final["IterationsUsed"] == stopped["IterationsUsed"] + phase["after"], \
            ("Compaction cannot reset or spend kernel iterations", final["IterationsUsed"],
             stopped["IterationsUsed"], phase, errors)
        assert (workspace / "once.txt").read_text() == "CREATED_ONCE" and not (workspace / "late.txt").exists()
        assert (workspace / "app.js").read_text() == (EXTERNAL.replace("EXTERNAL_MARKER", "VERIFIED_MARKER") if recover else EXTERNAL)
        assert not errors and len(summaries) == 1, (phase, len(summaries), errors)
        assert "rna://" not in json.dumps(requests)
        for request in requests:
            pending = set()
            for message in request["messages"]:
                if message.get("tool_calls"):
                    assert not pending, "Unanswered native call at a checkpoint boundary"
                    pending = {item["id"] for item in message["tool_calls"]}
                elif message["role"] == "tool":
                    assert message["tool_call_id"] in pending, "Orphan native result after replay"
                    pending.remove(message["tool_call_id"])
                else:
                    assert not pending, "Native result must follow its accepted call"
            assert not pending, "Missing native result after compaction"
        print("PASS compaction/resume", role, "repair" if recover else "honest incomplete finish", ": exact checkpoint, task/provenance, drift guards, native pairs and original counters")
    finally:
        release.set()
        if process is not None and process.poll() is None:
            process.kill(); process.communicate(timeout=5)
        (root / "requests.json").write_text(json.dumps(requests, indent=2))
        server.shutdown(); server.server_close()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-compact-resume-") as folder:
        for recover in (False, True):
            run_case(Path(folder) / ("repair" if recover else "done"), recover)
