"""Check CLI wire settings and their persistence across confirmation."""

from conversation_transport import conversation_content, finish

import json
import os
import re
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory
from jsonschema import Draft202012Validator


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def execute(message, name, arguments):
    return {"action": "execute", "reason": "Run the requested tool.",
            "tool_calls": [{"name": name, "arguments": arguments}], "message": message}


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    scripted = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if "response_format" in request:
                requests.append(request)
                content = json.dumps(scripted.pop(0) if scripted else finish(request))
            else:
                content = "Test title"
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": conversation_content(request, content)}}]}).encode()
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
    try:
        with TemporaryDirectory(prefix="rna-response-mode-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")
            env.pop("RNA_INSTRUCTION_ROLE", None)
            env.pop("RNA_TOOL_RESULT_ROLE", None)

            def cli(*args):
                return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                      cwd=REPO, env=env, text=True, capture_output=True,
                                      timeout=30, check=False)

            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", "invalid")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)
            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--tool-result-role", "assistant")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)
            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--instruction-role", "user")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)

            for depth in ("-1", "21", "bad"):
                invalid = cli("run", "--workspace", str(workspace), "--message", "Hi", "--max-reflections", depth)
                assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)
            for mode in ("json_schema", "json_object"):
                role_args = () if mode == "json_schema" else ("--instruction-role", "SYSTEM")
                role = "developer" if mode == "json_schema" else "system"
                run = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", mode, "--max-reflections", "0", *role_args)
                assert run.returncode == 0, (run.stdout, run.stderr)
                result = json.loads(run.stdout.splitlines()[-1])["data"]
                assert result["reason"] == "model_finish" and result["action"] == "finish", result
                assert requests[-1].get("response_format", {}).get("type") == mode, requests[-1]
                assert requests[-1]["messages"][0]["role"] == role, requests[-1]
                # Depth zero removes reflection from the actual wire contract.
                prompt = requests[-1]["messages"][0]["content"]
                assert "conversation-response-v8" in prompt and "Choose execute" in prompt \
                    and "Choose reflection" not in prompt and "Choose finish" in prompt \
                    and "Reflection is disabled for this run" in prompt \
                    and '"action":"reflection"' not in prompt and "execute/reflection" not in prompt \
                    and "conversation-response-v7" not in prompt, \
                    "Initial model context lacks the canonical v8 envelope"
                if mode == "json_schema":
                    schema = requests[-1]["response_format"]["json_schema"]["schema"]
                    Draft202012Validator.check_schema(schema)
                    actions = {branch["properties"]["action"]["const"]
                               for branch in schema["properties"]["response"]["anyOf"]}
                    assert actions == {"execute", "finish"}, actions
                    assert not Draft202012Validator(schema).is_valid({"response": {
                        "action": "reflection", "reason": "Choose an approach.",
                        "decision": "Inspect first.", "message": ""}})
                inspect = cli("inspect", "--workspace", str(workspace),
                              "--session", result["SessionId"])
                assert inspect.returncode == 0, inspect.stderr
                inspected = json.loads(inspect.stdout)["data"]
                assert inspected["Limits"]["MaxConsecutiveReflections"] == 0, inspected
                saved = inspected["modelConfiguration"]
                assert saved["AgentResponseMode"] == mode, saved
                assert saved["SystemPromptRole"] == role, saved
                assert saved["ToolResultRole"] == "user", saved
            assert len(requests) == 2, len(requests)
            strict_prompt, object_prompt = [request["messages"][0]["content"] for request in requests]
            assert "TOOL_NAME" not in strict_prompt and "TOOL_NAME" in object_prompt
            assert "argument contracts are in json_schema" in strict_prompt
            assert "exact argument schemas" in object_prompt

            # Validate what the model actually sees, not the internal schema dialect.
            catalog = json.JSONDecoder().raw_decode(object_prompt.split(
                "Available tools and exact argument schemas: ", 1)[1])[0]
            schemas = {tool["Id"]: tool["Schema"] for tool in catalog}
            for schema in schemas.values():
                Draft202012Validator.check_schema(schema)
            for name, arguments, valid in (
                ("common.resources_read", {"target": "catalog.txt"}, True),
                ("common.resources_read", {"target": "catalog.txt", "startLine": 1, "lineCount": 2}, True),
                ("common.resources_read", {"target": "catalog.txt", "startChar": 0, "charCount": 12}, True),
                ("common.resources_read", {"target": "catalog.txt", "startLine": 1}, False),
                ("common.resources_read", {"target": "catalog.txt", "startLine": 1, "lineCount": 2,
                                           "startChar": 0, "charCount": 12}, False),
                ("common.resources_search", {"target": "catalog.txt", "query": "needle"}, True),
                ("common.resources_search", {"scope": "workspace", "query": "needle"}, True),
                ("common.resources_search", {"scope": "workspace", "target": "catalog.txt", "query": "needle"}, False),
                ("common.resources_search", {"target": "catalog.txt"}, False),
                ("common.resources_find", {"query": "catalog.txt"}, True),
                ("common.resources_find", {"type": "skill", "query": "VBA"}, True),
                ("common.resources_find", {"target": "catalog.txt"}, False),
            ):
                errors = list(Draft202012Validator(schemas[name]).iter_errors(arguments))
                assert (not errors) == valid, (name, arguments, [e.message for e in errors])

            def assert_pair(request, role, name, arguments):
                assert "tools" not in request, "v8 stays the only generation protocol"
                messages = request["messages"]
                if role == "tool":
                    pending = set()
                    for message in messages:
                        if message.get("tool_calls"):
                            assert not pending, "unanswered native call"
                            pending = {call["id"] for call in message["tool_calls"]}
                        elif message["role"] == "tool":
                            assert message["tool_call_id"] in pending, "orphan native result"
                            pending.remove(message["tool_call_id"])
                        else:
                            assert not pending, "native results must follow their calls"
                    assert not pending, "missing native result"
                    result = next(i for i in range(len(messages) - 1, 0, -1)
                                  if messages[i]["role"] == "tool")
                    call, output = messages[result - 1], messages[result]
                    assert set(output) == {"role", "tool_call_id", "content"}, output
                    native = call["tool_calls"][0]
                    assert native["id"] == output["tool_call_id"], messages
                    assert native["function"]["name"] == name, native
                    assert json.loads(native["function"]["arguments"]) == arguments, native
                    receipt = json.loads(output["content"])
                    # Native tool pairing uses its transport ID; model evidence uses E refs.
                    assert re.fullmatch(r"E[1-9][0-9]*", receipt["tool_call_id"]), receipt
                else:
                    assert any(m.get("content", "").startswith("TOOL_RESULT:\n") for m in messages), [
                        (m["role"], m.get("content", "")[-1600:]) for m in messages[1:]]
                    result = next(i for i in range(len(messages) - 1, 0, -1)
                                  if messages[i].get("content", "").startswith("TOOL_RESULT:\n"))
                    call, output = messages[result - 1], messages[result]
                    assert call["role"] == "assistant" and output["role"] == role, messages
                    receipt = json.loads(output["content"].split("\n", 1)[1])
                    assert re.fullmatch(r"E[1-9][0-9]*", receipt["tool_call_id"]), receipt
                    assert json.loads(call["content"])["tool_calls"] == [
                        {"id": receipt["tool_call_id"], "name": name, "arguments": arguments}], call
                assert receipt["status"] == "ok" and receipt["name"] == name, receipt
                assert "rna://" not in output["content"], output

            (workspace / "catalog.txt").write_text("needle in source\n", encoding="utf-8")
            scripted.extend([
                execute("Search one source.", "common.resources_search", {"target": "catalog.txt", "query": "needle"}),
                execute("Read the source.", "common.resources_read", {"target": "catalog.txt"}),
            ])
            start = len(requests)
            run = cli("run", "--workspace", str(workspace), "--message", "Find needle in catalog.txt and read it.",
                      "--response-mode", "json_object")
            assert run.returncode == 0 and len(requests) == start + 3 and not scripted, (run.stdout, run.stderr)
            assert_pair(requests[start + 1], "user", "common.resources_search", {"target": "catalog.txt", "query": "needle"})
            assert_pair(requests[start + 2], "user", "common.resources_read", {"target": "catalog.txt"})
            assert json.loads(run.stdout.splitlines()[-1])["data"]["ToolCounts"]["ReadOk"] == 2

            for role in ("user", "developer", "tool"):
                path = role + ".html"
                arguments = {"relativePath": path, "text": "<p>Exact accepted source.</p>"}
                scripted.extend([
                    execute("Create.", "files.create", arguments),
                    execute("Read back.", "common.resources_read", {"target": path}),
                ])
                start = len(requests)
                run = cli("run", "--workspace", str(workspace), "--message", "Create and read " + path,
                          "--tool-result-role", role.upper(), "--auto-confirm", "on")
                assert run.returncode == 0 and not scripted, (run.stdout, run.stderr)
                assert len(requests) == start + 3, len(requests)
                assert_pair(requests[start + 1], role, "files.create", arguments)
                assert_pair(requests[start + 2], role, "common.resources_read", {"target": path})
                assert (workspace / path).read_text() == arguments["text"]
                session = json.loads(run.stdout.splitlines()[-1])["data"]["SessionId"]
                inspected = cli("inspect", "--workspace", str(workspace), "--session", session)
                assert json.loads(inspected.stdout)["data"]["modelConfiguration"]["ToolResultRole"] == role

            original = "Preserve until confirmation."
            (workspace / "victim.txt").write_text(original, encoding="utf-8")
            scripted.extend([
                execute("Read before requesting deletion.", "common.resources_read", {"target": "victim.txt"}),
                execute("Request guarded deletion.", "files.delete", {"relativePath": "victim.txt"}),
            ])
            env["RNA_INSTRUCTION_ROLE"] = "system"
            env["RNA_TOOL_RESULT_ROLE"] = "tool"
            start = len(requests)
            pending = cli("run", "--workspace", str(workspace), "--message", "Delete victim.txt",
                          "--response-mode", "json_object")
            assert pending.returncode == 3, (pending.stdout, pending.stderr)
            state = json.loads(pending.stdout.splitlines()[-1])["data"]
            assert state["pendingTool"] == "files.delete", state
            assert len(requests) == start + 2 and not scripted, len(requests)
            assert all(request["messages"][0]["role"] == "system" for request in requests[start:])
            assert (workspace / "victim.txt").read_text() == original
            env.pop("RNA_INSTRUCTION_ROLE")
            env.pop("RNA_TOOL_RESULT_ROLE")
            inspected = cli("inspect", "--workspace", str(workspace), "--session", state["SessionId"])
            assert json.loads(inspected.stdout)["data"]["modelConfiguration"]["ToolResultRole"] == "tool"
            approval_args = ("approve", "--workspace", str(workspace), "--session", state["SessionId"],
                             "--pending", state["pendingId"])
            changed = cli(*approval_args, "--instruction-role", "developer")
            assert changed.returncode == 5 and "instruction role" in changed.stderr, changed
            changed = cli(*approval_args, "--tool-result-role", "user")
            assert changed.returncode == 5 and "tool result role" in changed.stderr, changed
            assert len(requests) == start + 2, "Profile drift must fail before model or tool dispatch"
            assert (workspace / "victim.txt").read_text() == original
            approved = cli(*approval_args)
            assert approved.returncode == 0, (approved.stdout, approved.stderr)
            assert len(requests) == start + 3 and requests[-1]["messages"][0]["role"] == "system", requests[-1]
            assert requests[-1]["response_format"]["type"] == "json_object", requests[-1]
            assert_pair(requests[-1], "tool", "files.delete", {"relativePath": "victim.txt"})
            assert not (workspace / "victim.txt").exists()
            assert json.loads(approved.stdout.splitlines()[-1])["data"]["ToolCounts"]["WriteOk"] == 1
            print("PASS CLI response modes, advertised JSON schemas, search/read dispatch, all tool-result roles, exact pairs and guarded approval inheritance")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
