"""Local design preview; serves the prototype and the product with a mock bridge."""
from argparse import ArgumentParser
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import json
import re
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


class PreviewHandler(SimpleHTTPRequestHandler):
    def do_GET(self):
        path = unquote(urlsplit(self.path).path)
        if path in ("/preview/web/", "/preview/web/index.html"):
            html = (ROOT / "web/index.html").read_text(encoding="utf-8")
            marker = '<script src="js/app-core.js'
            if html.count(marker) != 1:
                self.send_error(500, "Product bridge entrypoint changed")
                return
            catalog = (ROOT / "src/RNAssistant.Office/Contracts/BridgeOperationCatalog.cs").read_text(encoding="utf-8")
            enum = re.search(r"public enum BridgeOperationId\s*\{(.*?)\n\s*\}", catalog, re.S)
            if not enum:
                self.send_error(500, "Bridge catalog unavailable")
                return
            operations = []
            for match in re.finditer(r"^\s*(?:\[BridgeOperationPolicy\(([^)]*)\)\]\s*)?([A-Z]\w+),\s*$", enum.group(1), re.M):
                policy, name = match.groups()
                parts = [part.strip() for part in policy.split(",")] if policy else []
                operations.append({"name": name[0].lower() + name[1:],
                                   "cancellable": parts[0] == "true" if parts else False,
                                   "idleTimeoutMs": int(parts[1]) * 1000 if len(parts) > 1 else 120000})
            if not operations or operations[0]["name"] != "reportClientTiming":
                self.send_error(500, "Bridge catalog parse failed")
                return
            bootstrap = "<script>window.__RNAMockBridgeContract=" + json.dumps(
                {"version": 2, "operations": operations}, separators=(",", ":")) + ";</script>\n  "
            html = html.replace(marker, bootstrap + '<script src="/preview/mock-bridge.js"></script>\n  ' + marker)
            content = html.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return
        super().do_GET()

    def translate_path(self, path):
        path = unquote(urlsplit(path).path)
        preview_web = path.startswith("/preview/web/")
        base = ROOT / "web" if path.startswith("/web/") or preview_web else ROOT / "prototypes/ui"
        relative = path[13:] if preview_web else path[5:] if path.startswith("/web/") else path[9:] if path.startswith("/preview/") else path.lstrip("/")
        target = (base / relative).resolve()
        if not target.is_relative_to(base):
            return str(ROOT / ".preview-not-found")
        return str(target)

    def list_directory(self, path):
        self.send_error(404)

    def send_head(self):
        # A preview is edited in place; always return the current asset, including in WebView.
        if "If-Modified-Since" in self.headers:
            del self.headers["If-Modified-Since"]
        return super().send_head()

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()


if __name__ == "__main__":
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8897)
    args = parser.parse_args()
    with ThreadingHTTPServer(("127.0.0.1", args.port), PreviewHandler) as server:
        print(f"RNAssistant design: http://127.0.0.1:{server.server_address[1]}/", flush=True)
        server.serve_forever()
