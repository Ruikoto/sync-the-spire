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

// listen for messages from C# backend (WebView2 uses 'message' event)
window.chrome.webview.addEventListener('message', e => {
    let msg;
    const raw = e.data;
    try { msg = (typeof raw === 'string') ? JSON.parse(raw) : raw; } catch { return; }

    const event = msg.event;
    const data = msg.data || {};

    // show progress toasts automatically
    if (data.status === 'progress') {
        showLoading(data.message || 'Processing...', data.percent);
        return;
    }

    hideLoading();

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

// one-shot IPC call — returns a Promise that resolves with the response data
function ipcCall(action, payload) {
    return new Promise(resolve => {
        const handler = (data) => {
            const list = handlers[action];
            if (list) {
                const idx = list.indexOf(handler);
                if (idx !== -1) list.splice(idx, 1);
            }
            resolve(data);
        };
        on(action, handler);
        sendMessage(action, payload);
    });
}
