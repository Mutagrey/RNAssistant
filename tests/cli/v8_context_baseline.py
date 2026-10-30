"""Capture exact production CLI HTTP bytes against a scripted, synthetic endpoint.

Does not qualify a real model or Office. Builds must be supplied separately.
"""
import argparse
import hashlib
import json
import os
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory

from conversation_transport import conversation_content

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"


def source_bodies(request):
    """Inspect delivered source blocks, never literal strings inside call arguments."""
    bodies, receipts = [], []
    for index, message in enumerate(request["messages"]):
        content = message.get("content") or ""
        if not isinstance(content, str):
            continue
        if content.startswith("CURRENT_RESOURCE_VIEW ("):
            header, body = content.split("\n", 1)[1].split("\n", 1)
            bodies.append(dict(json.loads(header), text=body, message=index, location="current-view"))
            continue
        if content.startswith(("TOOL_RESULT:\n", "TOOL_INTERACTION (")):
            content = content.split("\n", 1)[1]
        try:
            receipt = json.loads(content)
        except ValueError:
            continue
        if isinstance(receipt, dict) and message.get("role") == "tool":
            receipt = dict(receipt, tool_call_id=message["tool_call_id"])
        if not isinstance(receipt, dict) or "tool_call_id" not in receipt:
            continue
        receipts.append((index, receipt))
        data = receipt.get("data") or {}
        if data.get("kind") == "resource-read" and data.get("rawContentIncluded") and isinstance(data.get("text"), str):
            bodies.append(dict(data, message=index, location="tool-result"))
    for index, receipt in receipts:
        retained = receipt.get("retained_result", {}).get("tool_call_id")
        if retained:
            assert any(position > index and other["tool_call_id"] == retained and
                       (other.get("data") or {}).get("rawContentIncluded")
                       for position, other in receipts), "Dangling retained_result in actual request"
    return bodies


def assert_native_pairs(request):
    pending, seen = set(), set()
    for message in request["messages"]:
        if message["role"] == "tool":
            reference = message.get("tool_call_id")
            assert reference in pending, "Orphan or repeated native result"
            pending.remove(reference)
        else:
            assert not pending, "Native call interrupted before its result"
            for call in message.get("tool_calls", []):
                assert call["id"] not in seen, "Duplicate native call ID"
                seen.add(call["id"])
                pending.add(call["id"])
    assert not pending, "Missing native result"


class ExactCapture:
    """Opt-in fixture export; no auth headers, external endpoint or alternate runtime."""
    def __init__(self, output, case, role):
        output.mkdir(parents=True, exist_ok=False)
        self.output, self.case, self.role = output, case, role
        self.requests, self.exchanges, self.lock = [], [], threading.Lock()

    def __call__(self, raw, response):
        request = json.loads(raw)
        content = "\n".join(m.get("content") or "" for m in request["messages"])
        purpose = "title" if "response_format" not in request else (
            "compaction" if "CONVERSATION_PREFIX (data to summarize" in content else "conversation")
        with self.lock:
            index = len(self.requests)
            self.requests.append(request)
            request_file, response_file = f"{index:02d}.request.json", f"{index:02d}.response.json"
            (self.output / request_file).write_bytes(raw)
            (self.output / response_file).write_bytes(response)
            self.exchanges.append({"index": index, "purpose": purpose, "status": 200,
                "request": request_file, "response": response_file,
                "requestSha256": hashlib.sha256(raw).hexdigest(),
                "responseSha256": hashlib.sha256(response).hexdigest()})

    def finish(self, result):
        rows = []
        for exchange, request in zip(self.exchanges, self.requests):
            if exchange["purpose"] != "conversation":
                continue
            assert_native_pairs(request)
            bodies = source_bodies(request)
            skills = []
            for index, message in enumerate(request["messages"]):
                content = message.get("content") or ""
                if content.startswith("SELECTED_SKILL "):
                    header, instructions = content.split("\n", 1)
                    skills.append({"id": header[len("SELECTED_SKILL "):], "message": index,
                        "duplicateSourceMessages": [body["message"] for body in bodies if body["text"] == instructions]})
            rows.append({"request": exchange["index"], "roles": [m["role"] for m in request["messages"]],
                "responseFormat": request.get("response_format", {}).get("type"),
                "sourceBodies": [{"message": body["message"], "location": body["location"],
                    "target": body.get("target"), "ref": body.get("ref"), "coverage": body.get("coverage"),
                    "complete": body.get("complete"), "characters": len(body["text"]),
                    "sha256": hashlib.sha256(body["text"].encode()).hexdigest()} for body in bodies],
                "selectedSkills": skills})
        manifest = {"kind": "synthetic-http-baseline", "case": self.case, "role": self.role,
            "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
            "workingTreeDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=REPO)),
            "cliSha256": hashlib.sha256(CLI.read_bytes()).hexdigest(),
            "referenceRevision": 3, "finishResultField": "criterion", "realModelEvaluation": False,
            "exchanges": self.exchanges, "requests": rows, "fixtureResult": result}
        (self.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        print(f"Captured {len(rows)} conversation requests ({self.case}): {self.output}")


def capture(output, role, case="read-patch"):
    recorder = ExactCapture(output, case, role)
    read = {"action": "execute", "reason": "Inspect synthetic source.", "message": "Read.",
            "tool_calls": [{"name": "core.read", "arguments": {"target": "sample.txt"}}]}

    def patch(before, after):
        return {"action": "execute", "reason": "Change synthetic source.", "message": "Patch.",
                "tool_calls": [{"name": "core.patch", "arguments": {"targets": [{"target": "sample.txt",
                            "edits": [{"old": before, "new": after}]}]}}]}

    done = {"finish_from_request": True}
    scripts = {
        "read-patch": [read, read, patch("BASELINE_OLD", "BASELINE_NEW"), read, done, read, done],
        "multi-patch": [read, patch("BASELINE_OLD", "BASELINE_MIDDLE"),
                        patch("BASELINE_MIDDLE", "BASELINE_NEW"), read, done],
        "failure-correction": [read, patch("ABSENT_BEFORE", "MUST_NOT_APPEAR"),
                               patch("BASELINE_OLD", "BASELINE_NEW"), read, done],
    }
    script, calls, errors, finals = scripts[case], [], [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            raw = self.rfile.read(int(self.headers["Content-Length"]))
            request = json.loads(raw)
            if "response_format" in request:
                step = len(calls)
                calls.append(request)
                if step >= len(script):
                    errors.append("Unexpected conversation request; no scripted fallback")
                    self.send_error(500)
                    return
                decision = script[step]
                content = conversation_content(request, decision)
            else:
                content = "Synthetic baseline"
            response = json.dumps({"choices": [{"message": {"role": "assistant", "content": content},
                                                "finish_reason": "stop"}]}).encode()
            recorder(raw, response)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with TemporaryDirectory(prefix="rna-v8-baseline-") as temp:
            root = Path(temp)
            workspace = root / "workspace"
            workspace.mkdir()
            (workspace / "sample.txt").write_text("BASELINE_OLD\n", encoding="utf-8")
            env = {key: value for key, value in os.environ.items() if not key.startswith("RNA_")}
            env.update(RNA_STATE_ROOT=str(root / "state"), RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}",
                       RNA_MODEL="scripted-baseline", RNA_API_KEY="synthetic")
            session = []
            for turn in range(2 if case == "read-patch" else 1):
                result = subprocess.run(["dotnet", str(CLI), "run", "--workspace", str(workspace),
                    "--message", "Inspect, apply the requested edits and verify." if turn == 0 else "Check the saved change.",
                    "--tool-result-role", role, "--auto-confirm", "on", "--max-iterations", "10",
                    "--max-tool-steps", "10", "--context-tokens", "32768", "--jsonl", *session], env=env, cwd=REPO,
                    text=True, capture_output=True, timeout=60)
                (output / f"turn-{turn}.jsonl").write_text(result.stdout, encoding="utf-8")
                if result.returncode != 0 or errors:
                    raise RuntimeError(result.stderr + result.stdout)
                final = json.loads(result.stdout.splitlines()[-1])["data"]
                assert final["reason"] == "model_finish", final
                finals.append(dict(final, cliExitCode=result.returncode))
                session = ["--session", final["SessionId"]]
            assert (workspace / "sample.txt").read_text() == "BASELINE_NEW\n"
            assert len(calls) == len(script), (len(calls), len(script))
        expected_bodies = {
            "read-patch": [None, "BASELINE_OLD", "BASELINE_OLD", "BASELINE_NEW", "BASELINE_NEW", "BASELINE_NEW", "BASELINE_NEW"],
            "multi-patch": [None, "BASELINE_OLD", "BASELINE_MIDDLE", "BASELINE_NEW", "BASELINE_NEW"],
            "failure-correction": [None, "BASELINE_OLD", "BASELINE_OLD", "BASELINE_NEW", "BASELINE_NEW"],
        }[case]
        for request, expected in zip(calls, expected_bodies):
            bodies = source_bodies(request)
            assert [body["text"].strip() for body in bodies] == ([] if expected is None else [expected]), (
                case, expected, bodies)
        if case == "read-patch":
            users = [m["content"] for m in calls[-1]["messages"] if m["role"] == "user"]
            assert "Inspect, apply the requested edits and verify." in users and "Check the saved change." in users
        counts = finals[0]["ToolCounts"]
        assert counts["WriteOk"] == (2 if case == "multi-patch" else 1) and counts["WriteUnknown"] == 0, counts
        assert counts["WriteError"] == (1 if case == "failure-correction" else 0), counts
        recorder.finish(finals)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--role", choices=("user", "developer", "tool"), default="tool")
    parser.add_argument("--case", choices=("read-patch", "multi-patch", "failure-correction", "compaction", "skills", "all"),
                        default="read-patch")
    args = parser.parse_args()
    output = args.output.resolve()
    cases = ("read-patch", "multi-patch", "failure-correction", "compaction", "skills") if args.case == "all" else (args.case,)
    if args.case == "all":
        output.mkdir(parents=True, exist_ok=False)
    for case in cases:
        folder = output / case if args.case == "all" else output
        if case in ("compaction", "skills"):
            # Reuse the existing production fixtures instead of introducing a second context runner.
            from compacted_read_guard_smoke import run_case
            from skill_packages_smoke import check
            recorder = ExactCapture(folder, case, "tool" if case == "skills" else args.role)
            with TemporaryDirectory(prefix="rna-v8-" + case + "-") as temporary:
                result = check(Path(temporary), recorder) if case == "skills" else run_case(Path(temporary), args.role, recorder)
            recorder.finish(result)
        else:
            capture(folder, args.role, case)
