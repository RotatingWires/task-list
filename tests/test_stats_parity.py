"""Optional integration checks against the adjacent Stats checkout's real renderer."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest

from sql_app import ROOT, SqlApp

NODE = os.environ.get("TASKLIST_TEST_NODE") or shutil.which("node")
STATS_FIXTURE = ROOT.parent / "task-list-stats" / "tests" / "frontend-fixture.cjs"


@unittest.skipUnless(NODE and STATS_FIXTURE.is_file(), "Needs Node.js and the adjacent TaskList Stats test fixture")
class StatsParityTests(unittest.TestCase):
    def test_all_milestone_keys_match_backend_triggers_across_mixed_datasets(self):
        cases = []
        for size in [1, 100, 500, 5500, 10500, 11000]:
            app = SqlApp()
            try:
                a = app.new_list("homework renamed")
                b = app.new_list("tech stuff")
                app.connection.executemany("INSERT INTO universal_ids(id) VALUES(?)", [(n,) for n in range(1, size + 1)])
                rows = []
                for n in range(1, size + 1):
                    date = "Unknown" if n % 4 == 0 else "2026-10-14T12:00:00Z"
                    scope = a if n % 2 else b
                    rows.append((n, "Created", date, scope, str(n), "test", "legacy"))
                    for divisor, kind, when in [(3, "Completed", "2026-01-01T00:30:00+02:00"),
                                                (7, "Reopened", "Unknown"),
                                                (11, "Cancelled", "2025-12-31T23:30:00-02:00"),
                                                (17, "Deleted", "2026-02-30")]:
                        if n % divisor == 0:
                            rows.append((n, kind, when, scope, str(n), "test", "live"))
                app.connection.executemany("""
                    INSERT INTO task_events(universal_id,event_type,event_at,list_id,display_id,title,source)
                    VALUES(?,?,?,?,?,?,?)
                """, rows)
                names = dict(universal_id="universalId", event_type="eventType", event_at="eventAt", list_id="listId",
                             display_id="displayId", parent_display_id="parentDisplayId", from_status="fromStatus", to_status="toStatus")
                events = [{names.get(key, key): value for key, value in row.items()} for row in app.events()]
                expected = [row[0] for row in app.connection.execute("SELECT key FROM milestone_notifications")]
                cases.append(dict(
                    name=f"{size} creations with mixed events/dates/lists", expected=expected,
                    snapshot=dict(lists=[dict(id=a, name="homework renamed"), dict(id=b, name="tech stuff")],
                                  events=events, highestUniversalId=size),
                ))
            finally:
                app.close()
        result = subprocess.run([NODE, str(Path(__file__).with_name("milestone-parity.cjs")), str(STATS_FIXTURE)],
                                input=json.dumps(cases), capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
