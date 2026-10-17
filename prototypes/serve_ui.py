"""Local design preview; serves the prototype and the product's existing UI assets."""
from argparse import ArgumentParser
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


class PreviewHandler(SimpleHTTPRequestHandler):
    def translate_path(self, path):
        path = unquote(urlsplit(path).path)
        base = ROOT / "web" if path.startswith("/web/") else ROOT / "prototypes/ui"
        relative = path[5:] if path.startswith("/web/") else path.lstrip("/")
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
    print(f"RNAssistant design: http://127.0.0.1:{args.port}/", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), PreviewHandler).serve_forever()
