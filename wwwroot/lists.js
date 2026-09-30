'use strict';

async function loadStats() {
  const stats = await api('/api/stats');
  highestUniversalId = Number(stats.highestUniversalId) || 0;
  manageListsUidTotal.textContent = `${highestUniversalId.toLocaleString('en-US')} lifetime entries counting deletions`;
}

function currentList() {
  return [...lists, ...archivedLists].find(list => list.id === currentListId) ?? null;
}

async function loadLists(preferredId = null) {
  [lists, archivedLists] = await Promise.all([
    api('/api/lists'),
    api('/api/lists/archives')
  ]);
  if (!lists.length) throw new Error('No active task lists exist.');

  const candidate = preferredId ?? currentListId;
  const knownLists = [...lists, ...archivedLists];
  if (!knownLists.some(list => list.id === candidate))
    currentListId = lists[0].id;
  else
    currentListId = candidate;

  localStorage.setItem('task-list-current-list', String(currentListId));
  renderFileMenu();
  updateListTitle();
  if (manageListsDialog.open) renderManageLists();
  if (archivesDialog.open) renderArchives();
}

function updateListTitle() {
  const list = currentList();
  titleListName.textContent = list ? list.name : '';
  document.title = list ? `TaskList — ${list.name}` : 'TaskList';
}

function renderFileMenu() {
  fileDropdown.replaceChildren();

  for (const list of lists) {
    const button = document.createElement('button');
    button.type = 'button';
    button.setAttribute('role', 'menuitemradio');
    button.setAttribute('aria-checked', list.id === currentListId ? 'true' : 'false');
    button.className = 'list-choice';

    const check = document.createElement('span');
    check.className = 'menu-check';
    check.textContent = list.id === currentListId ? '•' : '';

    const label = document.createElement('span');
    label.className = 'menu-label';
    label.textContent = list.name;
    label.title = list.description || list.name;

    button.append(check, label);
    button.addEventListener('click', () => selectList(list.id));
    fileDropdown.append(button);
  }

  const separator = document.createElement('div');
  separator.className = 'menu-separator';
  separator.setAttribute('role', 'separator');
  fileDropdown.append(separator);

  fileDropdown.append(
    menuCommand('Refresh', refreshApp),
    menuCommand('Create a List...', openCreateList),
    menuCommand('Manage Lists...', openManageLists),
    menuCommand('Search...', openSearch)
  );

  const accountSeparator = document.createElement('div');
  accountSeparator.className = 'menu-separator';
  accountSeparator.setAttribute('role', 'separator');
  fileDropdown.append(accountSeparator, menuCommand('Log Out', logOut));
}

function refreshApp() {
  closeFileMenu();
  window.location.reload();
}

async function logOut() {
  closeFileMenu();
  try {
    await api('/api/auth/logout', { method: 'POST' });
  } finally {
    window.location.replace('/login.html');
  }
}

function menuCommand(label, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.setAttribute('role', 'menuitem');
  button.append(
    document.createElement('span'),
    Object.assign(document.createElement('span'), { textContent: label })
  );
  button.addEventListener('click', handler);
  return button;
}

async function selectList(id) {
  clearDeepLink();
  currentListId = id;
  localStorage.setItem('task-list-current-list', String(id));
  currentView = 'open';
  updateViewMenu();
  renderFileMenu();
  updateListTitle();
  closeFileMenu();
  await loadTasks();
}

async function refreshListCounts() {
  try {
    await loadLists(currentListId);
  } catch {}
}

function openCreateList() {
  closeFileMenu();
  editingList = null;
  listDialogTitle.textContent = 'Create a List';
  listName.value = '';
  listDescription.value = '';
  listDialog.showModal();
  listName.focus();
}

function openEditList(list) {
  editingList = list;
  listDialogTitle.textContent = 'Edit List';
  listName.value = list.name;
  listDescription.value = list.description ?? '';
  listDialog.showModal();
  listName.focus();
  listName.select();
}

listForm.addEventListener('submit', async event => {
  event.preventDefault();
  const name = listName.value.trim();
  if (!name) return;

  const description = listDescription.value.trim();
  const wasEditing = editingList;
  listDialog.close();

  try {
    if (wasEditing) {
      await jsonApi(`/api/lists/${wasEditing.id}`, 'PATCH', { name, description });
      editingList = null;
      await loadLists(currentListId);
      if (manageListsDialog.open) renderManageLists();
      setStatus('List updated.');
    } else {
      const created = await jsonApi('/api/lists', 'POST', { name, description });
      await loadLists(created.id);
      currentView = 'open';
      updateViewMenu();
      await loadTasks();
      setStatus('List created.');
    }
  } catch (error) {
    setStatus(`Error: ${error.message}`);
    if (!listDialog.open) listDialog.showModal();
  }
});

cancelListEdit.addEventListener('click', () => {
  listDialog.close();
  editingList = null;
});

async function openManageLists() {
  closeFileMenu();
  try {
    await loadStats();
  } catch {
    manageListsUidTotal.textContent = '? lifetime entries counting deletions';
  }
  renderManageLists();
  manageListsDialog.showModal();
}

function renderManageLists() {
  manageListsTitleText.textContent = `Manage Lists (${lists.length.toLocaleString('en-US')})`;
  manageListsBody.replaceChildren();

  for (const list of lists) {
    const row = document.createElement('div');
    row.className = 'manage-list-row';

    const details = document.createElement('div');
    details.className = 'manage-list-details';

    const heading = document.createElement('strong');
    heading.textContent = list.name;
    if (list.id === currentListId) heading.append(' (current)');

    const description = document.createElement('div');
    description.className = 'manage-list-description';
    description.textContent = list.description || 'No description';

    const count = document.createElement('div');
    count.className = 'manage-list-count';
    count.textContent = `${list.taskCount} task${list.taskCount === 1 ? '' : 's'}`;

    details.append(heading, description, count);

    const actions = document.createElement('div');
    actions.className = 'manage-list-actions';
    appendActions(actions, [
      ['Edit', () => openEditList(list)],
      ['Archive', () => archiveList(list), lists.length === 1],
      ['Delete', () => deleteList(list), lists.length === 1]
    ]);

    row.append(details, actions);
    manageListsBody.append(row);
  }
}

async function archiveList(list) {
  if (lists.length === 1) return;
  if (!confirm(`Archive list "${list.name}"?\n\nIt will be hidden from File and Manage Lists, but its tasks will be preserved.`)) return;

  try {
    await api(`/api/lists/${list.id}/archive`, { method: 'POST' });
    if (currentListId === list.id) currentListId = null;
    await loadLists(currentListId);
    await loadTasks();
    renderManageLists();
    setStatus('List archived.');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function renderArchives() {
  archivesTitleText.textContent = `Archives (${archivedLists.length.toLocaleString('en-US')})`;
  archivesUidTotal.textContent = manageListsUidTotal.textContent;
  archivesBody.replaceChildren();

  if (!archivedLists.length) {
    const empty = document.createElement('div');
    empty.className = 'empty-state';
    empty.textContent = 'No archived lists.';
    archivesBody.append(empty);
    return;
  }

  for (const list of archivedLists) {
    const row = document.createElement('div');
    row.className = 'manage-list-row';

    const details = document.createElement('div');
    details.className = 'manage-list-details';
    const heading = document.createElement('strong');
    heading.textContent = list.name;
    if (list.id === currentListId) heading.append(' (current)');
    const description = document.createElement('div');
    description.className = 'manage-list-description';
    description.textContent = list.description || 'No description';
    const count = document.createElement('div');
    count.className = 'manage-list-count';
    count.textContent = `${list.taskCount} task${list.taskCount === 1 ? '' : 's'}`;
    details.append(heading, description, count);

    const actions = document.createElement('div');
    actions.className = 'manage-list-actions';
    appendActions(actions, [
      ['View', () => viewArchivedList(list)],
      ['Restore', () => restoreList(list)]
    ]);

    row.append(details, actions);
    archivesBody.append(row);
  }
}

async function openArchives() {
  try {
    await loadStats();
  } catch {}
  renderArchives();
  manageListsDialog.close();
  archivesDialog.showModal();
}

async function viewArchivedList(list) {
  archivesDialog.close();
  manageListsDialog.close();
  clearDeepLink();
  currentListId = list.id;
  localStorage.setItem('task-list-current-list', String(currentListId));
  currentView = 'open';
  updateViewMenu();
  renderFileMenu();
  updateListTitle();
  await loadTasks();
  setStatus(`Viewing archived list ${list.name}.`);
}

async function restoreList(list) {
  try {
    await api(`/api/lists/${list.id}/restore`, { method: 'POST' });
    await loadLists(currentListId);
    renderArchives();
    setStatus('List restored.');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

async function deleteList(list) {
  if (!list.archived && lists.length === 1) return;
  const warning = list.taskCount > 0
    ? `\n\nThis also deletes all ${list.taskCount} task${list.taskCount === 1 ? '' : 's'} and their subtasks in this list.`
    : '';

  if (!confirm(`Delete list "${list.name}"?${warning}`)) return;

  try {
    await api(`/api/lists/${list.id}`, { method: 'DELETE' });
    if (currentListId === list.id) currentListId = null;
    await loadLists();
    await loadTasks();
    if (manageListsDialog.open) renderManageLists();
    if (archivesDialog.open) renderArchives();
    setStatus('List deleted.');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

openArchivesButton.addEventListener('click', openArchives);
closeManageLists.addEventListener('click', () => manageListsDialog.close());
closeArchives.addEventListener('click', () => archivesDialog.close());
