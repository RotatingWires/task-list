const taskList = document.querySelector('#taskList');
const emptyState = document.querySelector('#emptyState');
const statusText = document.querySelector('#statusText');
const taskCount = document.querySelector('#taskCount');
const newTaskForm = document.querySelector('#newTaskForm');
const newTaskTitle = document.querySelector('#newTaskTitle');
const importMenu = document.querySelector('#importMenu');
const markdownFile = document.querySelector('#markdownFile');

const fileMenu = document.querySelector('#fileMenu');
const fileDropdown = document.querySelector('#fileDropdown');
const viewMenu = document.querySelector('#viewMenu');
const viewDropdown = document.querySelector('#viewDropdown');
const viewChoices = [...viewDropdown.querySelectorAll('[data-view]')];
const aboutMenu = document.querySelector('#aboutMenu');
const titleListName = document.querySelector('#titleListName');

const editDialog = document.querySelector('#editDialog');
const editForm = document.querySelector('#editForm');
const editTaskId = document.querySelector('#editTaskId');
const editTitle = document.querySelector('#editTitle');
const editDescription = document.querySelector('#editDescription');
const cancelEdit = document.querySelector('#cancelEdit');

const subtaskDialog = document.querySelector('#subtaskDialog');
const subtaskForm = document.querySelector('#subtaskForm');
const subtaskParentId = document.querySelector('#subtaskParentId');
const subtaskTitle = document.querySelector('#subtaskTitle');
const subtaskDescription = document.querySelector('#subtaskDescription');
const cancelSubtask = document.querySelector('#cancelSubtask');

const infoDialog = document.querySelector('#infoDialog');
const closeInfo = document.querySelector('#closeInfo');
const infoTaskId = document.querySelector('#infoTaskId');
const infoParentTaskRow = document.querySelector('#infoParentTaskRow');
const infoParentTask = document.querySelector('#infoParentTask');
const infoUniversalId = document.querySelector('#infoUniversalId');
const infoTitle = document.querySelector('#infoTitle');
const infoDescription = document.querySelector('#infoDescription');
const infoStatus = document.querySelector('#infoStatus');
const infoCreated = document.querySelector('#infoCreated');
const infoUpdated = document.querySelector('#infoUpdated');
const infoCompleted = document.querySelector('#infoCompleted');
const infoCancelled = document.querySelector('#infoCancelled');
const infoReopened = document.querySelector('#infoReopened');

const listDialog = document.querySelector('#listDialog');
const listForm = document.querySelector('#listForm');
const listDialogTitle = document.querySelector('#listDialogTitle');
const listName = document.querySelector('#listName');
const listDescription = document.querySelector('#listDescription');
const cancelListEdit = document.querySelector('#cancelListEdit');
const manageListsDialog = document.querySelector('#manageListsDialog');
const manageListsBody = document.querySelector('#manageListsBody');
const manageListsTitleText = document.querySelector('#manageListsTitleText');
const manageListsUidTotal = document.querySelector('#manageListsUidTotal');
const closeManageLists = document.querySelector('#closeManageLists');
const searchDialog = document.querySelector('#searchDialog');
const keywordSearchTab = document.querySelector('#keywordSearchTab');
const dateSearchTab = document.querySelector('#dateSearchTab');
const keywordSearchPanel = document.querySelector('#keywordSearchPanel');
const dateSearchPanel = document.querySelector('#dateSearchPanel');
const keywordSearchForm = document.querySelector('#keywordSearchForm');
const dateSearchForm = document.querySelector('#dateSearchForm');
const searchKeyword = document.querySelector('#searchKeyword');
const searchStartDate = document.querySelector('#searchStartDate');
const searchEndDate = document.querySelector('#searchEndDate');
const searchSummary = document.querySelector('#searchSummary');
const searchListFilterButton = document.querySelector('#searchListFilterButton');
const searchListFilterLabel = document.querySelector('#searchListFilterLabel');
const searchListFilterDropdown = document.querySelector('#searchListFilterDropdown');
const searchResults = document.querySelector('#searchResults');
const closeSearch = document.querySelector('#closeSearch');

let lists = [];
let tasks = [];
let editingItem = null;
let subtaskParent = null;
let editingList = null;
let currentView = 'open';
let currentListId = Number(localStorage.getItem('task-list-current-list')) || null;
let highestUniversalId = 0;
let searchResultSet = [];
let searchListFilterValue = 'all';

const STATUS_ACTIONS = {
  Open: [['Complete', 'Done'], ['Cancel', 'Cancelled']],
  Done: [['Reopen', 'Open'], ['Cancel', 'Cancelled']],
  Cancelled: [['Reopen', 'Open']]
};

function setStatus(message) {
  statusText.textContent = message;
}

async function api(url, options = {}) {
  const response = await fetch(url, { credentials: 'same-origin', ...options });
  if (response.status === 401) {
    window.location.replace('/login.html');
    throw new Error('Your session has expired.');
  }
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body.error) message = body.error;
    } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

function jsonApi(url, method, body) {
  return api(url, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
}

async function loadStats() {
  const stats = await api('/api/stats');
  highestUniversalId = Number(stats.highestUniversalId) || 0;
  manageListsUidTotal.textContent = `${highestUniversalId.toLocaleString('en-US')} lifetime entries counting deletions`;
}

function currentList() {
  return lists.find(list => list.id === currentListId) ?? null;
}

async function loadLists(preferredId = null) {
  lists = await api('/api/lists');
  if (!lists.length) throw new Error('No task lists exist.');

  const candidate = preferredId ?? currentListId;
  if (!lists.some(list => list.id === candidate))
    currentListId = lists[0].id;
  else
    currentListId = candidate;

  localStorage.setItem('task-list-current-list', String(currentListId));
  renderFileMenu();
  updateListTitle();
  if (manageListsDialog.open) renderManageLists();
}

async function loadTasks() {
  if (!currentListId) return;

  setStatus('Loading...');
  try {
    tasks = await api(`/api/lists/${currentListId}/tasks`);
    renderTasks();
    setStatus('Ready');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
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
    menuCommand('Create a List...', openCreateList),
    menuCommand('Manage Lists...', openManageLists),
    menuCommand('Search...', openSearch)
  );

  const accountSeparator = document.createElement('div');
  accountSeparator.className = 'menu-separator';
  accountSeparator.setAttribute('role', 'separator');
  fileDropdown.append(accountSeparator, menuCommand('Log Out', logOut));
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
  button.append(document.createElement('span'), Object.assign(document.createElement('span'), { textContent: label }));
  button.addEventListener('click', handler);
  return button;
}

async function selectList(id) {
  currentListId = id;
  localStorage.setItem('task-list-current-list', String(id));
  currentView = 'open';
  updateViewMenu();
  renderFileMenu();
  updateListTitle();
  closeFileMenu();
  await loadTasks();
}

function tasksForCurrentView() {
  // Parent status controls which view the parent and all of its subtasks appear in.
  if (currentView === 'open') return tasks.filter(task => task.status === 'Open');
  if (currentView === 'done') return tasks.filter(task => task.status === 'Done');
  return tasks;
}

function renderTasks() {
  const visibleTasks = tasksForCurrentView();
  taskList.replaceChildren();
  emptyState.hidden = visibleTasks.length > 0;
  emptyState.textContent = currentView === 'all' ? 'No tasks.' : `No ${currentView} tasks.`;

  for (const task of visibleTasks)
    appendTaskTree(task, 0);

  const open = tasks.filter(task => task.status === 'Open').length;
  const done = tasks.filter(task => task.status === 'Done').length;
  const cancelled = tasks.filter(task => task.status === 'Cancelled').length;
  const subtaskTotal = tasks.reduce((sum, task) => sum + countDescendants(task), 0);
  taskCount.textContent = `${open} open / ${done} done / ${cancelled} cancelled / ${tasks.length} tasks / ${subtaskTotal} subtasks`;
}

function appendTaskTree(item, depth) {
  taskList.append(createTaskRow(item, depth));
  for (const child of item.subtasks ?? [])
    appendTaskTree(child, depth + 1);
}

function countDescendants(item) {
  return (item.subtasks ?? []).reduce((sum, child) => sum + 1 + countDescendants(child), 0);
}

function createTaskRow(item, depth) {
  const isSubtask = depth > 0;
  const row = document.createElement('tr');
  row.classList.add(`status-${item.status.toLowerCase()}`);
  if (isSubtask) row.classList.add('subtask-row');
  row.dataset.depth = String(depth);
  row.dataset.displayId = item.displayId;
  row.style.setProperty('--tree-indent', `${Math.max(0, depth - 1) * 18}px`);
  row.style.setProperty('--row-indent', `${depth * 12}px`);

  const idCell = document.createElement('td');
  idCell.dataset.label = 'ID';

  const idButton = document.createElement('button');
  idButton.type = 'button';
  idButton.className = 'task-id-link';
  idButton.textContent = `#${item.displayId}`;
  idButton.title = `View information for ${isSubtask ? 'subtask' : 'task'} #${item.displayId}`;
  idButton.addEventListener('click', () => openInfo(item));
  idCell.append(idButton);

  const titleCell = document.createElement('td');
  titleCell.dataset.label = 'Task';
  titleCell.className = 'task-title';

  if (isSubtask) {
    const tree = document.createElement('span');
    tree.className = 'task-title-tree';

    const marker = document.createElement('span');
    marker.className = 'task-tree-marker';
    marker.textContent = '└─';

    const titleText = document.createElement('span');
    titleText.className = 'task-title-text';
    titleText.textContent = item.title;
    tree.append(marker, titleText);
    titleCell.append(tree);
  } else {
    titleCell.textContent = item.title;
  }

  const statusCell = document.createElement('td');
  statusCell.dataset.label = 'Status';
  statusCell.textContent = item.status;

  const actionsCell = document.createElement('td');
  actionsCell.dataset.label = 'Actions';
  actionsCell.className = 'task-actions';

  const actions = (STATUS_ACTIONS[item.status] ?? [])
    .map(([label, status]) => [label, () => updateItem(item, { status })]);
  actions.push(['Add Subtask', () => openSubtaskDialog(item)]);
  actions.push(['Edit', () => openEdit(item)], ['Delete', () => deleteItem(item)]);
  appendActions(actionsCell, actions);

  row.append(idCell, titleCell, statusCell, actionsCell);
  return row;
}

function actionButton(label, handler, disabled = false) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = label;
  button.disabled = disabled;
  button.addEventListener('click', handler);
  return button;
}

function appendActions(container, actions) {
  container.append(...actions.map(([label, handler, disabled]) => actionButton(label, handler, disabled)));
}

newTaskForm.addEventListener('submit', async event => {
  event.preventDefault();
  const title = newTaskTitle.value.trim();
  if (!title || !currentListId) return;

  setStatus('Adding task...');
  try {
    await jsonApi(`/api/lists/${currentListId}/tasks`, 'POST', { title, description: '' });
    newTaskTitle.value = '';
    currentView = 'open';
    updateViewMenu();
    await Promise.all([loadTasks(), refreshListCounts()]);
    newTaskTitle.focus();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

async function refreshListCounts() {
  try {
    await loadLists(currentListId);
  } catch {}
}

function itemEndpoint(item) {
  return `/api/lists/${item.listId}/items/${encodeURIComponent(item.displayId)}`;
}

async function updateItem(item, changes) {
  setStatus(`Updating #${item.displayId}...`);
  try {
    await jsonApi(itemEndpoint(item), 'PATCH', changes);
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function openEdit(item) {
  editingItem = item;
  editTaskId.textContent = `#${item.displayId}`;
  editTitle.value = item.title;
  editDescription.value = item.description ?? '';
  editDialog.showModal();
  editTitle.focus();
  editTitle.select();
}

editForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (editingItem === null) return;

  const title = editTitle.value.trim();
  if (!title) return;

  const item = editingItem;
  const description = editDescription.value.trim();
  editDialog.close();
  editingItem = null;
  await updateItem(item, { title, description });
});

function descriptionEnterToSave(textarea, form) {
  textarea.addEventListener('keydown', event => {
    if (event.key !== 'Enter') return;
    if (event.shiftKey) return;
    event.preventDefault();
    form.requestSubmit();
  });
}

descriptionEnterToSave(editDescription, editForm);
descriptionEnterToSave(subtaskDescription, subtaskForm);
descriptionEnterToSave(listDescription, listForm);

cancelEdit.addEventListener('click', () => {
  editDialog.close();
  editingItem = null;
});

function openSubtaskDialog(parent) {
  subtaskParent = parent;
  subtaskParentId.textContent = `#${parent.displayId} - ${parent.title}`;
  subtaskTitle.value = '';
  subtaskDescription.value = '';
  subtaskDialog.showModal();
  subtaskTitle.focus();
}

subtaskForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (!subtaskParent) return;

  const title = subtaskTitle.value.trim();
  if (!title) return;

  const parent = subtaskParent;
  const description = subtaskDescription.value.trim();
  subtaskDialog.close();
  subtaskParent = null;

  setStatus(`Adding subtask to #${parent.displayId}...`);
  try {
    await jsonApi(`/api/lists/${parent.listId}/items/${encodeURIComponent(parent.displayId)}/subtasks`, 'POST', { title, description });
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

cancelSubtask.addEventListener('click', () => {
  subtaskDialog.close();
  subtaskParent = null;
});

function openInfo(item, sourceItems = tasks) {
  infoTaskId.textContent = `#${item.displayId}`;

  if (item.parentDisplayId) {
    const parent = findItemByDisplayId(sourceItems, item.parentDisplayId);
    infoParentTask.textContent = parent
      ? `#${parent.displayId} — ${parent.title}`
      : `#${item.parentDisplayId}`;
    infoParentTaskRow.hidden = false;
  } else {
    infoParentTask.textContent = '';
    infoParentTaskRow.hidden = true;
  }

  infoUniversalId.textContent = String(item.universalId);
  infoTitle.textContent = item.title;
  infoDescription.textContent = item.description || '—';
  infoStatus.textContent = item.status;
  infoCreated.textContent = formatDate(item.createdAt);
  infoUpdated.textContent = formatDate(item.updatedAt);
  infoCompleted.textContent = formatDate(item.completedAt);
  infoCancelled.textContent = formatDate(item.cancelledAt);
  infoReopened.textContent = formatDate(item.reopenedAt);
  infoDialog.showModal();
}

function formatDate(value) {
  if (!value) return '—';
  if (/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    const [year, month, day] = value.split('-').map(Number);
    return new Date(year, month - 1, day).toLocaleDateString();
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

closeInfo.addEventListener('click', () => infoDialog.close());

async function deleteItem(item) {
  const subtaskCount = countDescendants(item);
  const extra = subtaskCount > 0
    ? `\n\nThis will also delete ${subtaskCount} descendant subtask${subtaskCount === 1 ? '' : 's'}.`
    : '';

  if (!confirm(`Delete ${item.isSubtask ? 'subtask' : 'task'} #${item.displayId}?\n\n${item.title}${extra}`)) return;

  setStatus(`Deleting #${item.displayId}...`);
  try {
    await api(itemEndpoint(item), { method: 'DELETE' });
    await Promise.all([loadTasks(), refreshListCounts()]);
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function chooseMarkdown() {
  if (!currentListId) return;
  markdownFile.value = '';
  markdownFile.click();
}

importMenu.addEventListener('click', chooseMarkdown);

markdownFile.addEventListener('change', async () => {
  const file = markdownFile.files?.[0];
  if (!file || !currentListId) return;

  setStatus(`Importing ${file.name}...`);
  try {
    const markdown = await file.text();
    const result = await api(`/api/lists/${currentListId}/import/markdown`, {
      method: 'POST',
      headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      body: markdown
    });
    await Promise.all([loadTasks(), refreshListCounts()]);
    await loadStats().catch(() => {});
    setStatus(`Imported ${result.tasksImported} task${result.tasksImported === 1 ? '' : 's'} and ${result.subtasksImported} subtask${result.subtasksImported === 1 ? '' : 's'}.`);
  } catch (error) {
    setStatus(`Import error: ${error.message}`);
  }
});

function setView(view) {
  currentView = view;
  updateViewMenu();
  renderTasks();
  closeViewMenu();
}

function updateViewMenu() {
  for (const choice of viewChoices) {
    const selected = choice.dataset.view === currentView;
    choice.setAttribute('aria-checked', selected ? 'true' : 'false');
    choice.querySelector('.menu-check').textContent = selected ? '•' : '';
  }
}

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
      ['Delete', () => deleteList(list), lists.length === 1]
    ]);

    row.append(details, actions);
    manageListsBody.append(row);
  }
}

async function deleteList(list) {
  if (lists.length === 1) return;
  const warning = list.taskCount > 0
    ? `\n\nThis also deletes all ${list.taskCount} task${list.taskCount === 1 ? '' : 's'} and their subtasks in this list.`
    : '';

  if (!confirm(`Delete list "${list.name}"?${warning}`)) return;

  try {
    await api(`/api/lists/${list.id}`, { method: 'DELETE' });
    if (currentListId === list.id) currentListId = null;
    await loadLists();
    await loadTasks();
    renderManageLists();
    setStatus('List deleted.');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

closeManageLists.addEventListener('click', () => manageListsDialog.close());


function closeSearchListFilterMenu() {
  searchListFilterDropdown.hidden = true;
  searchListFilterButton.setAttribute('aria-expanded', 'false');
}

function resetSearchListFilter() {
  searchResultSet = [];
  searchListFilterValue = 'all';
  searchListFilterLabel.textContent = 'All lists';
  searchListFilterDropdown.replaceChildren();
  searchListFilterButton.disabled = true;
  closeSearchListFilterMenu();
}

function openSearch() {
  closeFileMenu();
  setSearchTab('keyword');
  searchKeyword.value = '';
  searchStartDate.value = '';
  searchEndDate.value = '';
  searchSummary.textContent = 'Searches all lists.';
  resetSearchListFilter();
  searchResults.replaceChildren(Object.assign(document.createElement('div'), {
    className: 'search-placeholder',
    textContent: 'Enter a keyword or date range to search.'
  }));
  searchDialog.showModal();
  searchKeyword.focus();
}

function setSearchTab(tab) {
  const keyword = tab === 'keyword';
  keywordSearchTab.setAttribute('aria-selected', keyword ? 'true' : 'false');
  dateSearchTab.setAttribute('aria-selected', keyword ? 'false' : 'true');
  keywordSearchPanel.hidden = !keyword;
  dateSearchPanel.hidden = keyword;

  if (searchDialog.open) {
    if (keyword) searchKeyword.focus();
    else searchStartDate.focus();
  }
}

keywordSearchTab.addEventListener('click', () => setSearchTab('keyword'));
dateSearchTab.addEventListener('click', () => setSearchTab('date'));
closeSearch.addEventListener('click', () => searchDialog.close());
searchDialog.addEventListener('close', closeSearchListFilterMenu);

function flattenSearchItems(items, list, depth = 0, output = [], sourceItems = items) {
  for (const item of items) {
    output.push({ item, list, depth, sourceItems });
    flattenSearchItems(item.subtasks ?? [], list, depth + 1, output, sourceItems);
  }
  return output;
}

async function loadAllSearchItems() {
  const listSnapshot = [...lists];
  const groups = await Promise.all(listSnapshot.map(async list => {
    const listTasks = await api(`/api/lists/${list.id}/tasks`);
    return flattenSearchItems(listTasks, list, 0, [], listTasks);
  }));
  return groups.flat();
}

function normalizeSearchText(value) {
  return (value ?? '')
    .toLocaleLowerCase()
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();
}

function levenshteinDistance(a, b) {
  if (a === b) return 0;
  if (!a.length) return b.length;
  if (!b.length) return a.length;

  let previous = Array.from({ length: b.length + 1 }, (_, index) => index);
  let current = new Array(b.length + 1);

  for (let i = 1; i <= a.length; i++) {
    current[0] = i;
    for (let j = 1; j <= b.length; j++) {
      current[j] = Math.min(
        current[j - 1] + 1,
        previous[j] + 1,
        previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1)
      );
    }
    [previous, current] = [current, previous];
  }

  return previous[b.length];
}

function tokenSimilarity(term, word) {
  if (term === word) return 1;
  if (!term || !word) return 0;

  // Short search terms should behave as literal substring searches rather than
  // fuzzy matches. This makes queries like "e", "re", or "hom" match words
  // that actually contain those characters, while typo tolerance stays reserved
  // for longer terms where it is useful instead of noisy.
  if (word.includes(term)) {
    if (term.length <= 3) return 1;
    return 0.9;
  }
  if (term.includes(word) && word.length >= 3) return 0.9;

  if (term.length < 4 || word.length < 4) return 0;
  const longest = Math.max(term.length, word.length);
  return 1 - (levenshteinDistance(term, word) / longest);
}

function bestTokenSimilarity(term, words) {
  let best = 0;
  for (const word of words) {
    const similarity = tokenSimilarity(term, word);
    if (similarity > best) best = similarity;
    if (best === 1) break;
  }
  return best;
}

function keywordMatchScore(query, item) {
  const q = normalizeSearchText(query);
  if (!q) return 0;

  const title = normalizeSearchText(item.title);
  const description = normalizeSearchText(item.description);
  const titleWords = title.split(' ').filter(Boolean);
  const descriptionWords = description.split(' ').filter(Boolean);
  const terms = q.split(' ').filter(Boolean);

  let score = 0;
  if (title.includes(q)) score += 300;
  else if (description.includes(q)) score += 180;

  for (const term of terms) {
    const titleScore = bestTokenSimilarity(term, titleWords);
    const descriptionScore = bestTokenSimilarity(term, descriptionWords);
    const best = Math.max(titleScore * 1.15, descriptionScore);

    const threshold = term.length <= 3 ? 0.92 : term.length <= 5 ? 0.72 : 0.64;
    if (best < threshold) return 0;
    score += best * 100;
  }

  if (title === q) score += 500;
  if (title.startsWith(q)) score += 100;
  return score;
}

function parseSearchDate(value) {
  const match = /^\s*(\d{1,2})\/(\d{1,2})(?:\/(\d{2}|\d{4}))?\s*$/.exec(value);
  if (!match) return null;

  const month = Number(match[1]);
  const day = Number(match[2]);
  let year;

  if (!match[3]) {
    year = new Date().getFullYear();
  } else {
    year = Number(match[3]);
    if (match[3].length === 2) year += 2000;
  }

  const date = new Date(year, month - 1, day);
  if (
    date.getFullYear() !== year ||
    date.getMonth() !== month - 1 ||
    date.getDate() !== day
  ) return null;

  return year * 10000 + month * 100 + day;
}

function creationDateKey(value) {
  if (!value || value === 'Unknown') return null;

  const sourceDate = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (sourceDate) {
    return Number(sourceDate[1]) * 10000 + Number(sourceDate[2]) * 100 + Number(sourceDate[3]);
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return null;
  return date.getFullYear() * 10000 + (date.getMonth() + 1) * 100 + date.getDate();
}

function compareSearchCreatedNewest(a, b) {
  const aKey = creationDateKey(a.item.createdAt) ?? 0;
  const bKey = creationDateKey(b.item.createdAt) ?? 0;
  if (aKey !== bKey) return bKey - aKey;
  return b.item.universalId - a.item.universalId;
}

function addSearchListFilterChoice(value, label) {
  const button = document.createElement('button');
  button.type = 'button';
  button.setAttribute('role', 'menuitemradio');
  button.setAttribute('aria-checked', value === searchListFilterValue ? 'true' : 'false');
  button.dataset.value = value;

  const check = document.createElement('span');
  check.className = 'menu-check';
  check.textContent = value === searchListFilterValue ? '•' : '';

  const text = document.createElement('span');
  text.className = 'menu-label';
  text.textContent = label;

  button.append(check, text);
  button.addEventListener('click', () => {
    searchListFilterValue = value;
    searchListFilterLabel.textContent = label;

    for (const choice of searchListFilterDropdown.querySelectorAll('[data-value]')) {
      const selected = choice.dataset.value === searchListFilterValue;
      choice.setAttribute('aria-checked', selected ? 'true' : 'false');
      choice.querySelector('.menu-check').textContent = selected ? '•' : '';
    }

    closeSearchListFilterMenu();
    renderFilteredSearchResults();
  });

  searchListFilterDropdown.append(button);
}

function configureSearchListFilter(results) {
  const counts = new Map();
  for (const result of results) {
    counts.set(result.list.id, (counts.get(result.list.id) ?? 0) + 1);
  }

  searchListFilterValue = 'all';
  searchListFilterLabel.textContent = 'All lists';
  searchListFilterDropdown.replaceChildren();
  addSearchListFilterChoice('all', 'All lists');

  for (const list of lists) {
    const count = counts.get(list.id);
    if (!count) continue;
    addSearchListFilterChoice(String(list.id), `${list.name} (${count.toLocaleString('en-US')})`);
  }

  searchListFilterButton.disabled = results.length === 0;
  closeSearchListFilterMenu();
}

function renderFilteredSearchResults() {
  const selected = searchListFilterValue;
  const visibleResults = selected === 'all'
    ? searchResultSet
    : searchResultSet.filter(result => String(result.list.id) === selected);

  searchResults.replaceChildren();

  if (!visibleResults.length) {
    const empty = document.createElement('div');
    empty.className = 'search-placeholder';
    empty.textContent = 'No matching tasks or subtasks.';
    searchResults.append(empty);
    return;
  }

  for (const result of visibleResults) {
    const { item, list, depth } = result;
    const row = document.createElement('div');
    row.className = 'search-result-row';

    const details = document.createElement('div');
    details.className = 'search-result-details';

    const heading = document.createElement('div');
    heading.className = 'search-result-heading';

    const location = document.createElement('button');
    location.type = 'button';
    location.className = 'task-id-link search-result-location';
    location.textContent = `${list.name} — #${item.displayId}`;
    location.title = `View information for ${depth > 0 ? 'subtask' : 'task'} #${item.displayId}`;
    location.addEventListener('click', () => openInfo(item, result.sourceItems));

    const kind = document.createElement('span');
    kind.className = 'search-result-kind';
    kind.textContent = depth > 0 ? 'Subtask' : 'Task';

    heading.append(location, kind);

    const title = document.createElement('div');
    title.className = 'search-result-title-text';
    title.textContent = item.title;

    const description = document.createElement('div');
    description.className = 'search-result-description';
    description.textContent = item.description || 'No description';

    const meta = document.createElement('div');
    meta.className = 'search-result-meta';
    meta.textContent = `${item.status} • Created ${formatDate(item.createdAt)}`;

    details.append(heading, title, description, meta);

    const actions = document.createElement('div');
    actions.className = 'search-result-actions';
    actions.append(actionButton('View', () => viewSearchResult(result)));

    row.append(details, actions);
    searchResults.append(row);
  }
}

function renderSearchResults(results, summary) {
  searchResultSet = results;
  searchSummary.textContent = summary;
  configureSearchListFilter(results);
  renderFilteredSearchResults();
}

searchListFilterButton.addEventListener('click', event => {
  event.stopPropagation();
  if (searchListFilterButton.disabled) return;
  const willOpen = searchListFilterDropdown.hidden;
  searchListFilterDropdown.hidden = !willOpen;
  searchListFilterButton.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

document.addEventListener('click', event => {
  if (!event.target.closest('.search-list-menu')) closeSearchListFilterMenu();
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeSearchListFilterMenu();
});

async function viewSearchResult(result) {
  searchDialog.close();
  currentListId = result.list.id;
  localStorage.setItem('task-list-current-list', String(currentListId));
  currentView = 'all';
  updateViewMenu();
  renderFileMenu();
  updateListTitle();

  await loadTasks();

  requestAnimationFrame(() => {
    const targetRow = [...taskList.querySelectorAll('tr[data-display-id]')]
      .find(row => row.dataset.displayId === result.item.displayId);
    if (!targetRow) return;

    targetRow.scrollIntoView({ block: 'start', inline: 'nearest' });

    // Keep the target row below the sticky desktop table header instead of
    // letting the header cover the row's top edge/actions.
    const taskPanel = targetRow.closest('.task-panel');
    const tableHead = taskPanel?.querySelector('thead');
    const headerVisible = tableHead && getComputedStyle(tableHead).display !== 'none';
    const headerHeight = headerVisible ? tableHead.getBoundingClientRect().height : 0;
    if (taskPanel && headerHeight > 0)
      taskPanel.scrollTop = Math.max(0, taskPanel.scrollTop - headerHeight - 2);
  });
}

function findItemByDisplayId(items, displayId) {
  for (const item of items) {
    if (item.displayId === displayId) return item;
    const nested = findItemByDisplayId(item.subtasks ?? [], displayId);
    if (nested) return nested;
  }
  return null;
}

keywordSearchForm.addEventListener('submit', async event => {
  event.preventDefault();
  const query = searchKeyword.value.trim();

  searchSummary.textContent = 'Searching all lists...';
  resetSearchListFilter();
  searchResults.replaceChildren();

  try {
    const allItems = await loadAllSearchItems();
    const matches = allItems
      .map(result => ({ ...result, score: keywordMatchScore(query, result.item) }))
      .filter(result => result.score > 0)
      .sort((a, b) => b.score - a.score || compareSearchCreatedNewest(a, b));

    renderSearchResults(
      matches,
      `${matches.length.toLocaleString('en-US')} match${matches.length === 1 ? '' : 'es'} across ${new Set(matches.map(result => result.list.id)).size.toLocaleString('en-US')} list${new Set(matches.map(result => result.list.id)).size === 1 ? '' : 's'}.`
    );
  } catch (error) {
    searchSummary.textContent = `Search error: ${error.message}`;
  }
});

dateSearchForm.addEventListener('submit', async event => {
  event.preventDefault();

  const start = parseSearchDate(searchStartDate.value);
  const end = parseSearchDate(searchEndDate.value);

  if (start === null || end === null) {
    searchSummary.textContent = 'Use m/d, m/d/yy, or m/d/yyyy.';
    return;
  }

  if (start > end) {
    searchSummary.textContent = 'Start date must be on or before End date.';
    return;
  }

  searchSummary.textContent = 'Searching all lists...';
  resetSearchListFilter();
  searchResults.replaceChildren();

  try {
    const allItems = await loadAllSearchItems();
    const matches = allItems
      .filter(result => {
        const created = creationDateKey(result.item.createdAt);
        return created !== null && created >= start && created <= end;
      })
      .sort(compareSearchCreatedNewest);

    renderSearchResults(
      matches,
      `${matches.length.toLocaleString('en-US')} item${matches.length === 1 ? '' : 's'} created in that range across ${new Set(matches.map(result => result.list.id)).size.toLocaleString('en-US')} list${new Set(matches.map(result => result.list.id)).size === 1 ? '' : 's'}.`
    );
  } catch (error) {
    searchSummary.textContent = `Search error: ${error.message}`;
  }
});

aboutMenu.addEventListener('click', () => alert('Tasks with dates of \"Unknown\" were imported from a third party application, and have no data regarding those dates.\n\nabout.lehighradio.com\nTaskList v1.3.5'));

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

async function start() {
  updateViewMenu();
  try {
    await loadLists();
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

start();
