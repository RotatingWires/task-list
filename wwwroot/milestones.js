(() => {
  'use strict';

  const PREVIEW_PARAM = 'previewMilestone';
  const numberFormat = new Intl.NumberFormat();
  const confettiColors = ['#ff3b30', '#ffcc00', '#34c759', '#0a84ff', '#bf5af2', '#ff9f0a'];
  const previewActive = new URLSearchParams(location.search).get(PREVIEW_PARAM) === '1';
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
    if (notice.kind === 'created') {
      return {
        title: `${value} tasks created!`,
        detail: `Your TaskList history has reached ${value} recorded task creations.`
      };
    }
    if (notice.kind === 'completed') {
      return {
        title: `${value} task completions!`,
        detail: `You have logged ${value} recorded task-completion transitions.`
      };
    }
    return {
      title: `Universal ID #${value} reached!`,
      detail: `TaskList has assigned its ${value}th Universal ID.`
    };
  }

  function reachedLabel(raw) {
    if (!raw) return '';
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
      piece.style.setProperty('--delay', `${Math.random() * 0.28}s`);
      piece.style.setProperty('--duration', `${1.15 + Math.random() * 0.75}s`);
      piece.style.setProperty('--spin', `${360 + Math.floor(Math.random() * 900)}deg`);
      piece.style.setProperty('--confetti-color', confettiColors[index % confettiColors.length]);
      layer.append(piece);
    }

    host.append(layer);
    setTimeout(() => layer.remove(), 2400);
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
    if (when && !notice.historical) {
      const timestamp = document.createElement('div');
      timestamp.className = 'milestone-notice-when';
      timestamp.textContent = when;
      card.append(timestamp);
    }
    return card;
  }

  function showMilestones(notices, preview = false) {
    if (!Array.isArray(notices) || notices.length === 0) return;

    const dialog = document.createElement('dialog');
    dialog.className = 'retro-dialog milestone-dialog';

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
    kicker.textContent = preview ? 'Preview celebration' : 'Nice work.';

    const list = document.createElement('div');
    list.className = 'milestone-notice-list';
    list.replaceChildren(...notices.map(buildNoticeCard));

    body.append(star, kicker, list);

    if (preview) {
      const note = document.createElement('div');
      note.className = 'milestone-preview-note';
      note.textContent = 'Preview only — nothing was written to milestone history.';
      body.append(note);
    }

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
    close.focus();
    launchConfetti(dialog);
  }

  async function claimPendingMilestones() {
    try {
      const response = await fetch('/api/milestones/claim', {
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
    if (previewActive) return;
    if (checking) return checking;

    checking = (async () => {
      await ensureStyles();
      const notices = await claimPendingMilestones();
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
    if (previewActive) {
      showMilestones([{
        kind: 'completed',
        threshold: 500,
        reachedAt: new Date().toISOString(),
        historical: false
      }], true);
      return;
    }
    await check();
  }

  start();
})();
