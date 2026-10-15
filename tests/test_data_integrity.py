from collections import Counter
from datetime import datetime, timezone
import random
import sqlite3
import tempfile
from pathlib import Path
import unittest

from sql_app import SqlApp


class DataIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.app = SqlApp()
        self.addCleanup(self.app.close)
        self.a = self.app.new_list("homework")
        self.b = self.app.new_list("tech stuff")

    def counters_match_history(self):
        expected = Counter({"global:recorded": 0})
        for event in self.app.events():
            kind = event["event_type"].lower()
            expected[f"global:{kind}"] += 1
            expected["global:recorded"] += 1
            if kind in ("created", "completed"):
                expected[f"list:{event['list_id']}:{kind}"] += 1
                try:
                    date = datetime.fromisoformat(event["event_at"].replace("Z", "+00:00"))
                except ValueError:
                    continue
                year = date.astimezone(timezone.utc).year if date.tzinfo else date.year
                expected[f"year:{year}:{kind}"] += 1
        actual = dict(self.app.connection.execute("SELECT scope_key,total FROM milestone_notification_counters"))
        self.assertEqual(actual, dict(expected))

    def tree(self, source):
        root = self.app.create(source, date="Unknown", title="root")
        child = self.app.create(source, parent=root, title="child")
        grandchild = self.app.create(source, parent=child, title="grandchild")
        self.app.update(child, status="Done")
        self.app.update(grandchild, status="Cancelled")
        return root, child, grandchild

    def test_move_entire_tree_preserves_uid_dates_statuses_and_history(self):
        ids = self.tree(self.a)
        self.app.create(self.b)
        before = {uid: dict(self.app.item(uid)) for uid in ids}
        history = self.app.events()
        self.app.move(ids[0], self.b)
        for uid in ids:
            after = dict(self.app.item(uid))
            for field in ("universal_id", "title", "created_at", "updated_at", "completed_at",
                          "cancelled_at", "reopened_at", "status", "next_child_number"):
                self.assertEqual(after[field], before[uid][field], field)
            self.assertEqual(after["list_id"], self.b)
        self.assertEqual(self.app.item(ids[0])["display_id"], "2")
        self.assertEqual(self.app.item(ids[2])["display_id"], "2.1.1")
        self.assertEqual(self.app.events(), history)
        self.app.assert_integrity()
        self.counters_match_history()

    def test_independent_subtask_moves_become_roots_and_keep_their_children(self):
        root, child, grandchild = self.tree(self.a)
        self.app.move(child, self.b)
        self.assertEqual(self.app.item(root)["list_id"], self.a)
        self.assertIsNone(self.app.item(child)["parent_display_id"])
        self.assertEqual(self.app.item(grandchild)["parent_display_id"], self.app.item(child)["display_id"])
        self.app.assert_integrity()
        self.counters_match_history()

    def test_moved_task_changes_are_attributed_to_its_current_list(self):
        root = self.app.create(self.a)
        self.app.move(root, self.b)
        self.app.rename(self.a, "old homework")
        self.app.rename(self.b, "homework year 2")
        self.app.update(root, status="Done", title="renamed quiz")
        created, completed = self.app.events()
        self.assertEqual((created["list_id"], created["title"]), (self.a, "task"))
        self.assertEqual((completed["list_id"], completed["title"]), (self.b, "renamed quiz"))
        self.assertEqual(completed["universal_id"], created["universal_id"])
        self.counters_match_history()

    def test_all_status_pairs_and_repeated_requests_record_only_actual_transitions(self):
        expected = {"Open": None, "Done": "Completed", "Cancelled": "Cancelled"}
        for initial in expected:
            for final in expected:
                with self.subTest(initial=initial, final=final):
                    uid = self.app.create(self.a)
                    if initial != "Open":
                        self.app.update(uid, status=initial)
                    before = len(self.app.events())
                    self.app.update(uid, status=final, date="2026-10-14T15:00:00Z")
                    self.app.update(uid, status=final, date="2026-10-14T15:01:00Z")
                    added = self.app.events()[before:]
                    self.assertEqual(len(added), int(initial != final))
                    if added:
                        self.assertEqual(added[0]["event_type"], "Reopened" if final == "Open" else expected[final])
                        self.assertEqual((added[0]["from_status"], added[0]["to_status"]), (initial, final))
        self.counters_match_history()

    def test_task_and_list_renames_do_not_create_history_or_reset_counters(self):
        root = self.app.create(self.a)
        before = self.app.events()
        for name in ["homework year 2", "WORK", "quotes ' and 雪", "homework"]:
            self.app.rename(self.a, name)
            self.app.update(root, title=name)
            self.assertEqual(self.app.events(), before)
            self.counters_match_history()

    def test_same_timestamp_transitions_are_distinct_and_moves_do_not_duplicate_them(self):
        uid = self.app.create(self.a)
        for status in ["Done", "Open", "Done", "Cancelled", "Open", "Cancelled"]:
            self.app.update(uid, status=status, date="2026-10-14T13:00:00Z")
        before = self.app.events()
        self.assertEqual(len(before), 7)
        self.app.move(uid, self.b)
        self.app.move(uid, self.a)
        self.app.restart()
        self.assertEqual(self.app.events(), before)
        self.counters_match_history()

    def test_archiving_and_restoring_preserve_ids_history_and_all_counts(self):
        ids = self.tree(self.a)
        before = self.app.events()
        self.app.archive(self.a)
        self.app.archive(self.a, False)
        self.assertEqual(self.app.events(), before)
        self.assertTrue(all(self.app.item(uid)["list_id"] == self.a for uid in ids))
        self.counters_match_history()

    def test_rejected_and_interrupted_moves_roll_back_whole_trees(self):
        root, child, grandchild = self.tree(self.a)
        before = list(self.app.connection.iterdump())
        for target in [self.a, 999999]:
            with self.subTest(target=target), self.assertRaises(ValueError):
                self.app.move(root, target)
            self.assertEqual(list(self.app.connection.iterdump()), before)
        self.app.archive(self.b)
        archived = list(self.app.connection.iterdump())
        with self.assertRaises(ValueError):
            self.app.move(root, self.b)
        self.assertEqual(list(self.app.connection.iterdump()), archived)
        self.app.archive(self.b, False)
        with self.assertRaises(RuntimeError):
            self.app.move(root, self.b, fail_after=1)
        self.assertEqual(list(self.app.connection.iterdump()), before)
        self.app.assert_integrity()

    def test_tree_and_list_deletion_record_every_descendant_and_preserve_deleted_names(self):
        ids = self.tree(self.a)
        self.app.rename(self.a, "homework year 2")
        self.app.delete_list(self.a)
        self.assertTrue(all(self.app.item(uid) is None for uid in ids))
        deleted = [event for event in self.app.events() if event["event_type"] == "Deleted"]
        self.assertEqual({event["universal_id"] for event in deleted}, set(ids))
        self.assertEqual([row[0] for row in self.app.connection.execute("SELECT name FROM deleted_lists")], ["homework year 2"])
        new_list = self.app.new_list("homework year 2")
        self.assertNotEqual(new_list, self.a)
        self.app.assert_integrity()
        self.counters_match_history()

    def test_imported_done_unknown_dates_survive_move_without_duplicate_transitions(self):
        uid = self.app.import_task(self.a, done=True)
        before = self.app.events()
        self.app.move(uid, self.b)
        self.assertEqual(self.app.events(), before)
        self.assertEqual(self.app.item(uid)["completed_at"], "Unknown")
        self.counters_match_history()

    def test_utc_year_boundaries_and_offset_dates_match_rebuilt_counters(self):
        for date in ["2026-01-01T00:30:00+02:00", "2025-12-31T23:30:00-02:00", "2026-01-01",
                     "Unknown", "2026-02-30", "2026-02-30T12:00:00Z", "invalid"]:
            self.app.create(self.a, date=date)
        self.counters_match_history()
        self.app.rebuild()
        self.counters_match_history()

    def test_restore_repairs_stale_counters_and_changes_history_revision_even_with_same_anchor(self):
        for _ in range(3):
            self.app.import_task(self.a)
        uid = self.app.create(self.a)
        backup = self.app.backup()
        self.addCleanup(backup.close)
        backup.execute("UPDATE milestone_notification_counters SET total=0")
        backup.execute("DROP TABLE task_history_state")  # Older ZIP schemas remain restorable.
        old_revision = self.app.connection.execute("SELECT revision FROM task_history_state").fetchone()[0]
        old_anchor = self.app.events()[-1]
        self.app.update(uid, title="new current title")
        self.app.restore(backup)
        self.assertEqual(self.app.events()[-1], old_anchor)
        self.assertNotEqual(self.app.connection.execute("SELECT revision FROM task_history_state").fetchone()[0], old_revision)
        self.counters_match_history()
        self.app.assert_integrity()

    def test_interrupted_restore_rolls_back_revision_ids_rows_and_triggers(self):
        self.tree(self.a)
        backup = self.app.backup()
        self.addCleanup(backup.close)
        self.app.create(self.b)
        before = list(self.app.connection.iterdump())
        with self.assertRaises(RuntimeError):
            self.app.restore(backup, interrupt=True)
        self.assertEqual(list(self.app.connection.iterdump()), before)
        self.counters_match_history()

    def test_restore_never_reuses_ids_or_display_numbers_allocated_after_backup(self):
        root = self.app.create(self.a)
        backup = self.app.backup()
        self.addCleanup(backup.close)
        previous_root = self.app.create(self.a)
        previous_child = self.app.create(self.a, parent=root)
        previous_uid = max(previous_root, previous_child)
        self.app.restore(backup)
        child = self.app.create(self.a, parent=root)
        new_root = self.app.create(self.a)
        self.assertGreater(child, previous_uid)
        self.assertGreater(new_root, previous_uid)
        self.assertEqual(self.app.item(child)["display_id"], "1.2")
        self.assertEqual(self.app.item(new_root)["display_id"], "3")
        self.app.assert_integrity()
        self.counters_match_history()

    def test_read_only_deferred_snapshot_keeps_revision_and_events_consistent_during_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory, "task-list.db")
            app = SqlApp(path)
            self.addCleanup(app.close)
            app.connection.execute("PRAGMA journal_mode=WAL")
            list_id = app.new_list("test")
            app.create(list_id, title="original first event")
            app.create(list_id, title="same final anchor")
            backup = app.backup()
            self.addCleanup(backup.close)
            backup.execute("UPDATE task_events SET title='restored first event' WHERE id=1")
            reader = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, isolation_level=None)
            self.addCleanup(reader.close)
            reader.execute("PRAGMA query_only=ON")
            reader.execute("BEGIN DEFERRED")
            revision = reader.execute("SELECT revision FROM task_history_state").fetchone()[0]
            app.restore(backup)
            self.assertEqual(reader.execute("SELECT revision FROM task_history_state").fetchone()[0], revision)
            self.assertEqual(reader.execute("SELECT title FROM task_events WHERE id=1").fetchone()[0], "original first event")
            reader.execute("COMMIT")
            self.assertNotEqual(reader.execute("SELECT revision FROM task_history_state").fetchone()[0], revision)
            self.assertEqual(reader.execute("SELECT title FROM task_events WHERE id=1").fetchone()[0], "restored first event")
            with self.assertRaises(sqlite3.OperationalError):
                reader.execute("UPDATE task_events SET title='forbidden'")

    def test_seeded_operation_sequences_preserve_history_and_counter_invariants(self):
        for seed in range(12):
            with self.subTest(seed=seed):
                rng = random.Random(seed)
                for step in range(100):
                    rows = self.app.connection.execute("SELECT * FROM items").fetchall()
                    action = rng.choice(["create", "child", "status", "rename", "move", "delete", "rebuild", "import"])
                    if action in ("create", "import") or not rows:
                        target = rng.choice([self.a, self.b])
                        if action == "import":
                            self.app.import_task(target, done=rng.choice([True, False]))
                        else:
                            self.app.create(target, date=rng.choice(["2026-10-14", "2026-10-14T12:00:00Z"]))
                    else:
                        row = rng.choice(rows)
                        uid = row["universal_id"]
                        before = self.app.events()
                        if action == "child":
                            self.app.create(row["list_id"], parent=uid)
                        elif action == "status":
                            self.app.update(uid, status=rng.choice(["Open", "Done", "Cancelled"]),
                                            date=f"2026-10-14T{step // 60:02}:{step % 60:02}:{seed:02}Z")
                        elif action == "rename":
                            self.app.rename(row["list_id"], f"list {row['list_id']} / seed {seed}")
                            self.assertEqual(self.app.events(), before)
                        elif action == "move":
                            self.app.move(uid, self.b if row["list_id"] == self.a else self.a)
                            self.assertEqual(self.app.events(), before)
                        elif action == "delete":
                            self.app.delete(uid, date=f"2026-10-14T16:{step % 60:02}:{seed:02}Z")
                        else:
                            self.app.restart()
                    self.app.assert_integrity()
                    self.counters_match_history()


if __name__ == "__main__":
    unittest.main()
