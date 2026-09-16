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
const editCompleted = document.querySelector('#editCompleted');
const cancelEdit = document.querySelector('#cancelEdit');
const newTaskMenu = document.querySelector('#newTaskMenu');
const aboutMenu = document.querySelector('#aboutMenu');

let tasks = [];
let editingId = null;

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

function renderTasks() {
  taskList.replaceChildren();
  emptyState.hidden = tasks.length > 0;

  for (const task of tasks) {
    const row = document.createElement('tr');
    if (task.completed) row.classList.add('completed');

    const idCell = document.createElement('td');
    idCell.dataset.label = 'ID';
    idCell.textContent = `#${task.id}`;

    const titleCell = document.createElement('td');
    titleCell.dataset.label = 'Task';
    titleCell.className = 'task-title';
    titleCell.textContent = task.title;

    const statusCell = document.createElement('td');
    statusCell.dataset.label = 'Status';
    statusCell.textContent = task.completed ? 'Done' : 'Open';

    const actionsCell = document.createElement('td');
    actionsCell.dataset.label = 'Actions';
    actionsCell.className = 'task-actions';

    const toggleButton = document.createElement('button');
    toggleButton.type = 'button';
    toggleButton.textContent = task.completed ? 'Reopen' : 'Complete';
    toggleButton.addEventListener('click', () => updateTask(task.id, { completed: !task.completed }));

    const editButton = document.createElement('button');
    editButton.type = 'button';
    editButton.textContent = 'Edit';
    editButton.addEventListener('click', () => openEdit(task));

    const deleteButton = document.createElement('button');
    deleteButton.type = 'button';
    deleteButton.textContent = 'Delete';
    deleteButton.addEventListener('click', () => deleteTask(task));

    actionsCell.append(toggleButton, editButton, deleteButton);
    row.append(idCell, titleCell, statusCell, actionsCell);
    taskList.append(row);
  }

  const open = tasks.filter(t => !t.completed).length;
  taskCount.textContent = `${open} open / ${tasks.length} total`;
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
      body: JSON.stringify({ title })
    });
    newTaskTitle.value = '';
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
  editCompleted.checked = task.completed;
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
  editDialog.close();
  editingId = null;
  await updateTask(id, { title, completed: editCompleted.checked });
});

cancelEdit.addEventListener('click', () => {
  editDialog.close();
  editingId = null;
});

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

newTaskMenu.addEventListener('click', () => newTaskTitle.focus());
aboutMenu.addEventListener('click', () => alert('RetroTodo v0.1\nSelf-hosted, minimal, and deliberately boring.'));

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => {});
}

loadTasks();
