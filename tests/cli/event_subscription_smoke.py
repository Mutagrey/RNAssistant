"""Canonical event following across CLI processes; controlled HTTP, not LLM qualification."""
import json
import os
import queue
import signal
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE


def run_case(root):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    requested, release = threading.Event(), threading.Event()
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            requested.set()
            assert release.wait(15)
            body = json.dumps({"choices": [{"message": {"content": json.dumps(DONE)}}]}).encode()
            self.send_response(200); self.send_header("Content-Length", str(len(body)))
            self.end_headers(); self.wfile.write(body)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
               RNA_MODEL="scripted", RNA_API_KEY="test")
    for key in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(key, None)

    def command(action, *args):
        return ["dotnet", str(CLI), *action.split(), "--workspace", str(workspace), "--jsonl", *args]

    def cli(action, *args, code=0):
        result = subprocess.run(command(action, *args), cwd=REPO, env=env, text=True, capture_output=True, timeout=10)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr)
        return [json.loads(line) for line in result.stdout.splitlines()]

    processes = []

    def follow(session, cursor, *args):
        process = subprocess.Popen(command("events", "--session", session, "--after", cursor,
                                           "--follow", "--limit", "2", *args),
                                   cwd=REPO, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        processes.append(process)
        rows = queue.Queue()

        def consume():
            for line in process.stdout:
                rows.put(json.loads(line))

        threading.Thread(target=consume, daemon=True).start()
        return process, rows

    try:
        snapshot = cli("session create")[-1]["data"]
        session, cursor = snapshot["Id"], snapshot["Cursor"]
        source = next(state.rglob("*.events.jsonl"))
        original = source.read_bytes()
        watcher, rows = follow(session, cursor)
        first = rows.get(timeout=10)
        assert first["type"] == "events" and first["data"]["Events"] == []
        assert source.read_bytes() == original, "subscribing cannot append lifecycle or recovery records"

        # Stopping a subscriber must not cancel a run owned by another process.
        runner = subprocess.Popen(command("run", "--session", session, "--message", "Reply done."),
                                  cwd=REPO, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        processes.append(runner)
        assert requested.wait(10), (runner.poll(), runner.communicate(timeout=2) if runner.poll() is not None else "model wait timed out")
        page = rows.get(timeout=10)["data"]
        assert 1 <= len(page["Events"]) <= 2
        watcher.send_signal(signal.SIGINT)
        assert watcher.wait(timeout=5) == 130
        assert runner.poll() is None, "subscriber cancellation cannot stop the agent"
        release.set()
        out, err = runner.communicate(timeout=10)
        assert runner.returncode == 0, (out, err)
        final = cli("inspect", "--session", session)[-1]["data"]
        retained = source.read_bytes()

        # Resume from the snapshot; bounded pages have exclusive continuity.
        watcher, rows = follow(session, cursor)
        sequences, position = [], cursor
        while position != final["Cursor"]:
            row = rows.get(timeout=10)
            assert row["type"] == "events", row
            page = row["data"]
            assert page["After"] == position and page["HighWater"] == final["Cursor"]
            assert 1 <= len(page["Events"]) <= 2
            position = page["NextCursor"]
            sequences.extend(item["Sequence"] for item in page["Events"])
            assert all(set(item) == {"Sequence", "EventId", "CreatedUtc", "Type", "RunId", "TurnId", "StepId", "Operations"}
                       for item in page["Events"]), "no event/payload bodies in notices"
        assert sequences == list(range(snapshot["Revision"] + 1, final["Revision"] + 1))
        # No replay or silent repair on corrupt history while following.
        source.write_bytes(retained + b'{"unfinished":')
        resync = rows.get(timeout=10)
        assert resync["type"] == "events.resync_required" and resync["data"]["Reason"] == "HistoryUnavailable"
        assert watcher.wait(timeout=5) == 3
        assert source.read_bytes() == retained + b'{"unfinished":'
        source.write_bytes(retained)

        timeout = cli("events", "--session", session, "--after", final["Cursor"], "--follow", "--timeout-seconds", "1")
        assert len(timeout) == 1 and timeout[0]["data"]["Events"] == []
        cli("events", "--session", session, "--after", cursor, "--follow", "--through", final["Cursor"], code=2)
        assert len(requests) == 1 and source.read_bytes() == retained
        print("PASS event subscription: bounded reconnect pages, Ctrl+C isolation, timeout, explicit corruption resync, no replay")
    finally:
        release.set()
        for process in processes:
            if process.poll() is None:
                process.kill(); process.wait(timeout=5)
        server.shutdown()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-events-") as root:
        run_case(Path(root))
