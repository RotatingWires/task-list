(() => {
  'use strict';

  function normalizeTitle() {
    if (document.title.includes('Task List'))
      document.title = document.title.replaceAll('Task List', 'TaskList');
  }

  normalizeTitle();
  const titleElement = document.querySelector('title');
  if (titleElement) {
    new MutationObserver(normalizeTitle).observe(titleElement, {
      childList: true,
      subtree: true,
      characterData: true
    });
  }

  const title = document.querySelector('.title-app-name');
  if (title) title.textContent = 'TaskList v1.3.4';

  const help = document.querySelector('#aboutMenu');
  if (help) {
    help.addEventListener('click', event => {
      event.preventDefault();
      event.stopImmediatePropagation();
      alert('Tasks with dates of "Unknown" were imported from a third party application, and have no data regarding those dates.\n\nabout.lehighradio.com\nTaskList v1.3.4');
    }, true);
  }
})();
