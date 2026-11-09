/* Lists: choosing, creating, managing, and archiving them. */
import { $, api, appendActions, archivedLists, archivesBody, archivesDialog, archivesTitleText, archivesUidTotal, cancelListEdit, clearDeepLink, confirmAction, currentListId, currentView, editingList, el, fileDropdown, highestUniversalId, jsonApi, listDescription, listDialog, listDialogTitle, listForm, listName, lists, manageListsBody, manageListsDialog, manageListsTitleText, manageListsUidTotal, openArchivesButton, setArchivedLists, setCurrentListId, setCurrentView, setEditingList, setHighestUniversalId, setLists, setStatus, titleListName } from './app.js';
import { loadTasks, updateViewMenu } from './tasks.js';
import { clearTaskSelection, selectionMenuCommand } from './bulk.js';
import { openTransfer } from './transfer.js';
import { openFreshStart } from './fresh-start.js';
import { openSearch } from './search.js';
import { closeFileMenu } from './ui.js';

export async function loadStats() {
  const stats = await api('/api/stats');
  setHighestUniversalId(Number(stats.highestUniversalId) || 0);
  manageListsUidTotal.textContent = `${highestUniversalId.toLocaleString()} lifetime entries counting deletions`;
}

export function currentList() {
  return [...lists, ...archivedLists].find(list => list.id === currentListId) ?? null;
}

export async function loadLists(preferredId = null) {
  const [activeLists, archived] = await Promise.all([
    api('/api/lists'),
    api('/api/lists/archives')
  ]);
  setLists(activeLists);
  setArchivedLists(archived);
  if (!lists.length) throw new Error('No active task lists exist.');

  const candidate = preferredId ?? currentListId;
  const knownLists = [...lists, ...archivedLists];
  if (!knownLists.some(list => list.id === candidate))
    setCurrentListId(lists[0].id);
  else
    setCurrentListId(candidate);

  localStorage.setItem('task-list-current-list', String(currentListId));
  renderFileMenu();
  updateListTitle();
  if (manageListsDialog.open) renderManageLists();
  if (archivesDialog.open) renderArchives();
}

export function updateListTitle() {
  const list = currentList();
  titleListName.textContent = list ? list.name : '';
}

export function renderFileMenu() {
  fileDropdown.replaceChildren();

  for (const list of lists) {
    const button = el('button');
    button.setAttribute('role', 'menuitemradio');
    button.setAttribute('aria-checked', list.id === currentListId ? 'true' : 'false');
    button.className = 'list-choice';

    const check = el('span', 'menu-check');
    check.textContent = list.id === currentListId ? '•' : '';

    const label = el('span', 'menu-label');
    label.textContent = list.name;
    label.title = list.description || list.name;

    button.append(check, label);
    button.addEventListener('click', () => selectList(list.id));
    fileDropdown.append(button);
  }

  const separator = el('div', 'menu-separator');
  separator.setAttribute('role', 'separator');
  fileDropdown.append(separator);

  // The same groups and order as the other apps' File menus: everyday commands, Import/Export..., display options,
  // Log Out, then Fresh Start... apart from everything else because it permanently clears data.
  const separator2 = () => {
    const line = el('div', 'menu-separator');
    line.setAttribute('role', 'separator');
    return line;
  };
  fileDropdown.append(
    menuCommand('Refresh', refreshApp),
    menuCommand('Manage Lists...', openManageLists),
    menuCommand('Search...', openSearch),
    selectionMenuCommand(),
    separator2(),
    menuCommand('Import/Export...', openTransfer),
    separator2(),
    rowHoverMenuCommand(),
    separator2(),
    menuCommand('Log Out', logOut),
    separator2(),
    menuCommand('Fresh Start...', openFreshStart)
  );
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

export function menuCommand(label, handler) {
  const button = el('button');
  button.setAttribute('role', 'menuitem');
  button.append(
    document.createElement('span'),
    Object.assign(document.createElement('span'), { textContent: label })
  );
  button.addEventListener('click', handler);
  return button;
}

async function selectList(id) {
  clearTaskSelection();
  clearDeepLink();
  setCurrentListId(id);
  localStorage.setItem('task-list-current-list', String(id));
  setCurrentView('open');
  updateViewMenu();
  renderFileMenu();
  updateListTitle();
  closeFileMenu();
  await loadTasks();
}

export async function refreshListCounts() {
  try {
    await loadLists(currentListId);
  } catch {}
}

function openCreateList() {
  closeFileMenu();
  setEditingList(null);
  listDialogTitle.textContent = 'Create a List';
  listName.value = '';
  listDescription.value = '';
  listDialog.showModal();
  listName.focus();
}

function openEditList(list) {
  setEditingList(list);
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
      setEditingList(null);
      await loadLists(currentListId);
      if (manageListsDialog.open) renderManageLists();
      setStatus('List updated.');
    } else {
      const created = await jsonApi('/api/lists', 'POST', { name, description });
      await loadLists(created.id);
      setCurrentView('open');
      updateViewMenu();
      await loadTasks();
      if (manageListsDialog.open) renderManageLists();
      setStatus('List created.');
    }
  } catch (error) {
    setStatus(`Error: ${error.message}`);
    if (!listDialog.open) listDialog.showModal();
  }
});

cancelListEdit.addEventListener('click', () => {
  setEditingList(null);
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
  manageListsTitleText.textContent = `Manage Lists (${lists.length.toLocaleString()})`;
  manageListsBody.replaceChildren();

  for (const list of lists) {
    const row = el('div', 'manage-list-row');

    const details = el('div', 'manage-list-details');

    const heading = el('strong');
    heading.textContent = list.name;
    if (list.id === currentListId) heading.append(' (current)');

    const description = el('div', 'manage-list-description');
    description.textContent = list.description || 'No description';

    const count = el('div', 'manage-list-count', `${list.taskCount} task${list.taskCount === 1 ? '' : 's'}`);

    details.append(heading, description, count);

    const actions = el('div', 'manage-list-actions');
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
  const confirmed = await confirmAction({
    title: 'Archive List',
    message: `Archive list "${list.name}"?`,
    detail: 'It will be hidden from File and Manage Lists, but its tasks will be preserved.',
    confirmLabel: 'Archive'
  });
  if (!confirmed) return;

  try {
    await api(`/api/lists/${list.id}/archive`, { method: 'POST' });
    if (currentListId === list.id) setCurrentListId(null);
    await loadLists(currentListId);
    await loadTasks();
    renderManageLists();
    setStatus('List archived.');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function renderArchives() {
  archivesTitleText.textContent = `Archives (${archivedLists.length.toLocaleString()})`;
  archivesUidTotal.textContent = manageListsUidTotal.textContent;
  archivesBody.replaceChildren();

  if (!archivedLists.length) {
    const empty = el('div', 'empty-state', 'No archived lists.');
    archivesBody.append(empty);
    return;
  }

  for (const list of archivedLists) {
    const row = el('div', 'manage-list-row');

    const details = el('div', 'manage-list-details');
    const heading = el('strong');
    heading.textContent = list.name;
    if (list.id === currentListId) heading.append(' (current)');
    const description = el('div', 'manage-list-description');
    description.textContent = list.description || 'No description';
    const count = el('div', 'manage-list-count', `${list.taskCount} task${list.taskCount === 1 ? '' : 's'}`);
    details.append(heading, description, count);

    const actions = el('div', 'manage-list-actions');
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
  setCurrentListId(list.id);
  localStorage.setItem('task-list-current-list', String(currentListId));
  setCurrentView('open');
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
  const detailParts = [];
  if (list.taskCount > 0)
    detailParts.push(`This also deletes all ${list.taskCount} task${list.taskCount === 1 ? '' : 's'} and their subtasks in this list.`);
  detailParts.push('Recorded task history is preserved for Stats.');

  const confirmed = await confirmAction({
    title: 'Delete List',
    message: `Delete list "${list.name}"?`,
    detail: detailParts.join('\n\n'),
    confirmLabel: 'Delete'
  });
  if (!confirmed) return;

  try {
    await api(`/api/lists/${list.id}`, { method: 'DELETE' });
    if (currentListId === list.id) setCurrentListId(null);
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
// Lists are created from Manage Lists, next to Archives, so the File menu keeps only everyday commands.
$('#createListButton').addEventListener('click', openCreateList);

/** File > Highlight rows on hover, like SpendTracker and TaskList Stats; saved in this browser. */
const ROW_HOVER_KEY = 'task-list-highlight-rows';

function rowHoverEnabled() {
  try {
    return localStorage.getItem(ROW_HOVER_KEY) !== 'false';
  } catch {
    return true;
  }
}

function applyRowHover() {
  document.documentElement.classList.toggle('row-hover-off', !rowHoverEnabled());
}

function rowHoverMenuCommand() {
  const button = menuCommand('Highlight rows on hover', () => {
    try { localStorage.setItem(ROW_HOVER_KEY, String(!rowHoverEnabled())); } catch {}
    applyRowHover();
    closeFileMenu();
  });
  button.setAttribute('role', 'menuitemcheckbox');
  button.setAttribute('aria-checked', String(rowHoverEnabled()));
  button.firstElementChild.className = 'menu-check';
  return button;
}

applyRowHover();
