"""Real terminal/pipe CLI boundaries using controlled HTTP, not model qualification."""
import json
import os
import pty
import re
import select
import signal
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call
from mode_questions_smoke import QUESTIONS


class Terminal:
    def __init__(self, command, env):
        self.master, slave = pty.openpty()
        self.process = subprocess.Popen(command, cwd=REPO, env=env, stdin=slave,
                                        stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave)
        self.text, self.position = "", 0

    def pump(self):
        if select.select([self.master], [], [], 0.05)[0]:
            try:
                self.text += os.read(self.master, 65536).decode("utf-8", errors="replace")
            except OSError:
                pass

    def until(self, marker):
        deadline = time.monotonic() + 20
        while marker not in self.text[self.position:]:
            self.pump()
            assert self.process.poll() is None and time.monotonic() < deadline, self.text
        self.position = self.text.index(marker, self.position) + len(marker)

    def send(self, value):
        os.write(self.master, value)

    def finish(self, code):
        deadline = time.monotonic() + 20
        try:
            while self.process.poll() is None:
                self.pump()
                assert time.monotonic() < deadline, self.text
            self.pump()
            assert self.process.returncode == code, (self.process.returncode, self.text)
            return self.text
        finally:
            self.close()

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait()
        if self.master is not None:
            os.close(self.master)
            self.master = None


class Scenario:
    def __init__(self, root):
        self.workspace = root / "workspace"
        self.workspace.mkdir(parents=True)
        self.responses, self.requests, self.errors, self.terminals = [], [], [], []
        self.before_response = lambda: None
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                try:
                    assert self.path == "/v1/chat/completions"
                    owner.requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                    owner.before_response()
                    assert owner.responses, "Unexpected request after pause/terminal result"
                    response = owner.responses.pop(0)
                    body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(response)}}]}).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                except Exception as error:
                    owner.errors.append(str(error))
                    self.send_error(500)

            def log_message(self, *_args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.env = {key: value for key, value in os.environ.items() if not key.startswith("RNA_")}
        self.env.update(RNA_STATE_ROOT=str(root / "state"), RNA_MODEL="scripted", RNA_API_KEY="test",
                        RNA_BASE_URL=f"http://127.0.0.1:{self.server.server_port}", RNA_BROWSER_EXECUTABLE="/missing/browser")

    def command(self, *args):
        return ["dotnet", str(CLI), args[0], "--workspace", str(self.workspace), *args[1:]]

    def cli(self, *args, code=0, input=""):
        result = subprocess.run(self.command(*args), env=self.env, cwd=REPO, input=input,
                                text=True, capture_output=True, timeout=20)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr, self.errors)
        return result

    def json(self, *args, **kwargs):
        result = self.cli(*args, "--jsonl", **kwargs)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    def terminal(self, *args):
        terminal = Terminal(self.command(*args), self.env)
        self.terminals.append(terminal)
        return terminal

    def close(self):
        for terminal in self.terminals:
            if terminal.master is not None:
                terminal.close()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()


def questions_case(s):
    s.responses.extend([call("files.create", relativePath="once.txt", text="ONCE"),
                        call("common.questions_ask", questions=[*QUESTIONS,
                            dict(QUESTIONS[1], header="Notes", prompt="Any additional notes?", selection="single")])])
    terminal = s.terminal("run", "--interactive", "--message", "Create once and ask questions.",
                          "--max-iterations", "6", "--max-tool-steps", "6", "--context-tokens", "24000",
                          "--min-writes", "1", "--min-reads", "1", "--tool-result-role", "tool")
    terminal.until("Choice number")
    session = re.search(r"Session: (\w+)", terminal.text)[1]
    before = s.json("inspect", "--session", session)
    assert before["userInput"] and before["acceptance"]["State"] == "Pending"
    assert before["Presentation"]["Files"][0]["Path"] == "once.txt"
    terminal.send(b"9\n")
    terminal.until("Choose a listed number")
    terminal.until("Choice number")
    assert len(s.requests) == 2
    # Leaving a question does not choose the recommended option or write an answer.
    terminal.send(b"\x04")
    terminal.finish(3)
    assert s.json("inspect", "--session", session)["Revision"] == before["Revision"]
    s.responses.extend([call("common.resources_read", target="once.txt"), DONE])
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Choice number")
    terminal.send(b"1\n")
    terminal.until("Choices (e.g. 1,2)")
    terminal.send(b"1,2\n")
    terminal.until("Choice number")
    terminal.send(b"text: My explicit note\n")
    output = terminal.finish(0)
    after = s.json("inspect", "--session", session)
    assert after["userInput"] is None and after["acceptance"]["State"] == "Passed"
    assert after["RunId"] != before["RunId"] and after["Limits"] == before["Limits"]
    assert after["modelConfiguration"]["ContextWindowTokens"] == 24000
    assert after["Presentation"]["Files"] == before["Presentation"]["Files"], "Lost prior-question file effects"
    assert (s.workspace / "once.txt").read_text() == "ONCE" and len(s.requests) == 4
    assert "PLAN_ANSWERS:" in json.dumps(s.requests[2]) and "Risks" in json.dumps(s.requests[2])
    assert "My explicit note" in json.dumps(s.requests[2])
    assert any(m["role"] == "tool" for m in s.requests[2]["messages"])
    assert "Acceptance: Passed" in output
    print("PASS terminal questions: invalid input, leave/restart, typed choices, native role, limits and prior effects")


def approval_case(s):
    (s.workspace / "keep.txt").write_text("ORIGINAL")
    def pending():
        s.responses.extend([call("common.resources_read", target="keep.txt"), call("files.delete", relativePath="keep.txt")])
        result = s.json("run", "--message", "Delete keep.txt after explicit approval.",
                        "--context-tokens", "24000", "--max-iterations", "6", "--max-tool-steps", "6",
                        "--tool-result-role", "tool", code=3, input="yes\n")
        assert (s.workspace / "keep.txt").exists(), "JSONL consumed piped approval"
        return result["SessionId"]

    session = pending()
    before = s.json("inspect", "--session", session)
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Type yes to approve")
    terminal.send(b"\n")
    terminal.finish(3)
    assert s.json("inspect", "--session", session)["Revision"] == before["Revision"]
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Type yes to approve")
    terminal.process.send_signal(signal.SIGINT)
    terminal.finish(130)
    assert s.json("inspect", "--session", session)["Revision"] == before["Revision"]
    terminal = s.terminal("resume", "--session", session, "--no-interactive")
    assert "Type yes" not in terminal.finish(3)
    # Denial works offline and does not dispatch or call the model.
    original_endpoint = s.env.pop("RNA_BASE_URL")
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Type yes to approve")
    terminal.send(b"no\n")
    terminal.finish(130)
    s.env["RNA_BASE_URL"] = original_endpoint
    assert (s.workspace / "keep.txt").read_text() == "ORIGINAL" and len(s.requests) == 2
    assert s.json("inspect", "--session", session)["Presentation"]["Files"] == []
    # Approval rechecks the originally observed revision after the user wait.
    session = pending()
    s.responses.append(DONE)
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Type yes to approve")
    (s.workspace / "keep.txt").write_text("EXTERNAL")
    terminal.send(b"yes\n")
    output = terminal.finish(0)
    assert (s.workspace / "keep.txt").read_text() == "EXTERNAL" and "health: Errors" in output
    assert s.json("inspect", "--session", session)["Presentation"]["Files"] == []
    session = pending()
    s.responses.append(DONE)
    terminal = s.terminal("resume", "--session", session)
    terminal.until("Type yes to approve")
    terminal.send(b"yes\n")
    output = terminal.finish(0)
    assert not (s.workspace / "keep.txt").exists() and "Verified change: files.delete keep.txt" in output
    assert len(s.requests) == 8
    print("PASS approvals: JSONL/blank/Ctrl+C/no-interactive preserve pending; offline denial; stale guard and explicit yes")


def presentation_case(s):
    s.responses.extend([call("files.create", relativePath="real.txt", text="ORIGINAL"),
                        call("files.create", relativePath="real.txt", text="OVERWRITE"),
                        call("common.resources_read", target="real.txt"),
                        call("files.replace", relativePath="real.txt", text="ORIGINAL"),
                        dict(DONE, message="I created imaginary.txt.\u001b[2J" + "x" * 3000)])
    result = s.cli("run", "--message", "Create one real file.")
    assert "Thinking (step" in result.stdout and "[truncated]" in result.stdout
    assert "\x1b" not in result.stdout and (s.workspace / "real.txt").read_text() == "ORIGINAL"
    effects = result.stdout.split("File effects recorded for this task:")[1]
    assert effects.count("Verified change:") == 1 and "imaginary.txt" not in effects
    session = re.search(r"Session: (\w+)", result.stdout)[1]
    files = s.json("inspect", "--session", session)["Presentation"]["Files"]
    assert len(files) == 1 and files[0]["Path"] == "real.txt" and files[0]["Effect"] == "VerifiedChanged"
    for options in [("--interactive",), ("--interactive", "--jsonl"), ("--interactive", "--no-interactive")]:
        s.cli("run", "--message", "Do not start.", *options, code=2)
    assert len(s.requests) == 5
    print("PASS presentation: canonical changes, failed/no-change excluded, narrative/ANSI bounded, invalid terminal flags")


def bounded_case(s):
    s.responses.extend(call("files.create", relativePath=f"note-{i:02}.txt", text="x") for i in range(33))
    s.responses.append(DONE)
    result = s.json("run", "--message", "Create notes.", "--max-iterations", "36", "--max-tool-steps", "36",
                    "--context-tokens", "65536")
    presentation = result["presentation"]
    assert presentation["FilesTruncated"] and len(presentation["Files"]) == 32
    assert presentation["Files"][0]["Path"] == "note-01.txt" and presentation["Files"][-1]["Path"] == "note-32.txt"
    assert s.json("inspect", "--session", result["SessionId"])["Presentation"] == presentation
    assert len(list(s.workspace.glob("note-*.txt"))) == 33 and len(s.requests) == 34
    print("PASS bounded presentation: latest 32 operations, explicit incompleteness, exact reconstructed snapshot")


def browser_case(s, stale):
    # One independent verification of an already-correct fixture. This does not
    # substitute for the separate verify/repair/reverify acceptance scenario.
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", s.workspace, dirs_exist_ok=True)
    script = s.workspace / "app.js"
    script.write_text(script.read_text().replace("count += increment", "count += 1"))
    def drift():
        if stale and len(s.requests) == 2:
            script.write_text(script.read_text().replace("count += 1", "count += 0"))
    s.before_response = drift
    s.responses.extend([call("web.verify", entryPath="index.html"), DONE])
    result = s.json("run", "--message", "Verify the existing counter snapshot.",
                    "--web-checks", str(REPO / "tests/cli/counter_checks.json"),
                    "--expect-files", "index.html,styles.css,app.js", code=5 if stale else 0)
    summary = result["presentation"]["LatestVerification"]
    assert summary["State"] == "Passed" and not summary["Unavailable"], summary
    saved = s.json("verification", "--id", summary["VerificationId"])
    assert summary["SnapshotId"] == saved["SnapshotId"] and saved["Origin"]["RunId"] == result["RunId"]
    assert summary["PassedChecks"] == 9 and summary["FailedChecks"] == summary["NotRunChecks"] == 0
    assert result["webSnapshotVerified"] == (not stale) and result["acceptance"] == ("failed" if stale else "passed")
    assert result["presentation"]["Files"] == [], "External changes are not accepted tool effects"
    assert s.json("inspect", "--session", result["SessionId"])["Presentation"] == result["presentation"]
    print("PASS browser presentation: exact record/snapshot, 9 checked assertions, current acceptance " + ("stale/failed" if stale else "passed"))


def main():
    cases = [("questions", questions_case), ("approval", approval_case), ("presentation", presentation_case),
             ("bounded", bounded_case)]
    if sys.argv[1:] == ["--browser-only"]:
        cases = [("browser", lambda s: browser_case(s, False)), ("browser-stale", lambda s: browser_case(s, True))]
    elif sys.argv[1:]:
        raise SystemExit("Usage: interactive_smoke.py [--browser-only]")
    with TemporaryDirectory(prefix="rna-interactive-") as folder:
        for name, run in cases:
            scenario = Scenario(Path(folder) / name)
            try:
                run(scenario)
                assert not scenario.responses and not scenario.errors, (scenario.responses, scenario.errors)
            finally:
                scenario.close()


if __name__ == "__main__":
    main()
