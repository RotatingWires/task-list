(() => {
  'use strict';

  const numberFormat = new Intl.NumberFormat();
  const confettiColors = ['#ff3b30', '#ffcc00', '#34c759', '#0a84ff', '#bf5af2', '#ff9f0a'];
  let checking = null;

  function ensureStyles() {
    const existing = document.querySelector('link[data-milestone-styles]');
    if (existing) return Promise.resolve();

    return new Promise(resolve => {
      const link = document.createElement('link');
      link.rel = 'stylesheet';
      link.href = '/milestones.css';
      link.dataset.milestoneStyles = '';
      link.addEventListener('load', resolve, { once: true });
      link.addEventListener('error', resolve, { once: true });
      document.head.append(link);
    });
  }

  function milestoneText(notice) {
    const value = numberFormat.format(notice.threshold);
    const first = notice.threshold === 1;
    const scope = notice.scope ?? 'global';
    const scopePrefix = scope === 'list' && notice.scopeLabel
      ? `${notice.scopeLabel} — `
      : scope === 'year' && notice.scopeValue
        ? `${notice.scopeValue} — `
        : '';

    let title;
    let detailNoun;

    switch (notice.kind) {
      case 'created':
        title = first ? 'First task created' : `${value} tasks created`;
        detailNoun = first ? 'its first recorded task creation' : `${value} recorded task creations`;
        break;
      case 'completed':
        title = first ? 'First task completion' : `${value} task completions`;
        detailNoun = first ? 'its first recorded task-completion transition' : `${value} recorded task-completion transitions`;
        break;
      case 'cancelled':
        title = first ? 'First task cancellation' : `${value} task cancellations`;
        detailNoun = first ? 'its first recorded task-cancellation transition' : `${value} recorded task-cancellation transitions`;
        break;
      case 'reopened':
        title = first ? 'First task reopened' : `${value} task reopens`;
        detailNoun = first ? 'its first recorded task-reopen transition' : `${value} recorded task-reopen transitions`;
        break;
      case 'deleted':
        title = first ? 'First task deleted' : `${value} task deletions`;
        detailNoun = first ? 'its first recorded task deletion' : `${value} recorded task deletions`;
        break;
      case 'recorded':
        title = first ? 'First recorded event' : `${value} recorded events`;
        detailNoun = first ? 'its first recorded task event' : `${value} recorded task events`;
        break;
      case 'universal':
      default:
        return {
          title: `Universal ID #${value} reached`,
          detail: `TaskList has assigned Universal ID #${value}.`
        };
    }

    let detail;
    if (scope === 'list' && notice.scopeLabel)
      detail = `The ${notice.scopeLabel} list has reached ${detailNoun}.`;
    else if (scope === 'year' && notice.scopeValue)
      detail = `TaskList reached ${detailNoun} in ${notice.scopeValue}.`;
    else
      detail = `Your TaskList history has reached ${detailNoun}.`;

    return {
      title: `${scopePrefix}${title}`,
      detail
    };
  }

  function reachedLabel(raw) {
    if (!raw) return '';

    if (/^\d{4}-\d{2}-\d{2}$/.test(raw)) {
      const [year, month, day] = raw.split('-').map(Number);
      return new Date(year, month - 1, day).toLocaleDateString(undefined, {
        year: 'numeric', month: 'long', day: 'numeric'
      });
    }

    const date = new Date(raw);
    if (Number.isNaN(date.getTime())) return '';
    return date.toLocaleString(undefined, {
      year: 'numeric', month: 'long', day: 'numeric',
      hour: 'numeric', minute: '2-digit'
    });
  }

  function launchConfetti(host) {
    if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    const layer = document.createElement('div');
    layer.className = 'milestone-confetti';
    layer.setAttribute('aria-hidden', 'true');

    for (let index = 0; index < 34; index++) {
      const piece = document.createElement('span');
      piece.className = 'milestone-confetti-piece';
      piece.style.setProperty('--x', `${4 + Math.random() * 92}%`);
      piece.style.setProperty('--drift', `${-70 + Math.random() * 140}px`);
      piece.style.setProperty('--delay', `${Math.random() * 0.45}s`);
      piece.style.setProperty('--duration', `${2.4 + Math.random() * 1.4}s`);
      piece.style.setProperty('--spin', `${360 + Math.floor(Math.random() * 900)}deg`);
      piece.style.setProperty('--confetti-color', confettiColors[index % confettiColors.length]);
      layer.append(piece);
    }

    host.append(layer);
    setTimeout(() => layer.remove(), 4500);
  }

  function buildNoticeCard(notice) {
    const text = milestoneText(notice);
    const card = document.createElement('div');
    card.className = 'milestone-notice-card';

    const title = document.createElement('strong');
    title.className = 'milestone-notice-title';
    title.textContent = text.title;

    const detail = document.createElement('div');
    detail.className = 'milestone-notice-detail';
    detail.textContent = text.detail;

    card.append(title, detail);

    const when = reachedLabel(notice.reachedAt);
    if (when) {
      const timestamp = document.createElement('div');
      timestamp.className = 'milestone-notice-when';
      timestamp.textContent = when;
      card.append(timestamp);
    }
    return card;
  }

  function showMilestones(notices) {
    if (!Array.isArray(notices) || notices.length === 0) return;

    const dialog = document.createElement('dialog');
    dialog.className = 'retro-dialog milestone-dialog';
    dialog.tabIndex = -1;

    const shell = document.createElement('div');
    shell.className = 'milestone-shell';

    const titlebar = document.createElement('div');
    titlebar.className = 'dialog-title milestone-dialog-title';
    titlebar.textContent = notices.length === 1 ? 'Milestone reached!' : 'Milestones reached!';

    const body = document.createElement('div');
    body.className = 'dialog-body milestone-body';

    const star = document.createElement('div');
    star.className = 'milestone-star';
    star.setAttribute('aria-hidden', 'true');
    star.textContent = '★';

    const kicker = document.createElement('div');
    kicker.className = 'milestone-kicker';
    kicker.textContent = 'Nice work.';

    const list = document.createElement('div');
    list.className = 'milestone-notice-list';
    list.replaceChildren(...notices.map(buildNoticeCard));

    body.append(star, kicker, list);

    const buttons = document.createElement('div');
    buttons.className = 'dialog-buttons milestone-buttons';
    const close = document.createElement('button');
    close.type = 'button';
    close.textContent = 'Nice!';
    buttons.append(close);

    shell.append(titlebar, body, buttons);
    dialog.append(shell);
    document.body.append(dialog);

    const dismiss = () => {
      if (dialog.open) dialog.close();
      dialog.remove();
    };
    close.addEventListener('click', dismiss);
    dialog.addEventListener('cancel', event => {
      event.preventDefault();
      dismiss();
    });

    dialog.showModal();
    dialog.focus({ preventScroll: true });
    launchConfetti(dialog);
  }

  async function consumePendingMilestones() {
    try {
      const response = await fetch('/api/milestones/consume', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store'
      });
      if (!response.ok) return [];
      const notices = await response.json();
      return Array.isArray(notices) ? notices : [];
    } catch {
      return [];
    }
  }

  async function check() {
    if (checking) return checking;

    checking = (async () => {
      await ensureStyles();
      const notices = await consumePendingMilestones();
      if (notices.length) showMilestones(notices);
    })();

    try {
      await checking;
    } finally {
      checking = null;
    }
  }

  window.TaskMilestones = Object.freeze({ check });

  async function start() {
    await ensureStyles();
    await check();
  }

  start();
})();
