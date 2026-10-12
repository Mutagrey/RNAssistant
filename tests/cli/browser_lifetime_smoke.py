"""Mac/Linux process-loss boundaries; only processes from these launches are touched."""
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

from interactive_smoke import Scenario
from resource_preview_smoke import finish_preview, start_preview
from web_verifier_smoke import CLI, REPO


def until(read, message, seconds=10):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        value = read()
        if value:
            return value
        time.sleep(0.05)
    raise AssertionError(message)


def processes():
    result = {}
    for line in subprocess.check_output(["ps", "-axo", "pid=,ppid=,stat=,lstart=,command="], text=True).splitlines():
        fields = line.split(None, 8)
        if len(fields) == 9 and not fields[2].startswith("Z"):
            result[int(fields[0])] = (int(fields[1]), " ".join(fields[3:]))
    return result


def descendants(pid, temporary):
    current = processes()
    owned = {pid}
    while True:
        added = {child for child, (parent, _) in current.items() if parent in owned} - owned
        if not added:
            break
        owned.update(added)
    # Chromium's detached crash handler also carries this test's unique profile path.
    owned.update(child for child, (_, command) in current.items() if str(temporary) + "/rna-browser-" in command)
    return {child: current[child][1] for child in owned if child != pid and child in current}


def survivors(owned):
    current = processes()
    return {pid: identity for pid, identity in owned.items() if current.get(pid, (None, None))[1] == identity}


def cleaned(owned, temporary):
    until(lambda: not survivors(owned) and not list(temporary.glob("rna-browser-*")),
          "Owned browser/helper/profile survived caller loss")


def cleanup(process, owned):
    # Failure cleanup rechecks command + start identity; never scan/kill user browsers.
    if process is not None and process.poll() is None:
        process.kill()
        process.communicate(timeout=10)
    for pid in survivors(owned):
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def isolated_temp(s, root, name):
    directory = root / name
    directory.mkdir()
    s.env["TMPDIR"] = str(directory)
    return directory


def closed_url(url):
    try:
        urllib.request.urlopen(url, timeout=1).close()
        return False
    except OSError:
        return True


def opened_loss(s, root):
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    original = "<!doctype html><h1>Retained snapshot</h1>"
    (s.workspace / "index.html").write_text(original)
    first = second = None
    owned_first, owned_second = {}, {}
    try:
        first_temp = isolated_temp(s, root, "first")
        first, opened = start_preview(s, "--timeout-seconds", "120")
        owned_first = descendants(first.pid, first_temp)
        assert len(owned_first) > 2 and any("RNAssistant.BrowserHost.dll" in x for x in owned_first.values())
        second_temp = isolated_temp(s, root, "second")
        second, other = start_preview(s, "--timeout-seconds", "120")
        owned_second = descendants(second.pid, second_temp)
        assert not owned_first.keys() & owned_second.keys()
        first.kill()
        output, errors = first.communicate(timeout=10)
        assert first.returncode == -signal.SIGKILL and not output and not errors
        cleaned(owned_first, first_temp)
        assert closed_url(opened["Url"])
        assert second.poll() is None and survivors(owned_second)
        assert urllib.request.urlopen(other["Url"], timeout=2).read().decode() == original
        second.send_signal(signal.SIGINT)
        assert finish_preview(second, code=130)["CloseReason"] == "Cancelled"
        cleaned(owned_second, second_temp)
        (s.workspace / "index.html").unlink()
        retained = s.json("open", "--path", "index.html", "--snapshot", opened["SnapshotId"])
        assert retained["Content"]["text"] == original
        assert s.json("verifications")["Total"] == 0 and not s.json("sessions")["sessions"] and not s.requests
        print("PASS opened loss: browser/helper/profile/server cleanup, other viewer intact, retained CAS, no invented terminal result")
    finally:
        cleanup(first, owned_first)
        cleanup(second, owned_second)


def startup_loss(s, root):
    temporary = isolated_temp(s, root, "startup-temp")
    (s.workspace / "index.html").write_text("<!doctype html><h1>Startup</h1>")
    marker = root / "started.json"
    fake = root / "waiting-browser"
    fake.write_text("#!" + sys.executable + "\nimport json, pathlib, subprocess, sys, time\n"
                    "profile = next(a.split('=',1)[1] for a in sys.argv if a.startswith('--user-data-dir='))\n"
                    "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])\n"
                    "pathlib.Path(" + repr(str(marker)) + ").write_text(json.dumps({'profile': profile}))\n"
                    "time.sleep(120)\n")
    fake.chmod(0o700)
    s.env["RNA_BROWSER_EXECUTABLE"] = str(fake)
    process = subprocess.Popen(s.command("preview", "--headless", "--jsonl"), cwd=REPO,
                               env=s.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    owned = {}
    try:
        until(lambda: marker.exists() and marker.stat().st_size, "Browser never reached startup")
        owned = descendants(process.pid, temporary)
        assert len(owned) >= 3
        assert not (Path(json.loads(marker.read_text())["profile"]) / "DevToolsActivePort").exists()
        process.kill()
        output, errors = process.communicate(timeout=10)
        assert not output and not errors and process.returncode == -signal.SIGKILL
        cleaned(owned, temporary)
        assert s.json("verifications")["Total"] == 0
        print("PASS startup loss: cleanup before DevTools readiness, including owned descendant")
    finally:
        cleanup(process, owned)

    # A stuck caller holding its pipe open is still bounded. Use the same
    # production helper with a one-second safety lifetime, without a CLI run.
    marker.unlink()
    process = subprocess.Popen(["dotnet", str(CLI.with_name("RNAssistant.BrowserHost.dll")),
                                str(fake), "headless", "preview", "1"], cwd=REPO, env=s.env,
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    owned = {}
    try:
        until(lambda: marker.exists() and marker.stat().st_size, "Safety-lifetime browser never started")
        owned = descendants(process.pid, temporary)
        process.wait(timeout=8)  # Keep stdin open: communicate() would release ownership.
        output, errors = process.communicate(timeout=2)
        assert process.returncode == 5 and "safety deadline expired" in errors and output
        cleaned(owned, temporary)
        print("PASS safety lifetime: held caller pipe cannot leave an unbounded browser")
    finally:
        cleanup(process, owned)


def verification_loss(s, root):
    s.env.pop("RNA_BROWSER_EXECUTABLE")
    temporary = isolated_temp(s, root, "verification-temp")
    (s.workspace / "index.html").write_text("<!doctype html><script>while(true){}</script>")
    process = subprocess.Popen(s.command("verify", "--jsonl"), cwd=REPO, env=s.env,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    owned = {}
    try:
        def page_url():
            for active in temporary.glob("rna-browser-*/DevToolsActivePort"):
                try:
                    port = active.read_text().splitlines()[0]
                    with urllib.request.urlopen("http://127.0.0.1:" + port + "/json/list", timeout=1) as response:
                        for target in json.load(response):
                            if target.get("type") == "page" and target.get("url", "").startswith("http://127.0.0.1:"):
                                return target["url"]
                except (OSError, ValueError, IndexError):
                    pass
            return None

        url = until(page_url, "Verifier never reached navigation")
        owned = descendants(process.pid, temporary)
        assert len(owned) > 2
        process.kill()
        output, errors = process.communicate(timeout=10)
        assert not output and not errors and process.returncode == -signal.SIGKILL
        cleaned(owned, temporary)
        assert closed_url(url)
        listing = s.json("verifications")
        assert listing["Total"] == 1
        saved = s.json("verification", "--id", listing["Items"][0]["Id"])
        assert saved["State"] == "Pending" and saved["SnapshotId"] and not saved["Diagnostics"], saved
        assert s.json("verification", "--id", saved["Id"]) == saved, "Read mutated/replayed interrupted verification"
        print("PASS verification loss: owned cleanup, Pending retained, no invented diagnostics or retry")
    finally:
        cleanup(process, owned)


def main():
    with tempfile.TemporaryDirectory(prefix="rna-browser-lifetime-") as directory:
        root = Path(directory).resolve()
        for case in (opened_loss, startup_loss, verification_loss):
            case_root = root / case.__name__
            s = Scenario(case_root)
            try:
                case(s, case_root)
            finally:
                s.close()


if __name__ == "__main__":
    main()
