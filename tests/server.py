"""Starts the real app server against a throwaway data folder for tests. Standard library only.

The same file in TaskList, TaskList Stats, and SpendTracker: the app comes from the .csproj beside tests/."""

import http.cookiejar
import json
import os
import re
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = next(ROOT.glob("*.csproj")).stem
PASSWORD = "test-password-123"


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def build():
    subprocess.run(["dotnet", "build", str(ROOT), "-nologo", "-v", "quiet"], check=True, stdout=subprocess.DEVNULL)


class Server:
    """One running server with its own content root. `client()` returns a logged-out HTTP client."""

    def __init__(self, prepare=None, args=()):
        """`prepare(data_dir)` can put files in the data folder before the server starts; `args` are extra options."""
        self.data_root = tempfile.TemporaryDirectory(prefix=f"{APP.lower()}-test-")
        if prepare:
            self.data_dir.mkdir(parents=True)
            prepare(self.data_dir)
        self.port = free_port()
        self.url = f"http://127.0.0.1:{self.port}"
        self.output = []
        self.token = None
        dll = ROOT / "bin" / "Debug" / "net10.0" / f"{APP}.dll"
        self.process = subprocess.Popen(
            ["dotnet", str(dll), "--urls", self.url, "--contentRoot", self.data_root.name, "--webroot", str(ROOT / "wwwroot"), *args],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            env={**os.environ, "ASPNETCORE_ENVIRONMENT": "Production"})
        threading.Thread(target=self._read_output, daemon=True).start()
        self._wait_until_ready()

    def _read_output(self):
        for line in self.process.stdout:
            self.output.append(line)
            match = re.fullmatch(r"\s+([0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4})\s*", line)
            if match:
                self.token = match.group(1)

    def _wait_until_ready(self):
        deadline = time.time() + 30
        while time.time() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f"{APP} exited:\n" + "".join(self.output))
            try:
                with urllib.request.urlopen(f"{self.url}/api/auth/status", timeout=1):
                    pass
                if self.token or "already" in "".join(self.output):
                    return
            except OSError:
                pass
            time.sleep(0.2)
        raise RuntimeError(f"{APP} did not start:\n" + "".join(self.output))

    @property
    def data_dir(self):
        return Path(self.data_root.name) / "data"

    def client(self):
        return Client(self.url)

    def logged_in_client(self):
        client = self.client()
        if not (self.data_dir / "auth.json").exists():
            client.post("/api/auth/setup", {"setupToken": self.token, "password": PASSWORD, "confirmPassword": PASSWORD})
            # Answer the first-start backup prompt, so pages opened with this session go straight to the app.
            try:
                client.post("/api/onboarding/acknowledge", {})
            except ApiError as error:
                if error.status != 404:  # TaskList Stats keeps no data, so it has no first-start prompt.
                    raise
        else:
            client.post("/api/auth/login", {"password": PASSWORD})
        return client

    def stop(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
        self.data_root.cleanup()


class ApiError(Exception):
    def __init__(self, status, body):
        super().__init__(f"HTTP {status}: {body}")
        self.status = status
        self.body = body


class Client:
    def __init__(self, base_url):
        self.base_url = base_url
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, method, path, body=None, raw=None, content_type="application/json", binary_response=False):
        data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
        request = urllib.request.Request(self.base_url + path, data=data, method=method)
        if data is not None:
            request.add_header("Content-Type", content_type)
        try:
            with self.opener.open(request, timeout=60) as response:
                payload = response.read()
                if binary_response:
                    return payload
                return json.loads(payload) if payload else None
        except urllib.error.HTTPError as error:
            with error:
                payload = error.read()
            try:
                parsed = json.loads(payload) if payload else None
            except ValueError:
                parsed = payload.decode(errors="replace")
            raise ApiError(error.code, parsed) from None

    def get(self, path):
        return self.request("GET", path)

    def post(self, path, body=None, raw=None):
        return self.request("POST", path, body, raw)

    def download(self, path):
        return self.request("GET", path, binary_response=True)

    def upload_zip(self, path, data):
        return self.request("POST", path, raw=data, content_type="application/zip")

    def put(self, path, body):
        return self.request("PUT", path, body)

    def patch(self, path, body):
        return self.request("PATCH", path, body)

    def delete(self, path):
        return self.request("DELETE", path)
