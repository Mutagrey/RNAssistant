"""Read prerequisites reach the kernel as typed, target-bound recovery."""

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
    return {"message": "Execute the next checked step.", "action": "tool",
            "tool_calls": [{"name": name, "arguments": arguments}]}


DONE = {"message": "Verified.", "action": "done", "tool_calls": []}


def results(request):
    for message in request["messages"]:
        content = message.get("content", "")
        if message["role"] == "tool":
            yield json.loads(content)
        elif content.startswith("TOOL_RESULT:\n"):
            yield json.loads(content.split("\n", 1)[1])


def run_case(root, operation, restart=False):
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    target = workspace / "app.txt"
    target.write_text("original", encoding="utf-8")
    (workspace / "other.txt").write_text("unrelated", encoding="utf-8")
    arguments = {"relativePath": "app.txt"}
    if operation == "replace":
        arguments["text"] = "updated"
    elif operation == "patch":
        arguments.update(oldText="original", newText="updated")
    elif operation in ("copy", "move"):
        arguments["targetPath"] = "destination.txt"
    mutation = call("files." + operation, **arguments)
    responses = [mutation, call("common.resources_read", target="other.txt")]
    if restart:
        responses.append(call("files.delete", relativePath="other.txt"))
    responses += [mutation, call("common.resources_read", target="app.txt"), mutation, DONE]
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            index = len(requests) - 1
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
               RNA_MODEL="scripted", RNA_API_KEY="ollama")
    env.pop("RNA_TOOL_RESULT_ROLE", None)

    def cli(*args):
        return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"], cwd=REPO,
            env=env, text=True, capture_output=True, timeout=45, check=False)

    try:
        run = cli("run", "--workspace", str(workspace), "--message", "Execute the checked file task.",
                  "--tool-result-role", "tool" if operation == "move" or restart else "user",
                  "--min-writes", "2" if restart else "1", "--max-iterations", "10", "--max-tool-steps", "10")
        assert len(requests) >= 2, (run.stdout, run.stderr)
        failure = list(results(requests[1]))[-1]
        assert failure["status"] == "error" and failure["data"]["code"] == "source_observation_required", failure
        assert failure["data"].get("recovery") == {
            "failureKind": "RejectedNoEffect", "retryPolicy": "RefreshRequired",
            "target": "app.txt", "representation": "text"}, failure
        if run.returncode == 3:
            pending = json.loads(run.stdout.splitlines()[-1])["data"]
            assert target.read_text() == "original", "No effect before approval"
            run = cli("approve", "--workspace", str(workspace), "--session", pending["SessionId"],
                      "--pending", pending["pendingId"])
        assert run.returncode == 0, (run.stdout, run.stderr)
        assert len(requests) == len(responses), (len(requests), len(responses))
        final = json.loads(run.stdout.splitlines()[-1])["data"]
        assert final["reason"] == "model_done" and final["acceptance"] == "passed", final
        assert final["ToolCounts"]["WriteOk"] == (2 if restart else 1), final
        retry_index = 3 if restart else 2
        rejected = list(results(requests[retry_index + 1]))[-1]
        assert rejected["data"]["code"] == "repeated_tool_failure", rejected
        assert list(results(requests[-1]))[-1]["status"] == "ok", requests[-1]
        assert "rna://" not in json.dumps(requests), "Runtime identity leaked into model context"
        if operation in ("replace", "patch"):
            assert target.read_text() == "updated"
        elif operation == "copy":
            assert target.read_text() == "original" and (workspace / "destination.txt").read_text() == "original"
        elif operation == "move":
            assert not target.exists() and (workspace / "destination.txt").read_text() == "original"
        else:
            assert not target.exists()
        if restart:
            assert not (workspace / "other.txt").exists()
        print("PASS", operation, "restored recovery" if restart else "target-bound read recovery")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    with TemporaryDirectory(prefix="rna-read-recovery-") as folder:
        root = Path(folder)
        for operation in ("replace", "patch", "copy", "move", "delete"):
            run_case(root / operation, operation)
        run_case(root / "restart", "replace", restart=True)


if __name__ == "__main__":
    main()
