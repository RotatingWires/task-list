"""Sign-in tests against the real server. The same file in TaskList, TaskList Stats, and SpendTracker, which share
src/PasswordAuth.cs; NewsReview has the same checks in its C# AuthTests. Run: python3 -m unittest discover -s tests"""

import os
import stat
import unittest

from server import APP, PASSWORD, ApiError, Server, build

# An address that needs a session in each app.
PROTECTED = {"TaskList": "/api/lists", "TaskListStats": "/api/snapshot", "SpendTracker": "/api/snapshot"}[APP]


def setUpModule():
    build()


class SignInTests(unittest.TestCase):
    def setUp(self):
        self.server = Server()
        self.addCleanup(self.server.stop)

    def assertStatus(self, call, status):
        with self.assertRaises(ApiError) as context:
            call()
        self.assertEqual(context.exception.status, status, context.exception.body)
        return context.exception.body

    def test_first_run_setup_checks_the_token_and_password(self):
        client = self.server.client()
        self.assertEqual(client.get("/api/auth/status")["configured"], False)
        self.assertStatus(lambda: client.get(PROTECTED), 401)
        self.assertStatus(lambda: client.post("/api/auth/setup", {"setupToken": "0000-0000-0000-0000", "password": PASSWORD, "confirmPassword": PASSWORD}), 401)
        self.assertStatus(lambda: client.post("/api/auth/setup", {"setupToken": self.server.token, "password": "short", "confirmPassword": "short"}), 400)
        self.assertStatus(lambda: client.post("/api/auth/setup", {"setupToken": self.server.token, "password": PASSWORD, "confirmPassword": PASSWORD + "x"}), 400)
        # The token is accepted in any case and without its dashes, as typed from the console.
        client.post("/api/auth/setup", {"setupToken": self.server.token.replace("-", "").lower(), "password": PASSWORD, "confirmPassword": PASSWORD})
        self.assertTrue(client.get("/api/auth/status")["authenticated"])
        body = self.assertStatus(lambda: self.server.client().post("/api/auth/setup", {"setupToken": self.server.token, "password": PASSWORD, "confirmPassword": PASSWORD}), 409)
        self.assertEqual(body["error"], "A password is already configured.")

    def test_login_logout_and_the_saved_password(self):
        self.server.logged_in_client()
        client = self.server.client()
        self.assertStatus(lambda: client.post("/api/auth/login", {"password": "wrong-password"}), 401)
        self.assertStatus(lambda: client.get(PROTECTED), 401)
        client.post("/api/auth/login", {"password": PASSWORD})
        self.assertTrue(client.get("/api/auth/status")["authenticated"])
        client.post("/api/auth/logout", {})
        self.assertFalse(client.get("/api/auth/status")["authenticated"])
        self.assertStatus(lambda: client.get(PROTECTED), 401)
        auth = self.server.data_dir / "auth.json"
        self.assertNotIn(PASSWORD, auth.read_text())
        if os.name == "posix":
            self.assertEqual(stat.S_IMODE(auth.stat().st_mode), 0o600)


if __name__ == "__main__":
    unittest.main()
