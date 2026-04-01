'use strict';

// ── IPC bridge ───────────────────────────────────────────────────────────────

function sendMessage(action, payload) {
    const msg = JSON.stringify({ action, payload: payload || {} });
    window.chrome.webview.postMessage(msg);
}

// incoming message handler map: event name -> callback
const handlers = {};

function on(event, fn) {
    if (!handlers[event]) handlers[event] = [];
    handlers[event].push(fn);
}

// H6 fix: track which action triggered the loading overlay
let _loadingAction = null;

// listen for messages from C# backend (WebView2 uses 'message' event)
window.chrome.webview.addEventListener('message', e => {
    let msg;
    const raw = e.data;
    try { msg = (typeof raw === 'string') ? JSON.parse(raw) : raw; } catch { return; }

    const event = msg.event;
    const data = msg.data || {};

    // show progress toasts automatically
    if (data.status === 'progress') {
        _loadingAction = event;
        showLoading(data.message || 'Processing...', data.percent);
        return;
    }

    // H6 fix: only hide loading if this response matches the action that caused it
    // or if there's no tracked action (legacy/safety fallback)
    if (_loadingAction === null || _loadingAction === event) {
        hideLoading();
        _loadingAction = null;
    }

    // show error toasts
    if (data.status === 'error') {
        toast(data.message || 'Unknown error', 'error');
    }

    // fire registered handlers
    if (handlers[event]) {
        handlers[event].forEach(fn => {
            try { fn(data); } catch (err) { /* prevent one bad handler from breaking the rest */ }
        });
    }
});

// H4 fix: one-shot IPC call with timeout — returns a Promise
function ipcCall(action, payload, timeoutMs = 30000) {
    return new Promise((resolve, reject) => {
        let settled = false;

        const handler = (data) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            const list = handlers[action];
            if (list) {
                const idx = list.indexOf(handler);
                if (idx !== -1) list.splice(idx, 1);
            }
            resolve(data);
        };

        const timer = setTimeout(() => {
            if (settled) return;
            settled = true;
            const list = handlers[action];
            if (list) {
                const idx = list.indexOf(handler);
                if (idx !== -1) list.splice(idx, 1);
            }
            resolve({ status: 'error', message: `${action} 请求超时` });
        }, timeoutMs);

        on(action, handler);
        sendMessage(action, payload);
    });
}
