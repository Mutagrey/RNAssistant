"""Synthetic, non-executing probes of the local OpenAI-compatible Ollama endpoint.

Uses the production codec's exported schema and validates each generated envelope
with both independent JSON Schema and the source-linked production parser.
"""
import argparse
import hashlib
import json
import subprocess
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from jsonschema import Draft202012Validator

BASE = "http://127.0.0.1:11434"


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=10) as response:
        return json.load(response)


def closed(properties):
    return {"type": "object", "properties": properties, "required": list(properties), "additionalProperties": False}


def run(args):
    source = Path(args.export).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[2]
    model = next(model for model in get("/api/tags")["models"] if model["name"] == args.model)
    schema = json.loads((source / "schema.json").read_text())
    guidance = (source / "guidance.txt").read_text()
    manifest = {"endpoint": BASE + "/v1/chat/completions", "started_utc": datetime.now(timezone.utc).isoformat(),
                "endpoint_version": get("/api/version"), "model": model,
                "base_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
                "working_tree": "uncommitted phase-1 candidate; full application not integrated",
                "limits": "Synthetic format probes; no tool dispatch, evidence admission, Office or task-success qualification.",
                "sources": {}, "probes": []}
    for path in [*sorted((repo / "src/RNAssistant.Core/ModelProtocol").glob("*.cs")),
                 repo / "src/RNAssistant.Core/Tools/ToolSchemaSupport.cs", Path(__file__).resolve(),
                 source / "schema.json", source / "guidance.txt"]:
        label = str(path.relative_to(repo)) if path.is_relative_to(repo) else path.name
        manifest["sources"][label] = hashlib.sha256(path.read_bytes()).hexdigest()
    save(output / "schema.json", schema)
    (output / "guidance.txt").write_text(guidance)
    variants = [closed({"action": {"type": "string", "const": name}, "value": {"type": "integer", "enum": [1, 2]}}) for name in ("alpha", "beta")]
    probes = [
        ("root-anyof", {"anyOf": variants}, 'Return {"action":"wrong","value":9,"extra":true}.', False, None),
        ("root-oneof", {"oneOf": variants}, 'Return {"action":"wrong","value":9,"extra":true}.', False, None),
        ("const", closed({"value": {"type": "string", "const": "fixed"}}), 'Return {"value":"different"}.', False, None),
        ("minitems-enum", closed({"items": {"type": "array", "items": {"type": "string", "enum": ["a", "b"]}, "minItems": 3, "maxItems": 3}}), 'Return {"items":[]}.', False, None),
        ("defs", {**closed({"value": {"$ref": "#/$defs/choice"}}), "$defs": {"choice": {"type": "string", "enum": ["allowed"]}}}, 'Return {"value":"wrong"}.', False, None),
        ("dynamic-execute", schema, "Synthetic protocol test. Choose execute and propose exactly one probe.read call with kind=sheets. No actual tool will be executed. Use an empty message.", True, "execute"),
        ("reflection", schema, "Synthetic protocol test. Choose reflection: decide to inspect before making changes. No tool call and no results. Empty message.", True, "reflection"),
        ("finish", schema, "Synthetic protocol test. Accepted call call-1 returned a terminal failure. Choose finish with one not_satisfied claim referencing call-1, explaining that the operation failed. Do not claim success.", True, "finish"),
        ("json-object", None, "Synthetic protocol test. There were no tool calls. Answer directly with finish, results=[], and message='VBA automates Office.'.", True, "finish")
    ]
    for name, constraint, prompt, envelope, expected_action in probes:
        body = {"model": args.model, "messages": [{"role": "system", "content": guidance if envelope else "Return only JSON for this synthetic format probe."}, {"role": "user", "content": prompt}],
                "max_tokens": 768, "temperature": 0, "top_p": 1, "stream": False,
                "response_format": {"type": "json_object"} if constraint is None else {"type": "json_schema", "json_schema": {"name": "probe_" + name.replace("-", "_"), "strict": True, "schema": constraint}}}
        request_bytes = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode()
        (output / (name + ".request.json")).write_bytes(request_bytes)
        start = time.monotonic()
        result = {"name": name}
        try:
            request = urllib.request.Request(manifest["endpoint"], data=request_bytes, headers={"Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(request, timeout=180) as response:
                    response_bytes = response.read()
                    result["status"] = response.status
            except urllib.error.HTTPError as error:
                result["status"] = error.code
                response_bytes = error.read()
            (output / (name + ".response.json")).write_bytes(response_bytes)
            if result["status"] == 200:
                response = json.loads(response_bytes)
                content = response["choices"][0]["message"]["content"]
                result["finish_reason"] = response["choices"][0].get("finish_reason")
                result["usage"] = response.get("usage")
                content_path = output / (name + ".content.json")
                content_path.write_text(content)
                value = json.loads(content)
                errors = list(Draft202012Validator(constraint or schema).iter_errors(value))
                result["schema_valid"] = not errors
                result["schema_errors"] = [error.message for error in errors]
                if expected_action:
                    result["requested_action_observed"] = value.get("action") == expected_action
                if envelope:
                    check = subprocess.run(["dotnet", str(repo / "tests/RNAssistant.ProtocolTests/bin/Debug/net8.0/RNAssistant.ProtocolTests.dll"), "--validate", str(content_path)], capture_output=True, text=True, cwd=repo)
                    result["parser_valid"] = check.returncode == 0
                    (output / (name + ".parser.txt")).write_text(check.stdout + check.stderr)
                result["pass"] = result["schema_valid"] and result.get("parser_valid", True) and result.get("requested_action_observed", True)
            else:
                result["pass"] = False
        except Exception as error:
            result["error"] = str(error)
            result["pass"] = False
        result["elapsed_seconds"] = round(time.monotonic() - start, 3)
        manifest["probes"].append(result)
        save(output / "manifest.json", manifest)
        print(("PASS " if result["pass"] else "FAIL ") + name + " " + str(result["elapsed_seconds"]) + "s", flush=True)
    manifest["completed_utc"] = datetime.now(timezone.utc).isoformat()
    manifest["pass"] = all(probe["pass"] for probe in manifest["probes"])
    save(output / "manifest.json", manifest)
    return 0 if manifest["pass"] else 1


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--export", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--model", default="rna-gemma4-12b-32k:latest")
    raise SystemExit(run(parser.parse_args()))
