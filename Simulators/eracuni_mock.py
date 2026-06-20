import json
import os
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path


LOG_DIR = Path(os.environ.get("ERACUNI_MOCK_LOG_DIR", "/tmp/ocpp_invoice_smoke"))
REQUESTS_PATH = LOG_DIR / "eracuni_requests.ndjson"
LISTEN_HOST = os.environ.get("ERACUNI_MOCK_HOST", "127.0.0.1")
LISTEN_PORT = int(os.environ.get("ERACUNI_MOCK_PORT", "9123"))
DOCUMENT_ID = os.environ.get("ERACUNI_MOCK_DOCUMENT_ID", "mock-doc-001")
INVOICE_NUMBER = os.environ.get("ERACUNI_MOCK_INVOICE_NUMBER", "MOCK-2026-0001")
PUBLIC_URL = os.environ.get("ERACUNI_MOCK_PUBLIC_URL", "https://example.test/invoices/mock-2026-0001")
PDF_URL = os.environ.get("ERACUNI_MOCK_PDF_URL", "https://example.test/invoices/mock-2026-0001.pdf")

LOG_DIR.mkdir(parents=True, exist_ok=True)


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8")
        entry = {
            "path": self.path,
            "headers": {k: v for k, v in self.headers.items()},
            "body": json.loads(body) if body else None,
        }

        with REQUESTS_PATH.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(entry, ensure_ascii=False) + "\n")

        payload = json.dumps(
            {
                "status": "ok",
                "documentId": DOCUMENT_ID,
                "invoiceNumber": INVOICE_NUMBER,
                "publicURL": PUBLIC_URL,
                "pdfURL": PDF_URL,
            }
        )
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload.encode("utf-8"))))
        self.end_headers()
        self.wfile.write(payload.encode("utf-8"))

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    print(f"e-racuni mock listening on http://{LISTEN_HOST}:{LISTEN_PORT} and writing {REQUESTS_PATH}")
    HTTPServer((LISTEN_HOST, LISTEN_PORT), Handler).serve_forever()
