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

SAVE, RESTORE, DELETE = ("common.plan_doc_" + name for name in ("save", "restore", "delete"))
FIND, READ = "common.resources_find", "common.resources_read"
V1, V2 = "  # EXACT_PLAN_V1\r\n\n- Review source  \n", "# EXACT_PLAN_V2\n- Review and repair\n"


def plan(request):
    contexts = [json.loads(message["content"].split("\n", 1)[1])
                for message in request["messages"]
                if (message.get("content") or "").startswith("RUNTIME_CONTEXT:\n")]
    return next((item["active_plan"] for item in contexts if "active_plan" in item), None)


def terminal(request):
    message = request["messages"][-1]
    content = message["content"].removeprefix("TOOL_RESULT:\n")
    return json.loads(content)


def save(markdown, title="Shared plan", **extra):
    return call(SAVE, title=title, markdown=markdown, status="ready", **extra)


def run_case(root, role):
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
                    "--max-iterations", iterations, "--max-tool-steps", "12", "--read-only",
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
        record = copy.deepcopy(next(item for item in attempts if item["Operation"] == SAVE))
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
        assert {SAVE, RESTORE, DELETE} <= set(cli("env", "--mode", "plan")["capabilities"])
        agent_tools = cli("env", "--mode", "agent")["capabilities"]
        assert SAVE in agent_tools and RESTORE not in agent_tools and DELETE not in agent_tools
        assert SAVE not in cli("env", "--mode", "chat")["capabilities"]
        first, wire = run([save(V1, startNew=None), call(READ, type="plan", target=None, version=None), DONE])
        session = first["SessionId"]
        assert first["verifiedFileChanges"] == 0 and first["completeFileReads"] == 0
        assert terminal(wire[-1])["data"]["text"] == V1, terminal(wire[-1])
        assert terminal(wire[-1])["data"]["historical"] is False
        assert inspect(session) == plan(wire[-1]) == {"available": True, "title": "Shared plan", "status": "ready", "version": 1}
        assert "EXACT_PLAN" not in json.dumps(plan(wire[-1])), "Pinned context must contain metadata only"

        _, wire = run([call(READ, type="plan"), save(V2), call(READ, type="plan", version=1),
                       call(RESTORE, version=1), call(READ, type="plan"), DONE], session)
        assert plan(wire[0])["version"] == 1, "Selection must survive a new process"
        assert terminal(wire[3])["data"]["text"] == V1 and terminal(wire[3])["data"]["historical"] is True
        assert plan(wire[3])["version"] == 2, "Historical reads cannot replace current selection"
        assert terminal(wire[-1])["data"]["text"] == V1 and plan(wire[-1])["version"] == 3
        stale = [json.loads(message["content"].split("\n", 1)[1]) for message in wire[2]["messages"]
                 if (message.get("content") or "").startswith("TOOL_INTERACTION")]
        assert any(item.get("observation", {}).get("state") == "Superseded" and
                   item["observation"]["bodyIncluded"] is False for item in stale), stale

        # A second chat in the same workspace discovers shared authority, then advances it.
        second, wire = run([call(FIND, type="plan"), call(READ, type="plan", target="Shared plan"), save(V2), DONE])
        assert terminal(wire[1])["data"]["items"][0]["target"] == "Shared plan"
        assert "text" not in terminal(wire[1])["data"]["items"][0]
        assert plan(wire[-1])["version"] == 4
        _, wire = run([save("MUST_NOT_BE_WRITTEN"), call(READ, type="plan"), save(V1), DONE], session)
        assert plan(wire[0])["available"] is False
        assert terminal(wire[1])["data"]["code"] == "stale_plan_revision", terminal(wire[1])
        assert terminal(wire[2])["data"]["text"] == V2
        assert plan(wire[-1])["version"] == 5

        # Keep a separate lineage, reject ambiguous titles, delete only the selected lineage.
        batch = call(READ, type="plan", target="Shared plan")
        batch["tool_calls"].append(call(READ, type="plan")["tool_calls"][0])
        _, wire = run([save("# Separate", title="Other plan", startNew=True), batch, save("# Separate"), call(FIND, type="plan"),
                       call(READ, type="plan", target="Shared plan"), call(DELETE), call(FIND, type="plan"),
                       call(READ, type="plan", target="Shared plan"), DONE], session)
        assert terminal(wire[2])["data"]["text"] == "# Separate", "Implicit selection must stay frozen within a read batch"
        assert plan(wire[2])["title"] == "Other plan"
        assert len(terminal(wire[4])["data"]["items"]) == 2
        assert terminal(wire[5])["data"]["code"] == "plan_target_ambiguous"
        assert len(terminal(wire[7])["data"]["items"]) == 1
        assert terminal(wire[-1])["data"]["code"] == "repeated_tool_failure", terminal(wire[-1])
        # The failed ambiguous selector has no exact recovery dependency. Deleting
        # another selected lineage cannot release it inside the same logical run.
        # A fresh user turn can read/select the now unique title normally.
        _, wire = run([call(READ, type="plan", target="Shared plan"), DONE], session)
        assert plan(wire[-1])["version"] == 5
        assert any(item.get("effect", {}).get("outcome") == "Restored" for item in commits())

        other = root / "other-workspace"
        other.mkdir()
        _, wire = run([call(FIND, type="plan"), call(READ, type="plan", target="Shared plan"), DONE], directory=other)
        assert terminal(wire[1])["data"]["complete"] is True and terminal(wire[1])["data"]["items"] == []
        assert terminal(wire[-1])["status"] == "error"

        # Publication is durable before the chat event; a missing terminal journal entry only repairs its link.
        path, attempts = journal()
        committed = next(item for item in reversed(attempts) if item["Operation"] == SAVE and item["State"] == "Resolved")
        before = len(commits())
        path.write_text("".join(json.dumps(item) + "\n" for item in attempts if item is not committed))
        run([DONE], session)
        recovered = [item for item in journal()[1] if item["AttemptId"] == committed["AttemptId"]][-1]
        assert recovered["LinkedAuthorityCommitId"] == committed["LinkedAuthorityCommitId"] and len(commits()) == before
        prepared = inject(False)
        run([DONE], session)
        assert [item for item in journal()[1] if item["AttemptId"] == prepared][-1]["State"] == "AbandonedBeforeDispatch"
        assert inspect(session)["version"] == 5

        dispatched = inject(True)
        assert inspect(session)["available"] is False
        _, wire = run([save("DO_NOT_REPLAY"), call(READ, type="plan"), DONE], session)
        assert plan(wire[-1])["available"] is False
        assert "plan_snapshot_unavailable" in json.dumps(wire[1])
        assert terminal(wire[-1])["data"]["code"] == "resource_unavailable"
        assert [item for item in journal()[1] if item["AttemptId"] == dispatched][-1]["State"] == "Resolved"

        # Shared Plans are allowed in Agent/read-only; they cannot satisfy actual file-write acceptance.
        value, _ = run([save("# Assessment only"), DONE], mode="agent", code=5, extra=("--min-writes", "1"), iterations="2")
        assert value["verifiedFileChanges"] == 0
        plan_attempts = [item for item in journal()[1] if item["Operation"] in (SAVE, RESTORE, DELETE)]
        assert all(item["ScopeId"]["kind"] == "workspace" for item in plan_attempts)
        raw = json.dumps(requests)
        assert not re.search(r"rna://|plan_doc_[0-9a-f]{64}|artifactId|restoredFromArtifactId", raw), "Runtime identity leaked"
        if role == "tool":
            assert any(message["role"] == "tool" for request in requests for message in request["messages"])
        assert [path.name for path in workspace.iterdir() if path.name != ".rnassistant"] == ["notes.txt"]
        assert (workspace / "notes.txt").read_text() == "KEEP_ORIGINAL"
        print(f"PASS workspace Plan/{role}: exact versions, historical/current evidence, shared authority, stale denial, "
              "restore/delete, process recovery, scope isolation, native projection and file-count isolation")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-plans-") as folder:
        for role in ("tool", "user"):
            run_case(Path(folder) / role, role)
