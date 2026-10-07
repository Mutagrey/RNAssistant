"""Pure grader checks with synthetic canonical operations and exact CAS bodies.

These are fault-injection fixtures, not authenticated logs or model qualification.
No production runtime, provider, saved chat or user file is changed.
"""
import copy
import gzip
import json
from pathlib import Path
from tempfile import TemporaryDirectory

from compaction_resume_live import audit_readback_order, digest, fixture, OLD_MARKER, NEW_MARKER


class Trace:
    def __init__(self, root, role="user"):
        self.root, self.role, self.events, self.results = root, role, [], []
        self.bodies = {name: value for name, value in fixture().items() if name.startswith("notes/")}
        self.revisions = dict.fromkeys(self.bodies, 0)
        (root / "state/chats").mkdir(parents=True)

    def ref(self, path):
        return {"uri": "rna://file/note" + path[6:8], "revision": "r" + str(self.revisions[path])}

    def record(self, name, path, evidence=None, effect=None):
        call_id = "call-" + str(len(self.results))
        args = {"target" if name == "common.resources_read" else "relativePath": path}
        call = {"Role": "assistant", "RunId": "same-run", "ToolCalls": [
            {"Id": call_id, "Name": name, "ArgumentsJson": json.dumps(args)}]}
        result = {"Role": self.role, "RunId": "same-run", "ToolCallId": call_id, "ToolName": name,
                  "ExecutionProgress": {"Outcome": 0}, "ResourceEffect": effect, "ResourceEvidence": evidence or []}
        for kind, value in (("tool.call.recorded", call), ("tool.result.recorded", result)):
            self.events.append({"Sequence": len(self.events) + 1, "Type": "session.commit",
                                "Data": {"Operations": [{"Type": kind, "Data": {"Value": value}}]}})
        self.results.append(result)
        return result

    def read(self, path):
        body = self.bodies[path]
        sha = digest(body)
        blob = self.root / "state/chat-blobs" / sha[:2] / (sha + ".blob")
        blob.parent.mkdir(parents=True, exist_ok=True)
        packed = gzip.compress(body)
        blob.write_bytes(b"RNACAS01" + (1).to_bytes(4, "little") + len(body).to_bytes(8, "little") +
                         len(packed).to_bytes(8, "little") + packed)
        return self.record("common.resources_read", path, evidence=[{
            "Complete": True, "Coverage": {"kind": "whole"}, "ScopeId": {"kind": "file"}, "View": "text",
            "Resource": self.ref(path), "ContentSha256": sha, "Payload": {"sha256": sha, "byteLength": len(body)}}])

    def patch(self, path, review=False):
        before = self.ref(path)
        self.revisions[path] += 1
        self.bodies[path] = self.bodies[path].replace(b"review: pending\n", b"review: complete\n") if review else \
            self.bodies[path].replace(b"status: draft\n", b"status: ready\n")
        return self.record("files.patch", path, effect={"operation": "files.patch", "outcome": "VerifiedChanged",
            "verification": "exact read-back", "impacts": [{"relation": "Exact", "coverage": {"kind": "whole"},
            "before": before, "after": self.ref(path)}]})

    def external_edit(self):
        path = "notes/01.md"
        before = self.bodies[path]
        self.bodies[path] = before.replace(OLD_MARKER.encode(), NEW_MARKER.encode())
        self.revisions[path] += 1
        (self.root / "external-edit.json").write_text(json.dumps({
            "path": path, "before": digest(before), "after": digest(self.bodies[path])}))
        (self.root / "events-before-resume.jsonl").write_text(self.encoded())

    def encoded(self):
        return "".join(json.dumps(event) + "\n" for event in self.events)

    def save(self):
        (self.root / "state/chats/test.events.jsonl").write_text(self.encoded())


def run_case(mode):
    with TemporaryDirectory(prefix="rna-order-grader-") as folder:
        root = Path(folder)
        trace = Trace(root, role="tool" if mode == "native" else "user")
        first_write, first_readback = None, None
        if mode == "prefetch":
            for path in trace.bodies:
                trace.read(path)
        for index, path in enumerate(trace.bodies):
            trace.read(path)
            write = trace.patch(path)
            if index == 0 and mode in ("external_drift", "unrecorded_drift"):
                trace.external_edit()
            if mode != "late_readbacks":
                readback = trace.read(path)
                if index == 0:
                    first_write, first_readback = write, readback
        if mode == "late_readbacks":
            for path in trace.bodies:
                trace.read(path)
        trace.read("notes/01.md")
        trace.patch("notes/01.md", review=True)
        trace.read("notes/01.md")

        if mode == "no_effect":
            first_write["ResourceEffect"] = None
        elif mode == "no_initial_read":
            trace.results[0]["ResourceEvidence"][0]["Complete"] = False
        elif mode == "unchanged_revision":
            impact = first_write["ResourceEffect"]["impacts"][0]
            impact["after"] = copy.deepcopy(impact["before"])
            first_readback["ResourceEvidence"][0]["Resource"] = copy.deepcopy(impact["before"])
        elif mode == "unknown":
            first_write["ExecutionProgress"]["Outcome"] = 2
            first_write["ResourceEffect"]["outcome"] = "UnknownAfterDispatch"
        elif mode == "stale_revision":
            first_readback["ResourceEvidence"][0]["Resource"] = copy.deepcopy(first_write["ResourceEffect"]["impacts"][0]["before"])
        elif mode == "partial":
            first_readback["ResourceEvidence"][0]["Complete"] = False
        elif mode == "missing_payload":
            first_readback["ResourceEvidence"][0]["Payload"]["sha256"] = "0" * 64
        elif mode == "duplicate_result":
            first_readback["ToolCallId"] = first_write["ToolCallId"]
        elif mode == "unrecorded_drift":
            edit = json.loads((root / "external-edit.json").read_text())
            edit["after"] = "0" * 64
            (root / "external-edit.json").write_text(json.dumps(edit))
        trace.save()
        try:
            passed = audit_readback_order(root)["passed"]
        except (ValueError, FileNotFoundError):
            passed = False
        assert passed == (mode in ("ordered", "native", "external_drift")), mode
        print("PASS read-back grader:", mode)


if __name__ == "__main__":
    for case in ("ordered", "native", "external_drift", "late_readbacks", "prefetch", "no_effect", "no_initial_read", "unchanged_revision", "unknown",
                 "stale_revision", "partial", "missing_payload", "duplicate_result", "unrecorded_drift"):
        run_case(case)
