/* The login page: sign-in and first-run password setup. */
const REQUEST_TIMEOUT_MS = 30000;

/** fetch that gives up after 30 seconds, like NewsReview, instead of waiting forever on a stopped server. */
async function timedFetch(url, options = {}, timeoutMs = REQUEST_TIMEOUT_MS) {
  try {
    return await fetch(url, timeoutMs ? { ...options, signal: AbortSignal.timeout(timeoutMs) } : options);
  } catch (error) {
    if (error.name === 'TimeoutError') throw new Error('The server did not respond within 30 seconds. Check that it is running.');
    throw error;
  }
}

const authForm = document.querySelector('#authForm');
const password = document.querySelector('#password');
const setupTokenGroup = document.querySelector('#setupTokenGroup');
const setupToken = document.querySelector('#setupToken');
const confirmGroup = document.querySelector('#confirmGroup');
const confirmPassword = document.querySelector('#confirmPassword');
const loginButton = document.querySelector('#loginButton');
const loginError = document.querySelector('#loginError');
const loginWindowTitle = document.querySelector('#loginWindowTitle');
const loginIntro = document.querySelector('#loginIntro');
const titleAppName = document.querySelector('.title-app-name');

let setupMode = false;

function loginReturnUrl() {
  const params = new URLSearchParams(location.search);
  const value = params.get('returnUrl') || params.get('ReturnUrl');
  if (!value || !value.startsWith('/') || value.startsWith('//')) return '/';
  try {
    const url = new URL(value, location.origin);
    return url.origin === location.origin ? `${url.pathname}${url.search}${url.hash}` : '/';
  } catch {
    return '/';
  }
}

async function request(url, options = {}) {
  const response = await timedFetch(url, { credentials: 'same-origin', cache: 'no-store', ...options });
  if (response.ok) return response.status === 204 ? null : response.json();

  let message = response.status === 401
    ? 'Incorrect password.'
    : response.status === 429
      ? 'Too many attempts. Try again in one minute.'
      : `${response.status} ${response.statusText}`;
  try {
    const body = await response.json();
    if (body.error) message = body.error;
  } catch {}
  throw new Error(message);
}

function applyReleaseLabel(version) {
  if (version) titleAppName.replaceChildren('TaskList', Object.assign(document.createElement('span'), { className: 'title-version', textContent: ` v${version}` }));
}

/** The release from /version.json, like the other apps; the server's version if that file can't be read. */
async function frontendReleaseVersion(fallback = '') {
  try {
    const response = await timedFetch('/version.json', { cache: 'no-store', credentials: 'same-origin' });
    if (!response.ok) return fallback;
    const body = await response.json();
    return body.version || fallback;
  } catch {
    return fallback;
  }
}

async function initialize() {
  try {
    const status = await request('/api/auth/status');
    applyReleaseLabel(await frontendReleaseVersion(status.version));
    if (status.authenticated) {
      window.location.replace(loginReturnUrl());
      return;
    }

    setupMode = !status.configured;
    if (setupMode) {
      loginWindowTitle.textContent = 'Create Password';
      loginIntro.textContent = 'First run: enter the setup token printed in the TaskList server console, then create your password.';
      setupTokenGroup.hidden = false;
      setupToken.required = true;
      confirmGroup.hidden = false;
      confirmPassword.required = true;
      password.minLength = 8;
      confirmPassword.minLength = 8;
      password.autocomplete = 'new-password';
      loginButton.textContent = 'Create Password';
    }
    (setupMode ? setupToken : password).focus();
  } catch (error) {
    loginError.textContent = error.message;
  }
}

authForm.addEventListener('submit', async event => {
  event.preventDefault();
  loginError.textContent = '';
  loginButton.disabled = true;

  try {
    const body = setupMode
      ? { setupToken: setupToken.value, password: password.value, confirmPassword: confirmPassword.value }
      : { password: password.value };
    await request(setupMode ? '/api/auth/setup' : '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    window.location.replace(loginReturnUrl());
  } catch (error) {
    loginError.textContent = error.message;
    const target = setupMode && error.message.toLowerCase().includes('setup token') ? setupToken : password;
    target.focus();
    target.select();
  } finally {
    loginButton.disabled = false;
  }
});

initialize();
