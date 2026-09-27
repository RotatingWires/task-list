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

let setupMode = false;

function loginReturnUrl() {
  const value = new URLSearchParams(location.search).get('returnUrl');
  if (!value || !value.startsWith('/') || value.startsWith('//')) return '/';
  try { const url = new URL(value, location.origin); return url.origin === location.origin ? `${url.pathname}${url.search}${url.hash}` : '/'; }
  catch { return '/'; }
}

async function request(url, options = {}) {
  const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', ...options });
  if (response.ok) return response.status === 204 ? null : response.json();

  let message = response.status === 401 ? 'Incorrect password.' : response.status === 429 ? 'Too many attempts. Try again in one minute.' : `${response.status} ${response.statusText}`;
  try {
    const body = await response.json();
    if (body.error) message = body.error;
  } catch {}
  throw new Error(message);
}

async function initialize() {
  try {
    const status = await request('/api/auth/status');
    if (status.authenticated) {
      window.location.replace(loginReturnUrl());
      return;
    }

    setupMode = !status.configured;
    if (setupMode) {
      loginWindowTitle.textContent = 'Create Password';
      loginIntro.textContent = 'First run: enter the setup token printed in the Task List server console, then create your password.';
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
