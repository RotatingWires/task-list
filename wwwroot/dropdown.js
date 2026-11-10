'use strict';

/*
 * Dropdown and menu behavior shared word for word by TaskList, TaskList Stats, NewsReview, and SpendTracker. Each app
 * builds its own buttons and menus; this file gives every one of them the same placement and keyboard use:
 * - A menu that opens inside a dialog floats over the page instead of being cut off by the dialog's scrolling body:
 *   below its button, or above when there is more room there, as tall as the window allows (SpendTracker's overflow).
 *   Scrolling or resizing closes it, so it never drifts away from its button. On touch screens it opens in place
 *   under its button instead, pushing the fields below it down: a phone's keyboard scrolls and resizes the dialog
 *   while you type in a list's search box, and a floating menu jumped around chasing its button.
 * - A dialog opens scrolled to its top, not where it was left the last time.
 * - Arrow Down/Up move through a menu's items (Arrow Down on a closed dropdown opens it), Home and End jump to the
 *   first and last item, and Escape closes the menu and returns to its button without closing the dialog under it.
 */
(() => {
  const MENUS = '.menu-dropdown, [role="menu"], [role="listbox"]';
  const ITEMS = '[role="menuitem"], [role="menuitemradio"], [role="menuitemcheckbox"], [role="option"]';
  const TRIGGERS = '[aria-haspopup]';

  const isShown = element => !element.hidden && element.getClientRects().length > 0;

  /** The menu a dropdown button opens: the one it names in aria-controls, or the menu beside it. */
  function menuFor(trigger) {
    const named = trigger.getAttribute('aria-controls');
    if (named) return document.getElementById(named);
    return [...trigger.parentElement.children].find(child => child !== trigger && child.matches(MENUS)) ?? null;
  }

  /** The button that opens a menu. */
  function triggerFor(menu) {
    if (menu.id) {
      const named = document.querySelector(`[aria-controls="${CSS.escape(menu.id)}"]`);
      if (named) return named;
    }
    return [...menu.parentElement.children].find(child => child !== menu && child.matches(TRIGGERS)) ?? null;
  }

  /** The items a keyboard can move through: menu items or options, or else every control (a panel of check boxes), shown and enabled. */
  function itemsOf(menu) {
    const marked = [...menu.querySelectorAll(ITEMS)];
    const controls = 'button, input:not([type="hidden"]), select, textarea, [tabindex]:not([tabindex="-1"])';
    return (marked.length ? marked : [...menu.querySelectorAll(controls)]).filter(item => !item.disabled && isShown(item));
  }

  function placeMenu(button, menu) {
    menu.classList.remove('floating-menu');
    for (const property of ['position', 'left', 'top', 'bottom', 'minWidth', 'maxHeight']) menu.style[property] = '';
    menu.classList.remove('in-place-menu');
    if (!button.closest('dialog')) return;
    if (window.matchMedia('(pointer: coarse)').matches) {
      menu.classList.add('in-place-menu');
      menu.style.position = 'static';
      menu.style.minWidth = `${button.getBoundingClientRect().width}px`;
      menu.scrollIntoView({ block: 'nearest' });
      return;
    }
    const rect = button.getBoundingClientRect();
    const below = window.innerHeight - rect.bottom - 8;
    const above = rect.top - 8;
    const upward = below < 220 && above > below;
    menu.classList.add('floating-menu');
    menu.style.position = 'fixed';
    menu.style.left = `${Math.max(4, Math.min(rect.left, window.innerWidth - Math.max(rect.width, 165) - 4))}px`;
    menu.style.minWidth = `${rect.width}px`;
    menu.style.maxHeight = `${Math.max(120, Math.min(480, upward ? above : below))}px`;
    // Opening upward, the stylesheet's own top (just below the button) must not apply too, or the menu collapses.
    if (upward) {
      menu.style.top = 'auto';
      menu.style.bottom = `${window.innerHeight - rect.top + 1}px`;
    } else menu.style.top = `${rect.bottom + 1}px`;
  }
  window.placeMenu = placeMenu;

  /** Closes through the app's own button when it can, so the app's idea of which menu is open stays right. */
  function closeMenu(menu, trigger, focus = true) {
    if (trigger && trigger.getAttribute('aria-expanded') === 'true') trigger.click();
    if (!menu.hidden) {
      menu.hidden = true;
      trigger?.setAttribute('aria-expanded', 'false');
    }
    if (focus) trigger?.focus();
  }

  // Each app opens its menus in its own click handlers, some of which stop the click there; placing the menu once
  // those handlers have run works for all of them.
  // A menu opens scrolled to its top each time, not where it was left the last time it was open.
  document.addEventListener('click', event => {
    const trigger = event.target.closest?.(TRIGGERS);
    if (!trigger) return;
    const menu = menuFor(trigger);
    const wasHidden = menu?.hidden;
    requestAnimationFrame(() => {
      if (!menu || menu.hidden) return;
      if (wasHidden) for (const box of [menu, ...menu.querySelectorAll('*')]) if (box.scrollTop) box.scrollTop = 0;
      placeMenu(trigger, menu);
    });
  }, true);

  // Right after it opens: a closed dialog has no layout, so it cannot be scrolled back while closed.
  const showModal = HTMLDialogElement.prototype.showModal;
  HTMLDialogElement.prototype.showModal = function (...args) {
    showModal.apply(this, args);
    for (const box of [this, ...this.querySelectorAll('*')]) if (box.scrollTop) box.scrollTop = 0;
  };

  // While typing in a menu (its search box), a phone's keyboard resizes the window and scrolls the dialog to show the
  // box; the menu then moves with its button instead of closing, which would also put the keyboard away.
  const followOrClose = menu => {
    const trigger = triggerFor(menu);
    if (menu.contains(document.activeElement) && trigger) placeMenu(trigger, menu);
    else closeMenu(menu, trigger, false);
  };
  document.addEventListener('scroll', event => {
    const floating = [...document.querySelectorAll('.floating-menu')].find(menu => !menu.hidden);
    if (floating && !floating.contains(event.target)) followOrClose(floating);
  }, true);
  window.addEventListener('resize', () => {
    for (const menu of document.querySelectorAll('.floating-menu')) if (!menu.hidden) followOrClose(menu);
  });
  window.visualViewport?.addEventListener('resize', () => {
    for (const menu of document.querySelectorAll('.floating-menu')) if (!menu.hidden) followOrClose(menu);
  });

  document.addEventListener('keydown', event => {
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End', 'Escape'].includes(event.key) || event.altKey || event.ctrlKey || event.metaKey) return;
    const target = event.target;
    // Typing in a text box inside a menu (SpendTracker's list search) keeps Home and End for the text.
    const typing = target.matches?.('input:not([type="checkbox"]):not([type="radio"]), textarea');
    let menu = target.closest?.(MENUS);
    let trigger = menu ? triggerFor(menu) : null;
    if (!menu) {
      trigger = target.closest?.(TRIGGERS);
      if (!trigger) return;
      menu = menuFor(trigger);
      if (!menu) return;
    }
    if (event.key === 'Escape') {
      if (menu.hidden) return;
      event.preventDefault();
      event.stopPropagation();
      closeMenu(menu, trigger);
      return;
    }
    if (typing && (event.key === 'Home' || event.key === 'End')) return;
    event.preventDefault();
    event.stopPropagation();
    const move = () => {
      const items = itemsOf(menu);
      if (!items.length) return;
      let index = items.indexOf(document.activeElement);
      if (event.key === 'Home') index = 0;
      else if (event.key === 'End') index = items.length - 1;
      else if (index < 0) {
        // A list of choices, like a select, starts at the current one; a menu of commands starts at its first item.
        const choices = items.every(item => ['menuitemradio', 'option'].includes(item.getAttribute('role')));
        const current = choices ? items.findIndex(item => item.getAttribute('aria-checked') === 'true' || item.getAttribute('aria-selected') === 'true') : -1;
        index = current >= 0 ? current : event.key === 'ArrowUp' ? items.length - 1 : 0;
      } else index = (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items[index].focus();
    };
    if (menu.hidden && trigger) {
      trigger.click();
      requestAnimationFrame(move);
    } else move();
  }, true);
})();
