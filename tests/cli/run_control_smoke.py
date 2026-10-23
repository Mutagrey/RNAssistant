"""Exact-checkpoint cancellation/continuation through real CLI and controlled HTTP.

This verifies runtime semantics, not real-model quality or Office delivery.
"""
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO


DONE = {"finish_from_request": True}


def call(name, **arguments):
    return {"action": "execute", "reason": "Perform the requested step.",
            "tool_calls": [{"name": name, "arguments": arguments}],
            "message": "Perform the requested step."}


def finish_response(request):
    evidence = []
    for item in request["messages"]:
        content = item.get("content") or ""
        if item.get("role") == "tool" and item.get("tool_call_id"):
            evidence.append(item["tool_call_id"])
        elif isinstance(content, str) and content.startswith("TOOL_RESULT:\n"):
            evidence.append(json.loads(content.split("\n", 1)[1])["tool_call_id"])
    evidence = list(dict.fromkeys(evidence))
    return {"action": "finish", "reason": "Report completed tool outcomes.",
            "results": [{"summary": "Finished.", "evidence": evidence,
                         "assessment": "satisfied"}] if evidence else [],
            "message": "Finished."}


def run_case(root):
    workspace = root / "workspace"
    workspace.mkdir()
    replies, requests, errors, gates = [], [], [], []

    def reply(body, pause=False):
        entered, released = threading.Event(), threading.Event()
        if not pause:
            released.set()
        replies.append((body, entered, released))
        gates.append(released)
        return entered, released

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if "response_format" not in request:
                    body = json.dumps({"choices": [{"message": {"role": "assistant",
                        "content": "Test title"}}]}).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                    return
                requests.append(request)
                assert replies, "Unexpected model request/replay"
                response, entered, released = replies.pop(0)
                if response == DONE:
                    response = finish_response(request)
                entered.set()
                assert released.wait(30), "Fixture did not release model wait"
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(response)}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as error:
                errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"), RNA_MODEL="scripted", RNA_API_KEY="test",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}", RNA_BROWSER_EXECUTABLE="/missing/browser")
    for key in ("RNA_THINKING", "RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE"):
        env.pop(key, None)

    def command(action, *args):
        return ["dotnet", str(CLI), action, "--workspace", str(workspace), "--jsonl", *args]

    def cli(action, *args, code=0):
        result = subprocess.run(command(action, *args), cwd=REPO, env=env, text=True, capture_output=True, timeout=15)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"] if result.stdout.strip() else None

    def inspect(session):
        return cli("inspect", "--session", session)

    def history(session):
        for path in (root / "state").rglob("*.events.jsonl"):
            rows = [json.loads(line) for line in path.read_text().splitlines()]
            if rows[0]["SessionId"] == session:
                return rows
        raise AssertionError("Canonical session events missing")

    def control(snapshot):
        return ("--session", snapshot["Id"], "--run", snapshot["RunId"], "--cursor", snapshot["Cursor"])

    def start(action, *args):
        return subprocess.Popen(command(action, *args), cwd=REPO, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def finish(process, code):
        out, err = process.communicate(timeout=12)
        assert process.returncode == code, (process.returncode, out, err, errors)
        return json.loads(out.splitlines()[-1])

    process = None
    try:
        reply(call("files.create", relativePath="note.txt", text="ONCE"))
        entered, released = reply(call("files.create", relativePath="late.txt", text="MUST_NOT_RUN"), True)
        process = start("run", "--message", "Create and read note.txt.", "--min-writes", "1", "--min-reads", "1",
                        "--max-iterations", "4", "--max-tool-steps", "4", "--tool-result-role", "tool", "--instruction-role", "system",
                        "--auto-confirm", "on")
        assert entered.wait(12), "CLI did not reach the second model wait"
        session = cli("sessions")["sessions"][0]["Id"]
        running = inspect(session)
        assert running["IterationsUsed"] == 2 and running["ToolStepsUsed"] == 1 and running["Status"] == "running"
        wrong = dict(running, RunId="wrong-run")
        assert cli("cancel", *control(wrong), code=3)["Status"] == "StaleState"
        assert cli("cancel", *control(running))["Status"] == "Requested"
        stopped_result = finish(process, 130); process = None
        assert stopped_result["type"] == "run.cancelled" and stopped_result["data"]["health"] == "Clean"
        released.set()
        stopped = inspect(session)
        assert stopped["Status"] == "cancelled" and stopped["RunId"] == running["RunId"]
        assert not (workspace / "late.txt").exists() and (workspace / "note.txt").read_text() == "ONCE"
        assert cli("cancel", *control(stopped))["Status"] == "AlreadyStopped"
        assert cli("cancel", *control(running), code=3)["Status"] == "StaleState"
        cli("resume", "--continue", *control(stopped), "--max-iterations", "10", code=2)
        cli("resume", "--continue", *control(stopped), "--context-tokens", "8192", code=5)
        cli("resume", "--continue", *control(running), code=5)
        assert len(requests) == 2 and inspect(session)["Cursor"] == stopped["Cursor"]

        entered, released = reply(call("common.resources_read", target="note.txt"), True)
        reply(DONE)
        process = start("resume", "--continue", *control(stopped))
        assert entered.wait(10)
        assert cli("cancel", *control(stopped), code=3)["Status"] == "StaleState", "Old cancellation targeted a later invocation"
        assert inspect(session)["RunId"] == running["RunId"]
        released.set()
        final = finish(process, 0)["data"]; process = None
        assert final["RunId"] == running["RunId"] and final["IterationsUsed"] == 4 and final["ToolStepsUsed"] == 2
        assert final["verifiedFileChanges"] == 1 and final["acceptance"] == "passed" and final["completeFileReads"] == 1
        assert final["ToolCounts"]["WriteOk"] == 1
        assert inspect(session)["Limits"] == running["Limits"]
        cli("resume", "--continue", *control(stopped), code=5)
        cli("resume", "--continue", *control(inspect(session)), code=5)
        assert len(requests) == 4 and (workspace / "note.txt").read_text() == "ONCE"
        context = requests[2]["messages"]
        assert sum(message["role"] == "assistant" and bool(message.get("tool_calls")) for message in context) == 1
        assert any(message["role"] == "tool" for message in context), "Completed call/result lost on continuation"
        assert sum(message.get("content") == "Create and read note.txt." for message in context) == 1
        transitions = [event["Type"] for event in history(session) if event["Type"] in ("turn.started", "turn.ended")]
        assert transitions == ["turn.started", "turn.ended", "turn.started", "turn.ended"], transitions

        # Process death while waiting on the model consumes its iteration. The
        # active run lease prevents concurrent continue; no cancelled input replay.
        entered, released = reply(DONE, True)
        process = start("run", "--message", "Wait through a process restart.", "--max-iterations", "2")
        assert entered.wait(10)
        session2 = next(item["Id"] for item in cli("sessions")["sessions"] if item["Id"] != session)
        checkpoint = inspect(session2)
        cli("resume", "--continue", *control(checkpoint), code=5)
        process.kill(); process.communicate(timeout=5); process = None
        released.set()
        assert cli("cancel", *control(checkpoint), code=3)["Status"] == "Unconfirmed"
        reply(DONE)
        result = cli("resume", "--continue", *control(checkpoint))
        assert result["RunId"] == checkpoint["RunId"] and result["IterationsUsed"] == 2
        facts = history(session2)
        started = {event["StepId"] for event in facts if event["Type"] == "step.started"}
        ended = [event for event in facts if event["Type"] == "step.ended"]
        assert started == {event["StepId"] for event in ended} and len(ended) == len(started)
        assert sum(event["Data"].get("Synthetic") is True and event["Data"].get("Status") == "interrupted" for event in ended) == 1

        entered, released = reply(DONE, True)
        process = start("run", "--message", "One iteration only.", "--max-iterations", "1")
        assert entered.wait(10)
        session3 = next(item["Id"] for item in cli("sessions")["sessions"] if item["Id"] not in (session, session2))
        checkpoint = inspect(session3)
        process.kill(); process.communicate(timeout=5); process = None
        released.set()
        count = len(requests)
        cli("resume", "--continue", *control(checkpoint), code=5)
        assert len(requests) == count and inspect(session3)["Cursor"] == checkpoint["Cursor"]

        # A confirmed mutation completes once; cancelling the following model
        # wait and continuing must preserve its effect and reservation accounting.
        reply(call("common.resources_read", target="note.txt"))
        reply(call("files.delete", relativePath="note.txt"))
        pending = cli("run", "--message", "Delete note.txt after approval.", "--max-iterations", "4",
                      "--tool-result-role", "tool", "--instruction-role", "system", code=3)
        approved_session = pending["SessionId"]
        pending_state = inspect(approved_session)
        assert cli("cancel", *control(pending_state))["Status"] == "AlreadyStopped"
        assert (workspace / "note.txt").exists()
        entered, released = reply(DONE, True)
        process = start("approve", "--session", approved_session, "--pending", pending["pendingId"])
        assert entered.wait(10)
        assert not (workspace / "note.txt").exists()
        approved_running = inspect(approved_session)
        assert cli("cancel", *control(approved_running))["Status"] == "Requested"
        cancelled = finish(process, 130)["data"]; process = None
        released.set()
        assert cancelled["ToolCounts"]["WriteOk"] == 1 and cancelled["health"] == "Clean"
        reply(DONE)
        completed = cli("resume", "--continue", *control(inspect(approved_session)))
        assert completed["RunId"] == cancelled["RunId"] and completed["ToolStepsUsed"] == cancelled["ToolStepsUsed"]
        assert completed["ToolCounts"]["WriteOk"] == 1 and not (workspace / "note.txt").exists()
        assert not replies and not errors, (replies, errors)
        print("PASS run control: exact cancellation, late response exclusion, same run/budget/effects/history, "
              "stale/conflicting continuation denial, owner loss, lease exclusion, exhausted limit and approved write")
    finally:
        for gate in gates:
            gate.set()
        if process is not None:
            process.kill(); process.communicate(timeout=5)
        server.shutdown(); server.server_close(); thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-run-control-") as folder:
        run_case(Path(folder))
