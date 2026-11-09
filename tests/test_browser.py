"""Browser checks with Playwright against a real server, like SpendTracker's. Optional: needs `pip install playwright`
and `playwright install firefox`; without Playwright the tests are skipped, so CI leaves them to local runs.

Run: python3 -m unittest discover -s tests -p "test_browser.py"
"""

import unittest

from server import PASSWORD, Server, build

try:
    from playwright.sync_api import expect, sync_playwright
except ImportError:  # Playwright is a development-only tool.
    sync_playwright = None


@unittest.skipIf(sync_playwright is None, "Playwright is not installed")
class BrowserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        build()
        cls.server = Server()
        cls.server.logged_in_client()  # sets the password and answers the first-start prompt
        cls.playwright = sync_playwright().start()
        cls.browser = cls.playwright.firefox.launch()

    @classmethod
    def tearDownClass(cls):
        cls.browser.close()
        cls.playwright.stop()
        cls.server.stop()

    def new_page(self, **options):
        context = self.browser.new_context(**{"locale": "en-US", **options})
        self.addCleanup(context.close)
        page = context.new_page()
        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))
        self.addCleanup(lambda: self.assertEqual(errors, [], "page errors"))
        page.goto(self.server.url + "/login.html")
        page.fill("#password", PASSWORD)
        page.click("#loginButton")
        page.wait_for_url(self.server.url + "/")
        expect(page.locator("#statusText")).to_have_text("Ready")
        return page

    @staticmethod
    def close_celebrations(page):
        """Milestone celebrations open over the page after a first task; close them like a click on their button."""
        page.wait_for_timeout(300)
        page.evaluate("document.querySelectorAll('dialog[open]').forEach(dialog => dialog.close())")

    def add_task(self, page, title):
        page.fill("#newTaskTitle", title)
        page.press("#newTaskTitle", "Enter")
        page.wait_for_load_state("networkidle")
        expect(page.locator("#taskList")).to_contain_text(title, timeout=10000)
        self.close_celebrations(page)

    def test_tasks_edit_search_menus_and_theme(self):
        page = self.new_page(viewport={"width": 1200, "height": 800})
        self.add_task(page, "Alpha task")
        self.add_task(page, "Bravo task")

        # Double-click a row to edit it.
        page.locator("tr", has_text="Bravo task").locator(".task-title").dblclick()
        expect(page.locator("#editDialog")).to_be_visible()
        page.fill("#editTitle", "Bravo edited")
        page.locator("#editForm button[type=submit]").click()
        expect(page.locator("#taskList")).to_contain_text("Bravo edited")

        # A subtask from the Add Subtask dialog.
        page.locator("tr", has_text="Alpha task").locator(".task-action-arrow").click()
        page.get_by_role("menuitem", name="Add Subtask").click()
        page.fill("#subtaskTitle", "Alpha child")
        page.locator("#subtaskForm button[type=submit]").click()
        expect(page.locator("#taskList")).to_contain_text("Alpha child")

        # File > Search... finds a task by keyword.
        page.click("#fileMenu")
        page.get_by_role("menuitem", name="Search...").click()
        page.fill("#searchKeyword", "child")
        page.press("#searchKeyword", "Enter")
        expect(page.locator("#searchResults")).to_contain_text("Alpha child")
        page.click("#closeSearch")
        expect(page.locator("#searchDialog")).not_to_be_visible()

        # The keyboard opens and closes menus (dropdown.js, shared with the other apps).
        page.focus("#fileMenu")
        page.keyboard.press("ArrowDown")
        expect(page.locator("#fileDropdown")).to_be_visible()
        page.keyboard.press("Escape")
        expect(page.locator("#fileDropdown")).to_be_hidden()
        expect(page.locator("#fileMenu")).to_be_focused()

        # View > Dark switches the theme.
        page.click("#viewMenu")
        page.locator('[data-theme-choice="dark"]').click()
        expect(page.locator("html")).to_have_attribute("data-theme", "dark")

    def test_phone_width_has_no_sideways_scroll(self):
        page = self.new_page(viewport={"width": 390, "height": 800})
        self.add_task(page, "A task with a fairly long title that has to wrap on a phone screen")
        self.assertLessEqual(page.evaluate("document.documentElement.scrollWidth"), 390)


if __name__ == "__main__":
    unittest.main()
