"""API tests against the real TaskList server on a throwaway data folder, like SpendTracker's and TaskList Stats'.
The SQL tests beside this file check the database rules in detail; these check the server itself.
Run: python3 -m unittest discover -s tests"""

import unittest

from server import ApiError, Server, build


def setUpModule():
    build()


class TaskListApiTests(unittest.TestCase):
    def setUp(self):
        self.server = Server()
        self.addCleanup(self.server.stop)
        self.api = self.server.logged_in_client()

    def test_lists_and_tasks_round_trip(self):
        created = self.api.post("/api/lists", {"name": "Errands", "description": ""})
        list_id = created["id"]
        task = self.api.post(f"/api/lists/{list_id}/tasks", {"title": "Buy milk", "description": ""})
        subtask = self.api.post(f"/api/lists/{list_id}/items/{task['displayId']}/subtasks", {"title": "Check the fridge", "description": ""})
        self.api.patch(f"/api/lists/{list_id}/items/{subtask['displayId']}", {"status": "Done"})
        tasks = self.api.get(f"/api/lists/{list_id}/tasks")
        self.assertIn("Buy milk", str(tasks))
        self.assertIn("Done", str(tasks))
        self.api.delete(f"/api/lists/{list_id}/items/{task['displayId']}")
        self.assertNotIn("Buy milk", str(self.api.get(f"/api/lists/{list_id}/tasks")))
        self.assertIn("Errands", str(self.api.get("/api/lists")))

    def test_bad_requests_and_unknown_addresses(self):
        with self.assertRaises(ApiError) as context:
            self.api.post("/api/lists", {"name": "", "description": ""})
        self.assertEqual(context.exception.status, 400)
        with self.assertRaises(ApiError) as context:
            self.api.get("/api/no-such-thing")
        self.assertEqual(context.exception.status, 404)
        self.assertEqual(context.exception.body, {"error": "Not found"})


if __name__ == "__main__":
    unittest.main()
