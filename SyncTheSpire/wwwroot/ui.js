'use strict';

// ── UI helpers ───────────────────────────────────────────────────────────────

const $ = (sel) => document.querySelector(sel);

// html escape to prevent XSS when injecting data into innerHTML
function esc(str) {
    const d = document.createElement('div');
    d.textContent = str;
    return d.innerHTML;
}

// escape for use inside HTML attribute values
function escAttr(str) {
    return str.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function showPage(name) {
    $('#page-setup').classList.add('hidden');
    $('#page-setup').classList.remove('flex');
    $('#page-main').classList.add('hidden');
    $('#page-main').classList.remove('flex');

    const el = $(`#page-${name}`);
    if (!el) return;
    el.classList.remove('hidden');
    el.classList.add('flex');

    // pick a random quote when entering the main page
    if (name === 'main') {
        setRandomQuote();
    }
}

function showLoading(text, percent) {
    $('#loading-text').textContent = text;
    const bar = $('#loading-progress');
    const fill = $('#loading-bar-fill');
    if (percent != null && bar && fill) {
        fill.style.width = percent + '%';
        bar.classList.remove('hidden');
    } else if (bar) {
        bar.classList.add('hidden');
    }
    $('#loading-overlay').classList.remove('hidden');
    // safety net: auto-hide after 2 min if backend never responds
    clearTimeout(showLoading._timer);
    showLoading._timer = setTimeout(() => {
        hideLoading();
        toast('操作超时，请重试', 'error');
    }, 120_000);
}

function hideLoading() {
    clearTimeout(showLoading._timer);
    $('#loading-overlay').classList.add('hidden');
}

function toast(message, type = 'info') {
    const container = $('#toast-container');
    const el = document.createElement('div');

    const bgMap = {
        info: 'bg-spire-card border-spire-accent',
        error: 'bg-spire-card border-spire-danger',
        success: 'bg-spire-card border-spire-success',
    };

    el.className = `toast border rounded-lg px-4 py-2 text-sm max-w-sm cursor-pointer overflow-y-auto ${bgMap[type] || bgMap.info}`;
    el.style.maxHeight = '50vh';
    el.style.whiteSpace = 'pre-wrap';
    el.style.wordBreak = 'break-word';
    el.textContent = message;
    container.appendChild(el);

    let dismissed = false;
    const dismiss = () => {
        if (dismissed) return;
        dismissed = true;
        clearTimeout(timer);
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.3s';
        setTimeout(() => el.remove(), 300);
    };

    el.addEventListener('click', dismiss);

    // longer messages stay longer
    const duration = Math.max(4000, message.length * 60);
    const timer = setTimeout(dismiss, duration);
}

// debounce guard: disable button during IPC round-trip, re-enable on hideLoading
const _guardedBtns = new Set();
function guardClick(btn, fn) {
    btn.addEventListener('click', async () => {
        if (btn.disabled) return;
        btn.disabled = true;
        _guardedBtns.add(btn);
        try { await fn(); } catch { /* handled elsewhere */ }
        // if no IPC loading was triggered, re-enable immediately
        if (_guardedBtns.has(btn)) {
            btn.disabled = false;
            _guardedBtns.delete(btn);
        }
    });
}
// patch hideLoading to re-enable guarded buttons
const _origHideLoading = hideLoading;
hideLoading = function () {
    _origHideLoading();
    // reset progress bar for next use
    const bar = $('#loading-progress');
    if (bar) bar.classList.add('hidden');
    const fill = $('#loading-bar-fill');
    if (fill) fill.style.width = '0';
    _guardedBtns.forEach(btn => btn.disabled = false);
    _guardedBtns.clear();
};

// themed confirm dialog — returns a Promise<boolean>
function showConfirm(message, title) {
    return new Promise(resolve => {
        $('#confirm-title').textContent = title || '确认';
        $('#confirm-message').textContent = message;
        const modal = $('#confirm-modal');
        modal.classList.remove('hidden');

        function cleanup() {
            modal.classList.add('hidden');
            $('#confirm-ok').removeEventListener('click', onOk);
            $('#confirm-cancel').removeEventListener('click', onCancel);
            modal.removeEventListener('click', onBackdrop);
            document.removeEventListener('keydown', onKeydown);
        }

        function onOk() { cleanup(); resolve(true); }
        function onCancel() { cleanup(); resolve(false); }
        function onBackdrop(e) { if (e.target === modal) { cleanup(); resolve(false); } }
        function onKeydown(e) { if (e.key === 'Escape') { cleanup(); resolve(false); } }

        $('#confirm-ok').addEventListener('click', onOk);
        $('#confirm-cancel').addEventListener('click', onCancel);
        modal.addEventListener('click', onBackdrop);
        document.addEventListener('keydown', onKeydown);
    });
}

// conflict resolution dialog — returns 'force' | 'reset' | null
function showConflictDialog(message) {
    return new Promise(resolve => {
        $('#conflict-message').textContent = message;
        const modal = $('#conflict-modal');
        modal.classList.remove('hidden');

        function cleanup() {
            modal.classList.add('hidden');
            $('#conflict-use-local').removeEventListener('click', onLocal);
            $('#conflict-use-remote').removeEventListener('click', onRemote);
            $('#conflict-cancel').removeEventListener('click', onCancel);
            modal.removeEventListener('click', onBackdrop);
            document.removeEventListener('keydown', onKeydown);
        }

        function onLocal() { cleanup(); resolve('force'); }
        function onRemote() { cleanup(); resolve('reset'); }
        function onCancel() { cleanup(); resolve(null); }
        function onBackdrop(e) { if (e.target === modal) { cleanup(); resolve(null); } }
        function onKeydown(e) { if (e.key === 'Escape') { cleanup(); resolve(null); } }

        $('#conflict-use-local').addEventListener('click', onLocal);
        $('#conflict-use-remote').addEventListener('click', onRemote);
        $('#conflict-cancel').addEventListener('click', onCancel);
        modal.addEventListener('click', onBackdrop);
        document.addEventListener('keydown', onKeydown);
    });
}

// ── helpers ──────────────────────────────────────────────────────────────────

function formatSize(bytes) {
    if (bytes == null) return '—';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatRelativeTime(ms) {
    const diff = Date.now() - ms;
    if (diff < 0) return '刚刚'; // clock skew guard
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return '刚刚';
    if (mins < 60) return `${mins} 分钟前`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours} 小时前`;
    const days = Math.floor(hours / 24);
    if (days < 30) return `${days} 天前`;
    return new Date(ms).toLocaleDateString('zh-CN');
}

// open external links via WebView2's built-in navigation handler
function openExternal(url) {
    window.open(url, '_blank');
}
