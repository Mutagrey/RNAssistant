"""Check CLI wire settings and their persistence across confirmation."""

import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    scripted = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            content = json.dumps(scripted.pop(0) if scripted else
                                 {"message": "Ready.", "action": "done", "tool_calls": []})
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
    try:
        with TemporaryDirectory(prefix="rna-response-mode-cli-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")
            env.pop("RNA_INSTRUCTION_ROLE", None)

            def cli(*args):
                return subprocess.run(["dotnet", str(CLI), *args, "--jsonl"],
                                      cwd=REPO, env=env, text=True, capture_output=True,
                                      timeout=30, check=False)

            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", "invalid")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)
            invalid = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--instruction-role", "user")
            assert invalid.returncode == 2 and not requests, (invalid.stdout, invalid.stderr)

            for mode in ("json_schema", "json_object"):
                role_args = () if mode == "json_schema" else ("--instruction-role", "SYSTEM")
                role = "developer" if mode == "json_schema" else "system"
                run = cli("run", "--workspace", str(workspace), "--message", "Hi",
                          "--response-mode", mode, *role_args)
                assert run.returncode == 0, (run.stdout, run.stderr)
                result = json.loads(run.stdout.splitlines()[-1])["data"]
                assert result["reason"] == "model_done", result
                assert requests[-1]["response_format"]["type"] == mode, requests[-1]
                assert requests[-1]["messages"][0]["role"] == role, requests[-1]
                inspect = cli("inspect", "--workspace", str(workspace),
                              "--session", result["SessionId"])
                assert inspect.returncode == 0, inspect.stderr
                saved = json.loads(inspect.stdout)["data"]["modelConfiguration"]
                assert saved["AgentResponseMode"] == mode, saved
                assert saved["SystemPromptRole"] == role, saved
            assert len(requests) == 2, len(requests)
            assert requests[0]["messages"][0]["content"] == requests[1]["messages"][0]["content"], \
                "Changing the wire role must not rewrite the instruction text"

            original = "Preserve until confirmation."
            (workspace / "victim.txt").write_text(original, encoding="utf-8")
            scripted.extend([
                {"message": "Read before requesting deletion.", "action": "tool", "tool_calls": [
                    {"name": "common.resources_read", "arguments": {"target": "victim.txt"}}]},
                {"message": "Request guarded deletion.", "action": "tool", "tool_calls": [
                    {"name": "files.delete", "arguments": {"relativePath": "victim.txt"}}]},
            ])
            env["RNA_INSTRUCTION_ROLE"] = "system"
            pending = cli("run", "--workspace", str(workspace), "--message", "Delete victim.txt",
                          "--response-mode", "json_object")
            assert pending.returncode == 3, (pending.stdout, pending.stderr)
            state = json.loads(pending.stdout.splitlines()[-1])["data"]
            assert state["pendingTool"] == "files.delete", state
            assert len(requests) == 4 and not scripted, len(requests)
            assert all(request["messages"][0]["role"] == "system" for request in requests[2:])
            assert (workspace / "victim.txt").read_text() == original
            env.pop("RNA_INSTRUCTION_ROLE")
            approval_args = ("approve", "--workspace", str(workspace), "--session", state["SessionId"],
                             "--pending", state["pendingId"])
            changed = cli(*approval_args, "--instruction-role", "developer")
            assert changed.returncode == 5 and "instruction role" in changed.stderr, changed
            assert len(requests) == 4, "Profile drift must fail before model or tool dispatch"
            assert (workspace / "victim.txt").read_text() == original
            approved = cli(*approval_args)
            assert approved.returncode == 0, (approved.stdout, approved.stderr)
            assert len(requests) == 5 and requests[-1]["messages"][0]["role"] == "system", requests[-1]
            assert requests[-1]["response_format"]["type"] == "json_object", requests[-1]
            assert not (workspace / "victim.txt").exists()
            assert json.loads(approved.stdout.splitlines()[-1])["data"]["ToolCounts"]["WriteOk"] == 1
            print("PASS CLI response modes, instruction roles, persisted metadata and guarded approval inheritance")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
