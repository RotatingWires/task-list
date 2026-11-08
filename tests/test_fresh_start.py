"""Fresh start against a real TaskList server (run `dotnet build` first; skipped without the built app)."""

from contextlib import closing
import http.cookiejar
import json
import re
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "bin" / "Debug" / "net10.0" / "TaskList.dll"
PASSWORD = "fresh-start-test"


@unittest.skipUnless(DLL.exists() and shutil.which("dotnet"), "build TaskList first: dotnet build")
class ServerTestCase(unittest.TestCase):
    """A signed-in TaskList server in a temporary folder, shared with the data-folder tests."""

    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="task-list-fresh-start-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.start()
        self.addCleanup(self.stop)
        token = re.search(r"\n\s+([0-9A-F]{4}(?:-[0-9A-F]{4}){3})\s*\n", self.output).group(1)
        self.call("POST", "/api/auth/setup", {"setupToken": token, "password": PASSWORD, "confirmPassword": PASSWORD})
        self.call("POST", "/api/auth/login", {"password": PASSWORD})
        self.list_id = self.call("GET", "/api/lists")[0]["id"]

    def start(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            self.url = f"http://127.0.0.1:{sock.getsockname()[1]}"
        self.process = subprocess.Popen(["dotnet", str(DLL), "--urls", self.url], cwd=self.root,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        self.output = ""
        while "Now listening" not in self.output:
            line = self.process.stdout.readline()
            if not line:
                raise RuntimeError("TaskList did not start:\n" + self.output)
            self.output += line
        # Keep reading so a full console pipe never blocks the server.
        threading.Thread(target=lambda stream=self.process.stdout: [None for _ in stream], daemon=True).start()
        self.opener = getattr(self, "opener", None) or urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def stop(self):
        self.process.terminate()
        self.process.wait(timeout=10)
        self.process.stdout.close()

    def call(self, method, path, body=None, raw=None, content_type="application/json"):
        data = raw if raw is not None else (None if body is None else json.dumps(body).encode())
        request = urllib.request.Request(self.url + path, data=data, method=method, headers={"Content-Type": content_type})
        with self.opener.open(request, timeout=60) as response:
            payload = response.read()
        try:
            return json.loads(payload) if payload else None
        except ValueError:
            return payload

    def status_of(self, method, path, body):
        try:
            self.call(method, path, body)
            return 200
        except urllib.error.HTTPError as error:
            with error:
                return error.code

    def fresh_start(self, **selections):
        return self.call("POST", "/api/fresh-start", {"confirmation": "RESET SELECTED", "selections": selections})

    def task(self, title, parent=None, list_id=None, status=None, description=""):
        list_id = list_id or self.list_id
        path = f"/api/lists/{list_id}/items/{parent}/subtasks" if parent else f"/api/lists/{list_id}/tasks"
        display_id = self.call("POST", path, {"title": title, "description": description})["displayId"]
        if status:
            self.call("PATCH", f"/api/lists/{list_id}/items/{display_id}", {"status": status})
        return display_id

    def query(self, sql):
        with closing(sqlite3.connect(self.root / "data" / "tasklist.db")) as connection:
            return connection.execute(sql).fetchall()

    def scalar(self, sql):
        return self.query(sql)[0][0]

    def counters(self):
        return dict(self.query("SELECT scope_key, total FROM milestone_notification_counters"))


class FreshStartTests(ServerTestCase):
    def test_rejects_missing_confirmation_or_unknown_options(self):
        self.task("Keep me", status="Done")
        for body in [{"confirmation": "reset selected", "selections": {"finishedTasks": True}},
                     {"confirmation": "RESET SELECTED", "selections": {"bogus": True}},
                     {"confirmation": "RESET SELECTED", "selections": {"finishedTasks": False}},
                     {"confirmation": "RESET SELECTED"}]:
            self.assertEqual(self.status_of("POST", "/api/fresh-start", body), 400, body)
        self.assertEqual(self.scalar("SELECT COUNT(*) FROM items"), 1)

    def test_finished_tasks_and_archived_lists_keep_open_trees_and_record_deletions(self):
        done = self.task("Done tree", status="Done")
        self.task("Done child", parent=done, status="Done")
        waiting = self.task("Done with open child")
        self.task("Open child", parent=waiting)
        self.call("PATCH", f"/api/lists/{self.list_id}/items/{waiting}", {"status": "Done"})
        open_parent = self.task("Open parent")
        self.task("Done child", parent=open_parent, status="Done")
        self.task("Cancelled", status="Cancelled")
        archived = self.call("POST", "/api/lists", {"name": "Old"})["id"]
        self.task("Archived task", list_id=archived)
        self.call("POST", f"/api/lists/{archived}/archive")

        summary = self.call("GET", "/api/fresh-start")
        self.assertEqual((summary["finishedTasks"], summary["archivedLists"], summary["archivedListTasks"]), (4, 1, 1))
        before = self.counters()
        result = self.fresh_start(finishedTasks=True, archivedLists=True)
        self.assertEqual((result["removedTasks"], result["removedLists"]), (5, 1))
        remaining = sorted(row[0] for row in self.query("SELECT display_id FROM items"))
        self.assertEqual(remaining, sorted([waiting, f"{waiting}.1", open_parent]))
        self.assertEqual(self.scalar("SELECT COUNT(*) FROM task_events WHERE event_type = 'Deleted'"), 5)
        self.assertEqual(self.scalar("SELECT name FROM deleted_lists"), "Old")
        self.assertTrue(all(self.counters().get(key, 0) >= total for key, total in before.items()))

    def test_history_clears_restart_cleanly_and_change_the_stats_revision(self):
        self.task("Gone", status="Done")
        self.fresh_start(finishedTasks=True)
        kept = self.task("Kept", status="Done")
        revision = self.scalar("SELECT revision FROM task_history_state")
        result = self.fresh_start(deletedTaskHistory=True)
        self.assertEqual(self.scalar("SELECT COUNT(*) FROM task_events WHERE universal_id NOT IN (SELECT universal_id FROM items)"), 0)
        self.assertGreater(result["removedEvents"], 0)
        self.assertNotEqual(self.scalar("SELECT revision FROM task_history_state"), revision)

        self.call("PATCH", f"/api/lists/{self.list_id}/items/{kept}", {"status": "Open"})
        self.call("PATCH", f"/api/lists/{self.list_id}/items/{kept}", {"status": "Done"})
        self.fresh_start(allHistory=True)
        events = self.query("SELECT event_type, source FROM task_events ORDER BY id")
        self.assertEqual(events, [("Created", "legacy"), ("Completed", "legacy"), ("Reopened", "legacy")])
        counters, pending = self.counters(), self.scalar("SELECT COUNT(*) FROM milestone_notifications")
        self.assertEqual(counters["global:recorded"], 3)

        self.stop()
        self.start()
        self.assertEqual(len(self.query("SELECT id FROM task_events")), 3)
        self.assertEqual(self.counters(), counters)
        self.assertEqual(self.scalar("SELECT COUNT(*) FROM milestone_notifications"), pending)

    def test_everything_and_recovery_backups_free_space_without_reusing_ids(self):
        for index in range(80):
            self.task(f"Task {index}", description="x" * 2000)
        archive = self.call("GET", "/api/backup")
        self.call("POST", "/api/restore", raw=archive, content_type="application/zip")
        summary = self.call("GET", "/api/fresh-start")
        self.assertEqual(summary["recoveryBackups"], 1)
        highest = self.scalar("SELECT MAX(id) FROM universal_ids")

        result = self.fresh_start(everything=True, allHistory=True, recoveryBackups=True)
        self.assertEqual((result["removedTasks"], result["removedBackups"]), (80, 1))
        self.assertTrue(result["compacted"])
        self.assertEqual([entry["name"] for entry in self.call("GET", "/api/lists")], ["Tasks"])
        self.assertEqual(self.scalar("SELECT COUNT(*) FROM task_events"), 0)
        self.assertEqual(list((self.root / "data" / "backups").iterdir()), [])
        after = self.call("GET", "/api/fresh-start")
        self.assertLess(after["databaseBytes"], summary["databaseBytes"] / 2)
        self.task("After", list_id=self.call("GET", "/api/lists")[0]["id"])
        self.assertGreater(self.scalar("SELECT universal_id FROM items"), highest)


if __name__ == "__main__":
    unittest.main()
