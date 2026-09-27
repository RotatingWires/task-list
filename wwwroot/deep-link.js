(() => {
  'use strict';

  const match = /^\/task\/(\d+)\/?$/.exec(location.pathname);
  const universalId = match ? Number(match[1]) : null;
  const hasDeepLink = Number.isSafeInteger(universalId) && universalId > 0;

  function clearDeepLink() {
    if (!/^\/task\/\d+\/?$/.test(location.pathname)) return;
    history.replaceState(null, '', '/');
  }

  function findUniversal(items, id) {
    for (const item of items) {
      if (item.universalId === id) return item;
      const nested = findUniversal(item.subtasks || [], id);
      if (nested) return nested;
    }
    return null;
  }

  async function resolveUniversalLocation(id) {
    for (const list of lists) {
      const listTasks = await api(`/api/lists/${list.id}/tasks`);
      const item = findUniversal(listTasks, id);
      if (item) return { listId: list.id, displayId: item.displayId };
    }
    return null;
  }

  function scrollAndHighlight(displayId) {
    requestAnimationFrame(() => {
      const row = [...taskList.querySelectorAll('tr[data-display-id]')]
        .find(candidate => candidate.dataset.displayId === displayId);
      if (!row) {
        setStatus('Linked task was not found.');
        return;
      }

      row.scrollIntoView({ block: 'start', inline: 'nearest' });
      const taskPanel = row.closest('.task-panel');
      const tableHead = taskPanel?.querySelector('thead');
      const headerVisible = tableHead && getComputedStyle(tableHead).display !== 'none';
      const headerHeight = headerVisible ? tableHead.getBoundingClientRect().height : 0;
      if (taskPanel && headerHeight > 0)
        taskPanel.scrollTop = Math.max(0, taskPanel.scrollTop - headerHeight - 2);

      row.style.background = '#fff4a8';
      [...row.cells].forEach(cell => { cell.style.background = '#fff4a8'; });
      setTimeout(() => {
        row.style.background = '';
        [...row.cells].forEach(cell => { cell.style.background = ''; });
      }, 3200);
    });
  }

  if (hasDeepLink) {
    const originalLoadTasks = loadTasks;
    let handled = false;

    loadTasks = async function() {
      if (!handled) {
        try {
          const target = await resolveUniversalLocation(universalId);
          if (!target) {
            handled = true;
            setStatus(`Task with Universal ID ${universalId} was not found.`);
            return originalLoadTasks();
          }

          currentListId = target.listId;
          localStorage.setItem('task-list-current-list', String(target.listId));
          currentView = 'all';
          updateViewMenu();
          renderFileMenu();
          updateListTitle();
          handled = target.displayId;
        } catch (error) {
          handled = true;
          setStatus(`Deep-link error: ${error.message}`);
        }
      }

      const result = await originalLoadTasks();
      if (typeof handled === 'string') {
        scrollAndHighlight(handled);
        handled = true;
      }
      return result;
    };
  }

  const originalSelectList = selectList;
  selectList = async function(id) {
    clearDeepLink();
    return originalSelectList(id);
  };

  const originalSetView = setView;
  setView = function(view) {
    clearDeepLink();
    return originalSetView(view);
  };
})();
