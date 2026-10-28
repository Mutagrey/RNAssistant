"""Current read requirements remain visible through edits and frozen repair."""

from conversation_transport import conversation_content
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def call(name, **arguments):
    return {"message": "Perform the checked file step.", "action": "execute", "reason": "Perform the checked step.",
            "tool_calls": [{"name": name, "arguments": arguments}]}


DONE = {"message": "Finished.", "finish_from_request": True}


def read_progress(request):
    contexts = [json.loads(message["content"].split("\n", 1)[1])
                for message in request["messages"]
                if (message.get("content") or "").startswith("RUNTIME_CONTEXT:\n")]
    assert len(contexts) == 1, "The request needs one explicit, frozen read-acceptance projection"
    return contexts[0]["readAcceptance"]


def run_case(root, ending):
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    requests = []
    responses = [
        call("core.write", target="app.txt", content="original"),
        call("core.write", target="other.txt", content="other"),
        call("core.read", target="app.txt"),
        call("core.read", target="app.txt"),
        call("core.read", target="other.txt"),
        call("core.patch", target="app.txt", hunks=[{"oldText": "original", "newText": "updated"}]),
    ]
    if ending != "early":
        responses += [{"invalid_protocol": "Repair must preserve the frozen read assessment."},
                      call("core.read", target="app.txt"),
                      call("core.read", target="other.txt")]
    responses.append(DONE)
    first_done = len(responses) - 1
    if ending != "repair":
        responses.extend([call("core.read", target="app.txt"), DONE])
    first_run_requests = len(responses)

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            title_request = any((message.get("content") or "").startswith("# Chat title")
                                for message in request["messages"])
            if title_request:
                content = "Read acceptance test"
            else:
                requests.append(request)
                index = len(requests) - 1
                if index == 6 and ending != "early":
                    (workspace / "other.txt").write_text("external edit during format repair")
                if index == first_done and ending == "late_edit":
                    (workspace / "app.txt").write_text("external edit during final model wait")
                response = responses[index] if index < len(responses) else DONE
                content = conversation_content(request, response)
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": content}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = dict({k: v for k, v in os.environ.items() if not k.startswith("RNA_")}, RNA_STATE_ROOT=str(root / "state"),
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
               RNA_MODEL="scripted", RNA_API_KEY="test")

    def run(*extra, iterations="12"):
        return subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--message", "Update and read both final files.",
            *([] if ending == "late_edit" else ["--expect-files", "app.txt,other.txt"]),
            "--min-reads", "2", "--auto-confirm", "on", "--tool-result-role", "tool" if ending == "repair" else "user",
            "--max-iterations", iterations, "--max-tool-steps", "12", "--jsonl", *extra],
            cwd=REPO, env=env, text=True, capture_output=True, timeout=45, check=False)

    try:
        result = run("--min-writes", "3")
        assert result.returncode == 0, (result.stdout, result.stderr)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_finish", final
        assert final["completeFileReads"] == 2 and final["acceptance"] == "passed", final
        assert len(requests) == first_run_requests, "Only the existing kernel continues after unmet criteria"
        if ending != "repair":
            feedback = json.loads(requests[first_done + 1]["messages"][-1]["content"].split("\n", 1)[1])["completionFeedback"]
            assert "1/2" in feedback, feedback
            assert not any(message["role"] == "assistant" and "Finished." in message.get("content", "")
                           for message in requests[first_done + 1]["messages"]), "Rejected done entered accepted history"
            events = [json.loads(line) for path in (root / "state" / "chats").rglob("*.events.jsonl") for line in path.read_text().splitlines()]
            assert "completion_requirements_unmet" in json.dumps(events), "Completion rejection was not saved"
        expected = [0, 0, 0, 1, 1, 2, 1] + ([1, 1, 2] if ending != "early" else []) + ([1, 2] if ending != "repair" else [])
        for index, (request, count) in enumerate(zip(requests, expected)):
            # Repair adds its notice after the same frozen request; all fresh
            # decisions see one required read-state block after the tool results.
            position = -2 if ending != "early" and index == 7 else -1
            assert request["messages"][position]["content"].startswith("RUNTIME_CONTEXT:\n"), request["messages"][-2:]
            progress = read_progress(request)
            assert progress["currentCompleteFileReads"] == count, progress
            assert progress["minimumCompleteFileReads"] == 2, progress
            assert progress["remainingCompleteFileReads"] == 2 - count, progress
        if ending != "late_edit":
            assert read_progress(requests[0])["expectedFilesWithoutCurrentRead"] == ["app.txt", "other.txt"]
            assert read_progress(requests[6])["expectedFilesWithoutCurrentRead"] == ["app.txt"]
        if ending != "early":
            assert requests[7]["messages"][:-1] == requests[6]["messages"], "Format repair recomputed frozen authority"
            assert read_progress(requests[8])["expectedFilesWithoutCurrentRead"] == (["other.txt"] if ending == "repair" else [])
            assert read_progress(requests[9])["expectedFilesWithoutCurrentRead"] == []
        if ending == "repair":
            # Accepted reads in the previous run remain history, not proof that
            # this new run performed the required final reads.
            again = run("--session", final["SessionId"], iterations="2")
            assert again.returncode == 5, (again.stdout, again.stderr)
            assert read_progress(requests[-1])["currentCompleteFileReads"] == 0
            assert json.loads(again.stdout.splitlines()[-1])["data"]["completeFileReads"] == 0
            assert len(requests) == first_run_requests + 2
            assert json.loads(again.stdout.splitlines()[-1])["data"]["reason"] == "iteration_limit"
        assert "rna://" not in json.dumps(requests), "Runtime identity leaked to the model"
        print("PASS read-acceptance context:", ending)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def run_completion_case(root, ending):
    # Import locally: the shared HTTP fixture imports this module's call helpers.
    from interactive_smoke import Scenario
    scenario = Scenario(root)
    try:
        if ending == "repair":
            scenario.responses.extend([DONE, {"invalid": "Preserve completion feedback during repair."},
                call("core.write", target="required.txt", content="Delivered"), DONE])
        else:
            if ending == "needs_input":
                scenario.responses.append(call("core.ask", questions=[{"header": "Obstacle", "prompt": "What should I create?",
                    "selection": "single", "allowFreeText": True, "options": [{"label": "required.txt", "description": "Create the required file"}, {"label": "Stop", "description": "Leave the task incomplete"}]}]))
            else:
                scenario.responses.append({**DONE, "assessment": "not_satisfied" if ending == "blocked" else "satisfied"})
        expected = {"repair": (0, "model_finish"), "plain": (0, "model_finish"),
                    "blocked": (5, "model_finish"), "needs_input": (3, "awaiting_user")}[ending]
        result = scenario.json("run", "--message", "Create the required file, or name the concrete obstacle.",
            "--auto-confirm", "on", "--max-iterations", "4", "--max-tool-steps", "2",
            *([] if ending == "plain" else ["--expect-files", "required.txt", "--min-writes", "1"]), code=expected[0])
        assert result["reason"] == expected[1] and not scenario.errors and not scenario.responses, result
        if ending == "repair":
            assert result["acceptance"] == "passed" and result["IterationsUsed"] == 3 and result["ToolStepsUsed"] == 1, result
            request, repaired = scenario.requests[1:3]
            assert repaired["messages"][:-1] == request["messages"], "Format repair changed the frozen completion feedback"
            context = json.loads(request["messages"][-1]["content"].split("\n", 1)[1])
            assert "Missing required files" in context["completionFeedback"] and "0/1" in context["completionFeedback"]
            assert "readAcceptance" not in context and "webAcceptance" not in context
        else:
            assert len(scenario.requests) == 1, "Unfinished outcomes or a task without criteria started extra model work"
        print("PASS explicit completion criteria:", ending)
    finally:
        scenario.close()


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with TemporaryDirectory(prefix="rna-read-acceptance-") as folder:
        for ending in ("early", "repair", "late_edit"):
            run_case(Path(folder) / ending, ending)
        for ending in ("repair", "plain", "blocked", "needs_input"):
            run_completion_case(Path(folder) / ("completion-" + ending), ending)


if __name__ == "__main__":
    main()
