(() => {
  const target = `/${location.search}`;
  fetch('/api/auth/status', {credentials:'same-origin', cache:'no-store'})
    .then(r => r.json())
    .then(status => location.replace(status.authenticated ? target : `/login.html?returnUrl=${encodeURIComponent(target)}`))
    .catch(() => location.replace(`/login.html?returnUrl=${encodeURIComponent(target)}`));
})();
