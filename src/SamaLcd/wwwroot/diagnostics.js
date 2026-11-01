'use strict';
(() => {
  let debug = false, windowStart = Date.now(), errorsSent = 0, tracesSent = 0;
  const clip = (value, max = 2048) => String(value ?? '').slice(0, max);
  const send = message => {
    try {
      if (Date.now() - windowStart > 60000) { windowStart = Date.now(); errorsSent = tracesSent = 0; }
      if (message.cmd === 'uiError' ? ++errorsSent <= 40 : ++tracesSent <= 240) window.chrome?.webview?.postMessage(message);
    } catch { /* Diagnostics must not interrupt an operation. */ }
  };
  window.configureDiagnostics = state => {
    debug = !!state.debugLogEnabled;
    const toggle = document.querySelector('#debugLogs');
    if (toggle) toggle.checked = debug;
    const path = document.querySelector('#logsPath'), error = document.querySelector('#logsError');
    if (path) path.textContent = state.diagnostics?.directory || '';
    if (error) error.textContent = state.diagnostics?.error || '';
  };
  window.recordUiError = (message, context = 'interface') => send({cmd:'uiError',message:clip(message,4096),context:clip(context,512)});
  window.addEventListener('error', event => {
    const resource = event.target !== window && event.target?.tagName;
    send({cmd:'uiError', message:clip(event.message || `Resource failed: ${resource || 'unknown'}`), stack:clip(event.error?.stack, 8192), context:clip(resource ? `${resource}#${event.target.id || ''}` : `${event.filename || ''}:${event.lineno || 0}:${event.colno || 0}`, 512)});
  }, true);
  window.addEventListener('unhandledrejection', event => {
    send({cmd:'uiError', message:clip(event.reason?.message || event.reason || 'Unhandled promise rejection'), stack:clip(event.reason?.stack, 8192), context:'unhandledrejection'});
  });
  for (const action of ['click','change']) document.addEventListener(action, event => {
    if (!debug) return;
    const element = event.target.closest?.('button,input,select');
    if (!element) return;
    // Do not record custom text, media bytes, entire layouts or file contents.
    const value = element.type === 'checkbox' ? String(element.checked) : element.tagName === 'SELECT' || element.type === 'range' || element.type === 'number' ? element.value : element.dataset.value || '';
    send({cmd:'uiTrace', action, control:clip(element.id || element.dataset.tab || element.dataset.i18n || element.tagName, 128), value:clip(value,256), page:document.querySelector('#tabs .tab-active')?.dataset.tab || ''});
  }, true);
  for (const action of ['pointerdown','pointerup']) document.addEventListener(action, event => {
    if (!debug || event.target.id !== 'cropCanvas') return;
    const rect = event.target.getBoundingClientRect();
    send({cmd:'uiTrace',action,control:'cropCanvas',value:`x=${((event.clientX-rect.x)/rect.width).toFixed(3)}, y=${((event.clientY-rect.y)/rect.height).toFixed(3)}, zoom=${document.querySelector('#cropZoom')?.value || ''}`,page:'crop'});
  }, true);
})();
