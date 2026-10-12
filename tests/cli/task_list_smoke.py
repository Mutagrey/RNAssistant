"""Shared task lists: durable revisions, frozen context, admission and uncertain effects.

Uses scripted HTTP through the production CLI; this is not model-quality evidence.
"""
import copy
import json
import os
import re
import subprocess
import threading
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call


TOOL = "common.task_list_set"


def task(request):
    contexts = [json.loads(message["content"].split("\n", 1)[1])
                for message in request["messages"]
                if (message.get("content") or "").startswith("RUNTIME_CONTEXT:\n")]
    return next((context["active_task_list"] for context in contexts if "active_task_list" in context), None)


def run_case(root, mode, role):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir(parents=True)
    (workspace / "notes.txt").write_text("KEEP_ORIGINAL")
    requests, responses, errors = [], [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                assert responses, "Unexpected model request"
                body = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": json.dumps(responses.pop(0))}}]}).encode()
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
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_MODEL="scripted", RNA_API_KEY="test",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}", RNA_BROWSER_EXECUTABLE="/missing/browser")
    for name in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(name, None)

    def cli(command, *extra, code=0):
        result = subprocess.run(["dotnet", str(CLI), command, "--workspace", str(workspace), "--jsonl", *extra],
                                cwd=REPO, env=env, text=True, capture_output=True, timeout=45, check=False)
        assert result.returncode == code, (result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    def run(*extra, code=0):
        permissions = ["--read-only"] if mode == "plan" else []
        value = cli("run", "--mode", mode, "--tool-result-role", role, "--instruction-role", "system",
                    "--message", "Keep an accurate task list; do not modify notes.txt.",
                    "--max-iterations", "12", "--max-tool-steps", "12", *permissions, *extra, code=code)
        assert not responses and not errors, (responses, errors)
        return value

    def inspect(session):
        return cli("inspect", "--session", session)["taskList"]

    def journal():
        path = next(state.rglob("mutation-attempts.jsonl"))
        return path, [json.loads(line) for line in path.read_text().splitlines()]

    def inject_attempt(dispatch):
        path, records = journal()
        record = copy.deepcopy(next(item for item in records if item["Operation"] == TOOL))
        record.update(AttemptId="ma_" + uuid.uuid4().hex, ExpectedRevision=None,
                      State="Prepared", LinkedAuthorityCommitId=None)
        with path.open("a") as stream:
            stream.write(json.dumps(record) + "\n")
            if dispatch:
                record["State"] = "DispatchMayHaveOccurred"
                stream.write(json.dumps(record) + "\n")
        return record["AttemptId"]

    try:
        assert TOOL in cli("env", "--mode", mode)["capabilities"]
        assert TOOL not in cli("env", "--mode", "chat")["capabilities"]
        responses.extend([
            call(TOOL, action="save", goal="Review source", steps=[
                {"text": "Read source", "status": "in_progress", "note": "Need exact text"},
                {"text": "Explain result"}]),
            call("common.resources_read", target="notes.txt"),
            call(TOOL, action="save", goal="Review source", steps=[
                {"text": "Explain result"}, {"text": "Read source"}], reason="Read before explaining"),
            call(TOOL, action="update_statuses", updates=[{"index": 32, "status": "completed"}]),
            call(TOOL, action="update_statuses", updates=[{"index": 2, "status": "completed", "note": "KEEP_ORIGINAL observed"}]),
            call(TOOL, action="close", outcome="blocked", reason="Await a new requirement"),
            DONE])
        first = run("--min-reads", "1")
        session = first["SessionId"]
        assert first["verifiedFileChanges"] == 0, first
        blocked = inspect(session)
        assert blocked == task(requests[-1]), (blocked, task(requests[-1]))
        assert blocked["status"] == "blocked" and blocked["blocker"] == "Await a new requirement"
        assert blocked["statusSource"] == "agent_assessment"
        assert blocked["steps"][0]["status"] == "pending"
        assert blocked["steps"][1]["note"] == "KEEP_ORIGINAL observed"
        reordered = task(requests[3])
        assert reordered["steps"][1]["status"] == "in_progress"
        assert reordered["steps"][1]["note"] == "Need exact text"
        assert task(requests[4]) == reordered, "Invalid indexes must not append a revision"
        assert "task_list_status_updates_invalid" in json.dumps(requests[4])
        assert "currentTaskList" in json.dumps(requests[4])

        count = len(requests)
        responses.extend([
            call(TOOL, action="update_statuses", updates=[{"index": 1, "status": "pending", "note": "Report remaining work"}]),
            call(TOOL, action="close", outcome="completed"), DONE])
        run("--session", session)
        assert task(requests[count]) == blocked, "A fresh process must retain the active list independently of old history"
        assert inspect(session) is None and task(requests[-1]) is None
        path, attempts = journal()
        latest = {item["AttemptId"]: item for item in attempts if item["Operation"] == TOOL}
        assert sum(item["State"] == "Resolved" for item in latest.values()) == 6, latest
        assert sum(item["State"] == "AbandonedBeforeDispatch" for item in latest.values()) == 1, latest
        assert all(item["ScopeId"] == {"kind": "conversation", "id": session} for item in latest.values())

        # Simulate death after authority commit but before the journal terminal:
        # only the journal link is recovered; the already saved revision is not repeated.
        committed = next(item for item in reversed(attempts) if item["Operation"] == TOOL and item["State"] == "Resolved")
        path.write_text("".join(json.dumps(item) + "\n" for item in attempts if item is not committed))
        responses.append(DONE)
        run("--session", session)
        recovered = [item for item in journal()[1] if item["AttemptId"] == committed["AttemptId"]][-1]
        assert recovered["State"] == "Resolved" and recovered["LinkedAuthorityCommitId"] == committed["LinkedAuthorityCommitId"]
        assert inspect(session) is None

        # A prepared but undispatched operation is abandoned on restart, never replayed.
        prepared = inject_attempt(False)
        responses.append(DONE)
        run("--session", session)
        assert inspect(session) is None
        assert [item for item in journal()[1] if item["AttemptId"] == prepared][-1]["State"] == "AbandonedBeforeDispatch"

        # Unknown effects stay unknown: context cannot claim the old list and
        # another save is rejected before dispatch. No guessed restoration.
        dispatched = inject_attempt(True)
        assert inspect(session)["available"] is False
        responses.extend([call(TOOL, action="save", goal="Forbidden replay", steps=[{"text": "Do not create"}]), DONE])
        run("--session", session)
        assert task(requests[-1])["available"] is False
        assert "task_list_authority_unavailable" in json.dumps(requests[-1])
        assert [item for item in journal()[1] if item["AttemptId"] == dispatched][-1]["State"] == "Resolved"
        assert inspect(session)["available"] is False

        if mode == "agent":
            responses.extend([call(TOOL, action="save", goal="Only assessment", steps=[{"text": "Assessed", "status": "completed"}]), DONE])
            result = run("--min-writes", "1", code=5)
            assert result["verifiedFileChanges"] == 0

        # A definite refusal can be retried after its exact logical state changes.
        # Start a new conversation: the uncertain list above remains unavailable.
        close = call(TOOL, action="close", outcome="completed")
        responses.extend([close,
            call(TOOL, action="save", goal="Record review", steps=[{"text": "Reviewed", "status": "completed"}]),
            close, DONE])
        repaired = run()
        assert repaired["ToolCounts"]["WriteOk"] == 2, repaired
        assert repaired["ToolCounts"]["WriteError"] == 1, repaired
        assert inspect(repaired["SessionId"]) is None and task(requests[-1]) is None
        assert inspect(session)["available"] is False, "A different conversation cannot repair uncertain state"
        raw = json.dumps(requests)
        assert not re.search(r"(?:tasks_|step_)[0-9a-f]{32}|rna://|artifactId", raw), "Runtime identity leaked into model context"
        if role == "tool":
            assert any(message.get("role") == "tool" for request in requests for message in request["messages"])
        assert (workspace / "notes.txt").read_text() == "KEEP_ORIGINAL"
        print(f"PASS task list {mode}/{role}: persistence, context, admission, exact state repair and interrupted effects")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-task-lists-") as temp:
        for mode, role in (("plan", "user"), ("agent", "tool")):
            run_case(Path(temp) / mode, mode, role)
