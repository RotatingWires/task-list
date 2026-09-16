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
const closeManageLists = document.querySelector('#closeManageLists');

let lists = [];
let tasks = [];
let editingItem = null;
let subtaskParent = null;
let editingList = null;
let currentView = 'open';
let currentListId = Number(localStorage.getItem('task-list-current-list')) || null;

function setStatus(message) {
  statusText.textContent = message;
}

async function api(url, options = {}) {
  const response = await fetch(url, options);
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
  document.title = list ? `Task List — ${list.name}` : 'Task List';
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
    menuCommand('Manage Lists...', openManageLists)
  );
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

  for (const task of visibleTasks) {
    taskList.append(createTaskRow(task, false));
    for (const subtask of task.subtasks ?? [])
      taskList.append(createTaskRow(subtask, true));
  }

  const open = tasks.filter(task => task.status === 'Open').length;
  const done = tasks.filter(task => task.status === 'Done').length;
  const cancelled = tasks.filter(task => task.status === 'Cancelled').length;
  const subtaskTotal = tasks.reduce((sum, task) => sum + (task.subtasks?.length ?? 0), 0);
  taskCount.textContent = `${open} open / ${done} done / ${cancelled} cancelled / ${tasks.length} tasks / ${subtaskTotal} subtasks`;
}

function createTaskRow(item, isSubtask) {
  const row = document.createElement('tr');
  row.classList.add(`status-${item.status.toLowerCase()}`);
  if (isSubtask) row.classList.add('subtask-row');

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
  titleCell.textContent = item.title;

  const statusCell = document.createElement('td');
  statusCell.dataset.label = 'Status';
  statusCell.textContent = item.status;

  const actionsCell = document.createElement('td');
  actionsCell.dataset.label = 'Actions';
  actionsCell.className = 'task-actions';

  if (item.status === 'Open') {
    actionsCell.append(
      actionButton('Complete', () => updateItem(item, { status: 'Done' })),
      actionButton('Cancel', () => updateItem(item, { status: 'Cancelled' }))
    );
  } else if (item.status === 'Done') {
    actionsCell.append(
      actionButton('Reopen', () => updateItem(item, { status: 'Open' })),
      actionButton('Cancel', () => updateItem(item, { status: 'Cancelled' }))
    );
  } else if (item.status === 'Cancelled') {
    actionsCell.append(actionButton('Reopen', () => updateItem(item, { status: 'Open' })));
  }

  if (!isSubtask)
    actionsCell.append(actionButton('Add Subtask', () => openSubtaskDialog(item)));

  actionsCell.append(
    actionButton('Edit', () => openEdit(item)),
    actionButton('Delete', () => deleteItem(item))
  );

  row.append(idCell, titleCell, statusCell, actionsCell);
  return row;
}

function actionButton(label, handler) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = label;
  button.addEventListener('click', handler);
  return button;
}

newTaskForm.addEventListener('submit', async event => {
  event.preventDefault();
  const title = newTaskTitle.value.trim();
  if (!title || !currentListId) return;

  setStatus('Adding task...');
  try {
    await api(`/api/lists/${currentListId}/tasks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, description: '' })
    });
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
  return item.isSubtask ? `/api/subtasks/${item.id}` : `/api/tasks/${item.id}`;
}

async function updateItem(item, changes) {
  setStatus(`Updating #${item.displayId}...`);
  try {
    await api(itemEndpoint(item), {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(changes)
    });
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
  subtaskParentId.textContent = `#${parent.displayId}`;
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
    await api(`/api/tasks/${parent.id}/subtasks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, description })
    });
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

cancelSubtask.addEventListener('click', () => {
  subtaskDialog.close();
  subtaskParent = null;
});

function openInfo(item) {
  infoTaskId.textContent = `#${item.displayId}`;
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
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

closeInfo.addEventListener('click', () => infoDialog.close());

async function deleteItem(item) {
  const subtaskCount = item.isSubtask ? 0 : (item.subtasks?.length ?? 0);
  const extra = subtaskCount > 0
    ? `\n\nThis will also delete ${subtaskCount} subtask${subtaskCount === 1 ? '' : 's'}.`
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
      await api(`/api/lists/${wasEditing.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, description })
      });
      editingList = null;
      await loadLists(currentListId);
      if (manageListsDialog.open) renderManageLists();
      setStatus('List updated.');
    } else {
      const created = await api('/api/lists', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, description })
      });
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

function openManageLists() {
  closeFileMenu();
  renderManageLists();
  manageListsDialog.showModal();
}

function renderManageLists() {
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
    actions.append(
      actionButton('Edit', () => openEditList(list)),
      actionButton('Delete', () => deleteList(list))
    );
    if (lists.length === 1)
      actions.lastElementChild.disabled = true;

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

aboutMenu.addEventListener('click', () => alert('Task List v0.7\nSelf-hosted, minimal, and deliberately boring.'));

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
