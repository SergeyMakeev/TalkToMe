"""Exercise the bundled client's HTTP path without a GPU or a running Qwen instance."""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
import subprocess
import sys
from threading import Thread


class Server(BaseHTTPRequestHandler):
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert self.path == "/v1/chat/completions"
        assert body["model"] == "ci-model"
        assert body["chat_template_kwargs"] == {"enable_thinking": False}
        system, user = body["messages"]
        assert system == {"role": "system", "content": "CI instructions"}
        assert user["role"] == "user"
        assert json.loads(user["content"]) == {"transcript": "Проверка Metal", "glossary": ["Metal"]}
        reply = {"choices": [{"message": {"content": "Check Metal."}, "finish_reason": "stop"}]}
        data = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    with HTTPServer(("127.0.0.1", 0), Server) as server:
        Thread(target=server.handle_request, daemon=True).start()
        url = f"http://127.0.0.1:{server.server_port}/v1"
        subprocess.run([sys.argv[1], "--check-server", url], check=True, timeout=30)
