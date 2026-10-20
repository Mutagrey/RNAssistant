"""Task-scoped skill selection and actual guarded file changes; controlled HTTP, no LLM claims."""
import json
import os
import subprocess
import threading
from collections import deque
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from tempfile import TemporaryDirectory
from read_acceptance_context_smoke import CLI, REPO, DONE, call


def check(root):
    workspace, state = root / "workspace", root / "state"
    workspace.mkdir()
    (workspace / "source.txt").write_text("total=12\n")
    package = state / "skills" / "workspace" / "fixture"
    package.mkdir(parents=True)
    manifest = {"formatVersion": 1, "packageId": "fixture.report", "requiredTools": [
        {"id": name, "contractVersion": "1"} for name in ("common.resources_read", "files.patch", "files.create")],
        "optionalTools": [{"id": "fixture.absent", "contractVersion": "1", "fallback": "Report missing validator; use exact source readback."}]}
    marker = "EXACT_PUBLISHED_PROCEDURE: read, update unique total, create report, read both final files."
    source = package / "SKILL.md"
    source.write_text("---\nid: fixture.report\nname: Report\nhost: Workspace\ndescription: Validate a file report\n"
                      "rnassistant: " + json.dumps(manifest) + "\n---\n" + marker)
    large = state / "skills" / "workspace" / "large"
    large.mkdir()
    large_body = "REQUIRED_LARGE_PROCEDURE " + "x" * 24000
    (large / "SKILL.md").write_text("---\nid: fixture.large\nname: Large\nhost: Workspace\ndescription: Large procedure\n---\n" + large_body)
    responses, requests, errors = deque(), [], []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            try:
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                requests.append(request)
                assert responses, "Unexpected model request"
                body = json.dumps({"choices": [{"message": {"role": "assistant", "content": json.dumps(responses.popleft())}}]}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except Exception as error:
                errors.append(str(error))
                self.send_error(500)

        def log_message(self, *_):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    env = {key: value for key, value in os.environ.items() if not key.startswith("RNA_")}
    env.update(RNA_STATE_ROOT=str(state), RNA_MODEL="scripted", RNA_API_KEY="fixture",
               RNA_BASE_URL=f"http://127.0.0.1:{server.server_port}")

    def cli(command, *args, code=0):
        result = subprocess.run(["dotnet", str(CLI), command, "--workspace", str(workspace), "--jsonl", *args],
                                env=env, cwd=REPO, text=True, capture_output=True, timeout=40)
        assert result.returncode == code, (result.returncode, result.stdout, result.stderr, errors)
        return json.loads(result.stdout.splitlines()[-1])["data"]

    def selected(request):
        return [item["content"] for item in request["messages"] if (item.get("content") or "").startswith("SELECTED_SKILL ")]

    try:
        responses.extend([
            call("common.resources_read", type="skill", target="fixture.report"),
            call("common.skills_load", id="fixture.report"),
            call("common.resources_read", target="source.txt"),
            call("files.patch", relativePath="source.txt", oldText="total=12", newText="total=13"),
            call("common.resources_read", target="source.txt"),
            call("files.create", relativePath="report.txt", text="Verified total: 13\n"),
            call("common.resources_read", target="report.txt"), DONE])
        waiting = cli("run", "--message", "Apply fixture.report, update total and create a verified report.",
                      "--tool-result-role", "tool", "--context-tokens", "32768", code=3)
        assert (workspace / "source.txt").read_text() == "total=12\n", "No effect before permission"
        assert not selected(requests[0]) and not selected(requests[1]), "Inspection never selects"
        assert selected(requests[2]) == ["SELECTED_SKILL fixture.report\n" + marker]
        assert "Report missing validator" in json.dumps(requests[2]), "Optional fallback must be visible"
        # Authoring bytes are not the published pin. Restart must use the exact publication.
        source.write_text(source.read_text().replace(marker, "UNPUBLISHED_CHANGED_BODY"))
        second = cli("approve", "--session", waiting["SessionId"], "--pending", waiting["pendingId"], code=3)
        assert (workspace / "source.txt").read_text() == "total=13\n"
        assert not (workspace / "report.txt").exists()
        final = cli("approve", "--session", second["SessionId"], "--pending", second["pendingId"])
        assert final["reason"] == "model_done", final
        assert (workspace / "report.txt").read_text() == "Verified total: 13\n"
        assert all(selected(request) == ["SELECTED_SKILL fixture.report\n" + marker] for request in requests[2:])
        assert all("UNPUBLISHED_CHANGED_BODY" not in json.dumps(request) for request in requests)
        prior = len(requests)
        responses.append(DONE)
        direct = cli("run", "--session", final["SessionId"], "--message", "Continue discussing the same report.")
        assert len(requests) == prior + 1 and selected(requests[-1]) == ["SELECTED_SKILL fixture.report\n" + marker], "Follow-up must retain selection without a load call"
        assert "Verified total: 13" in json.dumps(requests[-1]), "Current report evidence survives new input and process restart"
        assert direct["ToolCounts"]["ReadOk"] == 0, direct
        responses.extend([call("files.patch", relativePath="source.txt", oldText="total=13", newText="total=14"), DONE])
        guarded = cli("run", "--session", final["SessionId"], "--message", "Check the follow-up write guard.", "--auto-confirm", "on")
        assert (workspace / "source.txt").read_text() == "total=13\n", "Carried skill must not inherit the prior turn's write guard"
        assert guarded["ToolCounts"]["WriteOk"] == 0 and "source_observation_required" in json.dumps(requests[-1]), guarded
        responses.append(DONE)
        cli("run", "--session", final["SessionId"], "--message", "Inspect with restricted permissions.", "--mutation-policy", "deny")
        assert not selected(requests[-1]), "Current policy must suspend an incompatible retained selection"
        responses.extend([call("common.skills_load", id="fixture.report", action="remove"), DONE])
        cli("run", "--session", final["SessionId"], "--message", "Switch tasks and remove the report procedure.")
        assert not selected(requests[-1]), "Explicit removal takes effect on the next request"
        responses.append(DONE)
        cli("run", "--session", final["SessionId"], "--message", "Continue after removal.")
        assert not selected(requests[-1]), "Removed selection must not return after restart"
        responses.extend([call("common.skills_load", id="fixture.report"), DONE])
        cli("run", "--message", "Inspect only", "--mutation-policy", "deny")
        assert not selected(requests[-1]), "Unavailable required writer prevents partial selection under Deny"
        responses.extend([call("common.skills_load", id="fixture.large"), DONE])
        limited = cli("run", "--message", "Select fixture.large if the complete request fits.", "--context-tokens", "24576")
        assert not selected(requests[-1]), "Rejected candidate never becomes active instructions"
        refusals = [message.get("content", "") for message in requests[-1]["messages"]
                    if "skill_unavailable" in str(message.get("content", ""))]
        assert any("complete next request exceeds its budget" in text for text in refusals), refusals
        assert limited["ToolCounts"]["ReadError"] == 1, limited
        responses.append(DONE)
        cli("run", "--session", limited["SessionId"], "--message", "Continue without the rejected procedure.")
        assert not selected(requests[-1]), "Cold restart retains the previous usable selection"
        assert not errors and not responses, errors
        print("PASS skill packages CLI: canonical load, guarded read/patch/report/readback, approvals, exact publication, follow-up/removal, whole-request admission refusal and cold restart")
        print(json.dumps({"controlledModelRequests": len(requests), "pilotRequests": prior,
                          "selectedInstructionCharacters": len(marker), "realModelEvaluation": False}))
    finally:
        server.shutdown()
        thread.join()


if __name__ == "__main__":
    with TemporaryDirectory(prefix="rna-skill-packages-") as directory:
        check(Path(directory))
