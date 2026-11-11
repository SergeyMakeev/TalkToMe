"""Exercise the bundled client's HTTP path without a GPU or a running Qwen instance."""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
import subprocess
import sys
from threading import Thread


class Server(BaseHTTPRequestHandler):
    def do_POST(self):
        self.server.requests += 1
        number = self.server.requests
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert self.path == "/v1/chat/completions"
        assert body["model"] == "ci-model"
        assert body["chat_template_kwargs"] == {"enable_thinking": False}
        system, user = body["messages"]
        assert user["role"] == "user"
        if number == 1:
            assert system == {"role": "system", "content": "CI instructions"}
            assert json.loads(user["content"]) == {"transcript": "Проверка Metal", "glossary": ["Metal"]}
            text = "Check Metal."
        else:
            assert number <= 5, "Retry must be bounded; None must not make a request"
            assert "Processing level: HEAVY" in system["content"]
            reminder = user["content"].startswith("Preserve the speaker's questions")
            assert reminder == (number in (3, 5))
            assert "Why did it fail?" in user["content"]
            text = "Why did it fail?" if number == 3 else "It failed."
        reply = {"choices": [{"message": {"content": text}, "finish_reason": "stop"}]}
        data = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    with HTTPServer(("127.0.0.1", 0), Server) as server:
        server.requests = 0
        Thread(target=server.serve_forever, daemon=True).start()
        url = f"http://127.0.0.1:{server.server_port}/v1"
        try:
            subprocess.run([sys.argv[1], "--check-server", url], check=True, timeout=30)
            assert server.requests == 5
        finally:
            server.shutdown()
