"""Current read requirements remain visible through edits and frozen repair."""

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
    return {"message": "Perform the checked file step.", "action": "tool",
            "tool_calls": [{"name": name, "arguments": arguments}]}


DONE = {"message": "Finished.", "action": "done", "tool_calls": []}


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
        call("files.create", relativePath="app.txt", text="original"),
        call("files.create", relativePath="other.txt", text="other"),
        call("common.resources_read", target="app.txt"),
        call("common.resources_read", target="app.txt"),
        call("common.resources_read", target="other.txt"),
        call("files.patch", relativePath="app.txt", oldText="original", newText="updated"),
    ]
    if ending != "early":
        responses += [{"invalid_protocol": "Repair must preserve the frozen read assessment."},
                      call("common.resources_read", target="app.txt"),
                      call("common.resources_read", target="other.txt")]
    responses.append(DONE)
    first_run_requests = len(responses)

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            index = len(requests) - 1
            if index == 6 and ending != "early":
                (workspace / "other.txt").write_text("external edit during format repair")
            if index == first_run_requests - 1 and ending == "late_edit":
                (workspace / "app.txt").write_text("external edit during final model wait")
            response = responses[index] if index < len(responses) else DONE
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": json.dumps(response)}}]}).encode()
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
    env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
               RNA_MODEL="scripted", RNA_API_KEY="test")

    def run(*extra):
        return subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
            "--message", "Update and read both final files.",
            *([] if ending == "late_edit" else ["--expect-files", "app.txt,other.txt"]),
            "--min-reads", "2", "--tool-result-role", "tool" if ending == "repair" else "user",
            "--max-iterations", "12", "--max-tool-steps", "12", "--jsonl", *extra],
            cwd=REPO, env=env, text=True, capture_output=True, timeout=45, check=False)

    try:
        result = run("--min-writes", "3")
        assert result.returncode == (0 if ending == "repair" else 5), (result.stdout, result.stderr)
        final = json.loads(result.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_done", final
        assert final["completeFileReads"] == (2 if ending == "repair" else 1), final
        assert len(requests) == first_run_requests, "A premature done must not start another loop or model repair"
        expected = [0, 0, 0, 1, 1, 2, 1] + ([1, 1, 2] if ending != "early" else [])
        for request, count in zip(requests, expected):
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
            again = run("--session", final["SessionId"])
            assert again.returncode == 5, (again.stdout, again.stderr)
            assert read_progress(requests[-1])["currentCompleteFileReads"] == 0
            assert json.loads(again.stdout.splitlines()[-1])["data"]["completeFileReads"] == 0
            assert len(requests) == first_run_requests + 1
        assert "rna://" not in json.dumps(requests), "Runtime identity leaked to the model"
        print("PASS read-acceptance context:", ending)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with TemporaryDirectory(prefix="rna-read-acceptance-") as folder:
        for ending in ("early", "repair", "late_edit"):
            run_case(Path(folder) / ending, ending)


if __name__ == "__main__":
    main()
