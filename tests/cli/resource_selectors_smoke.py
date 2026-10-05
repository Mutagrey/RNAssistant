"""Workspace selector schemas and exact admission through the production CLI."""
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, DONE, call, read_progress
from mode_questions_smoke import catalog
from skills_runtime_smoke import install


def run_case(root):
    workspace, state = root / "workspace", root / "state"
    (workspace / "nested").mkdir(parents=True)
    (workspace / "notes.txt").write_text("ROOT_FILE_BYTES")
    (workspace / "nested" / "other.txt").write_text("NESTED_FILE_BYTES")
    draft = install(state, "common.example", "Common", "SKILL_CORE_BYTES")
    (draft.parent / "references").mkdir()
    (draft.parent / "references" / "details.md").write_text("SKILL_REFERENCE_BYTES")
    requests, errors = [], []
    responses = [
        call("common.resources_read", type="file", target="notes.txt", referencePath="notes.txt"),
        call("common.resources_find", type=None, directory=None, query=None),
        call("common.resources_find", type="skill", directory="nested"),
        call("common.resources_find", type="skill", query=None),
        call("common.resources_read", type="skill", target="common.example", referencePath=None),
        call("common.resources_read", type="skill", target="common.example", referencePath="references/details.md"),
        call("common.resources_find", directory="/"),
        call("common.resources_read", type=None, target="notes.txt"),
        call("common.resources_find", directory="."),
        call("common.resources_find", directory="nested"),
        call("common.resources_read", target="nested/other.txt"),
        DONE,
    ]

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                index = len(requests) - 1
                assert index < len(responses), "Unexpected model call"
                body = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": json.dumps(responses[index])}}]}).encode()
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
    try:
        result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--mode", "plan", "--message", "Discover files and skills and read both files and the published reference.",
            "--instruction-role", "system", "--tool-result-role", "tool", "--response-mode", "json_schema",
            "--context-tokens", "32768", "--max-iterations", "16", "--max-tool-steps", "16",
            "--min-reads", "2", "--jsonl"], cwd=REPO, env=env, text=True, capture_output=True, timeout=45)
        assert result.returncode == 0, (result.stdout, result.stderr, errors)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["acceptance"] == "passed" and final["completeFileReads"] == 2, final
        assert len(requests) == len(responses) and not errors, (len(requests), errors)
        prompt_catalog = catalog(requests[0])
        wire = requests[0]["response_format"]["json_schema"]["schema"]
        wire_catalog = {item["properties"]["name"]["const"]: item["properties"]["arguments"]
                        for item in wire["properties"]["tool_calls"]["items"]["anyOf"]}
        for operation, file_keys, skill_keys in (
                ("find", {"type", "directory", "query"}, {"type", "query"}),
                ("read", {"type", "target"}, {"type", "target", "referencePath"})):
            name = "common.resources_" + operation
            for schema in (prompt_catalog[name]["Schema"], wire_catalog[name]):
                assert len(schema["anyOf"]) == 2, schema
                branches = {branch["properties"]["type"]["enum"][0]: branch for branch in schema["anyOf"]}
                assert set(branches["file"]["properties"]) == file_keys, branches
                assert set(branches["skill"]["properties"]) == skill_keys, branches
                assert all(branch["additionalProperties"] is False for branch in branches.values())
                assert "type" in branches["skill"]["required"], branches
            for branch in wire_catalog[name]["anyOf"]:
                assert set(branch["required"]) == set(branch["properties"]), branch
            assert wire_catalog[name]["anyOf"][0]["properties"]["type"]["enum"] == ["file", None]
        # Wrong-kind selectors cause frozen format repair before dispatch; they
        # never become accepted calls with a provider failure or a read receipt.
        for invalid, repaired, field in ((0, 1, "referencePath"), (2, 3, "directory")):
            assert requests[repaired]["messages"][:-1] == requests[invalid]["messages"]
            assert field in requests[repaired]["messages"][-1]["content"]
            assert read_progress(requests[repaired])["currentCompleteFileReads"] == 0
        for after_failure in (7, 9):
            last = requests[after_failure]["messages"][-1]
            assert last["role"] == "tool", last
            assert "path_outside_mount" in last["content"] and "omit directory" in last["content"], last
        assert "SKILL_CORE_BYTES" in json.dumps(requests[5]["messages"])
        assert "SKILL_REFERENCE_BYTES" in json.dumps(requests[6]["messages"])
        assert read_progress(requests[6])["currentCompleteFileReads"] == 0
        assert read_progress(requests[-1])["currentCompleteFileReads"] == 2
        assert "rna://" not in json.dumps(requests)
        assert (workspace / "notes.txt").read_text() == "ROOT_FILE_BYTES"
        print("PASS resource selectors: closed file/skill schemas, rejected cross-selectors, nullable defaults, "
              "unsafe roots rejected, relative discovery, exact file/skill reads and isolated evidence")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-selectors-") as folder:
        run_case(Path(folder))
