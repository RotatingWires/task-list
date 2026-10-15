"""SQLite operation fixture executing SQL extracted from the owning C# source."""

from contextlib import contextmanager
from pathlib import Path
import re
import sqlite3

ROOT = Path(__file__).resolve().parents[1]
PROGRAM = (ROOT / "Program.cs").read_text()
NOTIFICATIONS = (ROOT / "MilestoneNotifications.cs").read_text()
TRANSFER = (ROOT / "DataTransfer.cs").read_text()


def raw_sql(text):
    return re.findall(r'"""(.*?)"""', text, re.S)


def notification_sql(method):
    body = re.split(rf"(?:public|private) static void {method}\(", NOTIFICATIONS, maxsplit=1)[1]
    return raw_sql(body)[0]


def route(method, path):
    body = PROGRAM.split(f'api.Map{method}("{path}"', 1)[1]
    return body.split("\napi.Map", 1)[0]


def quoted_sql(body, start):
    return next(text for text in re.findall(r'"([^"\n]+)"', body) if text.startswith(start))


def execute(connection, sql, parameters=None):
    pending = ""
    cursor = None
    for char in sql:
        pending += char
        if char == ";" and sqlite3.complete_statement(pending):
            cursor = connection.execute(pending, parameters or {})
            pending = ""
    if pending.strip():
        cursor = connection.execute(pending, parameters or {})
    return cursor


class SqlApp:
    def __init__(self, path=":memory:"):
        self.connection = sqlite3.connect(path, isolation_level=None)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute("PRAGMA foreign_keys=ON")
        initialize = PROGRAM.split("static void InitializeDatabase(", 1)[1]
        execute(self.connection, raw_sql(initialize)[0])
        item_triggers = PROGRAM.split("static void EnsureTaskEventSchema(", 1)[1]
        execute(self.connection, item_triggers.split('using var triggers = Sql(connection, """', 1)[1].split('"""', 1)[0])
        execute(self.connection, notification_sql("CreateNotificationTable"))
        self.rebuild()

    def close(self):
        self.connection.close()

    def rebuild(self):
        for method in ["DropTriggers", "RebuildCounters", "CreateTriggers"]:
            execute(self.connection, notification_sql(method))

    def restart(self):
        initialize = PROGRAM.split("static void InitializeDatabase(", 1)[1]
        execute(self.connection, raw_sql(initialize)[0])
        item_triggers = PROGRAM.split("static void EnsureTaskEventSchema(", 1)[1]
        execute(self.connection, item_triggers.split('using var triggers = Sql(connection, """', 1)[1].split('"""', 1)[0])
        execute(self.connection, raw_sql(initialize)[1])
        self.rebuild()

    @contextmanager
    def transaction(self):
        self.connection.execute("BEGIN IMMEDIATE")
        try:
            yield
            self.connection.execute("COMMIT")
        except BaseException:
            self.connection.execute("ROLLBACK")
            raise

    def new_list(self, name):
        return execute(self.connection, raw_sql(route("Post", "/lists"))[0], {
            "name": name, "description": "", "createdAt": "2026-10-14T12:00:00Z",
        }).fetchone()[0]

    def list(self, identifier):
        return self.connection.execute("SELECT * FROM lists WHERE id=?", (identifier,)).fetchone()

    def item(self, uid):
        return self.connection.execute("SELECT * FROM items WHERE universal_id=?", (uid,)).fetchone()

    def events(self):
        return [dict(row) for row in self.connection.execute("SELECT * FROM task_events ORDER BY id")]

    def uid(self):
        return execute(self.connection, "INSERT INTO universal_ids DEFAULT VALUES; SELECT last_insert_rowid();").fetchone()[0]

    def create(self, list_id, date="2026-10-14T12:00:00Z", parent=None, title="task"):
        with self.transaction():
            uid = self.uid()
            parameters = {"uid": uid, "listId": list_id, "createdAt": date, "title": title, "description": ""}
            if parent is None:
                number = self.list(list_id)["next_task_number"]
                parameters.update(displayId=str(number), nextNumber=number + 1)
                sql = raw_sql(route("Post", "/lists/{listId:long}/tasks"))[0]
            else:
                row = self.item(parent)
                number = row["next_child_number"]
                parameters.update(childDisplayId=f"{row['display_id']}.{number}", parentDisplayId=row["display_id"],
                                  nextNumber=number + 1)
                sql = raw_sql(route("Post", "/lists/{listId:long}/items/{displayId}/subtasks"))[0]
            execute(self.connection, sql, parameters)
        return uid

    def import_task(self, list_id, date="Unknown", done=False):
        with self.transaction():
            uid = self.uid()
            number = self.list(list_id)["next_task_number"]
            body = PROGRAM.split("static async Task InsertImportedItem(", 1)[1]
            execute(self.connection, raw_sql(body)[0], dict(
                uid=uid, listId=list_id, displayId=str(number), parentDisplayId=None, title="imported",
                status="Done" if done else "Open", createdAt=date, completedAt=date if done else None,
                nextChildNumber=1,
            ))
            self.connection.execute("UPDATE lists SET next_task_number=? WHERE id=?", (number + 1, list_id))
        return uid

    def update(self, uid, status=None, title=None, date="2026-10-14T13:00:00Z"):
        old = self.item(uid)
        changed = (status is not None and status != old["status"]) or (title is not None and title != old["title"])
        parameters = dict(listId=old["list_id"], displayId=old["display_id"], title=title if title is not None else old["title"],
                          description=old["description"], status=status or old["status"],
                          updatedAt=date if changed else old["updated_at"], completedAt=old["completed_at"],
                          cancelledAt=old["cancelled_at"], reopenedAt=old["reopened_at"])
        if status is not None and status != old["status"]:
            parameters[{"Done": "completedAt", "Cancelled": "cancelledAt", "Open": "reopenedAt"}[status]] = date
        body = PROGRAM.split("static async Task ApplyItemUpdate(", 1)[1]
        execute(self.connection, raw_sql(body)[0], parameters)

    def rename(self, list_id, name):
        execute(self.connection, quoted_sql(route("Patch", "/lists/{id:long}"), "UPDATE lists SET name"),
                {"id": list_id, "name": name, "description": ""})

    def archive(self, list_id, archived=True):
        path = "/lists/{id:long}/archive" if archived else "/lists/{id:long}/restore"
        execute(self.connection, quoted_sql(route("Post", path), "UPDATE lists SET archived"), {"id": list_id})

    def move(self, uid, target, fail_after=None):
        body = route("Post", "/lists/{listId:long}/items/{displayId}/move")
        old = self.item(uid)
        parameters = dict(listId=old["list_id"], displayId=old["display_id"],
                          descendantPattern=old["display_id"] + ".%", targetListId=target)
        with self.transaction():
            rows = execute(self.connection, raw_sql(body)[0], parameters).fetchall()
            number = execute(self.connection, quoted_sql(body, "SELECT next_task_number"), parameters).fetchone()
            if not number or target == old["list_id"]:
                raise ValueError("Destination is not an active different list")
            root = str(number[0])
            execute(self.connection, quoted_sql(body, "DELETE FROM items"), parameters)
            for index, row in enumerate(rows):
                new_id = root + row["display_id"][len(old["display_id"]):]
                new_parent = None if index == 0 else root + row["parent_display_id"][len(old["display_id"]):]
                values = dict(uid=row["universal_id"], targetListId=target, displayId=new_id, parentDisplayId=new_parent,
                              title=row["title"], description=row["description"], status=row["status"],
                              createdAt=row["created_at"], updatedAt=row["updated_at"], completedAt=row["completed_at"],
                              cancelledAt=row["cancelled_at"], reopenedAt=row["reopened_at"],
                              nextChildNumber=row["next_child_number"])
                execute(self.connection, raw_sql(body)[1], values)
                if fail_after == index:
                    raise RuntimeError("Injected interrupted move")
            execute(self.connection, quoted_sql(body, "UPDATE lists SET next_task_number"),
                    {"nextNumber": int(root) + 1, "targetListId": target})

    def delete(self, uid, date="2026-10-14T14:00:00Z"):
        row = self.item(uid)
        execute(self.connection, raw_sql(route("Delete", "/lists/{listId:long}/items/{displayId}"))[0], {
            "listId": row["list_id"], "displayId": row["display_id"],
            "descendantPattern": row["display_id"] + ".%", "deletedAt": date,
        })

    def delete_list(self, list_id, date="2026-10-14T14:00:00Z"):
        execute(self.connection, raw_sql(route("Delete", "/lists/{id:long}"))[0],
                {"id": list_id, "deletedAt": date})

    def backup(self):
        copy = sqlite3.connect(":memory:", isolation_level=None)
        self.connection.backup(copy)
        return copy

    def restore(self, backup, interrupt=False):
        tables = re.findall(r'new\("([a-z_]+)", "([^"]+)"\)', TRANSFER.split("public static void MapEndpoints", 1)[0])
        sequences = dict(self.connection.execute("SELECT name,seq FROM sqlite_sequence"))
        list_numbers = list(self.connection.execute("SELECT id,next_task_number FROM lists"))
        child_numbers = list(self.connection.execute("SELECT universal_id,next_child_number FROM items"))
        triggers = list(self.connection.execute("SELECT name,sql FROM sqlite_master WHERE type='trigger'"))
        with self.transaction():
            self.connection.execute("PRAGMA defer_foreign_keys=ON")
            for name, _ in triggers:
                self.connection.execute(f'DROP TRIGGER "{name}"')
            for table, _ in reversed(tables):
                if table != "universal_ids":
                    self.connection.execute(f"DELETE FROM {table}")
            for table, columns in tables:
                rows = backup.execute(f"SELECT {columns} FROM {table}").fetchall()
                names = columns.split(",")
                ignore = "OR IGNORE " if table == "universal_ids" else ""
                self.connection.executemany(
                    f"INSERT {ignore}INTO {table} ({columns}) VALUES ({','.join('?' for _ in names)})", rows,
                )
            clear = quoted_sql(TRANSFER, "DELETE FROM sqlite_sequence")
            execute(self.connection, clear)
            self.connection.executemany("INSERT INTO sqlite_sequence(name,seq) VALUES (?,?)",
                                       backup.execute("SELECT name,seq FROM sqlite_sequence WHERE name IN ('lists','universal_ids','task_events')"))
            for name, value in sequences.items():
                cursor = self.connection.execute("UPDATE sqlite_sequence SET seq=MAX(seq,?) WHERE name=?", (value, name))
                if cursor.rowcount == 0:
                    self.connection.execute("INSERT INTO sqlite_sequence VALUES (?,?)", (name, value))
            for identifier, value in list_numbers:
                execute(self.connection, quoted_sql(TRANSFER, "UPDATE lists SET next_task_number=MAX"),
                        {"id": identifier, "value": value})
            for identifier, value in child_numbers:
                execute(self.connection, quoted_sql(TRANSFER, "UPDATE items SET next_child_number=MAX"),
                        {"id": identifier, "value": value})
            execute(self.connection, notification_sql("RebuildCounters"))
            execute(self.connection, quoted_sql(TRANSFER, "UPDATE task_history_state SET revision"))
            if self.connection.execute("PRAGMA foreign_key_check").fetchall():
                raise ValueError("Backup contains invalid task relationships")
            for _, sql in triggers:
                execute(self.connection, sql)
            if interrupt:
                raise RuntimeError("Injected interrupted restore")

    def assert_integrity(self):
        assert self.connection.execute("PRAGMA foreign_key_check").fetchall() == []
        rows = self.connection.execute("SELECT * FROM items").fetchall()
        for row in rows:
            parent = row["display_id"].rsplit(".", 1)[0] if "." in row["display_id"] else None
            assert row["parent_display_id"] == parent
            assert self.connection.execute("SELECT 1 FROM universal_ids WHERE id=?", (row["universal_id"],)).fetchone()
        for row in self.connection.execute("SELECT * FROM lists"):
            roots = [int(item["display_id"]) for item in rows if item["list_id"] == row["id"] and item["parent_display_id"] is None]
            assert row["next_task_number"] > max(roots, default=0)
