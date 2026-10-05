'use strict';

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
const aboutDialog = document.querySelector('#aboutDialog');
const closeAbout = document.querySelector('#closeAbout');
const titleAppName = document.querySelector('.title-app-name');
const titleListName = document.querySelector('#titleListName');
const aboutVersion = document.querySelector('#aboutVersion');

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

const moveDialog = document.querySelector('#moveDialog');
const moveForm = document.querySelector('#moveForm');
const moveTaskId = document.querySelector('#moveTaskId');
const moveListButton = document.querySelector('#moveListButton');
const moveListLabel = document.querySelector('#moveListLabel');
const moveListDropdown = document.querySelector('#moveListDropdown');
const moveNote = document.querySelector('#moveNote');
const cancelMove = document.querySelector('#cancelMove');

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
const openArchivesButton = document.querySelector('#openArchives');
const archivesDialog = document.querySelector('#archivesDialog');
const archivesBody = document.querySelector('#archivesBody');
const archivesTitleText = document.querySelector('#archivesTitleText');
const archivesUidTotal = document.querySelector('#archivesUidTotal');
const closeArchives = document.querySelector('#closeArchives');

const searchDialog = document.querySelector('#searchDialog');
const keywordSearchTab = document.querySelector('#keywordSearchTab');
const dateSearchTab = document.querySelector('#dateSearchTab');
const keywordSearchPanel = document.querySelector('#keywordSearchPanel');
const dateSearchPanel = document.querySelector('#dateSearchPanel');
const keywordSearchForm = document.querySelector('#keywordSearchForm');
const dateSearchForm = document.querySelector('#dateSearchForm');
const searchKeyword = document.querySelector('#searchKeyword');
const searchStartDate = document.querySelector('#searchStartDate');
const searchStartTime = document.querySelector('#searchStartTime');
const searchEndDate = document.querySelector('#searchEndDate');
const searchEndTime = document.querySelector('#searchEndTime');
const searchSummary = document.querySelector('#searchSummary');
const searchListFilterButton = document.querySelector('#searchListFilterButton');
const searchListFilterLabel = document.querySelector('#searchListFilterLabel');
const searchListFilterDropdown = document.querySelector('#searchListFilterDropdown');
const searchResults = document.querySelector('#searchResults');
const closeSearch = document.querySelector('#closeSearch');

let lists = [];
let archivedLists = [];
let tasks = [];
let editingItem = null;
let subtaskParent = null;
let movingItem = null;
let moveTargetListId = null;
let editingList = null;
let currentView = 'open';
let currentListId = Number(localStorage.getItem('task-list-current-list')) || null;
let highestUniversalId = 0;
let searchResultSet = [];
let searchListFilterValue = 'all';

const deepLinkMatch = /^\/task\/(\d+)\/?$/.exec(location.pathname);
const deepLinkUniversalId = deepLinkMatch ? Number(deepLinkMatch[1]) : null;
const hasDeepLink = Number.isSafeInteger(deepLinkUniversalId) && deepLinkUniversalId > 0;
let pendingDeepLinkDisplayId = null;

const PRIMARY_STATUS_ACTION = {
  Open: ['Complete', 'Done'],
  Done: ['Reopen', 'Open'],
  Cancelled: ['Reopen', 'Open']
};

function setStatus(message) {
  statusText.textContent = message;
}

async function api(url, options = {}) {
  const method = (options.method ?? 'GET').toUpperCase();
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
  const result = response.status === 204 ? null : await response.json();
  if (method !== 'GET') queueMicrotask(() => window.TaskMilestones?.check?.());
  return result;
}

function jsonApi(url, method, body) {
  return api(url, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
}

function applyAppVersion(version) {
  if (!version) return;
  const label = `TaskList v${version}`;
  if (titleAppName) titleAppName.textContent = label;
  if (aboutVersion) aboutVersion.textContent = label;
}

function loadAppVersion() {
  applyAppVersion(window.TASKLIST_VERSION);
}

function clearDeepLink() {
  if (!/^\/task\/\d+\/?$/.test(location.pathname)) return;
  history.replaceState(null, '', '/');
}

function findUniversal(items, universalId) {
  for (const item of items) {
    if (item.universalId === universalId) return item;
    const nested = findUniversal(item.subtasks ?? [], universalId);
    if (nested) return nested;
  }
  return null;
}

function findItemByDisplayId(items, displayId) {
  for (const item of items) {
    if (item.displayId === displayId) return item;
    const nested = findItemByDisplayId(item.subtasks ?? [], displayId);
    if (nested) return nested;
  }
  return null;
}

async function resolveUniversalLocation(universalId) {
  for (const list of [...lists, ...archivedLists]) {
    const listTasks = await api(`/api/lists/${list.id}/tasks`);
    const item = findUniversal(listTasks, universalId);
    if (item) return { listId: list.id, displayId: item.displayId };
  }
  return null;
}

function scrollAndHighlightTask(displayId, highlight = true) {
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

    if (!highlight) return;
    row.classList.remove('task-visit-highlight');
    void row.offsetWidth;
    row.classList.add('task-visit-highlight');
    setTimeout(() => row.classList.remove('task-visit-highlight'), 3200);
  });
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

function descriptionEnterToSave(textarea, form) {
  textarea.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.shiftKey) return;
    event.preventDefault();
    form.requestSubmit();
  });
}

descriptionEnterToSave(editDescription, editForm);
descriptionEnterToSave(subtaskDescription, subtaskForm);
descriptionEnterToSave(listDescription, listForm);
