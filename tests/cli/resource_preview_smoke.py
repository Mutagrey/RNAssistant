"""Application resource views and isolated development preview; no real LLM score."""
import hashlib
import json
import select
import signal
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from interactive_smoke import Scenario
from read_acceptance_context_smoke import DONE, REPO, call


def views(s):
    original = "A" * 127 + "😀" + "B" * 300
    (s.workspace / "long.txt").write_text(original)
    (s.workspace / "other.txt").write_text("OTHER")
    (s.workspace / "binary.bin").write_bytes(b"\xff\x00")
    (s.workspace / ".env").write_text("PRIVATE")
    (s.workspace / "escape.txt").symlink_to(s.workspace.parent / "outside.txt")
    (s.workspace.parent / "outside.txt").write_text("OUTSIDE")
    listing = s.json("resources", "--query", ".txt", "--limit", "1")
    assert not listing["Complete"] and len(listing["Items"]) == 1 and listing["AvailabilityHint"]
    assert listing["Items"][0]["tracking"] == "metadata-only"
    assert s.json("resources", "--query", "absent")["Complete"]
    metadata = s.json("open", "--path", "binary.bin", "--view", "metadata")
    assert metadata["Content"] is None and metadata["Descriptor"]["byteLength"] == 2
    first = s.json("open", "--path", "long.txt", "--max-chars", "128")
    assert first["Content"]["text"] == "A" * 127 and first["NextOffset"] == 127
    assert {key: first["Content"]["coverage"][key] for key in ["kind", "start", "end"]} == {
        "kind": "character-range", "start": 0, "end": 127}
    assert not first["Content"]["complete"] and not first["Retained"]
    ref = first["Descriptor"]["reference"]
    (s.workspace / "long.txt").write_text("EXTERNAL EDIT")
    exact_args = ("--uri", ref["uri"], "--revision", ref["revision"])
    rest = s.json("open", "--path", "long.txt", "--offset", "127", *exact_args)
    assert rest["Content"]["text"] == "😀" + "B" * 300 and rest["Retained"] and rest["NextOffset"] is None
    assert not rest["Content"]["complete"] and rest["Content"]["offset"] == 127
    assert s.json("open", "--path", "long.txt")["Content"]["text"] == "EXTERNAL EDIT"
    for args in [("--offset", "1"), ("--uri", ref["uri"]), ("--max-chars", "127"),
                 ("--view", "raw"), ("--offset", "128", *exact_args)]:
        s.cli("open", "--path", "long.txt", *args, code=2)
    s.cli("open", "--path", "other.txt", *exact_args, code=5)
    for path in ["../outside.txt", str(s.workspace.parent / "outside.txt"), "escape.txt", ".env"]:
        s.cli("open", "--path", path, code=5)
    s.cli("open", "--path", "binary.bin", code=5)
    (s.workspace / "purge.txt").write_text("RETAINED_ONLY")
    retained = s.json("open", "--path", "purge.txt")["Descriptor"]
    digest = hashlib.sha256(b"RETAINED_ONLY").hexdigest()
    (Path(s.env["RNA_STATE_ROOT"]) / "chat-blobs" / digest[:2] / (digest + ".blob")).unlink()
    (s.workspace / "purge.txt").write_text("MUST_NOT_FALL_BACK")
    failed = s.cli("open", "--path", "purge.txt", "--uri", retained["reference"]["uri"],
                   "--revision", retained["reference"]["revision"], code=5)
    assert "snapshot_unavailable" in failed.stderr and not failed.stdout
    assert not s.requests and not s.json("sessions")["sessions"]
    s.responses.extend([call("files.replace", relativePath="other.txt", text="MODEL WRITE"), DONE])
    s.json("open", "--path", "other.txt")
    run = s.cli("run", "--message", "Replace other.txt.", "--max-iterations", "3", "--max-tool-steps", "3", "--jsonl")
    assert "source_observation_required" in json.dumps(s.requests) and not s.errors
    assert (s.workspace / "other.txt").read_text() == "OTHER", run.stdout
    print("PASS resources: bounded metadata, exact Unicode continuation, drift, missing payload, confinement, no model read credit")


def start_preview(s, *args):
    process = subprocess.Popen(s.command("preview", "--headless", "--jsonl", *args),
                               env=s.env, cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        assert select.select([process.stdout], [], [], 15)[0], "Preview did not report readiness"
        line = process.stdout.readline()
        event = json.loads(line)
        assert event["type"] == "preview.opened", event
        return process, event["data"]
    except BaseException:
        process.kill()
        process.communicate(timeout=10)
        raise


def finish_preview(process, code=0):
    try:
        output, errors = process.communicate(timeout=15)
        assert process.returncode == code, (process.returncode, output, errors)
        result = json.loads(output.splitlines()[-1])
        assert result["type"] == "preview.closed", result
        return result["data"]
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()


def previews(s):
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    profiles_before = set(Path(tempfile.gettempdir()).glob("rna-browser-*"))
    (s.workspace / "index.html").write_text('<!doctype html><h1>Snapshot</h1><script src="app.js"></script>')
    (s.workspace / "app.js").write_text('document.querySelector("h1").textContent="RETAINED";')
    (s.workspace / "private.txt").write_text("NOT_AN_ASSET")
    process, opened = start_preview(s, "--timeout-seconds", "3")
    try:
        assert opened["SourceMode"] == "snapshot" and not opened["Historical"]
        assert opened["Files"] == ["app.js", "index.html"] and opened["SnapshotId"]
        url = opened["Url"]
        with urllib.request.urlopen(url) as response:
            assert b"Snapshot" in response.read()
            csp = response.headers["Content-Security-Policy"]
            assert "sandbox allow-scripts allow-same-origin" in csp
            assert "worker-src 'none'" in csp and "form-action 'none'" in csp
            assert "allow-popups" not in csp and "allow-downloads" not in csp
        script_url = url.rsplit("/", 1)[0] + "/app.js"
        original = urllib.request.urlopen(script_url).read()
        (s.workspace / "app.js").write_text('throw new Error("CURRENT_MUST_NOT_RUN")')
        assert urllib.request.urlopen(script_url).read() == original
        for path in ["private.txt", ".rnassistant/workspace.json", "../outside.txt"]:
            try:
                urllib.request.urlopen(url.rsplit("/", 1)[0] + "/" + path)
                raise AssertionError("Unlisted route was served: " + path)
            except urllib.error.HTTPError as error:
                assert error.code == 404
        closed = finish_preview(process)
        assert closed["CloseReason"] == "LifetimeExpired" and not closed["Diagnostics"], closed
    finally:
        if process.poll() is None:
            process.send_signal(signal.SIGINT)
            finish_preview(process, 130)
    try:
        urllib.request.urlopen(url, timeout=1)
        raise AssertionError("Preview server survived its lifetime")
    except urllib.error.URLError:
        pass
    (s.workspace / "index.html").unlink()
    (s.workspace / "app.js").rename(s.workspace / "renamed.js")
    historical = s.json("open", "--path", "app.js", "--snapshot", opened["SnapshotId"])
    assert historical["Retained"] and historical["Content"]["text"].encode() == original
    process, reopened = start_preview(s, "--snapshot", opened["SnapshotId"], "--timeout-seconds", "30")
    process.send_signal(signal.SIGINT)
    cancelled = finish_preview(process, 130)
    assert reopened["Historical"] and reopened["SnapshotSha256"] == opened["SnapshotSha256"]
    assert cancelled["CloseReason"] == "Cancelled"
    assert not (s.workspace / "index.html").exists() and not (s.workspace / "app.js").exists()
    assert s.json("verifications")["Total"] == 0 and not s.requests and not s.json("sessions")["sessions"]
    assert set(Path(tempfile.gettempdir()).glob("rna-browser-*")) <= profiles_before, "Preview profile was retained"
    s.env["RNA_BROWSER_EXECUTABLE"] = "/missing/browser"
    unavailable = s.json("preview", "--snapshot", opened["SnapshotId"], "--headless", code=4)
    assert unavailable["Status"] == "NotAvailable"
    s.cli("preview", "--entry", "index.html", "--snapshot", opened["SnapshotId"], code=2)
    print("PASS preview: real isolated browser, immutable routes, historical reopen, timeout/cancel cleanup, no verification or run credit")


def isolation(s):
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    requests = []

    class Trap(BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append(self.path)
            self.send_response(200)
            self.end_headers()
        do_POST = do_GET
        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Trap)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        trap = f"http://127.0.0.1:{server.server_port}"
        (s.workspace / "index.html").write_text('''<!doctype html><h1>Isolated</h1><script>
console.error('storage:'+(localStorage.getItem('previous-preview')||'empty'));
localStorage.setItem('previous-preview','LEAK');
console.error('bridge:'+typeof window.RN+':'+typeof window.chrome?.webview);
setTimeout(()=>{
  fetch(''' + json.dumps(trap + "/fetch") + ''').catch(()=>{});
  window.open(''' + json.dumps(trap + "/popup") + ''');
  const a=document.createElement('a'); a.download='untrusted.txt';
  a.href=URL.createObjectURL(new Blob(['UNTRUSTED'])); a.click();
  navigator.serviceWorker.register('/worker.js').catch(()=>{});
  location.href=''' + json.dumps(trap + "/navigate") + ''';
},300);
</script>''')
        for _ in range(2):
            process, opened = start_preview(s, "--timeout-seconds", "2")
            closed = finish_preview(process)
            diagnostics = "\n".join(closed["Diagnostics"])
            assert "storage:empty" in diagnostics and "storage:LEAK" not in diagnostics
            assert "bridge:undefined:undefined" in diagnostics
            assert "Preview request blocked:" in diagnostics, diagnostics
            assert len(closed["Diagnostics"]) <= 16 and not requests, requests
            assert s.json("verifications")["Total"] == 0
    finally:
        server.shutdown()
        server.server_close()
        thread.join()
    print("PASS preview isolation: fresh storage, absent bridge, sandbox restrictions and blocked off-origin navigation/requests")


def main():
    with tempfile.TemporaryDirectory(prefix="rna-resource-preview-") as folder:
        for name, check in [("views", views), ("preview", previews), ("isolation", isolation)]:
            scenario = Scenario(Path(folder) / name)
            try:
                check(scenario)
            finally:
                scenario.close()


if __name__ == "__main__":
    main()
