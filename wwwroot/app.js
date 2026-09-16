const taskList = document.querySelector('#taskList');
const emptyState = document.querySelector('#emptyState');
const statusText = document.querySelector('#statusText');
const taskCount = document.querySelector('#taskCount');
const newTaskForm = document.querySelector('#newTaskForm');
const newTaskTitle = document.querySelector('#newTaskTitle');
const importButton = document.querySelector('#importButton');
const importMenu = document.querySelector('#importMenu');
const markdownFile = document.querySelector('#markdownFile');
const editDialog = document.querySelector('#editDialog');
const editForm = document.querySelector('#editForm');
const editTaskId = document.querySelector('#editTaskId');
const editTitle = document.querySelector('#editTitle');
const editDescription = document.querySelector('#editDescription');
const cancelEdit = document.querySelector('#cancelEdit');
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
let editingId = null;
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
    const row = document.createElement('tr');
    row.classList.add(`status-${task.status.toLowerCase()}`);

    const idCell = document.createElement('td');
    idCell.dataset.label = 'ID';

    const idButton = document.createElement('button');
    idButton.type = 'button';
    idButton.className = 'task-id-link';
    idButton.textContent = `#${task.id}`;
    idButton.title = `View information for task #${task.id}`;
    idButton.addEventListener('click', () => openInfo(task));
    idCell.append(idButton);

    const titleCell = document.createElement('td');
    titleCell.dataset.label = 'Task';
    titleCell.className = 'task-title';
    titleCell.textContent = task.title;

    const statusCell = document.createElement('td');
    statusCell.dataset.label = 'Status';
    statusCell.textContent = task.status;

    const actionsCell = document.createElement('td');
    actionsCell.dataset.label = 'Actions';
    actionsCell.className = 'task-actions';

    if (task.status === 'Open') {
      actionsCell.append(
        actionButton('Complete', () => updateTask(task.id, { status: 'Done' })),
        actionButton('Cancel', () => updateTask(task.id, { status: 'Cancelled' }))
      );
    } else if (task.status === 'Done') {
      actionsCell.append(
        actionButton('Reopen', () => updateTask(task.id, { status: 'Open' })),
        actionButton('Cancel', () => updateTask(task.id, { status: 'Cancelled' }))
      );
    } else if (task.status === 'Cancelled') {
      actionsCell.append(
        actionButton('Reopen', () => updateTask(task.id, { status: 'Open' }))
      );
    }

    actionsCell.append(
      actionButton('Edit', () => openEdit(task)),
      actionButton('Delete', () => deleteTask(task))
    );

    row.append(idCell, titleCell, statusCell, actionsCell);
    taskList.append(row);
  }

  const open = tasks.filter(task => task.status === 'Open').length;
  const done = tasks.filter(task => task.status === 'Done').length;
  const cancelled = tasks.filter(task => task.status === 'Cancelled').length;
  taskCount.textContent = `${open} open / ${done} done / ${cancelled} cancelled / ${tasks.length} total`;
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

async function updateTask(id, changes) {
  setStatus(`Updating #${id}...`);
  try {
    await api(`/api/tasks/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(changes)
    });
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function openEdit(task) {
  editingId = task.id;
  editTaskId.textContent = `#${task.id}`;
  editTitle.value = task.title;
  editDescription.value = task.description ?? '';
  editDialog.showModal();
  editTitle.focus();
  editTitle.select();
}

editForm.addEventListener('submit', async event => {
  event.preventDefault();
  if (editingId === null) return;

  const title = editTitle.value.trim();
  if (!title) return;

  const id = editingId;
  const description = editDescription.value.trim();
  editDialog.close();
  editingId = null;
  await updateTask(id, { title, description });
});

cancelEdit.addEventListener('click', () => {
  editDialog.close();
  editingId = null;
});

function openInfo(task) {
  infoTaskId.textContent = `#${task.id}`;
  infoTitle.textContent = task.title;
  infoDescription.textContent = task.description || '—';
  infoStatus.textContent = task.status;
  infoCreated.textContent = formatDate(task.createdAt);
  infoUpdated.textContent = formatDate(task.updatedAt);
  infoCompleted.textContent = formatDate(task.completedAt);
  infoCancelled.textContent = formatDate(task.cancelledAt);
  infoReopened.textContent = formatDate(task.reopenedAt);
  infoDialog.showModal();
}

function formatDate(value) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

closeInfo.addEventListener('click', () => infoDialog.close());

async function deleteTask(task) {
  if (!confirm(`Delete task #${task.id}?\n\n${task.title}`)) return;
  setStatus(`Deleting #${task.id}...`);
  try {
    await api(`/api/tasks/${task.id}`, { method: 'DELETE' });
    await loadTasks();
  } catch (error) {
    setStatus(`Error: ${error.message}`);
  }
}

function chooseMarkdown() {
  markdownFile.value = '';
  markdownFile.click();
}

importButton.addEventListener('click', chooseMarkdown);
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
    setStatus(`Imported ${result.imported} task${result.imported === 1 ? '' : 's'}.`);
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
    choice.querySelector('.menu-check').textContent = selected ? '✓' : '';
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
aboutMenu.addEventListener('click', () => alert('RetroTodo v0.2\nSelf-hosted, minimal, and deliberately boring.'));

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

updateViewMenu();
loadTasks();
