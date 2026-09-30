'use strict';

function closeFileMenu() {
  fileDropdown.hidden = true;
  fileMenu.setAttribute('aria-expanded', 'false');
}

function closeViewMenu() {
  viewDropdown.hidden = true;
  viewMenu.setAttribute('aria-expanded', 'false');
}

fileMenu.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = fileDropdown.hidden;
  closeViewMenu();
  if (willOpen) renderFileMenu();
  fileDropdown.hidden = !willOpen;
  fileMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

viewMenu.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = viewDropdown.hidden;
  closeFileMenu();
  viewDropdown.hidden = !willOpen;
  viewMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

for (const choice of viewChoices)
  choice.addEventListener('click', () => setView(choice.dataset.view));

document.addEventListener('click', event => {
  if (!event.target.closest('.menu-wrap')) {
    closeFileMenu();
    closeViewMenu();
  }
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    closeFileMenu();
    closeViewMenu();
  }
});

aboutMenu.addEventListener('click', () => aboutDialog.showModal());
closeAbout.addEventListener('click', () => aboutDialog.close());

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

async function start() {
  updateViewMenu();
  loadAppVersion();

  try {
    await loadLists();

    let deepLinkError = null;
    if (hasDeepLink) {
      const target = await resolveUniversalLocation(deepLinkUniversalId);
      if (target) {
        currentListId = target.listId;
        localStorage.setItem('task-list-current-list', String(target.listId));
        currentView = 'all';
        updateViewMenu();
        renderFileMenu();
        updateListTitle();
        pendingDeepLinkDisplayId = target.displayId;
      } else {
        deepLinkError = `Task with Universal ID ${deepLinkUniversalId} was not found.`;
      }
    }

    await loadTasks();
    if (deepLinkError) setStatus(deepLinkError);
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

start();
