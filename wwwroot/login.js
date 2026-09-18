function syncAppViewport() {
  const viewport = window.visualViewport;
  const height = Math.round(viewport?.height ?? window.innerHeight);
  const top = Math.round(viewport?.offsetTop ?? 0);
  document.documentElement.style.setProperty('--app-height', `${height}px`);
  document.documentElement.style.setProperty('--app-top', `${top}px`);
}

syncAppViewport();
window.addEventListener('resize', syncAppViewport);
window.addEventListener('orientationchange', syncAppViewport);
if (window.visualViewport) {
  window.visualViewport.addEventListener('resize', syncAppViewport);
  window.visualViewport.addEventListener('scroll', syncAppViewport);
}

const authForm = document.querySelector('#authForm');
const password = document.querySelector('#password');
const confirmGroup = document.querySelector('#confirmGroup');
const confirmPassword = document.querySelector('#confirmPassword');
const loginButton = document.querySelector('#loginButton');
const loginError = document.querySelector('#loginError');
const loginWindowTitle = document.querySelector('#loginWindowTitle');
const loginIntro = document.querySelector('#loginIntro');

let setupMode = false;

async function request(url, options = {}) {
  const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', ...options });
  if (response.ok) return response.status === 204 ? null : response.json();

  let message = response.status === 401 ? 'Incorrect password.' : `${response.status} ${response.statusText}`;
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
      window.location.replace('/');
      return;
    }

    setupMode = !status.configured;
    if (setupMode) {
      loginWindowTitle.textContent = 'Create Password';
      loginIntro.textContent = 'First run: create the password used to access Task List and its API.';
      confirmGroup.hidden = false;
      confirmPassword.required = true;
      password.autocomplete = 'new-password';
      loginButton.textContent = 'Create Password';
    }
    password.focus();
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
      ? { password: password.value, confirmPassword: confirmPassword.value }
      : { password: password.value };
    await request(setupMode ? '/api/auth/setup' : '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    window.location.replace('/');
  } catch (error) {
    loginError.textContent = error.message;
    password.focus();
    password.select();
  } finally {
    loginButton.disabled = false;
  }
});

initialize();
