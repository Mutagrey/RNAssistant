"""Published skills through the production CLI/compiler; scripted contract evidence."""
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from read_acceptance_context_smoke import CLI, REPO, call, DONE, read_progress


def install(state, skill_id, host, body, enabled=True):
    folder = state / "skills" / host.lower() / skill_id
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / "SKILL.md"
    path.write_text(f"---\nid: {skill_id}\nhost: {host}\nname: {skill_id}\n"
                    f"description: Fixture {skill_id}\nversion: 1.0.0\n"
                    f"enabled: {str(enabled).lower()}\n---\n\n{body}", encoding="utf-8")
    return path


def run_case(root):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    (workspace / "common.example").write_text("ORIGINAL_FILE_BYTES")
    (workspace / "SKILL.md").write_text("WORKSPACE_FILE_IS_NOT_INSTALLED_INSTRUCTIONS")
    draft = install(state, "common.example", "Common", "PUBLISHED_CORE_MARKER")
    refs = draft.parent / "references"
    refs.mkdir()
    reference = refs / "details.md"
    reference.write_text("PUBLISHED_REFERENCE_MARKER")
    install(state, "workspace.extra", "Workspace", "WORKSPACE_HOST_BODY")
    install(state, "excel.hidden", "Excel", "OFFICE_ONLY_BODY")
    install(state, "common.disabled", "Common", "DISABLED_BODY", enabled=False)
    install(state, "workspace.files", "Common", "SHADOWED_BUILTIN_BODY")
    requests, server_errors = [], []
    responses = [
        call("common.resources_find", type="skill"),
        call("common.resources_read", type="skill", target="workspace.files"),
        call("common.resources_read", type="skill", target="common.example"),
        # A skill with the same semantic name does not authorize a file edit.
        call("files.replace", relativePath="common.example", text="UNSAFE_REPLACEMENT"),
        call("common.resources_read", type="skill", target="common.example", referencePath="references/details.md"),
        call("common.resources_read", type="skill", target="workspace.extra"),
        call("files.create", relativePath="app.txt", text="first"),
        call("common.resources_read", target="app.txt"),
        call("files.patch", relativePath="app.txt", oldText="first", newText="fixed"),
        call("common.resources_read", target="app.txt"),
        DONE,
        # Separate-process continuation reads the same committed publication,
        # despite removal of the authoring package after its first capture.
        call("common.resources_read", type="skill", target="common.example"),
        call("common.resources_read", type="skill", target="common.example", referencePath="references/details.md"),
        DONE,
    ]

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                assert self.path == "/v1/chat/completions"
                requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
                index = len(requests) - 1
                if index == 0:
                    draft.write_text(draft.read_text().replace("PUBLISHED_CORE_MARKER", "UNPUBLISHED_CORE_DRIFT"))
                    reference.write_text("UNPUBLISHED_REFERENCE_DRIFT")
                assert index < len(responses), "Unexpected extra agent loop"
                body = json.dumps({"choices": [{"message": {"role": "assistant",
                    "content": json.dumps(responses[index])}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as error:
                server_errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_MODEL="scripted", RNA_API_KEY="test",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}")

    def run(*extra):
        return subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--message", "Use installed instructions to create, repair and read app.txt.",
            "--tool-result-role", "tool", "--context-tokens", "32768", "--max-iterations", "16",
            "--max-tool-steps", "16", "--min-reads", "1", "--jsonl", *extra],
            cwd=REPO, env=env, text=True, capture_output=True, timeout=60, check=False)

    def text_at(index):
        return "\n".join(message.get("content") or "" for message in requests[index]["messages"])

    try:
        result = run("--expect-files", "app.txt", "--min-writes", "2")
        assert result.returncode == 0, (result.stdout, result.stderr, server_errors)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_done" and final["completeFileReads"] == 1, final
        assert len(requests) == 11, len(requests)
        assert (workspace / "app.txt").read_text() == "fixed"
        assert (workspace / "common.example").read_text() == "ORIGINAL_FILE_BYTES"
        initial = text_at(0)
        for target in ("workspace.files", "common.example", "workspace.extra"):
            assert target in initial, target
        for hidden in ("PUBLISHED_CORE_MARKER", "WORKSPACE_HOST_BODY", "OFFICE_ONLY_BODY", "DISABLED_BODY",
                       "SHADOWED_BUILTIN_BODY", "WORKSPACE_FILE_IS_NOT_INSTALLED_INSTRUCTIONS", "excel.hidden", "common.disabled"):
            assert hidden not in initial, hidden
        assert "PUBLISHED_CORE_MARKER" not in text_at(1), "Discovery must remain metadata-only"
        assert "PUBLISHED_CORE_MARKER" in text_at(3)
        assert "references/details.md" in text_at(3), "Reference metadata was lost in projection"
        assert "source_observation_required" in text_at(4), "Skill read granted file write authority"
        assert "PUBLISHED_REFERENCE_MARKER" in text_at(5)
        for index, request in enumerate(requests):
            assert read_progress(request)["currentCompleteFileReads"] == (1 if index in (8, 10) else 0), index
        # Inspect the actual native call/result pair, not only durable records.
        messages = requests[3]["messages"]
        assert messages[-1]["role"] == "tool", messages[-1]
        assert messages[-1]["tool_call_id"] == messages[-2]["tool_calls"][0]["id"]
        assert json.loads(messages[-2]["tool_calls"][0]["function"]["arguments"])["type"] == "skill"
        draft.unlink()
        reference.unlink()
        again = run("--session", final["SessionId"])
        assert again.returncode == 5, (again.stdout, again.stderr)
        assert len(requests) == len(responses), (len(requests), server_errors)
        assert "PUBLISHED_CORE_MARKER" in text_at(12) and "PUBLISHED_REFERENCE_MARKER" in text_at(13)
        assert read_progress(requests[-1])["currentCompleteFileReads"] == 0
        assert "rna://" not in json.dumps(requests), "Runtime references leaked into model context"
        assert "UNPUBLISHED_CORE_DRIFT" not in json.dumps(requests)
        assert "UNPUBLISHED_REFERENCE_DRIFT" not in json.dumps(requests)
        for read_only in (False, True):
            responses.append(DONE)
            env["RNA_BROWSER_EXECUTABLE"] = "/missing/chromium"
            hidden = run(*(["--read-only"] if read_only else []))
            assert hidden.returncode == 5, (hidden.stdout, hidden.stderr)
            prompt = requests[-1]["messages"][0]["content"]
            metadata, _ = json.JSONDecoder().raw_decode(prompt.split("Available skill metadata: ", 1)[1])
            ids = {item["target"] for item in metadata["items"]}
            assert "workspace.web_verify_repair" not in ids, ids
            assert ("workspace.files" in ids) is not read_only, ids
        print("PASS CLI skills: shared publication, metadata/body separation, host/enabled/shadow filtering, "
              "exact reference reads, native pair, no file authority/count, repair, process continuation and capability filtering")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-skills-") as directory:
        run_case(Path(directory))
