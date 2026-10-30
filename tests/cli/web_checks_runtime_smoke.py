"""Scripted HTTP model exercises the production runtime's functional acceptance.

This checks orchestration/guards, not real-model reasoning quality.
"""
from conversation_transport import runtime_metadata, conversation_content
import json
import os
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from web_verifier_smoke import CLI, REPO, record
from read_acceptance_context_smoke import read_progress
from interactive_smoke import Scenario


def run_case(root, ending):
    workspace, state, checks = root / "work", root / "state", root / "checks.json"
    shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", workspace)
    shutil.copyfile(REPO / "tests/cli/counter_checks.json", checks)
    requests = []

    def call(name, arguments):
        return {"message": "Run the accepted counter checks.", "action": "execute", "reason": "Perform the checked step.",
                "tool_calls": [{"name": name, "arguments": arguments}]}

    responses = [
        call("core.skills.load", {"id": "skills.workspace.web.verify.repair"}),
        call("web.verify", {"entryPath": "index.html"}),
        call("core.read", {"target": "app.js"}),
        call("core.patch", {'targets': [{'target': 'app.js', 'edits': [{'old': "document.getElementById('plus').addEventListener('click', () => { count += increment; render(); });", 'new': "document.getElementById('plus').addEventListener('click', () => { count += 1; render(); });"}]}]}),
        call("core.read", {"target": "app.js"}),
        call("web.verify", {"entryPath": "index.html"}),
    ]
    if ending in ("stale", "reverified"):
        responses.extend([
            call("core.patch", {'targets': [{'target': 'app.js', 'edits': [{'old': "document.getElementById('plus').addEventListener('click', () => { count += 1; render(); });", 'new': "document.getElementById('plus').addEventListener('click', () => { count += 1 /* revised */; render(); });"}]}]}),
            call("core.read", {"target": "app.js"}),
        ])
        if ending == "reverified":
            responses.append(call("web.verify", {"entryPath": "index.html"}))
    if ending == "late_edit":
        responses.append("invalid JSON")
    done = {"message": "Verified repair.", "finish_from_request": True}
    responses.append(done)
    first_done = len(responses) - 1
    if ending == "late_edit":
        responses.extend([
            call("core.read", {"target": "app.js"}),
            call("core.patch", {'targets': [{'target': 'app.js', 'edits': [{'old': "document.getElementById('plus').addEventListener('click', () => { count += 0; render(); });", 'new': "document.getElementById('plus').addEventListener('click', () => { count += 1; render(); });"}]}]}),
            call("core.read", {"target": "app.js"}),
        ])
    if ending in ("late_edit", "stale"):
        responses.extend([call("web.verify", {"entryPath": "index.html"}), done])

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_POST(self):
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            title_request = any((message.get("content") or "").startswith("# Chat title")
                                for message in request["messages"])
            assert self.path == "/v1/chat/completions"
            if title_request:
                content = "Web check test"
            else:
                requests.append(request)
                index = len(requests) - 1
                assert index < len(responses)
                if index == 2:
                    checks.write_text("{}")  # The admitted contract must not be reread.
                    # Only app.js is read next. The failed verification must still
                    # refresh its other source dependencies before compiling context.
                    style = workspace / "styles.css"
                    style.write_text(style.read_text() + "\n/* external edit after verification */\n")
                if index == 6 and ending == "late_edit":
                    script = workspace / "app.js"
                    script.write_text(script.read_text().replace("count += 1", "count += 0"))
                content = conversation_content(request, responses[index])
            body = json.dumps({"choices": [{"message": {"role": "assistant", "content": content}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        env = {key: value for key, value in os.environ.items() if not key.startswith("RNA_")}
        env.update(RNA_STATE_ROOT=str(state), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                   RNA_MODEL="scripted", RNA_API_KEY="test")
        run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--task-file", str(REPO / "tests/cli/counter_repair_task.md"), "--web-checks", str(checks),
            "--min-reads", "1", "--min-writes", "1", "--expect-files", "index.html,styles.css,app.js",
            "--auto-confirm", "on", "--tool-result-role", "tool" if ending == "reverified" else "user",
            "--max-iterations", "12", "--max-tool-steps", "12", "--jsonl"],
            cwd=REPO, env=env, capture_output=True, text=True, timeout=90, check=False)
        assert run.stdout.strip(), run.stderr
        result = json.loads(run.stdout.splitlines()[-1])["data"]
        assert run.returncode == 0, (run.stdout, run.stderr)
        assert result["acceptance"] == "passed" and result["webSnapshotVerified"], result
        if ending in ("late_edit", "stale"):
            feedback = runtime_metadata(requests[first_done + 1]["messages"][1]["content"])["completionFeedback"]
            assert "Required browser checks" in feedback, feedback
        assert result["webCheckCount"] == 9 and len(requests) == len(responses), result
        initial_context = runtime_metadata(requests[0]["messages"][1]["content"])
        assert initial_context["webAcceptance"] == {"currentSnapshotVerified": False}
        requests = requests[1:]  # The selected skill exposes web.verify for the workflow below.
        read_counts = [0, 0, 1, 0, 1, 1] + ({"passed": [], "late_edit": [1, 0, 1, 0, 1, 1], "stale": [0, 1, 1, 1], "reverified": [0, 1, 1]}[ending])
        assert [read_progress(request)["currentCompleteFileReads"] for request in requests] == read_counts, \
            "Browser evidence and mutation read-back must not count as accepted final file reads"
        web_states = [False, False, False, False, False, True] + ({"passed": [], "late_edit": [True, False, False, False, False, True], "stale": [False, False, False, True], "reverified": [False, False, True]}[ending])
        for index, (request, expected) in enumerate(zip(requests, web_states)):
            messages = request["messages"]
            contexts = [m for m in messages if m.get("content", "").startswith("# RUNTIME_CONTEXT\n")]
            assert len(contexts) == 1
            assert runtime_metadata(contexts[0]["content"])["webAcceptance"] == {"currentSnapshotVerified": expected}
            assert messages[1] == contexts[0]
        if ending == "late_edit":
            assert requests[6]["messages"][:-1] == requests[5]["messages"], "Repair must preserve frozen verification state"
            assert requests[6]["messages"][-1]["content"].startswith("FORMAT_REPAIR:")
        assert "rna://" not in json.dumps(requests), "Runtime references leaked into the model context"
        def receipts(request, tool):
            return [body for message in request["messages"]
                    if message.get("content", "").startswith("TOOL_INTERACTION")
                    for body in [runtime_metadata(message["content"])]
                    if body["tool"] == tool]

        original_error = [message["content"] for message in requests[1]["messages"]
                          if (message.get("role") == "tool" or message.get("content", "").startswith("TOOL_RESULT")) and '"name":"web.verify"' in message["content"]]
        assert len(original_error) == 1 and "ReferenceError" in original_error[0], original_error
        historical = receipts(requests[2], "web.verify")
        assert len(historical) == 1 and historical[0]["outcome"] == "Error", historical
        assert historical[0]["observation"]["state"] == "Superseded", historical
        assert historical[0]["targets"] == ["index.html"] and historical[0]["data"]["checks"], historical
        writes = [json.loads(message["content"] if message.get("role") == "tool" else message["content"].split("\n", 1)[1])
                  for message in requests[3]["messages"]
                  if (message.get("role") == "tool" or message.get("content", "").startswith("TOOL_RESULT")) and '"name":"core.patch"' in message["content"]]
        assert len(writes) == 1 and writes[0]["data"]["targets"][0]["target"] == "app.js", writes
        assert writes[0]["status"] == "ok" and writes[0]["data"]["targets"][0]["status"] == "Changed", writes
        assert not receipts(requests[3], "core.patch"), "a recent patch remains a call/result pair"
        current_read = [message["content"] for message in requests[4]["messages"]
                        if (message.get("role") == "tool" or message.get("content", "").startswith("TOOL_RESULT")) and '"target":"app.js"' in message["content"] and '"name":"core.read"' in message["content"]]
        assert len(current_read) == 1 and "count += 1" in current_read[0] and "count += increment" not in current_read[0], current_read
        saved = record(workspace, state)["Items"]
        assert len(saved) == (2 if ending == "passed" else 3) and {item["State"] for item in saved} == {"Passed", "Failed"}, saved
        assert all(len(item["Checks"]["steps"]) == 9 for item in saved), saved
        passed = next(item for item in saved if item["State"] == "Passed")
        assert all(step["status"] == "Passed" for step in passed["CheckResults"]), passed
        assert passed["Origin"]["RunId"] == result["RunId"], passed
        print(f"PASS browser acceptance context: {ending}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def main():
    with tempfile.TemporaryDirectory(prefix="rna-checks-runtime-") as folder:
        root = Path(folder)
        for ending in ("passed", "late_edit", "stale", "reverified"):
            (root / ending).mkdir()
            run_case(root / ending, ending)
        run_recovery_guards(root / "recovery-guards")
    print("PASS scripted production runtime: verify/read/repair/read/verify/done, frozen checks, stale final evidence rejected")


def run_recovery_guards(root):
    scenario = Scenario(root)
    try:
        scenario.env.pop("RNA_BROWSER_EXECUTABLE")
        shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", scenario.workspace, dirs_exist_ok=True)
        original = (scenario.workspace / "app.js").read_text()
        def tool(name, **arguments):
            return {"message": "Exercise exact recovery.", "action": "execute", "reason": "Perform the checked step.",
                    "tool_calls": [{"name": name, "arguments": arguments}]}
        verify = tool("web.verify", entryPath="index.html")
        scenario.responses.extend([
            tool("core.skills.load", id="skills.workspace.web.verify.repair"), verify, verify,
            tool("core.write", target="unrelated.txt", content="not a dependency"), verify,
            tool("core.read", target="app.js"), verify,
            tool("core.write", target="app.js", content=original), verify,
            tool("core.patch", targets=[{'target': 'app.js', 'edits': [{'old': "document.getElementById('plus').addEventListener('click', () => { count += increment; render(); });", 'new': "document.getElementById('plus').addEventListener('click', () => { count += 1; render(); });"}]}]),
            tool("core.read", target="app.js"), verify,
            {"message": "Verified repair.", "finish_from_request": True},
        ])
        output = scenario.cli("run", "--message", "Repair the counter and verify its checked snapshot.",
                              "--auto-confirm", "on", "--web-checks", str(REPO / "tests/cli/counter_checks.json"),
                              "--min-writes", "1", "--max-iterations", "16",
                              "--max-tool-steps", "16", "--jsonl")
        events = [json.loads(line) for line in output.stdout.splitlines()]
        verifications = [event["data"]["activity"]["Outcome"] for event in events
                         if event["type"] == "tool.completed" and event["data"]["ToolId"] == "web.verify"]
        assert verifications == ["Error", *(["NotDispatched"] * 4), "Ok"], verifications
        result = events[-1]["data"]
        assert result["acceptance"] == "passed" and result["webSnapshotVerified"]
        assert result["ToolStepsUsed"] == 8 and result["IterationsUsed"] == 13, result
        assert result["presentation"]["LatestVerification"]["PassedChecks"] == 9
        assert scenario.json("verifications")["Total"] == 2, "Rejected repeats must not start browsers or publish results"
        assert len(scenario.requests) == 13 and not scenario.responses and not scenario.errors
        for request in scenario.requests:
            context = runtime_metadata(request["messages"][1]["content"])
            assert "readAcceptance" not in context and "webAcceptance" in context
        wire = json.dumps(scenario.requests)
        assert "ChangeRequired" in wire and "rna://" not in wire and "ChangeDependencies" not in wire
        assert (scenario.workspace / "app.js").read_text() == original.replace("count += increment", "count += 1")
        print("PASS browser recovery: unchanged retry, unrelated write, fresh read and no-op stay blocked; exact repair permits one verification")
    finally:
        scenario.close()


if __name__ == "__main__":
    main()
