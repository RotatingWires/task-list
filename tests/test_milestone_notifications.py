"""Exercise the application's actual SQLite SQL without requiring a running server."""

from pathlib import Path
import re
import sqlite3
import unittest


ROOT = Path(__file__).resolve().parents[1]
NOTIFICATIONS = (ROOT / "MilestoneNotifications.cs").read_text()
PROGRAM = (ROOT / "Program.cs").read_text()


def notification_sql(method):
    body = NOTIFICATIONS.split(f"private static void {method}(", 1)[1]
    return re.search(r'"""(.*?)"""', body, re.S).group(1)


class MilestoneNotificationTests(unittest.TestCase):
    def setUp(self):
        self.connection = sqlite3.connect(":memory:")
        self.addCleanup(self.connection.close)
        self.connection.executescript("""
            PRAGMA foreign_keys = ON;
            CREATE TABLE lists (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE universal_ids (id INTEGER PRIMARY KEY);
            INSERT INTO lists VALUES (7, 'homework'), (8, 'old imports');
        """)
        for table in ["items", "task_events"]:
            schema = re.search(rf"CREATE TABLE IF NOT EXISTS {table} \(.*?\);", PROGRAM, re.S).group()
            self.connection.executescript(schema)
        unique_index = re.search(r"CREATE UNIQUE INDEX IF NOT EXISTS idx_task_events_unique .*?;", PROGRAM).group()
        self.connection.executescript(unique_index)
        item_triggers = PROGRAM.split("static void EnsureTaskEventSchema(", 1)[1]
        item_triggers = item_triggers.split('using var triggers = Sql(connection, """', 1)[1].split('"""', 1)[0]
        self.connection.executescript(item_triggers)
        self.connection.executescript(notification_sql("CreateNotificationTable"))
        self.next_uid = 1

    def start_notifications(self):
        self.connection.executescript(notification_sql("DropTriggers"))
        self.connection.executescript(notification_sql("RebuildCounters"))
        self.connection.executescript(notification_sql("CreateTriggers"))

    def add_task(self, date, display_id=None, parent=None):
        uid = self.next_uid
        self.next_uid += 1
        self.connection.execute("INSERT INTO universal_ids VALUES (?)", (uid,))
        self.connection.execute("""
            INSERT INTO items (universal_id, list_id, display_id, parent_display_id, title, created_at, updated_at)
            VALUES (?, 7, ?, ?, 'quiz', ?, 'Unknown')
        """, (uid, display_id or str(uid), parent, date))
        return uid

    def seed_events(self, kind, count, date):
        self.connection.executemany("""
            INSERT INTO task_events (universal_id, event_type, event_at, list_id, display_id, title)
            VALUES (?, ?, ?, 7, ?, 'test')
        """, [(uid, kind, date, str(uid)) for uid in range(self.next_uid, self.next_uid + count)])
        self.next_uid += count

    def total(self, key):
        row = self.connection.execute(
            "SELECT total FROM milestone_notification_counters WHERE scope_key = ?", (key,),
        ).fetchone()
        return row[0] if row else 0

    def pending(self):
        return self.connection.execute(
            "SELECT key, scope_label, reached_at FROM milestone_notifications ORDER BY key",
        ).fetchall()

    def test_moved_undated_imports_and_list_rename_do_not_hide_100th_dated_subtask(self):
        for _ in range(100):
            self.add_task("Unknown")
        for _ in range(98):
            self.add_task("2026-09-01")
        self.add_task("2026-10-01", display_id="199")
        self.start_notifications()

        # Use the move endpoint's delete/reinsert pattern with the same UID/date.
        # The actual item trigger must not duplicate or reattribute Created events.
        rows = self.connection.execute(
            "SELECT universal_id, display_id, title, created_at FROM items WHERE created_at = 'Unknown'",
        ).fetchall()
        for uid, display_id, title, created_at in rows:
            self.connection.execute("DELETE FROM items WHERE universal_id = ?", (uid,))
            self.connection.execute("""
                INSERT INTO items (universal_id, list_id, display_id, title, created_at)
                VALUES (?, 8, ?, ?, ?)
            """, (uid, display_id, title, created_at))
        self.connection.execute("UPDATE lists SET name = 'homework year 2' WHERE id = 7")
        reached_at = "2026-10-15T01:58:21.0000000+00:00"
        self.add_task(reached_at, display_id="199.2", parent="199")

        self.assertEqual(self.total("list:7:created"), 100)
        self.assertEqual(self.total("global:created"), 100)
        self.assertEqual(self.total("global:recorded"), 100)
        self.assertEqual(self.total("list:8:created"), 0)
        self.assertIn(("list:7:created:100", "homework year 2", reached_at), self.pending())
        self.assertEqual(self.connection.execute(
            "SELECT COUNT(*) FROM task_events WHERE list_id = 7 AND event_at = 'Unknown'",
        ).fetchone()[0], 100)
        self.assertEqual(self.connection.execute(
            "SELECT COUNT(*) FROM items WHERE list_id = 8",
        ).fetchone()[0], 100)

    def test_all_event_families_ignore_undated_history_when_reaching_100(self):
        for kind in ["Created", "Completed", "Cancelled", "Reopened", "Deleted"]:
            with self.subTest(kind=kind):
                self.connection.execute("DELETE FROM task_events")
                self.connection.execute("DELETE FROM milestone_notifications")
                self.connection.executescript(notification_sql("DropTriggers"))
                self.seed_events(kind, 100, "Unknown")
                self.seed_events(kind, 99, "2026-10-14")
                self.start_notifications()
                self.seed_events(kind, 1, "2026-10-15T01:58:21Z")
                self.assertEqual(self.total(f"global:{kind.lower()}"), 100)
                self.assertEqual(self.total("global:recorded"), 100)
                keys = [row[0] for row in self.pending()]
                self.assertIn(f"global:{kind.lower()}:100", keys)
                self.assertIn("global:recorded:100", keys)
                if kind in ("Created", "Completed"):
                    self.assertIn(f"list:7:{kind.lower()}:100", keys)
                    self.assertIn(f"year:2026:{kind.lower()}:100", keys)

    def test_unknown_and_invalid_dates_do_not_increment_event_counters(self):
        self.start_notifications()
        for date in ["Unknown", "unknown", "", "invalid", "2026-not-a-date"]:
            self.add_task(date)
        self.assertEqual(self.total("global:created"), 0)
        self.assertEqual(self.total("global:recorded"), 0)
        self.assertEqual(self.total("list:7:created"), 0)
        self.assertEqual(self.total("year:2026:created"), 0)
        self.assertEqual([row[0] for row in self.pending()], ["universal:1"])
        self.assertEqual(self.connection.execute("SELECT COUNT(*) FROM task_events").fetchone()[0], 5)

    def test_date_only_imports_still_count_and_keep_their_date(self):
        self.start_notifications()
        self.add_task("2026-10-14")
        self.assertEqual(self.total("global:created"), 1)
        self.assertEqual(self.total("list:7:created"), 1)
        self.assertIn(("global:created:1", None, "2026-10-14"), self.pending())
        self.assertIn(("year:2026:created:1", None, "2026-10-14"), self.pending())

    def test_rebuild_does_not_replay_old_milestones_or_discard_pending_notices(self):
        self.seed_events("Created", 100, "Unknown")
        self.seed_events("Created", 99, "2026-10-14")
        self.start_notifications()
        self.assertEqual(self.pending(), [])
        self.seed_events("Created", 1, "2026-10-15T01:58:21Z")
        pending = self.pending()
        self.start_notifications()
        self.assertEqual(self.pending(), pending)
        self.connection.execute("DELETE FROM milestone_notifications")
        self.start_notifications()
        self.assertEqual(self.pending(), [])
        self.assertEqual(self.total("list:7:created"), 100)

    def test_continuing_list_and_year_thresholds_remain_every_500(self):
        self.seed_events("Created", 100, "Unknown")
        self.seed_events("Created", 5499, "2026-10-14")
        self.start_notifications()
        self.seed_events("Created", 1, "2026-10-15T01:58:21Z")
        keys = [row[0] for row in self.pending()]
        self.assertIn("list:7:created:5500", keys)
        self.assertIn("year:2026:created:5500", keys)
        self.assertEqual(self.total("global:created"), 5500)

    def test_continuing_global_and_universal_thresholds_remain_every_500(self):
        self.seed_events("Created", 100, "Unknown")
        self.seed_events("Created", 10499, "2026-10-14")
        self.start_notifications()
        self.seed_events("Created", 1, "2026-10-15T01:58:21Z")
        self.connection.execute("INSERT INTO universal_ids VALUES (10500)")
        keys = [row[0] for row in self.pending()]
        self.assertIn("global:created:10500", keys)
        self.assertIn("global:recorded:10500", keys)
        self.assertIn("universal:10500", keys)


if __name__ == "__main__":
    unittest.main()
