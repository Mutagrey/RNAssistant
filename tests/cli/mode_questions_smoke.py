"""Mode admission and typed questions across production CLI processes (scripted HTTP)."""
import copy
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call
from skills_runtime_smoke import install


QUESTIONS = [
    {"header": "Format", "prompt": "Choose the format", "selection": "single", "allowFreeText": False,
     "options": [{"label": "Markdown", "description": "Readable text", "recommended": True},
                 {"label": "HTML", "description": "Browser document"}]},
    {"header": "Sections", "prompt": "Choose sections", "selection": "multiple",
     "options": [{"label": "Steps", "description": "Ordered work"},
                 {"label": "Risks", "description": "Open constraints"}]},
]


def catalog(request):
    prompt = request["messages"][0]["content"]
    suffix = prompt.split("Available tools and exact argument schemas: ", 1)[1]
    entries, _ = json.JSONDecoder().raw_decode(suffix)
    return {tool["Id"]: tool for tool in entries}


def run_case(root, mode, role):
    root.mkdir()
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    (workspace / "notes.txt").write_text("CURRENT_SOURCE")
    install(state, "common.planning", "Common", "CUSTOM_SKILL_BODY")
    requests, errors, responses = [], [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                assert responses, "Unexpected model call after typed question stop"
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
    # Explicit values make inherited provider preferences irrelevant to this fixture.
    for name in ("RNA_RESPONSE_MODE", "RNA_INSTRUCTION_ROLE", "RNA_TOOL_RESULT_ROLE", "RNA_THINKING"):
        env.pop(name, None)

    def cli(command, *extra):
        result = subprocess.run(["dotnet", str(CLI), command, "--workspace", str(workspace),
            "--jsonl", *extra], cwd=REPO, env=env, text=True, capture_output=True, timeout=30, check=False)
        data = json.loads(result.stdout.splitlines()[-1]) if result.stdout.strip() else None
        return result, data

    def checked(command, *extra, code=0):
        result, output = cli(command, *extra)
        assert result.returncode == code, (result.stdout, result.stderr, errors)
        return output["data"] if output else None

    try:
        capabilities = checked("env", "--mode", mode)["capabilities"]
        assert {"common.resources_find", "common.resources_read"}.issubset(capabilities)
        if mode == "chat":
            assert len(capabilities) == 2, capabilities
            # These calls must fail validation/admission; neither skill nor mutation may execute.
            responses.extend([call("files.create", relativePath="forbidden.txt", text="bad"),
                call("common.resources_read", type="skill", target="common.planning"),
                call("common.resources_read", target="notes.txt"), DONE])
        else:
            assert "common.questions_ask" in capabilities
            assert ("files.create" in capabilities) == (mode == "agent")
            if mode == "plan":
                responses.append(call("files.create", relativePath="forbidden.txt", text="bad"))
            if mode == "agent":
                responses.append(call("files.create", relativePath="created-before-question.txt", text="ONLY_WRITE"))
            responses.extend([call("common.resources_read", target="notes.txt"),
                call("common.questions_ask", questions=QUESTIONS)])
        final = checked("run", "--mode", mode, "--tool-result-role", role, "--instruction-role", "system",
            "--message", "Read notes.txt and ask for the format and sections before proceeding.",
            "--min-reads", "1", "--min-writes", "1" if mode == "agent" else "0",
            "--max-iterations", "8", "--max-tool-steps", "8", code=0 if mode == "chat" else 3)
        assert not responses and not errors, (responses, errors)
        assert not (workspace / "forbidden.txt").exists()
        ids = catalog(requests[0])
        if mode != "agent":
            assert not any(tool.startswith("files.") for tool in ids), ids
        initial = json.dumps(requests[0])
        assert ("common.planning" in initial) == (mode != "chat")
        assert "CUSTOM_SKILL_BODY" not in initial
        if mode == "chat":
            assert ids["common.resources_read"]["Schema"]["properties"]["type"]["enum"] == ["file"]
            assert "referencePath" not in ids["common.resources_read"]["Schema"]["properties"]
            assert final["reason"] == "model_done" and final["acceptance"] == "passed"
        session_id = final["SessionId"]
        before = len(requests)
        result, _ = cli("run", "--session", session_id, "--mode", "agent" if mode != "agent" else "plan",
            "--message", "Try changing mode")
        assert result.returncode == 2 and len(requests) == before, result.stderr
        result, _ = cli("run", "--mode", "typo", "--message", "Do not escalate")
        assert result.returncode == 2 and len(requests) == before, result.stderr
        if mode == "chat":
            print("PASS chat: two file tools, no skills, rejected mutation/skill intent, no mode drift")
            return

        assert final["reason"] == "awaiting_user" and final["action"] == "needs_input", final
        assert final["acceptance"] == "pending", final
        pending = final["userInput"]
        restored = checked("resume", "--session", session_id, code=3)
        assert restored["userInput"] == pending and len(requests) == before
        inspected = checked("inspect", "--session", session_id)
        assert inspected["Mode"] == mode and inspected["userInput"] == pending
        questions = pending["QuestionSet"]
        answer = {"runId": pending["RunId"], "questionSetId": questions["questionSetId"], "answers": [
            {"questionId": question["id"], "optionIds": [option["id"] for option in question["options"][:1]],
             "freeText": "Include examples" if index == 1 else ""}
            for index, question in enumerate(questions["questions"])]}
        answer["answers"][1]["optionIds"].append(questions["questions"][1]["options"][1]["id"])
        invalid = []
        wrong_run = copy.deepcopy(answer); wrong_run["runId"] = "stale"; invalid.append(wrong_run)
        wrong_set = copy.deepcopy(answer); wrong_set["questionSetId"] = "stale"; invalid.append(wrong_set)
        wrong_id = copy.deepcopy(answer); wrong_id["answers"][0]["optionIds"] = ["unknown"]; invalid.append(wrong_id)
        many = copy.deepcopy(answer); many["answers"][0]["optionIds"].append(questions["questions"][0]["options"][1]["id"]); invalid.append(many)
        forbidden_text = copy.deepcopy(answer); forbidden_text["answers"][0]["freeText"] = "not allowed"; invalid.append(forbidden_text)
        missing = copy.deepcopy(answer); missing["answers"].pop(); invalid.append(missing)
        duplicate = copy.deepcopy(answer); duplicate["answers"][1] = duplicate["answers"][0]; invalid.append(duplicate)
        excessive = copy.deepcopy(answer); excessive["answers"][1]["freeText"] = "x" * 4001; invalid.append(excessive)
        empty = copy.deepcopy(answer); empty["answers"][0]["optionIds"] = []; invalid.append(empty)
        unknown_field = copy.deepcopy(answer); unknown_field["extra"] = True; invalid.append(unknown_field)
        path = root / "answers.json"
        for bad in invalid:
            path.write_text(json.dumps(bad))
            result, _ = cli("answer", "--session", session_id, "--answers-file", str(path))
            assert result.returncode == 2 and len(requests) == before, (result.stdout, result.stderr)
        assert checked("inspect", "--session", session_id)["Revision"] == inspected["Revision"], "Rejected answers changed durable state"
        path.write_text(json.dumps(answer))
        responses.extend([call("common.resources_read", target="notes.txt"), DONE])
        completed = checked("answer", "--session", session_id, "--answers-file", str(path))
        assert completed["reason"] == "model_done" and completed["acceptance"] == "passed", completed
        assert completed["verifiedFileChanges"] == (1 if mode == "agent" else 0), "Answer required repeating a prior verified write"
        assert completed["RunId"] != pending["RunId"] and completed["userInput"] is None
        continuation = requests[before]
        assert "Current mode: " + mode in continuation["messages"][0]["content"]
        content = json.dumps(continuation["messages"])
        assert "PLAN_ANSWERS:" in content and "Markdown" in content and "Include examples" in content
        for identity in [questions["questionSetId"], *[q["id"] for q in questions["questions"]],
                         *[o["id"] for q in questions["questions"] for o in q["options"]]]:
            assert identity not in content, "Runtime question identity leaked to model"
        if role == "tool":
            question_pair = False
            for index, message in enumerate(continuation["messages"]):
                if message["role"] == "tool":
                    previous = continuation["messages"][index - 1]
                    assert message["tool_call_id"] in [call["id"] for call in previous["tool_calls"]]
                    question_pair |= any(call["function"]["name"] == "common.questions_ask" for call in previous["tool_calls"])
            assert question_pair, "The accepted question call/result pair was lost"
        else:
            assert not any(message["role"] == "tool" for message in continuation["messages"])
        before = len(requests)
        result, _ = cli("answer", "--session", session_id, "--answers-file", str(path))
        assert result.returncode == 2 and len(requests) == before, "Answered question replayed"
        if mode == "agent":
            responses.append(DONE)
            new_task = checked("run", "--session", session_id, "--message", "A separate task requires a new write.",
                "--min-writes", "1", code=5)
            assert new_task["verifiedFileChanges"] == 0 and new_task["acceptance"] == "failed", "Unrelated task borrowed earlier writes"
        assert not errors and not responses
        print(f"PASS {mode}/{role}: needs_input, restored questions, 10 invalid answers rejected without writes, "
              "semantic answers/new turn, inherited mode/role, acceptance, no ID leak/replay")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-mode-questions-") as temporary:
        root = Path(temporary)
        for mode, role in [("chat", "user"), ("plan", "tool"), ("agent", "user")]:
            run_case(root / mode, mode, role)
