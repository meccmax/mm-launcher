"""
Fake Discord webhook for testing the MM Server Agent's report posting.
Records each POST (payload_json + attached file names/sizes) to a log as JSON lines.
Answers 413 for files over 10 MB, like Discord does.
    python fake_discord.py 8099 /path/to/fake_discord.log
"""
import json
import sys
from email.parser import BytesParser
from email.policy import default
from http.server import BaseHTTPRequestHandler, HTTPServer

LOG = sys.argv[2] if len(sys.argv) > 2 else "fake_discord.log"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n)
        msg = BytesParser(policy=default).parsebytes(
            b"Content-Type: " + self.headers["Content-Type"].encode() + b"\r\n\r\n" + body)
        entry = {"path": self.path, "payload": None, "files": []}
        for part in msg.iter_parts():
            name = part.get_param("name", header="content-disposition")
            data = part.get_payload(decode=True) or b""
            if name == "payload_json":
                entry["payload"] = json.loads(data.decode())
            elif name and name.startswith("files["):
                entry["files"].append({"name": part.get_filename(), "size": len(data)})
        too_big = any(f["size"] > 10 * 1024 * 1024 for f in entry["files"])
        with open(LOG, "a") as fh:
            fh.write(json.dumps({**entry, "rejected": too_big}) + "\n")
        self.send_response(413 if too_big else 200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"message":"Request entity too large"}' if too_big else b'{"id":"1"}')

    def log_message(self, *args):
        pass


HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
