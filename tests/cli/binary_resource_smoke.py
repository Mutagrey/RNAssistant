"""Exact binary views and real-browser assets, with controlled acceptance checks."""
import base64
import hashlib
import json
import struct
import subprocess
import tempfile
import urllib.request
import zlib
from pathlib import Path

from interactive_smoke import Scenario
from read_acceptance_context_smoke import DONE, REPO, call
from resource_preview_smoke import start_preview, finish_preview

# Two generated RGB pixels, JPEG encoded once; no fixture encoder dependency.
JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAAAqADAAQAAAABAAAAAQAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAAQACAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8PD//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEP/dAAQAAf/aAAwDAQACEQMRAD8A7PwZ/wAifoX/AF4Wv/opa6Wua8Gf8ifoX/Xha/8Aopa6Wv4DPqD/2Q==")


def png(color=bytes([255, 0, 0]), width=2):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, 1, 8, 2, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(b"\0" + color * width)) + chunk(b"IEND", b"")


def raw(s, path, *args, code=0):
    result = subprocess.run(s.command("open", "--path", path, "--view", "raw", "--raw", *args),
                            cwd=REPO, env=s.env, capture_output=True, timeout=10)
    assert result.returncode == code, (result.stdout, result.stderr)
    return result.stdout


def setup(s):
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    (s.workspace / "index.html").write_text(
        '<!doctype html><link rel="stylesheet" href="styles.css"><h1>Images</h1>'
        '<img src="picture.png"><img src="photo.jpg">')
    (s.workspace / "styles.css").write_text('body { background-image: url("picture.png"); }')
    (s.workspace / "picture.png").write_bytes(png())
    (s.workspace / "photo.jpg").write_bytes(JPEG)


def views(s):
    setup(s)
    for name, mime, body in [("picture.png", "image/png", png()), ("photo.jpg", "image/jpeg", JPEG)]:
        view = s.json("open", "--path", name, "--view", "image")
        assert view["Representation"] == "image" and "Bytes" not in view and view["Content"] is None
        assert view["Descriptor"]["mimeType"] == mime
        assert view["Binary"]["width"] == 2 and view["Binary"]["height"] == 1
        assert view["Binary"]["payload"]["sha256"] == hashlib.sha256(body).hexdigest()
        ref = view["Descriptor"]["reference"]
        exact = ("--uri", ref["uri"], "--revision", ref["revision"])
        assert raw(s, name, *exact) == body
        (s.workspace / name).unlink()
        assert raw(s, name, *exact) == body
        (s.workspace / name).write_bytes(body)
    (s.workspace / "unknown.bin").write_bytes(b"\xff\x00")
    assert raw(s, "unknown.bin") == b"\xff\x00"
    s.cli("open", "--path", "unknown.bin", "--view", "image", code=5)
    (s.workspace / "large.bin").write_bytes(b"a" * (1024 * 1024 + 1))
    raw(s, "large.bin", code=5)
    (s.workspace / "oversize.png").write_bytes(png(width=4097))
    s.cli("open", "--path", "oversize.png", "--view", "image", code=5)
    s.cli("open", "--path", "picture.png", "--view", "raw", "--raw", "--jsonl", code=2)
    assert not s.requests and not s.json("sessions")["sessions"]
    # UI/raw inspection cannot authorize a model overwrite of a text file.
    s.json("open", "--path", "index.html", "--view", "raw")
    original = (s.workspace / "index.html").read_bytes()
    s.responses.extend([call("common.resources_find"), call("files.replace", relativePath="index.html", text="overwrite"), DONE])
    s.cli("run", "--message", "Replace index.html.", "--max-iterations", "4", "--max-tool-steps", "3", "--jsonl")
    assert "source_observation_required" in json.dumps(s.requests) and (s.workspace / "index.html").read_bytes() == original
    assert "model text reading is unavailable" in json.dumps(s.requests)
    print("PASS binary views: PNG/JPEG metadata, exact raw export, restart/deletion, bounds, no model authority")


def browser(s):
    setup(s)
    process, opened = start_preview(s, "--timeout-seconds", "3")
    try:
        assert sorted(opened["Files"]) == ["index.html", "photo.jpg", "picture.png", "styles.css"]
        for name, mime, body in [("picture.png", "image/png", png()), ("photo.jpg", "image/jpeg", JPEG)]:
            (s.workspace / name).unlink()
            with urllib.request.urlopen(opened["Url"].rsplit("/", 1)[0] + "/" + name) as response:
                assert response.headers["Content-Type"] == mime and response.read() == body
        assert raw(s, "picture.png", "--snapshot", opened["SnapshotId"]) == png()
    finally:
        finish_preview(process)
    historical = s.json("verify", "--snapshot", opened["SnapshotId"])
    assert historical["status"] == "passed" and historical["historical"], historical
    setup_image = png()
    (s.workspace / "picture.png").write_bytes(setup_image)
    (s.workspace / "photo.jpg").write_bytes(JPEG)
    # A MIME mismatch is rejected before publication/browser work.
    (s.workspace / "picture.png").write_bytes(JPEG)
    assert s.json("verify", code=5)["status"] == "failed"
    # Valid header with broken compressed pixels must fail browser decoding.
    (s.workspace / "picture.png").write_bytes(setup_image[:33])
    broken = s.json("verify", code=5)
    assert any("Image loading check" in error for error in broken["errors"]), broken
    print("PASS binary browser: exact snapshot routes/MIME, historical replay, mismatch and broken decode")


def acceptance(s, changed, late=False):
    setup(s)
    s.responses.extend([call("common.skills_load", id="workspace.web_verify_repair"),
                        call("web.verify", entryPath="index.html"),
                        call("common.resources_read", target="index.html"), DONE])
    def before_response():
        if len(s.requests) == (4 if late else 3) and changed:
            (s.workspace / "picture.png").write_bytes(png(bytes([0, 255, 0])))
    s.before_response = before_response
    result = s.json("run", "--message", "Verify the existing project.", "--require-web-verify",
                    "--auto-confirm", "on", "--expect-files", "index.html", "--max-iterations", "4", "--max-tool-steps", "4", code=5 if changed else 0)
    assert result["webSnapshotVerified"] == (not changed), result
    assert len(s.requests) == 4
    for request, expected in zip(s.requests, [False, False, True, not changed or late]):
        context = json.loads(request["messages"][-1]["content"].split("\n", 1)[1])
        assert context["webAcceptance"] == {"currentSnapshotVerified": expected}
        assert "readAcceptance" not in context
    assert "rna://" not in json.dumps(s.requests) and not s.errors
    print("PASS binary acceptance:", "final-wait image edit rejects done" if late else "external image edit rejects done" if changed else "unchanged image evidence accepted")


def main():
    for check in [views, browser, lambda s: acceptance(s, False), lambda s: acceptance(s, True), lambda s: acceptance(s, True, late=True)]:
        with tempfile.TemporaryDirectory(prefix="rna-binary-smoke-") as directory:
            scenario = Scenario(Path(directory))
            try:
                check(scenario)
            finally:
                scenario.close()


if __name__ == "__main__":
    main()
