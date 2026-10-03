'use strict';

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

function prepareSearchRun() {
  const desiredFilter = searchDialog.open ? searchListFilterValue : 'all';
  searchResultSet = [];
  searchListFilterValue = 'all';
  searchListFilterLabel.textContent = 'All lists';
  searchListFilterDropdown.replaceChildren();
  searchListFilterButton.disabled = true;
  closeSearchListFilterMenu();
  searchResults.replaceChildren();
  return desiredFilter;
}

function openSearch() {
  closeFileMenu();
  setSearchTab('keyword');
  searchKeyword.value = '';
  searchDateRange.value = '';
  searchTimeRange.value = '';
  searchSummary.textContent = 'Searches all lists.';
  resetSearchListFilter();
  searchResults.replaceChildren(Object.assign(document.createElement('div'), {
    className: 'search-placeholder',
    textContent: 'Enter a keyword or date/time range to search.'
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
    else searchDateRange.focus();
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

function splitSearchRange(value) {
  const text = value.trim();
  if (!text) return null;
  const match = /^(.+?)\s*[-–—]\s*(.+)$/.exec(text);
  if (!match) return null;
  return [match[1].trim(), match[2].trim()];
}

function readSearchDateRange(value) {
  const text = value.trim();
  if (!text) return { specified: false, start: null, end: null };

  const parts = splitSearchRange(text);
  if (!parts) return null;
  const start = parseSearchDate(parts[0]);
  const end = parseSearchDate(parts[1]);
  if (start === null || end === null) return null;
  return { specified: true, start, end };
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

function parseSearchTime(value) {
  const text = value.trim().toLowerCase().replaceAll('.', '').replace(/\s+/g, '');
  if (!text) return { specified: false, minutes: null };

  const match = /^(\d{1,2})(?::(\d{1,2}))?(am|pm)?$/.exec(text);
  if (!match) return null;

  let hour = Number(match[1]);
  const minute = match[2] === undefined ? 0 : Number(match[2]);
  const meridiem = match[3] ?? null;
  if (minute > 59) return null;

  if (meridiem) {
    if (hour < 1 || hour > 12) return null;
    if (hour === 12) hour = 0;
    if (meridiem === 'pm') hour += 12;
  } else {
    if (hour < 1 || hour > 12) return null;
    if (hour === 12) hour = 0;
  }

  return { specified: true, minutes: hour * 60 + minute };
}

function readSearchTimeRange(value) {
  const text = value.trim();
  if (!text) return { specified: false, start: null, end: null };

  const parts = splitSearchRange(text);
  if (!parts) return null;
  const start = parseSearchTime(parts[0]);
  const end = parseSearchTime(parts[1]);
  if (!start?.specified || !end?.specified) return null;
  return { specified: true, start: start.minutes, end: end.minutes };
}

function creationDateTimeParts(value) {
  if (!value || value === 'Unknown') return null;

  const sourceDate = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (sourceDate) {
    return {
      dateKey: Number(sourceDate[1]) * 10000 + Number(sourceDate[2]) * 100 + Number(sourceDate[3]),
      minutes: null
    };
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return null;
  return {
    dateKey: date.getFullYear() * 10000 + (date.getMonth() + 1) * 100 + date.getDate(),
    minutes: date.getHours() * 60 + date.getMinutes()
  };
}

function matchesClockRange(minutes, startMinutes, endMinutes) {
  if (startMinutes <= endMinutes)
    return minutes >= startMinutes && minutes <= endMinutes;
  return minutes >= startMinutes || minutes <= endMinutes;
}

function matchesCreationRange(value, dateRange, timeRange) {
  const created = creationDateTimeParts(value);
  if (!created) return false;

  if (dateRange.specified && (created.dateKey < dateRange.start || created.dateKey > dateRange.end))
    return false;

  if (!timeRange.specified) return dateRange.specified;
  if (created.minutes === null) return false;
  return matchesClockRange(created.minutes, timeRange.start, timeRange.end);
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

function configureSearchListFilter(results, desiredValue = 'all') {
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

  if (desiredValue !== 'all') {
    const desiredChoice = [...searchListFilterDropdown.querySelectorAll('[data-value]')]
      .find(choice => choice.dataset.value === desiredValue);
    if (desiredChoice) {
      searchListFilterValue = desiredValue;
      searchListFilterLabel.textContent = desiredChoice.querySelector('.menu-label')?.textContent ?? 'All lists';

      for (const choice of searchListFilterDropdown.querySelectorAll('[data-value]')) {
        const selected = choice.dataset.value === searchListFilterValue;
        choice.setAttribute('aria-checked', selected ? 'true' : 'false');
        choice.querySelector('.menu-check').textContent = selected ? '•' : '';
      }
    }
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

function renderSearchResults(results, summary, desiredFilter = 'all') {
  searchResultSet = results;
  searchSummary.textContent = summary;
  configureSearchListFilter(results, desiredFilter);
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
  clearDeepLink();
  searchDialog.close();
  currentListId = result.list.id;
  localStorage.setItem('task-list-current-list', String(currentListId));
  currentView = 'all';
  updateViewMenu();
  renderFileMenu();
  updateListTitle();

  await loadTasks();
  scrollAndHighlightTask(result.item.displayId, true);
}

keywordSearchForm.addEventListener('submit', async event => {
  event.preventDefault();
  const query = searchKeyword.value.trim();

  searchSummary.textContent = 'Searching all lists...';
  const desiredFilter = prepareSearchRun();

  try {
    const allItems = await loadAllSearchItems();
    const matches = allItems
      .map(result => ({ ...result, score: keywordMatchScore(query, result.item) }))
      .filter(result => result.score > 0)
      .sort((a, b) => b.score - a.score || compareSearchCreatedNewest(a, b));

    const listCount = new Set(matches.map(result => result.list.id)).size;
    renderSearchResults(
      matches,
      `${matches.length.toLocaleString('en-US')} match${matches.length === 1 ? '' : 'es'} across ${listCount.toLocaleString('en-US')} list${listCount === 1 ? '' : 's'}.`,
      desiredFilter
    );
  } catch (error) {
    searchSummary.textContent = `Search error: ${error.message}`;
  }
});

dateSearchForm.addEventListener('submit', async event => {
  event.preventDefault();

  const dateRange = readSearchDateRange(searchDateRange.value);
  const timeRange = readSearchTimeRange(searchTimeRange.value);

  if (dateRange === null) {
    searchSummary.textContent = 'Use a date range like 10/3 - 10/8. Each date can be m/d, m/d/yy, or m/d/yyyy.';
    return;
  }

  if (timeRange === null) {
    searchSummary.textContent = 'Use a time range like 9 AM - 9 PM or 9:10 AM - 5:30 PM. Times without AM/PM are treated as AM.';
    return;
  }

  if (dateRange.specified && dateRange.start > dateRange.end) {
    searchSummary.textContent = 'Start date must be on or before End date.';
    return;
  }

  if (!dateRange.specified && !timeRange.specified) {
    searchSummary.textContent = 'Enter a date range, a time range, or both.';
    return;
  }

  searchSummary.textContent = 'Searching all lists...';
  const desiredFilter = prepareSearchRun();

  try {
    const allItems = await loadAllSearchItems();
    const matches = allItems
      .filter(result => matchesCreationRange(result.item.createdAt, dateRange, timeRange))
      .sort(compareSearchCreatedNewest);

    const timeOnly = !dateRange.specified && timeRange.specified;
    const listCount = new Set(matches.map(result => result.list.id)).size;
    const rangeDescription = timeOnly
      ? 'that time range across all dates'
      : timeRange.specified
        ? 'that date/time range'
        : 'that date range';

    renderSearchResults(
      matches,
      `${matches.length.toLocaleString('en-US')} item${matches.length === 1 ? '' : 's'} created in ${rangeDescription} across ${listCount.toLocaleString('en-US')} list${listCount === 1 ? '' : 's'}.`,
      desiredFilter
    );
  } catch (error) {
    searchSummary.textContent = `Search error: ${error.message}`;
  }
});
