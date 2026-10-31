'use strict';
// Same JSON catalogs as the native UI. No remote translation service is used.
const I18n = (() => {
  const languages = ['en', 'zh-Hans', 'ja', 'ru'];
  const cache = new Map();
  let language = 'en', generation = 0;
  const normalize = value => languages.find(l => l.toLowerCase() === String(value || '').toLowerCase()) || 'en';
  const load = code => {
    if (!cache.has(code)) cache.set(code, Promise.resolve(globalThis.SamaLcdCatalogs[code]));
    return cache.get(code);
  };
  let english = {}, current = {};
  function t(key, ...args) {
    return (current[key] ?? english[key] ?? key).replace(/\{(\d+)(?::([^}]+))?\}/g, (match, index, format) => {
      if (args[index] == null) return match;
      return format === '0.0' ? new Intl.NumberFormat(language, {minimumFractionDigits: 1, maximumFractionDigits: 1}).format(args[index]) : String(args[index]);
    });
  }
  function apply(root = document) {
    for (const el of root.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
    for (const attr of ['title', 'placeholder'])
      for (const el of root.querySelectorAll(`[data-i18n-${attr}]`)) el.setAttribute(attr, t(el.getAttribute(`data-i18n-${attr}`)));
  }
  async function setLanguage(value) {
    const code = normalize(value), revision = ++generation;
    const [fallback, selected] = await Promise.all([load('en'), load(code)]);
    if (revision !== generation) return;
    english = fallback; current = selected; language = code;
    document.documentElement.lang = code;
    apply();
  }
  function savedLanguage() { try { return normalize(localStorage.getItem('language')); } catch { return 'en'; } }
  function saveLanguage(value) { try { localStorage.setItem('language', normalize(value)); } catch { } }
  // Settings from the native host are authoritative; localStorage is used only by browser previews.
  const ready = setLanguage(window.chrome?.webview ? 'en' : savedLanguage());
  return {t, apply, setLanguage, normalize, savedLanguage, saveLanguage, ready, get language() { return language; }};
})();
