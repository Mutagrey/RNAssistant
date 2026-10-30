"""Revisioned workspace Plans through the real CLI/runtime with scripted HTTP.

Contract evidence only; this does not qualify an LLM or Office host.
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
from conversation_transport import conversation_content, reply_to_title

SAVE, PATCH, RESTORE, DELETE = "core.write", "core.patch", "resources.restore", "resources.delete"
OP_SAVE, OP_RESTORE, OP_DELETE = ("plan.document." + name for name in ("save", "restore", "delete"))
FIND, READ = "core.find", "core.read"
V1, V2 = "  # EXACT_PLAN_V1\r\n\n- Review source  \n", "# EXACT_PLAN_V2\n- Review and repair\n"


def plan(request):
    contexts = [json.loads(message["content"].split("\n", 1)[1])
                for message in request["messages"]
                if (message.get("content") or "").startswith("RUNTIME_CONTEXT:\n")]
    return next((item["active_plan"] for item in contexts if "active_plan" in item), None)


def terminal(request):
    for message in reversed(request["messages"]):
        content = message.get("content") or ""
        if content.startswith("TOOL_RESULT:\n"):
            content = content.split("\n", 1)[1]
        try:
            value = json.loads(content)
        except (ValueError, TypeError):
            continue
        if isinstance(value, dict) and "tool_call_id" in value and "status" in value:
            return value
    raise AssertionError("Tool result missing")


def source(markdown, title="Shared plan", status="ready"):
    return "---\ntitle: " + json.dumps(title) + "\nstatus: " + status + "\n---\n" + markdown


def save(markdown, title="Shared plan"):
    return call(SAVE, target="plan: " + title, content=source(markdown, title))


def patch(title, before, after):
    return [{"target": "plan: " + title, "edits": [{"old": before, "new": after}]}]


def run_case(root, role):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir(parents=True)
    (workspace / "notes.txt").write_text("KEEP_ORIGINAL")
    requests, responses, errors = [], [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if reply_to_title(self, request):
                    return
                requests.append(request)
                assert responses, "Unexpected model request"
                body = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": conversation_content(request, responses.pop(0))}}]}).encode()
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

    def cli(command, *extra, code=0, directory=None):
        result = subprocess.run(["dotnet", str(CLI), command, "--workspace", str(directory or workspace),
                                "--jsonl", *extra], cwd=REPO, env=env, text=True, capture_output=True, timeout=45)
        assert result.returncode == code, (result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    def run(sequence, session=None, mode="plan", code=0, directory=None, extra=(), iterations="12"):
        start = len(requests)
        responses.extend(sequence)
        value = cli("run", "--mode", mode, "--tool-result-role", role, "--instruction-role", "system",
                    "--response-mode", "json_schema", "--message", "Save, read, restore or remove the requested Plan; preserve notes.txt.",
                    "--max-iterations", iterations, "--max-tool-steps", "12",
                    *(["--read-only"] if mode == "plan" else ["--auto-confirm", "on", "--mutation-policy", "ask"]),
                    *(["--session", session] if session else []), *extra, code=code, directory=directory)
        assert not responses and not errors, (responses, errors)
        return value, requests[start:]

    def inspect(session):
        return cli("inspect", "--session", session)["plan"]

    def journal():
        path = next(state.rglob("mutation-attempts.jsonl"))
        return path, [json.loads(line) for line in path.read_text().splitlines()]

    def commits():
        return [json.loads(line) for path in state.rglob("*.authority.jsonl") for line in path.read_text().splitlines()]

    def inject(dispatch):
        path, attempts = journal()
        record = copy.deepcopy(next(item for item in attempts if item["Operation"] == OP_SAVE))
        head = next(change["after"] for commit in reversed(commits()) for change in commit["headChanges"]
                    if change["identity"] == record["Target"])
        record.update(AttemptId="ma_" + uuid.uuid4().hex, ExpectedRevision=head["revision"]["revision"],
                      State="Prepared", LinkedAuthorityCommitId=None)
        with path.open("a") as stream:
            stream.write(json.dumps(record) + "\n")
            if dispatch:
                record["State"] = "DispatchMayHaveOccurred"
                stream.write(json.dumps(record) + "\n")
        return record["AttemptId"]

    try:
        assert {SAVE, PATCH} <= set(cli("env", "--mode", "plan")["capabilities"])
        assert RESTORE not in cli("env", "--mode", "plan")["capabilities"]
        first, wire = run([save(V1), call(READ, target="plan: Shared plan"), DONE])
        session = first["SessionId"]
        assert first["verifiedFileChanges"] == 0 and first["completeFileReads"] == 0
        assert terminal(wire[-1])["data"]["text"] == source(V1)
        assert inspect(session) == plan(wire[-1]) == {"available": True, "title": "Shared plan", "status": "ready", "version": 1}

        changed_body = V1.replace("- Review source  ", "- Review and repair")
        _, wire = run([call(READ, target="plan: Shared plan"),
                       call(PATCH, targets=patch("Shared plan", "- Review source  ", "- Review and repair")),
                       call(PATCH, targets=patch("Shared plan", "status: ready", "status: draft")),
                       call(READ, target="plan: Shared plan"), DONE], session)
        assert any("CURRENT_RESOURCE_VIEW" in (m.get("content") or "") and "Review and repair" in m["content"]
                   for m in wire[2]["messages"]), "First patch after-source must reach the next call without an extra read"
        assert terminal(wire[-1])["data"]["text"] == source(changed_body, status="draft")
        assert plan(wire[-1])["version"] == 3

        # Separate identities share the same source contract and package preparation.
        _, wire = run([save("# Other", title="Other plan"), call(READ, target="plan: Shared plan"),
                       call(PATCH, targets=patch("Shared plan", "status: draft", "status: ready") +
                           patch("Other plan", "# Other", "# Changed other")), DONE], session)
        assert [t["status"] for t in terminal(wire[-1])["data"]["targets"]] == ["Changed", "Changed"]
        _, wire = run([call(READ, target="plan: Shared plan"), call(READ, target="plan: Other plan"),
                       call(PATCH, targets=patch("Shared plan", "status: ready", "status: draft") +
                           patch("Other plan", "missing", "bad")),
                       call(READ, target="plan: Shared plan"), DONE], session)
        failed = terminal(wire[3])
        assert failed["status"] == "error" and failed["data"]["writeStarted"] is False
        assert terminal(wire[-1])["data"]["text"] == source(changed_body)

        _, wire = run([call("core.tools.read", id=RESTORE), call(RESTORE, target="plan: Shared plan", version=1),
                       call(READ, target="plan: Shared plan"), DONE], session, mode="agent")
        assert terminal(wire[-1])["data"]["text"] == source(V1)
        assert plan(wire[-1])["version"] == 5
        _, wire = run([call("core.tools.read", id=DELETE), call(READ, target="plan: Other plan"),
                       call(DELETE, target="plan: Other plan"), call(FIND, type="plan"), DONE], session, mode="agent")
        assert [i["target"] for i in terminal(wire[-1])["data"]["items"]] == ["plan: Shared plan"]
        run([call(READ, target="plan: Shared plan"), DONE], session)

        # Another chat advances shared authority; stale source cannot authorize overwrite.
        second, _ = run([call(READ, target="plan: Shared plan"), save(V2), DONE])
        _, wire = run([save("MUST_NOT_BE_WRITTEN"), call(READ, target="plan: Shared plan"), save(V1), DONE], session)
        assert terminal(wire[1])["status"] == "error"
        assert terminal(wire[2])["data"]["text"] == source(V2)
        assert plan(wire[-1])["version"] == 7
        assert any(c.get("effect", {}).get("outcome") == "Restored" for c in commits())

        other = root / "other-workspace"
        other.mkdir()
        _, wire = run([call(FIND, type="plan"), call(READ, target="plan: Shared plan"), DONE], directory=other, code=5)
        assert terminal(wire[1])["data"]["complete"] is True and terminal(wire[1])["data"]["items"] == []
        assert terminal(wire[-1])["status"] == "error"

        # A missing terminal journal entry repairs its link, never replays the write.
        path, attempts = journal()
        committed = next(item for item in reversed(attempts) if item["Operation"] == OP_SAVE and item["State"] == "Resolved")
        before = len(commits())
        path.write_text("".join(json.dumps(item) + "\n" for item in attempts if item is not committed))
        run([DONE], session)
        recovered = [item for item in journal()[1] if item["AttemptId"] == committed["AttemptId"]][-1]
        assert recovered["LinkedAuthorityCommitId"] == committed["LinkedAuthorityCommitId"] and len(commits()) == before
        prepared = inject(False)
        run([DONE], session)
        assert [item for item in journal()[1] if item["AttemptId"] == prepared][-1]["State"] == "AbandonedBeforeDispatch"
        assert inspect(session)["version"] == 7
        dispatched = inject(True)
        assert inspect(session)["available"] is False
        _, wire = run([save("DO_NOT_REPLAY"), call(READ, target="plan: Shared plan"), DONE], session, code=5)
        assert plan(wire[-1])["available"] is False
        assert terminal(wire[-1])["status"] == "error"
        assert [item for item in journal()[1] if item["AttemptId"] == dispatched][-1]["State"] == "Resolved"

        value, _ = run([save("# Assessment only", title="Assessment"), DONE], mode="agent", code=5, extra=("--min-writes", "1"), iterations="2")
        assert value["verifiedFileChanges"] == 0
        plan_attempts = [item for item in journal()[1] if item["Operation"] in (OP_SAVE, OP_RESTORE, OP_DELETE)]
        assert all(item["ScopeId"]["kind"] == "workspace" for item in plan_attempts)
        assert not re.search(r"rna://|plan_doc_[0-9a-f]{64}|artifactId|restoredFromArtifactId", json.dumps(requests)), "Runtime identity leaked"
        assert (workspace / "notes.txt").read_text() == "KEEP_ORIGINAL"
        print(f"PASS workspace Plan/{role}: sequential/batched source edits, refusal before write, shared state, "
              "restore/delete, recovery, scope and file-count isolation")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-plans-") as folder:
        for role in ("tool", "user"):
            run_case(Path(folder) / role, role)
