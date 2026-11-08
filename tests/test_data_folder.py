"""Data folder location, database file name, and schema version against a real TaskList server (run `dotnet build` first)."""

from contextlib import closing
import hashlib
import io
import json
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

import test_fresh_start

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "bin" / "Debug" / "net10.0" / "TaskList.dll"


@unittest.skipUnless(DLL.exists() and shutil.which("dotnet"), "build TaskList first: dotnet build")
class DataFolderTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="task-list-data-folder-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def run_server(self, *args):
        """Starts TaskList until it answers (or exits), stops it, and returns (exit code, console output)."""
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            url = f"http://127.0.0.1:{sock.getsockname()[1]}"
        process = subprocess.Popen(["dotnet", str(DLL), "--urls", url, *args], cwd=self.root,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        deadline = time.time() + 30
        while time.time() < deadline and process.poll() is None:
            try:
                urllib.request.urlopen(url + "/api/auth/status", timeout=1).close()
                break
            except OSError:
                time.sleep(0.2)
        if process.poll() is None:
            process.terminate()
        output = process.communicate(timeout=15)[0]
        return process.returncode, output

    def user_version(self, path):
        with closing(sqlite3.connect(path)) as connection:
            return connection.execute("PRAGMA user_version").fetchone()[0]

    def test_new_database_is_tasklist_db_with_schema_version(self):
        self.run_server()
        database = self.root / "data" / "tasklist.db"
        self.assertTrue(database.exists())
        self.assertEqual(self.user_version(database), 1)

    def test_renames_the_old_database_file_and_keeps_its_data(self):
        self.run_server()
        data = self.root / "data"
        with closing(sqlite3.connect(data / "tasklist.db")) as connection:
            connection.execute("UPDATE lists SET name = 'Kept list'")
            connection.commit()
        for suffix in ("", "-wal", "-shm"):
            if (data / f"tasklist.db{suffix}").exists():
                (data / f"tasklist.db{suffix}").rename(data / f"task-list.db{suffix}")
        _, output = self.run_server()
        self.assertIn("Renamed task-list.db to tasklist.db.", output)
        self.assertFalse((data / "task-list.db").exists())
        with closing(sqlite3.connect(data / "tasklist.db")) as connection:
            self.assertEqual(connection.execute("SELECT name FROM lists").fetchall(), [("Kept list",)])

    def test_refuses_a_database_from_a_newer_version(self):
        self.run_server()
        database = self.root / "data" / "tasklist.db"
        with closing(sqlite3.connect(database)) as connection:
            connection.execute("PRAGMA user_version = 99")
        code, output = self.run_server()
        self.assertEqual(code, 1)
        self.assertIn("schema version 99", output)
        self.assertEqual(self.user_version(database), 99)

    def test_data_dir_option_moves_the_data_folder(self):
        folder = self.root / "elsewhere"
        self.run_server("--data-dir", str(folder))
        self.assertTrue((folder / "tasklist.db").exists())
        self.assertFalse((self.root / "data").exists())


class BackupCheckTests(test_fresh_start.ServerTestCase):
    """Exports carry a checksum and schema version like SpendTracker and NewsReview; older exports still restore."""

    def export(self):
        with zipfile.ZipFile(io.BytesIO(self.call("GET", "/api/backup"))) as archive:
            return json.loads(archive.read("manifest.json")), archive.read("tasklist.db")

    def restore(self, manifest, database, name="tasklist.db"):
        output = io.BytesIO()
        with zipfile.ZipFile(output, "w") as archive:
            archive.writestr("manifest.json", json.dumps(manifest))
            archive.writestr(name, database)
        return self.call("POST", "/api/restore", raw=output.getvalue(), content_type="application/zip")

    def test_export_records_checksum_and_schema_version(self):
        manifest, database = self.export()
        self.assertEqual(manifest["schemaVersion"], 1)
        self.assertEqual(manifest["sha256"], hashlib.sha256(database).hexdigest())

    def test_restore_refuses_a_database_that_does_not_match_its_checksum(self):
        manifest, database = self.export()
        manifest["sha256"] = "0" * 64
        with self.assertRaises(urllib.error.HTTPError) as raised:
            self.restore(manifest, database)
        with raised.exception as error:
            self.assertEqual(error.code, 400)
            self.assertIn("checksum", error.read().decode())

    def test_restore_accepts_older_exports_without_checksum_or_new_name(self):
        self.task("kept task")
        manifest, database = self.export()
        del manifest["sha256"], manifest["schemaVersion"]
        self.task("added after export")
        self.restore(manifest, database, name="task-list.db")
        titles = [task["title"] for task in self.call("GET", f"/api/lists/{self.list_id}/tasks")]
        self.assertIn("kept task", titles)
        self.assertNotIn("added after export", titles)


if __name__ == "__main__":
    unittest.main()
