const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

class Element {
  constructor(tag = 'div') {
    this.tagName = tag.toUpperCase(); this.children = []; this.dataset = {}; this.listeners = {};
    this.attributes = {}; this.className = ''; this._text = ''; this.style = { setProperty() {} };
    this.value = ''; this.inert = false;
    this.classList = {
      add: (...names) => { this.className += ' ' + names.join(' '); },
      remove: (...names) => { this.className = this.className.split(/\s+/).filter(x => !names.includes(x)).join(' '); },
      contains: name => this.className.split(/\s+/).includes(name),
      toggle: (name, enabled) => enabled ? this.classList.add(name) : this.classList.remove(name),
    };
  }
  set textContent(value) { this._text = String(value); this.children = []; }
  get textContent() { return this._text + this.children.map(child => child.textContent ?? String(child)).join(''); }
  get firstElementChild() { return this.children[0]; }
  append(...children) { for (const child of children) { if (child && typeof child === 'object') child.parentElement = this; this.children.push(child); } }
  prepend(...children) { this.children.unshift(...children); }
  replaceChildren(...children) { this._text = ''; this.children = []; this.append(...children); }
  setAttribute(key, value) { this.attributes[key] = String(value); }
  addEventListener(name, callback) { (this.listeners[name] ??= []).push(callback); }
  removeEventListener() {}
  querySelectorAll(selector) {
    return this.children.flatMap(child => {
      if (!(child instanceof Element)) return [];
      const matches = selector.startsWith('.') ? child.classList.contains(selector.slice(1))
        : selector === '[data-universal-id]' ? child.dataset.universalId !== undefined
          : child.tagName === selector.toUpperCase();
      return [...(matches ? [child] : []), ...child.querySelectorAll(selector)];
    });
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  closest() { return null; }
  remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(child => child !== this); }
  focus() {}
  showModal() { this.open = true; }
  close() { this.open = false; }
}

function fixture(files = ['milestones.js']) {
  const elements = new Map();
  const listeners = {};
  const get = selector => {
    if (!elements.has(selector)) elements.set(selector, new Element());
    return elements.get(selector);
  };
  const document = {
    visibilityState: 'hidden',
    hasFocus: () => context.focused,
    querySelector: selector => selector === 'dialog[open]'
      ? document.body.children.find(child => child.open) ?? null : get(selector),
    querySelectorAll: () => [],
    createElement: tag => new Element(tag), createElementNS: (_, tag) => new Element(tag),
    addEventListener: (name, callback) => { (listeners[name] ??= []).push(callback); },
    removeEventListener() {}, body: new Element('body'), head: new Element('head'),
  };
  const context = vm.createContext({
    document, focused: false, console, Intl, queueMicrotask,
    addEventListener: (name, callback) => { (listeners[name] ??= []).push(callback); },
    fetch: async () => ({ ok: true, json: async () => [] }),
    matchMedia: () => ({ matches: true, addEventListener() {} }),
    localStorage: { getItem: () => null, setItem() {} },
    location: { pathname: '/', search: '' }, navigator: {},
    setTimeout, clearTimeout,
    closeFileMenu() {}, closeViewMenu() {},
  });
  context.window = context;
  const run = source => vm.runInContext(source, context);
  for (const file of files)
    vm.runInContext(fs.readFileSync(path.join(__dirname, '..', 'wwwroot', file), 'utf8'), context, { filename: file });
  const activate = () => { context.focused = true; document.visibilityState = 'visible'; };
  const trigger = name => { for (const callback of listeners[name] ?? []) callback({}); };
  const json = source => JSON.parse(run('JSON.stringify(' + source + ')'));
  return { context, run, json, get, document, activate, trigger };
}

function task(uid, extra = {}) {
  return { universalId: uid, listId: 1, displayId: String(uid), isSubtask: false, title: 'task',
    description: '', status: 'Open', subtasks: [], createdAt: 'Unknown', ...extra };
}

module.exports = { fixture, task };
