"""Real CDP diagnostics: exact bytes, restart, bounds and outcome independence."""
import hashlib
import json
import os
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

from web_verifier_smoke import CLI, REPO, record, verify


def diagnostic(workspace, state, identity, kind, raw=True, extra=()):
    args = ["dotnet", str(CLI), "diagnostic", "--workspace", str(workspace),
            "--id", identity, "--kind", kind, "--raw" if raw else "--jsonl", *extra]
    return subprocess.run(args, cwd=REPO, env=dict(os.environ, RNA_STATE_ROOT=str(state)),
                          capture_output=True, timeout=10)


def exact_bytes(workspace, state, identity, kind, descriptor):
    output = diagnostic(workspace, state, identity, kind)
    assert output.returncode == 0, output.stderr
    body = output.stdout
    assert len(body) == descriptor["Payload"]["byteLength"]
    assert hashlib.sha256(body).hexdigest() == descriptor["Payload"]["sha256"]
    metadata = diagnostic(workspace, state, identity, kind, raw=False)
    assert metadata.returncode == 0, metadata.stderr
    assert "Bytes" not in json.loads(metadata.stdout)["data"]
    return body


def main():
    with tempfile.TemporaryDirectory(prefix="rna-web-diagnostics-") as directory:
        root = Path(directory)
        workspace, state = root / "work", root / "state"
        shutil.copytree(REPO / "tests/cli/fixtures/counter-repair", workspace)
        code, failed = verify(workspace, state, checks=REPO / "tests/cli/counter_checks.json")
        assert code == 5 and failed["status"] == "failed", failed
        saved = record(workspace, state, failed["verificationId"])
        descriptors = {item["Kind"]: item for item in saved["Diagnostics"]}
        assert len(descriptors) == 2 and all(d["State"] == "Captured" for d in descriptors.values()), saved
        assert saved["Diagnostics"] == failed["diagnostics"]
        png = exact_bytes(workspace, state, saved["Id"], "screenshot", descriptors["Screenshot"])
        assert png.startswith(b"\x89PNG\r\n\x1a\n") and len(png) <= 512 * 1024
        width, height = struct.unpack(">II", png[16:24])
        assert 0 < width <= 2048 and 0 < height <= 2048
        trace_bytes = exact_bytes(workspace, state, saved["Id"], "trace", descriptors["Trace"])
        trace = json.loads(trace_bytes)
        assert trace["VerificationId"] == saved["Id"] and trace["SnapshotId"] == saved["SnapshotId"]
        assert trace["SnapshotSha256"] == saved["SnapshotSha256"] and trace["Outcome"] == "Failed"
        assert any(e["Kind"] == "Check" and e["Detail"] == "Failed" for e in trace["Entries"]), trace
        assert any(e["Kind"] == "Request" and e["Detail"] == "/app.js" for e in trace["Entries"]), trace
        assert not trace["Truncated"] and not trace["DroppedEntries"]
        assert str(workspace).encode() not in trace_bytes and b"fixture-" not in trace_bytes
        evidence = os.environ.get("RNA_DIAGNOSTIC_EVIDENCE")
        if evidence:
            destination = Path(evidence)
            destination.mkdir(parents=True, exist_ok=True)
            (destination / "failed-counter.png").write_bytes(png)
            (destination / "failed-counter.trace.json").write_bytes(trace_bytes)
            (destination / "failed-result.json").write_text(json.dumps(saved, indent=2))

        # Reads have no browser/source fallback, even in a later process after source deletion.
        (workspace / "index.html").unlink()
        (workspace / "app.js").unlink()
        assert exact_bytes(workspace, state, saved["Id"], "screenshot", descriptors["Screenshot"]) == png
        assert exact_bytes(workspace, state, saved["Id"], "trace", descriptors["Trace"]) == trace_bytes
        assert record(workspace, state)["Total"] == 1, "diagnostic read unexpectedly ran another verification"
        foreign = root / "foreign"
        foreign.mkdir()
        denied = diagnostic(foreign, state, saved["Id"], "trace")
        assert denied.returncode == 5 and not denied.stdout
        conflicting = diagnostic(workspace, state, saved["Id"], "trace", extra=("--jsonl",))
        assert conflicting.returncode == 2 and not conflicting.stdout

        # An event flood must be explicitly incomplete and bounded, while preserving failure.
        (workspace / "index.html").write_text(
            '<!doctype html><h1>Event bound</h1><script>for(let i=0;i<500;i++)'
            '{console.error("я😀".repeat(350)+i)}</script>')
        code, flooded = verify(workspace, state)
        assert code == 5, flooded
        flood = json.loads(diagnostic(workspace, state, flooded["verificationId"], "trace").stdout)
        assert flood["Truncated"] and flood["DroppedEntries"] > 0 and len(flood["Entries"]) <= 256
        assert len(json.dumps(flood, separators=(",", ":")).encode()) <= 128 * 1024

        # A high-entropy viewport exceeds the screenshot cap. Observability cannot turn
        # otherwise passing browser checks into failed acceptance or store an oversized body.
        (workspace / "index.html").write_text(
            '<!doctype html><style>body{margin:0;overflow:hidden}</style><canvas id="c"></canvas>'
            '<script>c.width=innerWidth;c.height=innerHeight;let x=c.getContext("2d"),'
            'd=x.createImageData(c.width,c.height),v=123456789;'
            'for(let i=0;i<d.data.length;i+=4){for(let j=0;j<3;j++)'
            '{v^=v<<13;v^=v>>>17;v^=v<<5;d.data[i+j]=v&255}d.data[i+3]=255}'
            'x.putImageData(d,0,0)</script>')
        code, noise = verify(workspace, state)
        assert code == 0 and noise["status"] == "passed", noise
        screenshot = next(d for d in noise["diagnostics"] if d["Kind"] == "Screenshot")
        assert screenshot["State"] == "Unavailable" and screenshot["Payload"] is None, screenshot
        assert diagnostic(workspace, state, noise["verificationId"], "screenshot").returncode == 5
        assert json.loads(diagnostic(workspace, state, noise["verificationId"], "trace").stdout)["Outcome"] == "Passed"

        code, unavailable = verify(workspace, state, browser="/missing/chromium")
        assert code == 4 and all(d["State"] == "Unavailable" for d in unavailable["diagnostics"]), unavailable
        assert diagnostic(workspace, state, unavailable["verificationId"], "trace").returncode == 5
        print("PASS browser diagnostics: exact PNG/trace, restart, workspace isolation, bounded flood, oversized screenshot, not-run")


if __name__ == "__main__":
    main()
