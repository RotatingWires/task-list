"""The server enforces the same text limits as the page's maxlength, like SpendTracker (run `dotnet build` first)."""

import unittest

import test_fresh_start


class LengthLimitTests(test_fresh_start.ServerTestCase):
    def test_task_and_subtask_titles_and_descriptions(self):
        self.task("t" * 500, description="d" * 4000)
        self.assertEqual(self.status_of("POST", f"/api/lists/{self.list_id}/tasks", {"title": "t" * 501}), 400)
        self.assertEqual(self.status_of("POST", f"/api/lists/{self.list_id}/tasks", {"title": "ok", "description": "d" * 4001}), 400)
        self.assertEqual(self.status_of("POST", f"/api/lists/{self.list_id}/items/1/subtasks", {"title": "t" * 501}), 400)
        self.assertEqual(self.status_of("PATCH", f"/api/lists/{self.list_id}/items/1", {"title": "t" * 501}), 400)

    def test_list_names_and_descriptions(self):
        self.assertEqual(self.status_of("POST", "/api/lists", {"name": "n" * 101}), 400)
        self.assertEqual(self.status_of("POST", "/api/lists", {"name": "ok", "description": "d" * 1001}), 400)
        self.assertEqual(self.status_of("PATCH", f"/api/lists/{self.list_id}", {"name": "n" * 101}), 400)
        self.assertEqual(self.status_of("POST", "/api/lists", {"name": "n" * 100, "description": "d" * 1000}), 200)

    def test_markdown_import_titles(self):
        markdown = f"- [ ] {'t' * 501}\n".encode()
        self.assertEqual(self.status_of_raw(f"/api/lists/{self.list_id}/import/markdown", markdown), 400)
        self.assertEqual(self.status_of_raw(f"/api/lists/{self.list_id}/import/markdown", f"- [ ] {'t' * 500}\n".encode()), 200)

    def status_of_raw(self, path, data):
        try:
            self.call("POST", path, raw=data, content_type="text/markdown; charset=utf-8")
            return 200
        except test_fresh_start.urllib.error.HTTPError as error:
            with error:
                return error.code


if __name__ == "__main__":
    unittest.main()
