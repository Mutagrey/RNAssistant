#!/usr/bin/env python3
"""Pinned, isolated CLI task comparison with exact local HTTP evidence.

This is an exploratory runner, not the phase-5 qualification gate: the reason
ablation uses an explicit transport shim, and target-host checks remain open.
"""

import argparse
import copy
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
    logical = envelope.get("response", envelope) if isinstance(envelope, dict) else envelope
    return (logical.get("action") if isinstance(logical, dict) else None), usage


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
    if policy not in ("no_reflection_schema", "message_first_schema", "no_reason_schema"):
        return body
    request = json.loads(body)
    schema = (request.get("response_format") or {}).get("json_schema", {}).get("schema")
    message_first = policy == "message_first_schema"
    no_reason = policy == "no_reason_schema"
    old_order = "Order fields: action, reason, action-specific fields, message last."
    new_order = "Order fields: message first, then action, reason, and action-specific fields."
    no_reason_order = "Order fields: action, action-specific fields, message last. Do not include a root reason field."
    if schema is None:
        # Compatibility probes and JSON-object fallbacks have no branch schema.
        # Only task requests with the v8 guidance need a prompt-level policy.
        if not any("conversation-response-v8" in str(message.get("content", ""))
                   for message in request.get("messages", [])):
            return body
        request["messages"].append({"role": "developer", "content":
                                    "Benchmark policy: return message as the first JSON field." if message_first else
                                    "Benchmark ablation: omit the root reason field from every response." if no_reason else
                                    "Benchmark policy: reflection is unavailable. Choose execute or finish."})
        return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    decision_schema = schema.get("properties", {}).get("response", schema)
    branches = decision_schema.get("anyOf")
    if not isinstance(branches, list):
        return body
    if no_reason:
        schema["description"] = schema.get("description", "").replace(old_order, no_reason_order)
        for branch in branches:
            properties = branch["properties"]
            if "reason" not in properties or "reason" not in branch["required"]:
                raise ValueError("Expected required reason in v8 schema")
            del properties["reason"]
            branch["required"] = [field for field in branch["required"] if field != "reason"]
        for message in request.get("messages", []):
            if isinstance(message.get("content"), str):
                message["content"] = message["content"].replace(old_order, no_reason_order)
        request["messages"].append({"role": "developer", "content":
                                    "Benchmark ablation: omit the root reason field from every response. "
                                    "The test transport supplies a neutral runtime placeholder."})
        return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if message_first:
        schema["description"] = schema.get("description", "").replace(old_order, new_order)
        for branch in branches:
            properties = branch["properties"]
            if list(properties)[:2] != ["action", "reason"] or list(properties)[-1] != "message":
                raise ValueError("Unexpected v8 property order in field-order ablation")
            branch["properties"] = {"message": properties["message"],
                                    **{key: value for key, value in properties.items() if key != "message"}}
            branch["required"] = list(branch["properties"])
        for message in request.get("messages", []):
            if isinstance(message.get("content"), str):
                message["content"] = message["content"].replace(old_order, new_order)
        return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    selected = [branch for branch in branches
                if branch["properties"]["action"].get("const") != "reflection"]
    if len(branches) - len(selected) != 1 or len(selected) < 1:
        raise ValueError("Expected exactly one reflection branch in v8 schema")
    decision_schema["anyOf"] = selected
    return json.dumps(request, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def apply_response_policy(body, policy):
    if policy != "no_reason_schema":
        return body
    envelope, _usage = response_envelope(body)
    wrapped = isinstance(envelope, dict) and "response" in envelope
    if wrapped: envelope = envelope["response"]
    if not isinstance(envelope, dict) or envelope.get("action") not in ("execute", "reflection", "finish"):
        return body
    translated = {"action": envelope["action"],
                  "reason": "Benchmark transport placeholder; no model reason was requested."}
    translated.update({key: value for key, value in envelope.items() if key not in ("action", "reason")})
    content = json.dumps({"response": translated} if wrapped else translated, ensure_ascii=False, separators=(",", ":"))
    if not body.startswith(b"data:"):
        response = json.loads(body)
        response["choices"][0]["message"]["content"] = content
        return json.dumps(response, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    chunks = [json.loads(line[5:].strip()) for line in body.decode("utf-8").splitlines()
              if line.startswith("data:") and line[5:].strip() != "[DONE]"]
    first = copy.deepcopy(chunks[0])
    first["choices"][0]["delta"] = {"role": "assistant", "content": content}
    terminal = next((copy.deepcopy(chunk) for chunk in chunks
                     if chunk.get("choices") and chunk["choices"][0].get("finish_reason")), None)
    usage = next((copy.deepcopy(chunk) for chunk in reversed(chunks) if chunk.get("usage")), None)
    if terminal is None:
        raise ValueError("No terminal SSE chunk in reason ablation")
    terminal["choices"][0]["delta"] = {}
    selected = [first, terminal] + ([usage] if usage else [])
    return ("".join("data: " + json.dumps(chunk, ensure_ascii=False, separators=(",", ":")) + "\n\n"
                    for chunk in selected) + "data: [DONE]\n\n").encode("utf-8")


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
        try:
            body = apply_request_policy(client_body, self.server.request_policy) if self.path.startswith("/v1/chat/completions") else client_body
        except (ValueError, KeyError, TypeError) as error:
            with self.server.capture_lock:
                self.server.policy_errors.append(str(error))
            self.send_error(502, "Benchmark request policy rejected the request")
            return
        headers = {key: value for key, value in self.headers.items()
                   if key.lower() not in ("host", "content-length", "connection", "accept-encoding", "authorization")}
        headers["Accept-Encoding"] = "identity"
        request = Request(self.server.origin + self.path, data=body if self.command == "POST" else None,
                          headers=headers, method=self.command)
        started = time.monotonic()
        try:
            with urlopen(request, timeout=300) as response:
                status, mime, upstream_payload = response.status, response.headers.get("Content-Type", "application/json"), response.read()
        except HTTPError as error:
            status, mime, upstream_payload = error.code, error.headers.get("Content-Type", "application/json"), error.read()
        except Exception as error:
            status, mime, upstream_payload = 502, "text/plain", str(error).encode("utf-8")
        try:
            payload = apply_response_policy(upstream_payload, self.server.request_policy) if self.path.startswith("/v1/chat/completions") and status == 200 else upstream_payload
        except (ValueError, KeyError, TypeError) as error:
            with self.server.capture_lock:
                self.server.policy_errors.append(str(error))
            self.send_error(502, "Benchmark response policy rejected the response")
            return
        elapsed = round(time.monotonic() - started, 3)
        with self.server.capture_lock:
            index = self.server.capture_count
            self.server.capture_count += 1
        stem = "%04d" % index
        if client_body != body:
            (self.server.capture_dir / (stem + ".client_request.body")).write_bytes(client_body)
        (self.server.capture_dir / (stem + ".request.body")).write_bytes(body)
        if upstream_payload != payload:
            (self.server.capture_dir / (stem + ".upstream_response.body")).write_bytes(upstream_payload)
        (self.server.capture_dir / (stem + ".response.body")).write_bytes(payload)
        action, usage = response_action(payload) if self.path.startswith("/v1/chat/completions") else (None, None)
        record = {"index": index, "method": self.command, "path": self.path, "status": status,
                  "seconds": elapsed, "request_policy_transformed": client_body != body,
                  "request_sha256": sha256(body),
                  "client_request_sha256": sha256(client_body),
                  "upstream_response_sha256": sha256(upstream_payload),
                  "response_policy_transformed": upstream_payload != payload,
                  "response_sha256": sha256(payload),
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
    proxy.request_policy = {"v8_direct": "no_reflection_schema",
                            "v8_message_first": "message_first_schema",
                            "v8_no_reason": "no_reason_schema"}.get(name, "unchanged")
    proxy.capture_count, proxy.capture_lock, proxy.policy_errors = 0, threading.Lock(), []
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
    if proxy.policy_errors:
        raise RuntimeError("Benchmark request policy failed: " + "; ".join(proxy.policy_errors))
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
    upstream_responses = [response_envelope((captures / ("%04d.upstream_response.body" % item["index"])).read_bytes()
                       if (captures / ("%04d.upstream_response.body" % item["index"])).exists()
                       else (captures / ("%04d.response.body" % item["index"])).read_bytes())[0]
                       for item in http if item["path"].startswith("/v1/chat/completions")]
    responses = [item.get("response", item) if isinstance(item, dict) else item for item in responses]
    upstream_responses = [item.get("response", item) if isinstance(item, dict) else item for item in upstream_responses]
    proposed_calls = [call for item in responses if isinstance(item, dict) and item.get("action") in ("execute", "tool")
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
               "message_first_envelopes": sum(next(iter(item), None) == "message" for item in responses if isinstance(item, dict)),
               "upstream_reason_field_count": sum("reason" in item for item in upstream_responses if isinstance(item, dict)),
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
    parser.add_argument("--variants", default="v7,v8", help="Comma-separated v7, v8, v8_direct, v8_message_first and/or v8_no_reason variants")
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
            any(variant not in ("v7", "v8", "v8_direct", "v8_message_first", "v8_no_reason") for variant in selected_variants):
        parser.error("--variants must select v7, v8, v8_direct, v8_message_first and/or v8_no_reason without duplicates")
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
    variants = [(name, dll) for name, dll in (("v7", args.v7), ("v8", args.v8),
                ("v8_direct", args.v8), ("v8_message_first", args.v8), ("v8_no_reason", args.v8))
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
