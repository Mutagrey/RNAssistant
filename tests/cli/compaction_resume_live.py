"""Real LLM compaction + disposable CLI process loss + explicit same-run resume.

The proxy forwards exact HTTP bodies and bytes. It holds one post-checkpoint
request before upstream dispatch, so the test can kill its own CLI at model wait.
It never manufactures a model response, checkpoint, accepted history or hint.
The independent byte grader and task are outside the writable workspace.
"""
import argparse
import gzip
import hashlib
import http.client
import json
import os
import shutil
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import mkdtemp
from urllib.parse import urlsplit


REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll"
TASK_FILE = Path(__file__).with_name("compaction_resume_task.md")
OLD_MARKER = "ANCHOR_ORIGINAL_NOTE"
NEW_MARKER = "ANCHOR_EXTERNAL_NOTE_KEEP"
WORKSPACE_MANIFEST = ".rnassistant/workspace.json"


def digest(value):
    return hashlib.sha256(value).hexdigest()


def fixture():
    files = {"manifest.txt": "\n".join("notes/%02d.md" % n for n in range(1, 9)) + "\n"}
    for n in range(1, 9):
        lines = ["# Release note %02d" % n, "status: draft", "review: pending",
                 "operator_note: " + (OLD_MARKER if n == 1 else "retain-%02d" % n), ""]
        for row in range(1, 31):
            lines.append("Detail %02d.%02d: %s. This recorded description belongs to the release archive; "
                         "its identifiers and wording remain part of the original source record."
                         % (n, row, digest(("note:%d:row:%d" % (n, row)).encode())[:24]))
        files["notes/%02d.md" % n] = "\n".join(lines) + "\n"
    return {name: text.encode() for name, text in files.items()}


def read_blob(root, sha256, byte_length):
    if len(sha256) != 64 or any(c not in "0123456789abcdef" for c in sha256):
        raise ValueError("Invalid CAS hash")
    raw = (root / "state/chat-blobs" / sha256[:2] / (sha256 + ".blob")).read_bytes()
    if raw.startswith(b"RNACAS01"):
        if raw[8:12] != b"\x01\x00\x00\x00" or int.from_bytes(raw[20:28], "little") != len(raw) - 28:
            raise ValueError("Unsupported or corrupt CAS envelope")
        raw = gzip.decompress(raw[28:])
    if len(raw) != byte_length or digest(raw) != sha256:
        raise ValueError("CAS integrity failure")
    return raw


def audit_readback_order(root):
    """Grade this fixture's prescribed sequence, independently of context/counters.

    Only canonical accepted calls and typed terminal evidence count. A mutation's
    internal read-back is not the separate resource read requested by the task.
    """
    paths = sorted(name for name in fixture() if name.startswith("notes/"))
    streams = list((root / "state/chats").rglob("*.events.jsonl"))
    if len(streams) != 1:
        raise ValueError("Read-back grading requires exactly one canonical chat")
    events = [json.loads(line) for line in streams[0].read_text().splitlines()]
    calls, errors = {}, []
    for event in events:
        if event["Type"] != "session.commit":
            continue
        for index, operation in enumerate(event["Data"]["Operations"]):
            value = operation["Data"].get("Value", {})
            position = (event["Sequence"], index)
            if operation["Type"] == "tool.call.recorded":
                for call_index, call in enumerate(value["ToolCalls"]):
                    if call["Id"] in calls:
                        raise ValueError("Duplicate accepted call")
                    args = json.loads(call["ArgumentsJson"])
                    calls[call["Id"]] = {"id": call["Id"], "name": call["Name"],
                        "path": args.get("target", args.get("relativePath")),
                        "accepted": (*position, call_index), "run": value["RunId"]}
            elif operation["Type"] == "tool.result.recorded" and value.get("ToolCallId"):
                call = calls.get(value.get("ToolCallId"))
                if call is None or "result" in call or value.get("ToolName") != call["name"] or value.get("RunId") != call["run"]:
                    raise ValueError("Missing, duplicate or mismatched terminal call/result")
                call.update(result=value, ended=(*position, 0))

    # The runner's external edit is outside chat history. Admit only its exact
    # recorded marker-only transition at the saved interruption boundary.
    external, boundary = None, None
    if (root / "external-edit.json").is_file():
        external = json.loads((root / "external-edit.json").read_text())
        prefix = (root / "events-before-resume.jsonl").read_bytes()
        if not streams[0].read_bytes().startswith(prefix):
            raise ValueError("Interrupted canonical prefix changed")
        boundary = (json.loads(prefix.splitlines()[-1])["Sequence"], float("inf"), 0)
        if external["path"] != paths[0]:
            raise ValueError("Unexpected external edit target")

    observations, mutations = [], []
    for call in calls.values():
        if call["path"] not in paths:
            continue
        result = call.get("result", {})
        outcome = (result.get("ExecutionProgress") or {}).get("Outcome")
        effect = result.get("ResourceEffect") or {}
        if outcome == 2 or effect.get("outcome") == "UnknownAfterDispatch":
            errors.append({"code": "unknown_note_effect", "callId": call["id"]})
        if outcome != 0:  # ToolExecutionOutcome.Ok; refusals are not successful steps.
            continue
        if call["name"] == "common.resources_read":
            evidence = result.get("ResourceEvidence") or []
            if len(evidence) != 1:
                continue
            evidence = evidence[0]
            payload, reference = evidence.get("Payload"), evidence.get("Resource") or {}
            if evidence.get("Complete") is not True or (evidence.get("Coverage") or {}).get("kind") != "whole" or \
                    (evidence.get("ScopeId") or {}).get("kind") != "file" or evidence.get("View") not in ("text", "source") or \
                    not payload or not reference.get("uri") or not reference.get("revision"):
                continue
            body = read_blob(root, payload["sha256"], payload["byteLength"])
            if evidence.get("ContentSha256") != digest(body):
                raise ValueError("Read evidence and exact body disagree")
            observations.append(dict(call, reference=reference, body=body))
        elif call["name"].startswith("files."):
            if effect.get("outcome") == "VerifiedNoChange":
                continue
            impacts = effect.get("impacts") or []
            impact = impacts[0] if len(impacts) == 1 else {}
            before, after = impact.get("before") or {}, impact.get("after") or {}
            if effect.get("outcome") != "VerifiedChanged" or effect.get("operation") != call["name"] or \
                    impact.get("relation") != "Exact" or (impact.get("coverage") or {}).get("kind") != "whole" or \
                    not before.get("revision") or not after.get("revision") or before == after or \
                    not before.get("uri") or before.get("uri") != after.get("uri"):
                errors.append({"code": "unverified_note_write", "callId": call["id"]})
                continue
            mutations.append(dict(call, before=before, after=after))

    initial = fixture()
    stages, readback_positions = [], []
    for index, path in enumerate(paths + [paths[0]]):
        review = index == len(paths)
        writes = [call for call in mutations if call["path"] == path]
        write_index = 1 if review else 0
        stage = {"path": path, "phase": "review" if review else "status", "passed": False}
        stages.append(stage)
        readback_positions.append(None)
        if len(writes) <= write_index:
            errors.append({"code": "missing_verified_write", "path": path, "phase": stage["phase"]})
            continue
        write = writes[write_index]
        stage["writeCallId"], stage["writeSequence"] = write["id"], write["ended"][0]
        expected_before = initial[path]
        if review:
            expected_before = expected_before.replace(b"status: draft\n", b"status: ready\n")
        expected_after = expected_before.replace(b"review: pending\n", b"review: complete\n") if review else \
            expected_before.replace(b"status: draft\n", b"status: ready\n")

        def expected_body(body, expected):
            return body == expected or external is not None and path == paths[0] and \
                body == expected.replace(OLD_MARKER.encode(), NEW_MARKER.encode())

        prior = [read for read in observations if read["path"] == path and read["ended"] < write["accepted"] and
                 read["reference"] == write["before"] and expected_body(read["body"], expected_before)]
        read_before = prior[-1] if prior else None
        # A later note must not be started before this note's read-back result.
        # For note 08, the next phase starts with the required fresh anchor read.
        next_path = paths[index + 1] if index < len(paths) - 1 else paths[0]
        following = [call for call in calls.values() if call["path"] == next_path and
                     (index < len(paths) - 1 or call["accepted"] > write["accepted"])] if not review else []
        deadline = min((call["accepted"] for call in following), default=None)

        def is_after(read):
            if read["path"] != path or read["accepted"] <= write["ended"] or not expected_body(read["body"], expected_after):
                return False
            if read["reference"] == write["after"]:
                return True
            return external is not None and path == paths[0] and write["ended"] < boundary < read["accepted"] and \
                read["reference"]["uri"] == write["after"]["uri"] and external["before"] == digest(expected_after) and \
                external["after"] == digest(expected_after.replace(OLD_MARKER.encode(), NEW_MARKER.encode())) == digest(read["body"])

        read_after = next((read for read in observations if is_after(read)), None)
        stage.update(readSequence=read_before["ended"][0] if read_before else None,
                     readbackSequence=read_after["ended"][0] if read_after else None,
                     nextNoteSequence=deadline[0] if deadline else None)
        readback_positions[-1] = read_after["ended"] if read_after else None
        ordered = read_after is not None and (deadline is None or read_after["ended"] < deadline)
        if review:
            ordered = ordered and all(item["passed"] for item in stages[:-1]) and read_before is not None and \
                read_before["accepted"] > readback_positions[-2]
        stage["passed"] = read_before is not None and ordered
        if not stage["passed"]:
            errors.append({"code": "read_write_readback_order", "path": path, "phase": stage["phase"],
                           "writeCallId": write["id"]})
    for path in paths:
        if sum(call["path"] == path for call in mutations) > (2 if path == paths[0] else 1):
            errors.append({"code": "extra_verified_write", "path": path})
    return {"passed": not errors and all(stage["passed"] for stage in stages), "stages": stages, "errors": errors}


def audit_canonical(root, before=None, final=None, prefix_sequence=0):
    events = [json.loads(line) for path in (root / "state/chats").rglob("*.events.jsonl")
              for line in path.read_text().splitlines()]
    events.sort(key=lambda event: event["Sequence"])
    errors, bodies, call_ids, result_ids, steps = [], {}, [], [], set()
    pair_checks, new_calls, uncharged_calls, summaries, latest_result = 0, 0, 0, 0, None
    task = (root / "task.md").read_text()

    def body(ref):
        return json.loads(read_blob(root, ref["Sha256"], ref["ByteLength"]))

    for event in events:
        if event["Type"] == "llm.request":
            wire = body(event["Payload"])
            bodies[event["Sequence"]] = wire
            if event["Data"].get("Purpose") == "context_compaction":
                summaries += 1
                continue
            if event["Sequence"] > prefix_sequence:
                steps.add(event["Data"]["StepId"])
            text = "\n".join(m.get("content") or "" for m in wire["messages"])
            if task not in text:
                errors.append("Missing exact task at request " + str(event["Sequence"]))
            if latest_result and latest_result not in text:
                errors.append("Missing latest tool result at request " + str(event["Sequence"]))
            if "rna://" in text:
                errors.append("Runtime resource URI reached the model at " + str(event["Sequence"]))
            for index, message in enumerate(wire["messages"]):
                content = message.get("content") or ""
                if not content.startswith("TOOL_RESULT:\n"):
                    continue
                result = json.loads(content.split("\n", 1)[1])
                previous = wire["messages"][index - 1] if index else {}
                try:
                    call = json.loads(previous.get("content") or "{}")
                except ValueError:
                    call = {}
                if previous.get("role") != "assistant" or [c["name"] for c in call.get("tool_calls", [])] != [result["name"]]:
                    errors.append("Broken call/result pair at " + str(event["Sequence"]))
                pair_checks += 1
        elif event["Type"] == "llm.response":
            body(event["Payload"])
        elif event["Type"] == "session.commit":
            for operation in event["Data"]["Operations"]:
                value = operation["Data"].get("Value", {})
                if operation["Type"] == "tool.call.recorded":
                    ids = [c["Id"] for c in value.get("ToolCalls", [])]
                    call_ids.extend(ids)
                    if event["Sequence"] > prefix_sequence:
                        new_calls += len(ids)
                if operation["Type"] == "tool.result.recorded" and value.get("Role") != "assistant" and value.get("ToolCallId"):
                    result_ids.append(value["ToolCallId"])
                    latest_result = value["ToolCallId"]
                    # This fixture uses one-step workspace tools. Kernel-denied
                    # repeats close the accepted exchange without spending a step.
                    # 4 is the durable ToolExecutionOutcome.NotDispatched value.
                    if event["Sequence"] > prefix_sequence and (value.get("ExecutionProgress") or {}).get("Outcome") == 4:
                        uncharged_calls += 1
    if len(set(call_ids)) != len(call_ids) or sorted(call_ids) != sorted(result_ids):
        errors.append("Accepted calls/results are not unique and complete")
    raw_requests = [json.loads(p.read_bytes()) for p in sorted(root.glob("request-*.json"))]
    if list(bodies.values()) != raw_requests:
        errors.append("Proxy request bodies differ from canonical HTTP evidence")
    if before is not None and final is not None:
        if final["IterationsUsed"] != before["IterationsUsed"] + len(steps):
            errors.append("Resumed iteration accounting differs from dispatched kernel steps")
        if final["ToolStepsUsed"] != before["ToolStepsUsed"] + new_calls - uncharged_calls:
            errors.append("Resumed tool accounting differs from charged workspace calls")
    return {"errors": errors, "requests": len(bodies), "summaryRequests": summaries,
            "pairChecks": pair_checks, "acceptedCalls": len(call_ids), "stepsAfterBoundary": len(steps),
            "newCalls": new_calls, "unchargedCalls": uncharged_calls}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", required=True, help="Existing local OpenAI-compatible real-model endpoint")
    parser.add_argument("--model", required=True)
    parser.add_argument("--digest", required=True)
    parser.add_argument("--cli", type=Path, default=CLI, help="Pinned production CLI binary for comparisons")
    parser.add_argument("--source-root", type=Path, default=REPO, help="Source checkout used for the pinned build")
    parser.add_argument("--context-tokens", type=int, default=24576)
    parser.add_argument("--seconds", type=int, default=600, help="One total deadline, including restart")
    args = parser.parse_args()
    upstream = urlsplit(args.base_url)
    if upstream.scheme != "http" or upstream.hostname not in ("127.0.0.1", "localhost", "::1") or upstream.path not in ("", "/"):
        parser.error("Use a local endpoint root, without /v1 or credentials.")
    if upstream.username or upstream.password or upstream.query or upstream.fragment:
        parser.error("Endpoint must not contain credentials, query or fragment.")
    if not args.cli.is_file():
        parser.error("Build the production CLI first.")
    root = Path(mkdtemp(prefix="rna-live-compaction-resume-"))
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    initial = fixture()
    for name, content in initial.items():
        for base in (workspace, root / "initial"):
            target = base / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
    task = TASK_FILE.read_text()
    shutil.copy2(TASK_FILE, root / "task.md")
    shutil.copy2(__file__, root / "driver.py")
    shutil.copytree(args.cli.parent, root / "cli", ignore=shutil.ignore_patterns("*.pdb", "*.xml"))
    cli_path = root / "cli" / args.cli.name

    def save(name, value):
        (root / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")

    metadata = {"head": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=args.source_root, text=True).strip(),
                "model": args.model, "digest": args.digest, "baseUrl": args.base_url,
                "contextTokens": args.context_tokens, "maxOutputTokens": 4096,
                "maxIterations": 40, "maxToolSteps": 48, "totalDeadlineSeconds": args.seconds,
                "instructionRole": "system", "toolResultRole": "user", "responseMode": "json_schema",
                "thinking": False, "taskSha256": digest(task.encode()),
                "binarySource": str(args.cli.resolve()), "driverSha256": digest(Path(__file__).read_bytes()),
                "fixture": {name: digest(content) for name, content in initial.items()},
                "binaries": {p.name: digest(p.read_bytes()) for p in cli_path.parent.glob("RNAssistant*.dll")}}
    subprocess.run(["git", "archive", "--format=tar", "-o", str(root / "source-at-run.tar"), "HEAD"], cwd=args.source_root, check=True)
    (root / "working-tree.patch").write_bytes(subprocess.check_output(["git", "diff", "--binary", "HEAD"], cwd=args.source_root))
    save("metadata.json", metadata)
    print("Evidence:", root, flush=True)
    entered, release = threading.Event(), threading.Event()
    phase = {"resumed": False, "summaries": 0, "held": False}
    requests, proxy_errors = [], []

    class Proxy(BaseHTTPRequestHandler):
        def do_POST(self):
            connection = None
            try:
                if self.path != "/v1/chat/completions":
                    raise ValueError("Unexpected endpoint: " + self.path)
                length = int(self.headers["Content-Length"])
                if not 0 < length <= 8 * 1024 * 1024:
                    raise ValueError("Unexpected request size")
                raw = self.rfile.read(length)
                request = json.loads(raw)
                if not requests:
                    # WorkspaceStore creates this reserved manifest before the
                    # first model request. It is runtime state, not agent output.
                    metadata["workspaceManifestSha256"] = digest((workspace / WORKSPACE_MANIFEST).read_bytes())
                    save("metadata.json", metadata)
                content = "\n".join(m.get("content") or "" for m in request["messages"])
                summary = "CONVERSATION_PREFIX (data to summarize" in content
                record = {"index": len(requests), "resumed": phase["resumed"], "summary": summary,
                          "sha256": digest(raw), "forwarded": False, "completed": False}
                requests.append(record)
                (root / ("request-%03d.json" % record["index"])).write_bytes(raw)
                if not summary and not phase["resumed"] and phase["summaries"] and not phase["held"] and "STRUCTURED_CONTEXT_CLAIMS" in content:
                    phase["held"] = True
                    record["held"] = True
                    entered.set()
                    release.wait(args.seconds)
                    self.close_connection = True
                    return  # No provider call or synthetic model response for the killed invocation.
                connection = http.client.HTTPConnection(upstream.hostname, upstream.port or 80, timeout=180)
                headers = {"Content-Type": self.headers.get("Content-Type", "application/json")}
                if self.headers.get("Authorization"):
                    headers["Authorization"] = self.headers["Authorization"]
                connection.request("POST", self.path, body=raw, headers=headers)
                record["forwarded"] = True
                response = connection.getresponse()
                record["status"] = response.status
                self.send_response(response.status)
                self.send_header("Content-Type", response.getheader("Content-Type", "application/json"))
                self.send_header("Connection", "close")
                self.end_headers()
                with (root / ("response-%03d.body" % record["index"])).open("wb") as output:
                    while True:
                        chunk = response.read1(65536)
                        if not chunk:
                            break
                        output.write(chunk)
                        self.wfile.write(chunk)
                        self.wfile.flush()
                record["completed"] = True
                if summary and response.status == 200:
                    phase["summaries"] += 1
            except (BrokenPipeError, ConnectionResetError):
                pass
            except Exception as error:
                proxy_errors.append(repr(error))
                self.close_connection = True
            finally:
                if connection:
                    connection.close()

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = {k: v for k, v in os.environ.items() if not k.startswith("RNA_")}
    env.update(RNA_STATE_ROOT=str(state), RNA_BASE_URL="http://127.0.0.1:%d" % server.server_port,
               RNA_MODEL=args.model, RNA_MODEL_DIGEST=args.digest, RNA_API_KEY=os.environ.get("RNA_API_KEY", "ollama"),
               RNA_CONTEXT_TOKENS=str(args.context_tokens), RNA_BROWSER_EXECUTABLE="/missing/browser")
    metadata["proxyBaseUrl"] = env["RNA_BASE_URL"]
    save("metadata.json", metadata)

    def command(*parts):
        return ["dotnet", str(cli_path), *parts, "--workspace", str(workspace), "--jsonl"]

    def inspect(session, name):
        result = subprocess.run(command("inspect", "--session", session), env=env, text=True, capture_output=True, timeout=15)
        (root / (name + ".jsonl")).write_text(result.stdout)
        if result.returncode:
            raise RuntimeError("inspect failed: " + result.stderr)
        value = json.loads(result.stdout.splitlines()[-1])["data"]
        save(name + ".json", value)
        return value

    llm_options = ["--instruction-role", "system", "--tool-result-role", "user", "--response-mode", "json_schema",
                   "--reasoning-mode", "reasoning_effort", "--thinking", "off"]
    expected_paths = [*initial, "receipt.txt"]
    arguments = command("run", "--task-file", str(root / "task.md"), *llm_options,
                        "--max-iterations", "40", "--max-tool-steps", "48", "--min-reads", "10", "--min-writes", "10",
                        "--expect-files", ",".join(expected_paths))
    process, files, audit = None, [], None
    start = time.monotonic()
    result = {"acceptance": "failed", "interrupted": False, "resumed": False}

    def start_cli(arguments, label):
        save(label + "-command.json", arguments)
        out, err = (root / (label + ".jsonl")).open("w"), (root / (label + ".stderr")).open("w")
        files.extend([out, err])
        return subprocess.Popen(arguments, cwd=REPO, env=env, stdout=out, stderr=err)

    try:
        process = start_cli(arguments, "before")
        while process.poll() is None and not entered.wait(.1):
            if time.monotonic() - start >= args.seconds:
                raise TimeoutError("Original total deadline before checkpoint")
        if not entered.is_set():
            rows = [json.loads(line) for line in (root / "before.jsonl").read_text().splitlines()]
            if rows:
                result["terminalBeforeBoundary"] = rows[-1]["data"]
            session = next((row["data"]["SessionId"] for row in rows if row["type"] == "input.accepted"), None)
            if session:
                result["checkpointId"] = inspect(session, "terminal-state")["ContextCheckpointId"]
            raise RuntimeError("Run ended before the post-checkpoint model dispatch boundary")
        rows = [json.loads(line) for line in (root / "before.jsonl").read_text().splitlines()]
        session = next(row["data"]["SessionId"] for row in rows if row["type"] == "input.accepted")
        before = inspect(session, "before-stop")
        assert before["ContextCheckpointId"] and before["Status"] == "running", before
        receipt = workspace / "receipt.txt"
        # Byte correctness belongs to the final grader; a model formatting error
        # must not prevent this independent interruption/continuation exercise.
        assert receipt.is_file(), "Receipt must exist before interruption"
        receipt_stamp = receipt.stat().st_mtime_ns
        process.kill()
        process.wait(timeout=10)
        result["interrupted"] = True
        event_path = next((state / "chats").rglob("*.events.jsonl"))
        prefix = event_path.read_bytes()
        (root / "events-before-resume.jsonl").write_bytes(prefix)
        anchor = workspace / "notes/01.md"
        current = anchor.read_bytes()
        assert OLD_MARKER.encode() in current
        result["anchorReviewPendingAtRestart"] = b"review: pending\n" in current
        anchor.write_bytes(current.replace(OLD_MARKER.encode(), NEW_MARKER.encode()))
        save("external-edit.json", {"path": "notes/01.md", "before": digest(current), "after": digest(anchor.read_bytes())})
        release.set()
        stopped = inspect(session, "after-stop")
        assert stopped["ContextCheckpointId"] == before["ContextCheckpointId"]
        phase["resumed"] = True
        process = start_cli(command("resume", "--session", session, "--continue", "--run", stopped["RunId"],
                                    "--cursor", stopped["Cursor"], *llm_options), "after")
        result["resumed"] = True
        while process.poll() is None:
            if time.monotonic() - start >= args.seconds:
                raise TimeoutError("Original total deadline after restart")
            time.sleep(.1)
        result["exitCode"] = process.returncode
        rows = [json.loads(line) for line in (root / "after.jsonl").read_text().splitlines()]
        final = rows[-1]["data"]
        save("final.json", final)
        final_state = inspect(session, "final-state")
        expected = dict(initial)
        for name in expected:
            if name.startswith("notes/"):
                expected[name] = expected[name].replace(b"status: draft\n", b"status: ready\n")
        expected["notes/01.md"] = expected["notes/01.md"].replace(OLD_MARKER.encode(), NEW_MARKER.encode()).replace(b"review: pending\n", b"review: complete\n")
        expected["receipt.txt"] = b"RELEASE_NOTES_STARTED\n"
        actual_names = {str(p.relative_to(workspace)) for p in workspace.rglob("*") if p.is_file()}
        result["fileChecks"] = {name: (workspace / name).is_file() and (workspace / name).read_bytes() == content for name, content in expected.items()}
        manifest = workspace / WORKSPACE_MANIFEST
        result["runtimeMetadataUnchanged"] = manifest.is_file() and digest(manifest.read_bytes()) == metadata["workspaceManifestSha256"]
        runtime_files = {WORKSPACE_MANIFEST} if result["runtimeMetadataUnchanged"] else set()
        result["extraFiles"] = sorted(actual_names - set(expected) - runtime_files)
        result["receiptUnchangedAfterRestart"] = receipt.stat().st_mtime_ns == receipt_stamp
        result["canonicalPrefixUnchanged"] = event_path.read_bytes().startswith(prefix)
        result["sameRun"] = final.get("RunId") == before["RunId"]
        result["limitsUnchanged"] = final_state["Limits"] == before["Limits"]
        result["countersPreserved"] = final["IterationsUsed"] > before["IterationsUsed"] and final["ToolStepsUsed"] >= before["ToolStepsUsed"]
        resumed = next(r for r in requests if r["resumed"] and not r["summary"])
        wire = json.loads((root / ("request-%03d.json" % resumed["index"])).read_bytes())
        content = "\n".join(m.get("content") or "" for m in wire["messages"])
        result["exactTaskRetained"] = task in content
        result["staleMarkerExcluded"] = OLD_MARKER not in content
        result["checkpointDeliveredAfterRestart"] = "STRUCTURED_CONTEXT_CLAIMS" in content
        result["checkpointId"] = before["ContextCheckpointId"]
        result["summaries"] = phase["summaries"]
        audit = audit_canonical(root, before, final, json.loads(prefix.splitlines()[-1])["Sequence"])
        save("canonical-audit.json", audit)
        predicates = [result[k] for k in ("receiptUnchangedAfterRestart", "canonicalPrefixUnchanged", "sameRun", "limitsUnchanged",
                      "countersPreserved", "exactTaskRetained", "staleMarkerExcluded", "checkpointDeliveredAfterRestart", "anchorReviewPendingAtRestart", "runtimeMetadataUnchanged")]
        result["continuationChecksPassed"] = all(predicates) and not audit["errors"]
        if process.returncode == 0 and final.get("acceptance") == "passed" and all(result["fileChecks"].values()) and not result["extraFiles"] and result["continuationChecksPassed"] and not proxy_errors:
            result["acceptance"] = "passed"
    except Exception as error:
        result["error"] = repr(error)
    finally:
        release.set()
        if process and process.poll() is None:
            process.kill()
            process.wait(timeout=10)
        server.shutdown()
        server.server_close()
        for output in files:
            output.close()
        if audit is None:
            try:
                audit = audit_canonical(root)
                save("canonical-audit.json", audit)
            except Exception as error:
                result["canonicalAuditError"] = repr(error)
        result["canonicalAuditPassed"] = audit is not None and not audit["errors"]
        result["readbackOrderPassed"] = False
        try:
            order_audit = audit_readback_order(root)
            save("readback-order-audit.json", order_audit)
            result["readbackOrderPassed"] = order_audit["passed"]
        except Exception as error:
            result["readbackOrderError"] = repr(error)
        if not result["readbackOrderPassed"]:
            result["acceptance"] = "failed"
        result["seconds"] = round(time.monotonic() - start, 2)
        result["proxyErrors"] = proxy_errors
        save("requests.json", requests)
        save("result.json", result)
        print(json.dumps(result, ensure_ascii=False, indent=2), flush=True)
    return 0 if result["acceptance"] == "passed" else 5


if __name__ == "__main__":
    raise SystemExit(main())
