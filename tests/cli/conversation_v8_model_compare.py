#!/usr/bin/env python3
"""Pinned, isolated CLI task comparison with exact local HTTP evidence.

This is an exploratory runner, not the phase-5 qualification gate: field-order
and reason ablations still need distinct variants and target-host checks.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError
from urllib.request import Request, urlopen


TASKS = {
    "ready": {
        "prompt": "Create report.txt containing exactly ready, then read it back.",
        "initial": {}, "expected": {"report.txt": "ready"},
        "min_reads": 1, "min_writes": 1, "iterations": 6, "tool_steps": 4,
    },
    "uppercase": {
        "prompt": "Read source.txt. Create result.txt containing exactly the uppercase version of the entire source text, preserving both line breaks including the final newline. Then read result.txt back and report what you verified.",
        "initial": {"source.txt": "alpha\nbeta\n"},
        "expected": {"result.txt": "ALPHA\nBETA\n"},
        "min_reads": 2, "min_writes": 1, "iterations": 10, "tool_steps": 8,
    },
    "two_files": {
        "prompt": "Create first.txt containing exactly alpha and second.txt containing exactly beta. Read both files back before finishing.",
        "initial": {}, "expected": {"first.txt": "alpha", "second.txt": "beta"},
        "min_reads": 2, "min_writes": 2, "iterations": 10, "tool_steps": 8,
    },
}


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def binary_hashes(dll):
    return {path.name: sha256(path.read_bytes()) for path in sorted(dll.parent.iterdir())
            if path.is_file() and (path.suffix == ".dll" or
            path.name in (dll.stem + ".deps.json", dll.stem + ".runtimeconfig.json"))}


def model_digest(origin, model):
    with urlopen(origin + "/api/tags", timeout=10) as response:
        tags = json.load(response)
    matches = [item["digest"] for item in tags.get("models", []) if item.get("name") == model]
    if len(matches) != 1:
        raise ValueError("Pinned model is missing or ambiguous in Ollama /api/tags: " + model)
    return matches[0]


def response_action(body):
    envelope, usage = response_envelope(body)
    return (envelope.get("action") if isinstance(envelope, dict) else None), usage


def response_envelope(body):
    try:
        obj = json.loads(body)
        choice = obj["choices"][0]
        content = choice["message"].get("content") or ""
        usage = obj.get("usage")
    except (ValueError, KeyError, IndexError, TypeError, UnicodeDecodeError):
        try:
            chunks = [json.loads(line[5:].strip()) for line in body.decode("utf-8").splitlines()
                      if line.startswith("data:") and line[5:].strip() != "[DONE]"]
            content = "".join((item.get("choices") or [{}])[0].get("delta", {}).get("content") or ""
                              for item in chunks)
            usage = next((item.get("usage") for item in reversed(chunks) if item.get("usage")), None)
        except (ValueError, KeyError, IndexError, TypeError, UnicodeDecodeError):
            return None, None
    try:
        return json.loads(content), usage
    except (ValueError, TypeError):
        return None, usage


def apply_request_policy(body, policy):
    if policy != "no_reflection_schema":
        return body
    request = json.loads(body)
    schema = request["response_format"]["json_schema"]["schema"]
    branches = schema["anyOf"]
    selected = [branch for branch in branches
                if branch["properties"]["action"].get("const") != "reflection"]
    if len(branches) - len(selected) != 1 or len(selected) < 1:
        raise ValueError("Expected exactly one reflection branch in v8 schema")
    schema["anyOf"] = selected
    return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


class CaptureProxy(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        self.forward()

    def do_POST(self):
        self.forward()

    def log_message(self, _format, *_args):
        pass

    def forward(self):
        client_body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        body = apply_request_policy(client_body, self.server.request_policy) if self.path.startswith("/v1/chat/completions") else client_body
        headers = {key: value for key, value in self.headers.items()
                   if key.lower() not in ("host", "content-length", "connection", "accept-encoding", "authorization")}
        headers["Accept-Encoding"] = "identity"
        request = Request(self.server.origin + self.path, data=body if self.command == "POST" else None,
                          headers=headers, method=self.command)
        started = time.monotonic()
        try:
            with urlopen(request, timeout=300) as response:
                status, mime, payload = response.status, response.headers.get("Content-Type", "application/json"), response.read()
        except HTTPError as error:
            status, mime, payload = error.code, error.headers.get("Content-Type", "application/json"), error.read()
        except Exception as error:
            status, mime, payload = 502, "text/plain", str(error).encode("utf-8")
        elapsed = round(time.monotonic() - started, 3)
        with self.server.capture_lock:
            index = self.server.capture_count
            self.server.capture_count += 1
        stem = "%04d" % index
        if client_body != body:
            (self.server.capture_dir / (stem + ".client_request.body")).write_bytes(client_body)
        (self.server.capture_dir / (stem + ".request.body")).write_bytes(body)
        (self.server.capture_dir / (stem + ".response.body")).write_bytes(payload)
        action, usage = response_action(payload) if self.path.startswith("/v1/chat/completions") else (None, None)
        record = {"index": index, "method": self.command, "path": self.path, "status": status,
                  "seconds": elapsed, "request_sha256": sha256(body),
                  "client_request_sha256": sha256(client_body), "response_sha256": sha256(payload),
                  "action": action, "usage": usage}
        with self.server.capture_lock:
            with (self.server.capture_dir / "requests.jsonl").open("a", encoding="utf-8") as output:
                output.write(json.dumps(record, ensure_ascii=False) + "\n")
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        try:
            self.wfile.write(payload)
        except BrokenPipeError:
            pass


class CaptureServer(ThreadingHTTPServer):
    def handle_error(self, request, client_address):
        # The CLI may close an idle keep-alive socket after receiving a response.
        if not isinstance(sys.exc_info()[1], (BrokenPipeError, ConnectionResetError)):
            super().handle_error(request, client_address)


def run_case(name, dll, task_name, task, output, origin, model):
    workspace, state, captures = (output / part for part in ("workspace", "state", "http"))
    workspace.mkdir(parents=True)
    state.mkdir()
    captures.mkdir()
    for relative, content in task["initial"].items():
        (workspace / relative).write_bytes(content.encode("utf-8"))
    proxy = CaptureServer(("127.0.0.1", 0), CaptureProxy)
    proxy.origin, proxy.capture_dir = origin, captures
    proxy.request_policy = "no_reflection_schema" if name == "v8_direct" else "unchanged"
    proxy.capture_count, proxy.capture_lock = 0, threading.Lock()
    thread = threading.Thread(target=proxy.serve_forever, daemon=True)
    thread.start()
    command = ["dotnet", str(dll), "run", "--workspace", str(workspace), "--message", task["prompt"],
               "--context-tokens", "32768", "--max-iterations", str(task["iterations"]),
               "--max-tool-steps", str(task["tool_steps"]), "--expect-files", ",".join(task["expected"]),
               "--min-reads", str(task["min_reads"]), "--min-writes", str(task["min_writes"]),
               "--auto-confirm", "on", "--response-mode", "json_schema",
               "--instruction-role", "developer", "--tool-result-role", "user",
               "--reasoning-mode", "reasoning_effort", "--thinking", "off", "--jsonl"]
    env = dict(os.environ, RNA_STATE_ROOT=str(state), RNA_MODEL=model,
               RNA_BASE_URL="http://127.0.0.1:%d/v1" % proxy.server_port)
    started = time.monotonic()
    try:
        with (output / "stdout.jsonl").open("w") as stdout, (output / "stderr.log").open("w") as stderr:
            result = subprocess.run(command, stdout=stdout, stderr=stderr, env=env, timeout=360)
        exit_code = result.returncode
    except subprocess.TimeoutExpired:
        exit_code = 124
    finally:
        proxy.shutdown()
        proxy.server_close()
        thread.join()
    records = [json.loads(line) for line in (output / "stdout.jsonl").read_text().splitlines() if line.strip()]
    terminal = next((item["data"] for item in reversed(records) if item.get("type") == "run.completed"), {})
    exact = {relative: (workspace / relative).is_file() and
             (workspace / relative).read_bytes() == content.encode("utf-8")
             for relative, content in task["expected"].items()}
    unchanged = {relative: (workspace / relative).read_bytes() == content.encode("utf-8")
                 for relative, content in task["initial"].items()}
    capture_index = captures / "requests.jsonl"
    http = [json.loads(line) for line in capture_index.read_text().splitlines()] if capture_index.exists() else []
    usages = [item["usage"] for item in http if item["usage"]]
    responses = [response_envelope((captures / ("%04d.response.body" % item["index"])).read_bytes())[0]
                 for item in http if item["path"].startswith("/v1/chat/completions")]
    proposed_calls = [call for item in responses if isinstance(item, dict) and item.get("action") == "execute"
                      for call in item.get("tool_calls", []) if isinstance(call, dict)]
    signatures = [json.dumps(call, sort_keys=True, ensure_ascii=False) for call in proposed_calls]
    terminal_results = {}
    for item in http:
        if not item["path"].startswith("/v1/chat/completions"):
            continue
        request = json.loads((captures / ("%04d.request.body" % item["index"])).read_bytes())
        for message in request.get("messages", []):
            content = message.get("content")
            if isinstance(content, str) and content.startswith("TOOL_RESULT:\n"):
                try:
                    result = json.loads(content[len("TOOL_RESULT:\n"):])
                    terminal_results[result["tool_call_id"]] = result.get("status")
                except (ValueError, KeyError, TypeError):
                    pass
    claims = [claim for item in responses if isinstance(item, dict) and item.get("action") == "finish"
              for claim in item.get("results", []) if isinstance(claim, dict)]
    citations = [citation for claim in claims for citation in claim.get("evidence", []) if isinstance(citation, str)]
    external_success = all(exact.values()) and all(unchanged.values())
    readback_met = (terminal.get("completeFileReads") or 0) >= task["min_reads"]
    summary = {"variant": name, "request_policy": proxy.request_policy, "task": task_name,
               "dll_sha256": sha256(dll.read_bytes()),
               "exit": exit_code, "seconds": round(time.monotonic() - started, 2),
               "external_exact_files": exact, "external_inputs_unchanged": unchanged,
               "external_artifact_success": external_success,
               "required_readback_met": readback_met,
               "completed_without_external_artifact": terminal.get("lifecycle") == "Completed" and not external_success,
               "completed_without_required_readback": terminal.get("lifecycle") == "Completed" and not readback_met,
               "runtime_readback_count": terminal.get("completeFileReads"),
               "runtime_verified_writes": terminal.get("verifiedFileChanges"),
               "lifecycle": terminal.get("lifecycle"), "reason": terminal.get("reason"),
               "acceptance": terminal.get("acceptance"), "iterations": terminal.get("IterationsUsed"),
               "tool_steps": terminal.get("ToolStepsUsed"), "tool_counts": terminal.get("ToolCounts"),
               "reflection_count": sum(item == "reflection" for item in (entry.get("action") for entry in responses if isinstance(entry, dict))),
               "repeated_identical_calls": len(signatures) - len(set(signatures)),
               "uncited_terminal_ids": sorted(set(terminal_results) - set(citations)),
               "invalid_evidence_ids": sorted(set(citations) - set(terminal_results)),
               "satisfied_claim_without_external_artifact": any(claim.get("assessment") == "satisfied" for claim in claims) and not external_success,
               "http_actions": [item["action"] for item in http if item["path"].startswith("/v1/chat/completions")],
               "prompt_tokens": sum(item.get("prompt_tokens", 0) for item in usages) if usages else None,
               "completion_tokens": sum(item.get("completion_tokens", 0) for item in usages) if usages else None}
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n")
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v7", required=True, type=Path)
    parser.add_argument("--v8", required=True, type=Path)
    parser.add_argument("--model", required=True)
    parser.add_argument("--digest", required=True)
    parser.add_argument("--origin", default="http://127.0.0.1:11434")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--tasks", default="ready,uppercase,two_files")
    parser.add_argument("--repeats", type=int, default=1)
    parser.add_argument("--variants", default="v7,v8", help="Comma-separated v7, v8 and/or v8_direct variants")
    parser.add_argument("--start-v8", action="store_true", help="Reverse the first pair to balance order across runs")
    parser.add_argument("--v7-revision", required=True, help="Exact source revision used to build the v7 CLI")
    parser.add_argument("--v8-revision", required=True, help="Exact source revision used to build the v8 CLI")
    args = parser.parse_args()
    if args.repeats < 1 or args.repeats > 10:
        parser.error("--repeats must be 1..10")
    tasks = args.tasks.split(",")
    if not tasks or any(task not in TASKS for task in tasks):
        parser.error("--tasks must select ready, uppercase and/or two_files")
    selected_variants = args.variants.split(",")
    if not selected_variants or len(selected_variants) != len(set(selected_variants)) or \
            any(variant not in ("v7", "v8", "v8_direct") for variant in selected_variants):
        parser.error("--variants must select v7, v8 and/or v8_direct without duplicates")
    if args.output.exists():
        parser.error("--output must be a new directory")
    for dll in (args.v7, args.v8):
        if not dll.is_file():
            parser.error("Missing CLI DLL: " + str(dll))
    observed = model_digest(args.origin.rstrip("/"), args.model)
    if observed != args.digest:
        parser.error("Pinned model digest changed: " + observed)
    args.output.mkdir(parents=True)
    manifest = {"model": args.model, "digest": observed, "origin": args.origin,
                "platform": platform.platform(), "python": sys.version.split()[0],
                "v7_revision": args.v7_revision, "v8_revision": args.v8_revision,
                "binary_hashes": {"v7": binary_hashes(args.v7), "v8": binary_hashes(args.v8)},
                "settings": {"response_mode": "json_schema", "instruction_role": "developer",
                             "tool_result_role": "user", "reasoning_mode": "reasoning_effort",
                             "thinking": "off", "context_tokens": 32768},
                "tasks": {task: TASKS[task] for task in tasks}, "runs": []}
    variants = [(name, dll) for name, dll in (("v7", args.v7), ("v8", args.v8), ("v8_direct", args.v8))
                if name in selected_variants]
    for repeat in range(args.repeats):
        for task_index, task_name in enumerate(tasks):
            order = variants if (repeat + task_index + int(args.start_v8)) % 2 == 0 else list(reversed(variants))
            for name, dll in order:
                path = args.output / ("%s-%s-%02d" % (task_name, name, repeat + 1))
                path.mkdir()
                print("START", task_name, name, repeat + 1, flush=True)
                summary = run_case(name, dll, task_name, TASKS[task_name], path,
                                   args.origin.rstrip("/"), args.model)
                manifest["runs"].append(summary)
                (args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
                print("DONE", task_name, name, summary["lifecycle"], summary["acceptance"], flush=True)


if __name__ == "__main__":
    main()
