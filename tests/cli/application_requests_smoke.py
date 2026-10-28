"""SubmitInput receipts and snapshot/event reconnection through production CLI.

A scripted HTTP endpoint controls waits/crashes; this is not LLM qualification.
"""
import base64
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call


def run_case(root):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    requests, responses, errors = [], [], []
    blocked, release = threading.Event(), threading.Event()

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                assert responses, "Duplicate input started another model request"
                response, pause = responses.pop(0)
                if pause:
                    blocked.set()
                    assert release.wait(25), "Test did not release model wait"
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(response)}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass  # Expected after the process-abandonment case.
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
    for key in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(key, None)
    offline = {key: value for key, value in env.items() if key not in ("RNA_MODEL", "RNA_BASE_URL", "RNA_API_KEY", "OPENAI_API_KEY")}

    def command(action, *args):
        words = action.split()
        return ["dotnet", str(CLI), *words, "--workspace", str(workspace), "--jsonl", *args]

    def cli(action, *args, code=0, environment=None):
        result = subprocess.run(command(action, *args), cwd=REPO, env=environment or env,
                                text=True, capture_output=True, timeout=15)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr, errors)
        return [json.loads(line) for line in result.stdout.splitlines()], result.stderr

    def data(action, *args, **kwargs):
        rows, _ = cli(action, *args, **kwargs)
        return rows[-1]["data"]

    def start(args):
        return subprocess.Popen(command("run", *args), cwd=REPO, env=env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def finish(process):
        out, err = process.communicate(timeout=20)
        assert process.returncode == 0, (out, err, errors)
        return [json.loads(line) for line in out.splitlines()]

    def sequence(cursor):
        body = cursor.removeprefix("events1.")
        return int(base64.urlsafe_b64decode(body + "=" * (-len(body) % 4)).decode().split(":")[1])

    def events(session, after, through=None, limit="3", code=0):
        return data("events", "--session", session, "--after", after, "--limit", limit,
                    *(["--through", through] if through else []), code=code, environment=offline)

    process = None
    try:
        initial = data("session create", "--title", "Reconnect test", environment=offline)
        session, initial_cursor = initial["Id"], initial["Cursor"]
        assert sequence(initial_cursor) == initial["Revision"]
        args = ("--session", session, "--request-id", "create-once", "--message", "Create note.txt once.",
                "--mode", "agent", "--auto-confirm", "on", "--min-writes", "1", "--instruction-role", "system", "--tool-result-role", "tool")
        responses.extend([(call("core.write", target="note.txt", content="WRITTEN_ONCE"), True), (DONE, False)])
        process = start(args)
        assert blocked.wait(10), "Original run did not reach the controlled model wait"
        running = data("inspect", "--session", session, environment=offline)
        assert running["Status"] == "running"
        duplicate, _ = cli("run", *args, environment=offline)
        assert len(duplicate) == 1 and duplicate[0]["type"] == "input.already_accepted", duplicate
        receipt = duplicate[0]["data"]
        assert receipt["RequestId"] == "create-once" and receipt["DeliveryRecorded"]
        assert len(requests) == 1 and not (workspace / "note.txt").exists()
        for option, value in [("--message", "Different request"), ("--mode", "plan"), ("--min-writes", "2")]:
            conflicting = list(args)
            conflicting[conflicting.index(option) + 1] = value
            _, error = cli("run", *conflicting, code=2, environment=offline)
            assert "request_id_conflict" in error
        assert data("inspect", "--session", session)["Cursor"] == running["Cursor"], "Duplicate/conflict must not append events"

        # Page a fixed prefix while the original run finishes beyond its high-water.
        first_page = events(session, initial_cursor)
        high_water, cursor = first_page["HighWater"], first_page["NextCursor"]
        collected = first_page["Events"][:]
        assert first_page["HasMore"] and sequence(high_water) == running["Revision"]
        release.set()
        completed = finish(process)
        process = None
        result = completed[-1]["data"]
        assert result["acceptance"] == "passed" and result["verifiedFileChanges"] == 1
        assert result["inputReceipt"]["RunId"] == receipt["RunId"]
        assert next(item["data"] for item in completed if item["type"] == "input.accepted")["AcceptedCursor"] == receipt["AcceptedCursor"]
        assert all(item["data"]["Cursor"] for item in completed if item["type"].startswith("tool."))
        while cursor != high_water:
            page = events(session, cursor, high_water)
            assert not page["ResyncRequired"] and page["HighWater"] == high_water
            collected.extend(page["Events"])
            cursor = page["NextCursor"]
        assert [item["Sequence"] for item in collected] == list(range(sequence(initial_cursor) + 1, sequence(high_water) + 1))
        assert "Create note.txt" not in json.dumps(collected) and "Payload" not in json.dumps(collected)
        tail = events(session, high_water, limit="100")
        assert tail["Events"] and not tail["HasMore"] and tail["HighWater"] == result["Cursor"]
        assert any("tool.result.recorded" in item["Operations"] for item in tail["Events"])
        assert not events(session, tail["NextCursor"])["Events"]

        # A later turn and an external edit cannot make a duplicate re-run or re-grade the old task.
        responses.extend([(call("core.read", target="note.txt"), False), (DONE, False)])
        data("run", "--session", session, "--request-id", "later-turn", "--message", "Read the note.")
        (workspace / "note.txt").write_text("EXTERNAL_EDIT")
        count = len(requests)
        before = data("inspect", "--session", session)["Cursor"]
        old = data("run", *args, environment=offline)
        assert old == receipt and len(requests) == count
        assert data("inspect", "--session", session)["Cursor"] == before
        assert (workspace / "note.txt").read_text() == "EXTERNAL_EDIT"
        for path in state.rglob("*.sqlite*"):
            path.unlink()  # Disposable derived projection, never the event log.
        assert data("run", *args, environment=offline) == receipt

        # Reset/wrong-session/future/corrupt history gives explicit resync, never writes.
        other = data("session create")
        assert events(other["Id"], before, code=3)["Reason"] == "DifferentSession"
        assert events(session, "garbage", code=3)["Reason"] == "InvalidCursor"
        assert events(session, before, initial_cursor, code=3)["Reason"] == "InvalidCursor"
        raw = base64.urlsafe_b64decode(before[8:] + "=" * (-len(before[8:]) % 4)).decode().split(":")
        raw[2] = "0" * 64
        forged = "events1." + base64.urlsafe_b64encode(":".join(raw).encode()).decode().rstrip("=")
        assert events(session, forged, code=3)["Reason"] == "HistoryChanged"
        cli("events", "--session", session, "--after", before, "--limit", "101", code=2)
        source = next(path for path in state.rglob("*.events.jsonl")
                      if json.loads(path.read_text().splitlines()[0])["SessionId"] == session)
        retained = source.read_bytes()
        # A crash immediately after acceptance leaves only the exact canonical
        # prefix. Receipt lookup must not deliver that input or reconstruct a run.
        accepted_prefix = b"".join(retained.splitlines(keepends=True)[:receipt["AcceptedSequence"]])
        source.write_bytes(accepted_prefix)
        undelivered = data("run", *args, environment=offline)
        assert undelivered == dict(receipt, DeliveryRecorded=False)
        assert source.read_bytes() == accepted_prefix and len(requests) == count
        assert events(session, before, code=3)["Reason"] == "HistoryChanged"
        source.write_bytes(retained)
        assert data("run", *args, environment=offline) == receipt
        source.write_bytes(retained + b'{"incomplete":')
        assert events(session, before, code=3)["Reason"] == "HistoryUnavailable"
        source.write_bytes(retained)
        assert not events(session, before)["ResyncRequired"]
        source.write_bytes(b'{}\n')
        assert events(session, before, code=3)["Reason"] == "HistoryUnavailable"
        source.write_bytes(retained)

        # A killed process retains its receipt. Duplicate intake does not resume it.
        blocked.clear(); release.clear()
        crash_args = ("--session", session, "--request-id", "abandoned-input", "--message", "Wait for the model.")
        responses.append((DONE, True))
        process = start(crash_args)
        assert blocked.wait(10)
        process.kill(); process.communicate(timeout=5); process = None
        count = len(requests)
        killed = data("run", *crash_args, environment=offline)
        assert killed["RequestId"] == "abandoned-input" and killed["DeliveryRecorded"]
        assert data("inspect", "--session", session)["Status"] == "running"
        release.set()
        data("resume", "--session", session, code=3, environment=offline)
        assert data("inspect", "--session", session)["Status"] == "interrupted"
        assert data("run", *crash_args, environment=offline) == killed and len(requests) == count
        assert (workspace / "note.txt").read_text() == "EXTERNAL_EDIT"
        assert not responses and not errors, (responses, errors)
        assert "create-once" not in json.dumps(requests) and "RequestFingerprint" not in json.dumps(requests)
        print("PASS application requests: active/completed/old/crashed duplicates, conflicts, offline receipt, "
              "canonical snapshot/events, bounded pinned pages, projection rebuild and explicit resync without replay")
    finally:
        release.set()
        if process is not None:
            process.kill(); process.communicate(timeout=5)
        server.shutdown(); server.server_close(); thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-application-") as folder:
        run_case(Path(folder))
