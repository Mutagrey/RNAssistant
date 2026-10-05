#!/usr/bin/env python3
"""An exact read omitted from model context must not authorize a file mutation."""
import json
import os
from pathlib import Path
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from tempfile import TemporaryDirectory


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def main():
    if not CLI.is_file():
        raise SystemExit("Build src/RNAssistant.Cli/RNAssistant.Cli.csproj first.")
    requests = []
    responses = [
        {"message": "Attempt a replacement before reading.", "action": "tool", "tool_calls": [
            {"name": "files.replace", "arguments": {"relativePath": "large.txt", "text": "UNSAFE_REPLACEMENT"}}]},
        {"message": "Read the source.", "action": "tool", "tool_calls": [
            {"name": "common.resources_read", "arguments": {"target": "large.txt"}}]},
        {"message": "Attempt a replacement despite the omitted source.", "action": "tool", "tool_calls": [
            {"name": "files.replace", "arguments": {"relativePath": "large.txt", "text": "UNSAFE_REPLACEMENT"}}]},
        {"message": "The source cannot fit in context.", "action": "blocked", "tool_calls": []},
    ]

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path == "/v1/chat/completions", self.path
            requests.append(json.loads(self.rfile.read(int(self.headers["Content-Length"]))))
            index = len(requests) - 1
            assert index < len(responses), "Unexpected extra model request"
            body = json.dumps({"choices": [{"message": {"role": "assistant",
                "content": json.dumps(responses[index])}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with TemporaryDirectory(prefix="rna-omitted-read-") as folder:
            root = Path(folder)
            workspace = root / "workspace"
            workspace.mkdir()
            # Below the tool's 16000-character limit, above this request's budget.
            source = "ORIGINAL_BODY\n" + "界" * 15000 + "\n"
            target = workspace / "large.txt"
            target.write_text(source, encoding="utf-8")
            env = dict(os.environ, RNA_STATE_ROOT=str(root / "state"),
                       RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted", RNA_API_KEY="ollama")
            run = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                "--message", "Read large.txt and replace it only after seeing its complete source.",
                "--context-tokens", "16384", "--max-iterations", "4", "--max-tool-steps", "3",
                "--jsonl"], cwd=REPO, env=env, text=True, capture_output=True, timeout=45, check=False)
            assert run.returncode == 0, (run.stdout, run.stderr)
            assert len(requests) == 4, (len(requests), run.stdout, run.stderr)
            initial_context = "\n".join(message.get("content") or "" for message in requests[1]["messages"])
            assert "source_observation_required" in initial_context and "RefreshRequired" in initial_context
            read_context = "\n".join(message.get("content") or "" for message in requests[2]["messages"])
            assert '"bodyIncluded":false' in read_context, read_context
            assert "large.txt" in read_context and "ORIGINAL_BODY" not in read_context, read_context
            mutation_context = "\n".join(message.get("content") or "" for message in requests[3]["messages"])
            assert "source_observation_required" in mutation_context, mutation_context
            assert target.read_text(encoding="utf-8") == source, "Omitted source authorized a destructive write"
            assert "rna://" not in json.dumps(requests), "Runtime identity leaked to the model"
            final = json.loads(run.stdout.splitlines()[-1])["data"]
            assert final["reason"] == "model_blocked", final
            assert final["ToolCounts"]["WriteOk"] == 0 and final["ToolCounts"]["WriteUnknown"] == 0, final
            print("PASS typed recovery and shared compiler: omitted exact read cannot authorize replacement; original bytes preserved")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
