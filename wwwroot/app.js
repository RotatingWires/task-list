const taskList = document.querySelector('#taskList');
const emptyState = document.querySelector('#emptyState');
const statusText = document.querySelector('#statusText');
const taskCount = document.querySelector('#taskCount');
const newTaskForm = document.querySelector('#newTaskForm');
const newTaskTitle = document.querySelector('#newTaskTitle');
const importMenu = document.querySelector('#importMenu');
const markdownFile = document.querySelector('#markdownFile');
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
const newTaskMenu = document.querySelector('#newTaskMenu');
const aboutMenu = document.querySelector('#aboutMenu');
const viewMenu = document.querySelector('#viewMenu');
const viewDropdown = document.querySelector('#viewDropdown');
const viewChoices = [...viewDropdown.querySelectorAll('[data-view]')];
const infoDialog = document.querySelector('#infoDialog');
const closeInfo = document.querySelector('#closeInfo');
const infoTaskId = document.querySelector('#infoTaskId');
const infoTitle = document.querySelector('#infoTitle');
const infoDescription = document.querySelector('#infoDescription');
const infoStatus = document.querySelector('#infoStatus');
const infoCreated = document.querySelector('#infoCreated');
const infoUpdated = document.querySelector('#infoUpdated');
const infoCompleted = document.querySelector('#infoCompleted');
const infoCancelled = document.querySelector('#infoCancelled');
const infoReopened = document.querySelector('#infoReopened');

let tasks = [];
let editingItem = null;
let subtaskParent = null;
let currentView = 'open';

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

async function loadTasks() {
  setStatus('Loading...');
  try {
    tasks = await api('/api/tasks');
    renderTasks();
    setStatus('Ready');
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function tasksForCurrentView() {
  // Only the parent status decides which view the entire task group appears in.
  if (currentView === 'open') return tasks.filter(task => task.status === 'Open');
  if (currentView === 'done') return tasks.filter(task => task.status === 'Done');
  return tasks;
}

function renderTasks() {
  const visibleTasks = tasksForCurrentView();
  taskList.replaceChildren();
  emptyState.hidden = visibleTasks.length > 0;
  emptyState.textContent = currentView === 'all'
    ? 'No tasks.'
    : `No ${currentView} tasks.`;

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
    actionsCell.append(
      actionButton('Reopen', () => updateItem(item, { status: 'Open' }))
    );
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
  if (!title) return;

  setStatus('Adding task...');
  try {
    await api('/api/tasks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ title, description: '' })
    });
    newTaskTitle.value = '';
    currentView = 'open';
    updateViewMenu();
    await loadTasks();
    newTaskTitle.focus();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
});

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

    // iPhone/iPad users keep the normal Return key for new lines.
    const isAppleMobile = /iPhone|iPad|iPod/i.test(navigator.userAgent);
    if (isAppleMobile) return;

    // Ctrl+Enter (or Command+Enter) inserts a normal new line on desktop.
    if (event.ctrlKey || event.metaKey) return;

    event.preventDefault();
    form.requestSubmit();
  });
}

descriptionEnterToSave(editDescription, editForm);
descriptionEnterToSave(subtaskDescription, subtaskForm);

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
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function chooseMarkdown() {
  markdownFile.value = '';
  markdownFile.click();
}

importMenu.addEventListener('click', chooseMarkdown);

markdownFile.addEventListener('change', async () => {
  const file = markdownFile.files?.[0];
  if (!file) return;

  setStatus(`Importing ${file.name}...`);
  try {
    const markdown = await file.text();
    const result = await api('/api/import/markdown', {
      method: 'POST',
      headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
      body: markdown
    });
    await loadTasks();
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

function closeViewMenu() {
  viewDropdown.hidden = true;
  viewMenu.setAttribute('aria-expanded', 'false');
}

viewMenu.addEventListener('click', event => {
  event.stopPropagation();
  const willOpen = viewDropdown.hidden;
  viewDropdown.hidden = !willOpen;
  viewMenu.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
});

for (const choice of viewChoices)
  choice.addEventListener('click', () => setView(choice.dataset.view));

document.addEventListener('click', event => {
  if (!viewDropdown.hidden && !event.target.closest('.menu-wrap'))
    closeViewMenu();
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeViewMenu();
});

newTaskMenu.addEventListener('click', () => newTaskTitle.focus());
aboutMenu.addEventListener('click', () => alert('RetroTodo v0.4\nSelf-hosted, minimal, and deliberately boring.'));

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

updateViewMenu();
loadTasks();
