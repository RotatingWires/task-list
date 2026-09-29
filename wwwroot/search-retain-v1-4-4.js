// v1.4.4: keep the chosen Search list across new searches while the dialog stays open.
// Opening Search starts a fresh session and still resets the filter to All lists.
(() => {
  let pendingSearchListFilterValue = 'all';
  const baseResetSearchListFilter = resetSearchListFilter;
  const baseConfigureSearchListFilter = configureSearchListFilter;

  resetSearchListFilter = function resetSearchListFilterV144() {
    pendingSearchListFilterValue = searchDialog.open ? searchListFilterValue : 'all';
    baseResetSearchListFilter();
  };

  configureSearchListFilter = function configureSearchListFilterV144(results) {
    const desiredValue = pendingSearchListFilterValue;
    baseConfigureSearchListFilter(results);

    if (desiredValue === 'all') return;

    const desiredChoice = [...searchListFilterDropdown.querySelectorAll('[data-value]')]
      .find(choice => choice.dataset.value === desiredValue);
    if (!desiredChoice) return;

    searchListFilterValue = desiredValue;
    searchListFilterLabel.textContent = desiredChoice.querySelector('.menu-label')?.textContent ?? 'All lists';

    for (const choice of searchListFilterDropdown.querySelectorAll('[data-value]')) {
      const selected = choice.dataset.value === searchListFilterValue;
      choice.setAttribute('aria-checked', selected ? 'true' : 'false');
      choice.querySelector('.menu-check').textContent = selected ? '•' : '';
    }
  };
})();
