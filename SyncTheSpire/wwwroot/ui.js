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

// per-game-type icons, sized via inline style
const GAME_ICONS = {
    sts2: (size = 14) => `<img src="http://assets.local/sts2.png" style="width:${size}px;height:${size}px;object-fit:contain;image-rendering:pixelated;filter:drop-shadow(0 0 3px rgba(255,255,255,0.18)) brightness(1.15);" />`,
    stardew: (size = 14) => `<img src="http://assets.local/sv.png" style="width:${size}px;height:${size}px;object-fit:contain;image-rendering:pixelated;filter:drop-shadow(0 0 3px rgba(255,255,255,0.18)) brightness(1.15);" />`,
    minecraft: (size = 14) => `<img src="http://assets.local/mc.png" style="width:${size}px;height:${size}px;object-fit:contain;image-rendering:pixelated;filter:drop-shadow(0 0 3px rgba(255,255,255,0.18)) brightness(1.15);" />`,
    generic: (size = 14) => `<svg style="width:${size}px;height:${size}px;" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z"/><path d="M12 11v4m-2-2h4"/></svg>`,
};
function gameIcon(typeKey, size) { return (GAME_ICONS[typeKey] || GAME_ICONS.generic)(size); }

function showPage(name) {
    // hide all pages
    for (const id of ['page-home', 'page-setup', 'page-main']) {
        const el = $(`#${id}`);
        if (el) {
            el.classList.add('hidden');
            el.classList.remove('flex');
        }
    }

    const el = $(`#page-${name}`);
    if (!el) return;
    el.classList.remove('hidden');
    el.classList.add('flex');

    // pick a random quote when entering the main page
    if (name === 'main') {
        setRandomQuote();
    }

    // update tab bar highlight
    updateTabBarActive(name === 'home' ? 'home' : AppState.activeWorkspaceId);
}

// highlight the correct tab in the tab bar
function updateTabBarActive(tabId) {
    document.querySelectorAll('.tab-item').forEach(t => t.classList.remove('active'));
    if (tabId === 'home') {
        const homeTab = $('#tab-home');
        if (homeTab) homeTab.classList.add('active');
    } else if (tabId) {
        const wsTab = document.querySelector(`.tab-item[data-tab="${tabId}"]`);
        if (wsTab) wsTab.classList.add('active');
    }
}

function showLoading(text, percent, detail) {
    $('#loading-text').textContent = text || '';
    const bar = $('#loading-progress');
    const fill = $('#loading-bar-fill');
    const pctLabel = $('#loading-percent');
    const detailLabel = $('#loading-detail');
    if (percent != null && bar && fill) {
        const clamped = Math.max(0, Math.min(100, percent));
        fill.style.width = clamped + '%';
        if (pctLabel) pctLabel.textContent = `${clamped}%`;
        if (detailLabel) detailLabel.textContent = detail || '';
        bar.classList.remove('hidden');
    } else if (bar) {
        bar.classList.add('hidden');
    }
    $('#loading-overlay').classList.remove('hidden');
    // safety net: auto-hide after 2 min if backend never responds
    clearTimeout(showLoading._timer);
    showLoading._timer = setTimeout(() => {
        hideLoading();
        toast(I18n.t('common.operationTimeout'), 'error');
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
    // reset progress bar / labels for next use
    const bar = $('#loading-progress');
    if (bar) bar.classList.add('hidden');
    const fill = $('#loading-bar-fill');
    if (fill) fill.style.width = '0';
    const pctLabel = $('#loading-percent');
    if (pctLabel) pctLabel.textContent = '0%';
    const detailLabel = $('#loading-detail');
    if (detailLabel) detailLabel.textContent = '';
    _guardedBtns.forEach(btn => btn.disabled = false);
    _guardedBtns.clear();
};

// ── modal focus trap ─────────────────────────────────────────────────────────
// Keeps Tab focus inside whichever modal/overlay is currently on top.
// Modals are checked highest-to-lowest priority; loading overlay last.
const _modalFocusOrder = [
    '#mm-branch-copy-modal', '#mm-detail-modal',
    '#save-unlink-modal', '#confirm-modal', '#conflict-modal',
    '#mod-diff-modal', '#steam-account-modal', '#update-modal',
    '#branch-modal', '#welcome-modal', '#create-workspace-modal',
    '#backup-list-modal', '#settings-modal',
    '#mod-manager-modal',
    '#loading-overlay',
];

document.addEventListener('keydown', e => {
    if (e.key !== 'Tab') return;
    const modal = _modalFocusOrder.map(sel => $(sel)).find(m => m && !m.classList.contains('hidden'));
    if (!modal) return;

    const focusable = [...modal.querySelectorAll(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])'
    )].filter(el => el.offsetParent !== null);

    if (focusable.length === 0) { e.preventDefault(); return; }

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;

    if (e.shiftKey) {
        if (!modal.contains(active) || active === first) {
            e.preventDefault();
            last.focus();
        }
    } else {
        if (!modal.contains(active) || active === last) {
            e.preventDefault();
            first.focus();
        }
    }
}, true);

// themed confirm dialog — returns a Promise<boolean>
function showConfirm(message, title) {
    return new Promise(resolve => {
        $('#confirm-title').textContent = title || I18n.t('common.confirm');
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
    if (diff < 0) return I18n.t('time.justNow'); // clock skew guard
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return I18n.t('time.justNow');
    if (mins < 60) return I18n.t('time.minutesAgo', { n: mins });
    const hours = Math.floor(mins / 60);
    if (hours < 24) return I18n.t('time.hoursAgo', { n: hours });
    const days = Math.floor(hours / 24);
    if (days < 30) return I18n.t('time.daysAgo', { n: days });
    return new Date(ms).toLocaleDateString(I18n.getLang());
}

// open external links via WebView2's built-in navigation handler
function openExternal(url) {
    window.open(url, '_blank');
}
