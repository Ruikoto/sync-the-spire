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
        showLoading(data.message || 'Processing...');
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

let currentBranch = '';
let needsBranchSelection = false;
let appVersion = '';
let appArch = 'x64';
let savePathConfigured = false;

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

function showLoading(text) {
    $('#loading-text').textContent = text;
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

    el.className = `toast border rounded-lg px-4 py-2 text-sm max-w-xs cursor-pointer ${bgMap[type] || bgMap.info}`;
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

    // auto-dismiss
    const timer = setTimeout(dismiss, 4000);
}

// themed confirm dialog — returns a Promise<boolean>

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

// close any closeable modal on Escape
document.addEventListener('keydown', e => {
    if (e.key !== 'Escape') return;
    const modals = ['#branch-modal', '#backup-list-modal', '#about-modal', '#conflict-modal'];
    for (const sel of modals) {
        const m = $(sel);
        if (m && !m.classList.contains('hidden')) {
            m.classList.add('hidden');
            return;
        }
    }
    // update modal: only close via Escape if not forced (cancel button visible)
    const um = $('#update-modal');
    if (um && !um.classList.contains('hidden') && !$('#update-cancel').classList.contains('hidden')) {
        closeUpdateModal();
    }
});

function updatePushButton() {
    const btn = $('#btn-push');
    if (needsBranchSelection) {
        btn.textContent = '请先选择或创建分支';
        btn.disabled = true;
        btn.classList.add('opacity-50', 'cursor-not-allowed');
    } else {
        btn.textContent = currentBranch
            ? `保存改动并上传到 ${currentBranch}`
            : '保存改动并上传';
        btn.disabled = false;
        btn.classList.remove('opacity-50', 'cursor-not-allowed');
    }
}

function updateStatusCard(data) {
    const dot = $('#status-dot');
    const label = $('#status-label');
    const branch = $('#status-branch');
    const modDot = $('#mod-dot');
    const modLabel = $('#mod-label');
    const modCheckbox = $('#mod-checkbox');

    currentBranch = data.currentBranch || '';
    needsBranchSelection = !!data.needsBranchSelection;

    if (needsBranchSelection) {
        branch.textContent = '未选择';
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = '请通过下方选择一个分支开始';
        modCheckbox.checked = false;
        modCheckbox.disabled = true;
        modDot.className = 'w-2 h-2 rounded-full bg-gray-500';
        modLabel.textContent = '请先选择分支';
    } else {
        branch.textContent = currentBranch || '—';
        modCheckbox.disabled = false;
        if (data.isJunctionActive) {
            dot.className = 'w-2 h-2 rounded-full bg-spire-success';
            label.textContent = 'Mod 已连接';
            modCheckbox.checked = true;
            modDot.className = 'w-2 h-2 rounded-full bg-spire-success';
            modLabel.textContent = '已连接';
        } else {
            dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
            label.textContent = '纯净模式 (Mod 未连接)';
            modCheckbox.checked = false;
            modDot.className = 'w-2 h-2 rounded-full bg-spire-warn';
            modLabel.textContent = '未连接';
        }
    }

    updatePushButton();
}

// ── helpers ──────────────────────────────────────────────────────────────────

function formatSize(bytes) {
    if (bytes == null) return '—';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

// ── auth type UI sync ────────────────────────────────────────────────────────

function setAuthType(type) {
    // check the right radio
    document.querySelectorAll('input[name="authType"]').forEach(r => {
        r.checked = r.value === type;
    });
    // show/hide field groups
    $('#auth-https').classList.toggle('hidden', type !== 'https');
    $('#auth-ssh').classList.toggle('hidden', type !== 'ssh');
    // swap active pill style
    document.querySelectorAll('.auth-tab').forEach(tab => {
        const input = tab.querySelector('input');
        tab.classList.toggle('active', input.checked);
        tab.classList.toggle('text-spire-muted', !input.checked);
    });
}

// ── config form prefill ──────────────────────────────────────────────────────

let isEditMode = false; // true when coming from Settings button, false for first-time setup

function prefillConfigForm(cfg) {
    // always clear password fields first
    $('#cfg-token').value = '';
    $('#cfg-ssh-pass').value = '';

    if (!cfg) return;
    if (cfg.repoUrl) $('#cfg-repo').value = cfg.repoUrl;
    if (cfg.username) $('#cfg-user').value = cfg.username;
    if (cfg.sshKeyPath) $('#cfg-ssh-key').value = cfg.sshKeyPath;
    if (cfg.gameInstallPath) $('#cfg-path').value = cfg.gameInstallPath;
    if (cfg.saveFolderPath) $('#cfg-save').value = cfg.saveFolderPath;
    // password fields are intentionally left blank — backend merges them
    setAuthType(cfg.authType || 'anonymous');
}


// ── event handlers (from backend) ────────────────────────────────────────────

on('GET_VERSION', data => {
    if (data.status !== 'success') return;
    appVersion = data.payload?.version || 'unknown';
    appArch = data.payload?.arch || 'x64';
    $('#about-version').textContent = appVersion;
    if (!/^v?\d+\.\d+/.test(appVersion)) {
        toast('当前为非正式构建版本，建议前往 About 页面下载最新正式版', 'info');
    }
    checkForUpdates();
    checkAnnouncements();
});

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        if (!payload.isConfigured) {
            // first-time setup: request saved config for pre-fill
            $('#btn-setup-back').classList.add('hidden');
            $('#setup-subtitle').textContent = '首次配置 — 填写以下信息开始同步';
            sendMessage('GET_CONFIG');
            showPage('setup');
        } else {
            showPage('main');
            updateStatusCard(payload);
            // default to disabled until GET_SAVE_STATUS confirms save path
            savePathConfigured = false;
            updateSaveBackupCard();
            // pre-fetch branches so the modal opens instantly
            sendMessage('GET_BRANCHES');
            sendMessage('GET_SAVE_STATUS');
            sendMessage('GET_REDIRECT_STATUS');
        }
    }
});

on('GET_CONFIG', data => {
    if (data.status === 'success') {
        prefillConfigForm(data.payload);
        // update subtitle based on whether there's existing config
        if (data.payload?.repoUrl) {
            isEditMode = true;
            $('#setup-subtitle').textContent = '编辑配置';
        }
    }
});

on('INIT_CONFIG', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Done!', 'success');
        isEditMode = false;
        $('#btn-setup-back').classList.add('hidden');
        sendMessage('GET_STATUS');
    }
});

on('GET_BRANCHES', data => {
    if (data.status === 'success') {
        branchData = data.payload?.branches || [];
        branchCurrentName = data.payload?.currentBranch || '';
        // re-render if modal is open
        if (!$('#branch-modal').classList.contains('hidden')) {
            renderBranchTable();
        }
    }
});

on('SWITCH_TO_VANILLA', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Done', 'success');
        sendMessage('GET_STATUS');
    }
});

on('SYNC_OTHER_BRANCH', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Synced!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('CREATE_MY_BRANCH', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Branch created!', 'success');
        $('#inp-branch').value = '';
        sendMessage('GET_STATUS');
    }
});

on('SAVE_AND_PUSH_MY_BRANCH', async data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Pushed!', 'success');
        sendMessage('GET_STATUS');
    }
    if (data.status === 'conflict') {
        const choice = await showConflictDialog(
            data.payload?.message || '云端存在更新的配置，与本地改动冲突。'
        );
        if (choice === 'force') sendMessage('FORCE_PUSH');
        else if (choice === 'reset') sendMessage('RESET_TO_REMOTE');
    }
});

on('FORCE_PUSH', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Pushed!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('RESET_TO_REMOTE', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Synced!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('RESTORE_JUNCTION', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Restored!', 'success');
        sendMessage('GET_STATUS');
    }
});

// ── save redirect handlers ──────────────────────────────────────────────────

function updateRedirectCard(data) {
    const dot = $('#redirect-dot');
    const label = $('#redirect-label');
    const checkbox = $('#redirect-checkbox');

    if (!data.isJunctionActive) {
        dot.className = 'w-2 h-2 rounded-full bg-gray-500';
        label.textContent = '需要先连接 Mod';
        checkbox.checked = false;
        checkbox.disabled = true;
        return;
    }

    checkbox.disabled = false;

    if (!data.isModInstalled) {
        dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
        label.textContent = '辅助 Mod 未安装（启用时自动安装）';
        checkbox.checked = false;
    } else if (data.isEnabled) {
        dot.className = 'w-2 h-2 rounded-full bg-spire-success';
        label.textContent = '已启用';
        checkbox.checked = true;
    } else {
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = '未启用';
        checkbox.checked = false;
    }
}

on('GET_REDIRECT_STATUS', data => {
    if (data.status === 'success') {
        updateRedirectCard(data.payload);
    }
});

on('SET_REDIRECT', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '操作完成', 'success');
        sendMessage('GET_REDIRECT_STATUS');
    } else {
        // revert toggle on failure
        sendMessage('GET_REDIRECT_STATUS');
    }
});

// ── save management handlers ────────────────────────────────────────────────

function updateSaveBackupCard() {
    const btns = [$('#btn-backup-saves'), $('#btn-restore-saves')];
    const desc = $('#save-backup-desc');
    const openSaveBtn = $('#btn-open-save');

    if (savePathConfigured) {
        btns.forEach(b => { b.disabled = false; b.classList.remove('opacity-50', 'cursor-not-allowed'); });
        if (desc) desc.textContent = '手动备份或恢复游戏存档';
        openSaveBtn.disabled = false;
        openSaveBtn.classList.remove('opacity-50', 'cursor-not-allowed');
    } else {
        btns.forEach(b => { b.disabled = true; b.classList.add('opacity-50', 'cursor-not-allowed'); });
        if (desc) desc.textContent = '未配置存档路径，请在 Settings 中设置后使用';
        openSaveBtn.disabled = true;
        openSaveBtn.classList.add('opacity-50', 'cursor-not-allowed');
    }
}

on('GET_SAVE_STATUS', data => {
    if (data.status !== 'success') return;
    const p = data.payload;
    savePathConfigured = !!p.isConfigured;
    updateSaveBackupCard();
    // if saves are in merged state, prompt user to unlink
    if (p.isConfigured && (p.mergeState === 'linked' || p.mergeState === 'partial')) {
        $('#save-unlink-modal').classList.remove('hidden');
    }
});

on('UNLINK_SAVES', data => {
    const btn = $('#btn-do-unlink');
    if (data.status === 'success') {
        $('#save-unlink-modal').classList.add('hidden');
        btn.disabled = false;
        btn.textContent = '关闭存档合并';
        toast(data.payload?.message || '已关闭存档合并', 'success');
    } else {
        // show error inside the modal, keep it open
        btn.disabled = false;
        btn.textContent = '关闭存档合并';
        const errEl = $('#save-unlink-error');
        errEl.textContent = data.message || '操作失败，请重试';
        errEl.classList.remove('hidden');
    }
});

on('BACKUP_SAVES', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '备份完成', 'success');
    }
});

on('GET_BACKUP_LIST', data => {
    if (data.status === 'success') {
        renderBackupList(data.payload?.backups || []);
        $('#backup-list-modal').classList.remove('hidden');
    }
});

on('RESTORE_BACKUP', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '恢复完成', 'success');
        closeBackupListModal();
        sendMessage('GET_SAVE_STATUS');
    }
});

on('DELETE_BACKUP', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '已删除', 'success');
        // refresh the backup list
        sendMessage('GET_BACKUP_LIST');
    }
});

// folder picker response
on('PICK_FOLDER', data => {
    if (data.status === 'success' && data.payload?.path && pendingPickTarget) {
        pendingPickTarget.value = data.payload.path;
    }
    pendingPickTarget = null;
});


// ── UI event bindings ────────────────────────────────────────────────────────

// track which input to fill when folder picker returns
let pendingPickTarget = null;

// setup form
$('#setup-form').addEventListener('submit', e => {
    e.preventDefault();
    const authType = document.querySelector('input[name="authType"]:checked').value;
    const payload = {
        repoUrl: $('#cfg-repo').value.trim(),
        authType,
        gameInstallPath: $('#cfg-path').value.trim(),
        saveFolderPath: $('#cfg-save').value.trim(),
    };

    if (authType === 'ssh') {
        payload.sshKeyPath = $('#cfg-ssh-key').value.trim();
        payload.sshPassphrase = $('#cfg-ssh-pass').value.trim();
    } else if (authType === 'https') {
        payload.username = $('#cfg-user').value.trim();
        payload.token = $('#cfg-token').value.trim();
    }
    // anonymous: no extra fields

    sendMessage('INIT_CONFIG', payload);
});

// folder picker buttons
$('#btn-pick-game').addEventListener('click', () => {
    pendingPickTarget = $('#cfg-path');
    sendMessage('PICK_FOLDER');
});
$('#btn-pick-save').addEventListener('click', () => {
    pendingPickTarget = $('#cfg-save');
    sendMessage('PICK_FOLDER');
});

// refresh
guardClick($('#btn-refresh'), () => sendMessage('GET_STATUS'));

// mod toggle
$('#mod-checkbox').addEventListener('change', (e) => {
    if (e.target.checked) {
        sendMessage('RESTORE_JUNCTION');
    } else {
        sendMessage('SWITCH_TO_VANILLA');
    }
});

// ── branch modal ────────────────────────────────────────────────────────────

let branchData = [];
let branchCurrentName = '';
let branchSortKey = 'lastModified';
let branchSortAsc = false; // newest first by default

function openBranchModal() {
    sendMessage('GET_BRANCHES');
    renderBranchTable();
    $('#branch-modal').classList.remove('hidden');
}

function closeBranchModal() {
    $('#branch-modal').classList.add('hidden');
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

function renderBranchTable() {
    const tbody = $('#branch-table-body');
    const empty = $('#branch-empty');

    if (branchData.length === 0) {
        tbody.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    empty.classList.add('hidden');

    // sort a copy
    const sorted = [...branchData].sort((a, b) => {
        let va = a[branchSortKey], vb = b[branchSortKey];
        if (typeof va === 'string') { va = va.toLowerCase(); vb = vb.toLowerCase(); }
        if (va < vb) return branchSortAsc ? -1 : 1;
        if (va > vb) return branchSortAsc ? 1 : -1;
        return 0;
    });

    tbody.innerHTML = sorted.map(b => {
        const isCurrent = b.name === branchCurrentName;
        const rowHighlight = isCurrent ? 'bg-spire-accent/10' : 'hover:bg-spire-bg/50';
        const tag = isCurrent ? ' <span class="text-spire-accent text-[10px] ml-1">(当前)</span>' : '';
        return `<tr class="branch-row border-b border-spire-border/50 cursor-pointer transition-colors ${rowHighlight}" data-branch="${escAttr(b.name)}">
            <td class="px-4 py-2.5 font-mono text-xs">${esc(b.name)}${tag}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${esc(b.author)}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${formatRelativeTime(b.lastModified)}</td>
        </tr>`;
    }).join('');

    // bind row click
    tbody.querySelectorAll('.branch-row').forEach(row => {
        row.addEventListener('click', async () => {
            const name = row.dataset.branch;
            const ok = await showConfirm(
                `确定要强制同步到「${name}」？本地改动将被覆盖。`,
                '同步分支'
            );
            if (ok) {
                closeBranchModal();
                sendMessage('SYNC_OTHER_BRANCH', { branchName: name });
            }
        });
    });
}

// sort headers
document.querySelectorAll('.branch-sort-th').forEach(th => {
    th.addEventListener('click', () => {
        const key = th.dataset.key;
        if (branchSortKey === key) {
            branchSortAsc = !branchSortAsc;
        } else {
            branchSortKey = key;
            branchSortAsc = key === 'name'; // name defaults to A-Z, others default to desc
        }
        // update arrow indicators
        document.querySelectorAll('.branch-sort-th').forEach(h => {
            h.classList.remove('active-sort');
            const arrow = h.querySelector('.sort-arrow');
            if (arrow) arrow.remove();
        });
        th.classList.add('active-sort');
        th.insertAdjacentHTML('beforeend', ` <span class="sort-arrow">${branchSortAsc ? '▲' : '▼'}</span>`);
        renderBranchTable();
    });
});

$('#btn-browse-branches').addEventListener('click', openBranchModal);
$('#branch-modal-close').addEventListener('click', closeBranchModal);
// close modal when clicking backdrop
$('#branch-modal').addEventListener('click', e => {
    if (e.target === $('#branch-modal')) closeBranchModal();
});

// basic git branch name validation
function isValidBranchName(name) {
    if (/\s/.test(name)) return false;
    if (/\.\./.test(name)) return false;
    if (/[~^:\\\[\]{}]/.test(name)) return false;
    if (name.startsWith('.') || name.startsWith('-')) return false;
    if (name.endsWith('.') || name.endsWith('/') || name.endsWith('.lock')) return false;
    if (/\/\//.test(name)) return false;
    return true;
}

// create branch
guardClick($('#btn-create'), () => {
    const name = $('#inp-branch').value.trim();
    if (!name) { toast('请输入分支名称', 'error'); return; }
    if (!isValidBranchName(name)) { toast('分支名称格式无效（不能包含空格、..、~、^、:、\\、[ ] 等字符）', 'error'); return; }
    sendMessage('CREATE_MY_BRANCH', { branchName: name });
});

// save & push (with themed confirmation dialog showing target branch)
guardClick($('#btn-push'), async () => {
    const branch = currentBranch || '当前分支';
    const ok = await showConfirm(
        `此操作会将本地 Mod 文件夹的当前状态保存并上传到分支「${branch}」，覆盖云端配置，是否继续？`,
        '保存并上传'
    );
    if (ok) sendMessage('SAVE_AND_PUSH_MY_BRANCH');
});

// settings: go to setup page with config pre-filled
$('#btn-settings').addEventListener('click', () => {
    isEditMode = true;
    $('#setup-subtitle').textContent = '编辑配置';
    $('#btn-setup-back').classList.remove('hidden');
    sendMessage('GET_CONFIG');
    showPage('setup');
});

// back button on setup page — cancel edit, return to main
$('#btn-setup-back').addEventListener('click', () => {
    isEditMode = false;
    $('#btn-setup-back').classList.add('hidden');
    showPage('main');
});

// auth type toggle: show/hide relevant fields + swap active tab style
document.querySelectorAll('input[name="authType"]').forEach(radio => {
    radio.addEventListener('change', () => {
        setAuthType(radio.value);
    });
});

// quick-open folder buttons
$('#btn-open-mod').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'mod' }));
$('#btn-open-save').addEventListener('click', () => {
    if ($('#btn-open-save').disabled) return;
    sendMessage('OPEN_FOLDER', { folderType: 'save' });
});
$('#btn-open-config').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'config' }));


// ── save redirect toggle ────────────────────────────────────────────────────

$('#redirect-checkbox').addEventListener('change', (e) => {
    sendMessage('SET_REDIRECT', { enabled: e.target.checked });
});


// ── save backup buttons ─────────────────────────────────────────────────────

// migration modal: one-click unlink
$('#btn-do-unlink').addEventListener('click', () => {
    const btn = $('#btn-do-unlink');
    btn.disabled = true;
    btn.textContent = '正在处理...';
    $('#save-unlink-error').classList.add('hidden');
    sendMessage('UNLINK_SAVES');
});

guardClick($('#btn-backup-saves'), async () => {
    const ok = await showConfirm(
        '将备份整个存档文件夹到应用数据目录。\n可通过备份管理中的文件夹按钮查看备份位置。',
        '备份存档'
    );
    if (ok) sendMessage('BACKUP_SAVES');
});

$('#btn-restore-saves').addEventListener('click', () => {
    if ($('#btn-restore-saves').disabled) return;
    sendMessage('GET_BACKUP_LIST');
});

$('#btn-open-backup').addEventListener('click', () => {
    sendMessage('OPEN_FOLDER', { folderType: 'backup' });
});


// ── backup list modal ───────────────────────────────────────────────────────

function renderBackupList(backups) {
    const container = $('#backup-list-body');
    const empty = $('#backup-list-empty');

    if (backups.length === 0) {
        container.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    empty.classList.add('hidden');

    container.innerHTML = backups.map(b => {
        const typeBadge = b.type === 'save'
            ? '<span class="text-[10px] bg-spire-accent/20 text-spire-accent rounded px-1.5 py-0.5">存档</span>'
            : '<span class="text-[10px] bg-spire-warn/20 text-spire-warn rounded px-1.5 py-0.5">Mod</span>';

        const isSave = b.type === 'save';
        const safeName = esc(b.name);
        const safeAttrName = escAttr(b.name);

        return `
            <div class="flex items-center justify-between py-2.5 group">
                <div class="flex items-center gap-2 flex-1 min-w-0 ${isSave ? 'backup-row cursor-pointer' : ''}" data-backup="${safeAttrName}">
                    ${typeBadge}
                    <div class="min-w-0">
                        <div class="text-xs font-mono truncate">${safeName}</div>
                        <div class="text-[10px] text-spire-muted">${formatSize(b.sizeBytes)} · ${formatRelativeTime(b.createdAt)}</div>
                    </div>
                    ${isSave ? '<span class="text-[10px] text-spire-muted ml-auto shrink-0">点击恢复 →</span>' : ''}
                </div>
                <button class="backup-delete-btn text-spire-muted hover:text-spire-danger text-xs ml-2 opacity-0 group-hover:opacity-100 transition-opacity shrink-0" data-name="${safeAttrName}" title="删除备份">
                    <svg width="14" height="14" viewBox="0 0 20 20" fill="currentColor"><path fill-rule="evenodd" d="M9 2a1 1 0 00-.894.553L7.382 4H4a1 1 0 000 2v10a2 2 0 002 2h8a2 2 0 002-2V6a1 1 0 100-2h-3.382l-.724-1.447A1 1 0 0011 2H9zM7 8a1 1 0 012 0v6a1 1 0 11-2 0V8zm5-1a1 1 0 00-1 1v6a1 1 0 102 0V8a1 1 0 00-1-1z" clip-rule="evenodd"/></svg>
                </button>
            </div>
        `;
    }).join('');

    // bind click on save backup rows (for restore)
    container.querySelectorAll('.backup-row').forEach(row => {
        row.addEventListener('click', async () => {
            const name = row.dataset.backup;
            const ok = await showConfirm(
                `确定要恢复备份「${name}」？\n当前存档将被自动备份后替换。`,
                '恢复存档备份'
            );
            if (ok) {
                sendMessage('RESTORE_BACKUP', { backupName: name });
            }
        });
    });

    // bind delete buttons
    container.querySelectorAll('.backup-delete-btn').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.stopPropagation();
            const name = btn.dataset.name;
            const ok = await showConfirm(
                `确定要删除备份「${name}」？\n此操作不可撤销。`,
                '删除备份'
            );
            if (ok) {
                sendMessage('DELETE_BACKUP', { backupName: name });
            }
        });
    });
}

function closeBackupListModal() {
    $('#backup-list-modal').classList.add('hidden');
}

$('#backup-list-close').addEventListener('click', closeBackupListModal);
$('#backup-list-modal').addEventListener('click', e => {
    if (e.target === $('#backup-list-modal')) closeBackupListModal();
});


// ── about modal ──────────────────────────────────────────────────────────────

const REPO_URL = 'https://github.com/Ruikoto/sync-the-spire';
const AUTHOR_URL = 'https://github.com/Ruikoto';

const QUOTES = [
    '此事已成',
    '咔咔！',
    '咕嗷。。。',
    '你管这叫武器？',
    '败北？不可能的！！',
    '为什么你还在这里？',
    '永远别放弃！',
    '小的们，给我上！',
    '我们还没完呢！',
    '逃命哇！',
    '啊嗷嗷呜嗷！！',
    '在蓄能啦！',
    '要来咯！！',
    '把钱交出来！',
    '我的烟雾弹在哪儿呢……我找找……',
    '多谢你的钱啦，嘿嘿',
    '给我交出来！',
    '我可溜了！',
    '史莱姆撞击！！',
    '侦测到外来者',
    '你是我的了！',
    '啊……居然有人来了…',
    '愚蠢……何等愚蠢！',
    '一直……都不…喜欢你……',
    '嘎！哑！',
    '重生中……',
    '是时候了',
    '滴 答 滴 答',
    '撤退 撤退啊！',
    '你好啊！买点什么吧！买买买！',
    '你的发型很不错哦',
    '看起来你还挺闲？',
    '你是狗党还是猫党？',
    '你看起来很~危险~啊，嘿嘿……',
    '慢慢看…… 不慢也行',
    '你喜欢这张地毯吗？可惜这个不卖',
    '要支持我这样的小店啊！',
    '面具就是酷，我也是同党啊！',
    '多留会儿，听听音乐啊！',
    '一个人前进太危险了！把你的钱都给我吧！',
    '买点儿啥吧',
    '我喜欢金币。',
    '我最喜欢的颜色是蓝色。你呢？',
    '这可是最后一次买东西的机会咯！ *挤眼* *挤眼*',
    '其实呢，我是个猫党。',
    '不用着急，不用着急。',
    '曾经我也和你一样。',
    '唷，还往上爬呢？',
    '你好…… 我是… 涅奥……',
    '你又… 来啦……',
    '还想 再来……？',
    '见到 Boss... 就能获得 更多… 祝福……',
    '至少… 也要见到 第一个 Boss 吧……',
    '我把你 带回来了……',
    '选择……',
    '实现了……',
    '风险…… 与回报……',
    '来试试 挑战 吧……',
    '哎呀呀，这点金币可不够啊。',
    '嘿兄弟，你没钱啊！',
    '这个你买不起。',
    '我这儿不是做慈善的。',
    '谢 啦~',
    '又一笔买卖…… 嚯嚯嚯！有得赚！',
    '成交！',
    '概不退换。',
    '祝你顺利啊。',
    '面具就是酷，我也是同党啊！',
    '你有没有看见我的送货员？',
    '多留会儿，听听音乐啊！',
    '一个人前进太危险了！把你的钱都给我吧！',
    '这是… 怎么了…？…难道…真的… …做到了吗…？',
    '…高塔沉睡了… 那么… 我也… …该睡了……',
    '启程去屠戮这座高塔。',
];

function setRandomQuote(animate) {
    const el = $('#header-quote');
    const pick = () => {
        let q;
        do { q = QUOTES[Math.floor(Math.random() * QUOTES.length)]; } while (q === el.textContent && QUOTES.length > 1);
        return q;
    };
    if (!animate) { el.textContent = pick(); return; }
    el.style.transition = 'opacity 0.15s';
    el.style.opacity = '0';
    setTimeout(() => {
        el.textContent = pick();
        el.style.opacity = '1';
    }, 150);
}

$('#header-quote').addEventListener('click', () => setRandomQuote(true));

$('#about-repo').textContent = 'GitHub';
$('#about-author').textContent = 'Ruikoto（泡菜）';

// open external links via WebView2's built-in navigation handler
function openExternal(url) {
    window.open(url, '_blank');
}

$('#about-repo').addEventListener('click', e => { e.preventDefault(); openExternal(REPO_URL); });
$('#about-author').addEventListener('click', e => { e.preventDefault(); openExternal(AUTHOR_URL); });

$('#btn-about').addEventListener('click', () => {
    hideUpdateBadge();
    $('#about-modal').classList.remove('hidden');
});
$('#about-modal-close').addEventListener('click', () => {
    $('#about-modal').classList.add('hidden');
});
$('#about-modal').addEventListener('click', e => {
    if (e.target === $('#about-modal')) $('#about-modal').classList.add('hidden');
});


// ── update check ─────────────────────────────────────────────────────────────

const VERSION_CHECK_URL = 'https://sts.rkto.cc/version.json';
const ANNOUNCEMENTS_URL = 'https://sts.rkto.cc/announcements.json';
let latestVersionInfo = null;

function compareVersions(current, latest) {
    const parse = v => v.replace(/^v/i, '').split('.').map(Number);
    const c = parse(current), l = parse(latest);
    for (let i = 0; i < 3; i++) {
        if ((l[i] || 0) !== (c[i] || 0)) return (l[i] || 0) - (c[i] || 0);
    }
    return 0;
}

// decide which update tier the current client falls into
function getUpdateBehavior() {
    const info = latestVersionInfo;
    const hasThresholds = info.force_update_below || info.popup_update_below;

    if (hasThresholds) {
        if (info.force_update_below && compareVersions(appVersion, info.force_update_below) >= 0)
            return 'forced';
        if (info.popup_update_below && compareVersions(appVersion, info.popup_update_below) >= 0)
            return 'popup';
        return 'silent';
    }

    // legacy fallback — old server without threshold fields
    return info.force_update ? 'forced' : 'popup';
}

function showUpdateBadge() {
    const dot = $('#about-update-dot');
    if (dot) dot.classList.remove('hidden');
}

function hideUpdateBadge() {
    const dot = $('#about-update-dot');
    if (dot) dot.classList.add('hidden');
}

function updateAboutVersionStatus() {
    const row = $('#about-update-row');
    const latestEl = $('#about-latest');
    const dlBtn = $('#about-download');

    if (!latestVersionInfo) {
        row.classList.add('hidden');
        return;
    }

    row.classList.remove('hidden');
    const isNightly = appVersion.startsWith('nightly-');
    const hasUpdate = !isNightly && appVersion !== 'unknown' &&
                      compareVersions(appVersion, latestVersionInfo.latest_version) > 0;

    if (isNightly || hasUpdate) {
        latestEl.textContent = latestVersionInfo.latest_version;
        latestEl.className = 'font-mono text-xs text-spire-success';
        dlBtn.classList.remove('hidden');
    } else {
        latestEl.textContent = latestVersionInfo.latest_version + '（已是最新）';
        latestEl.className = 'font-mono text-xs text-spire-muted';
        dlBtn.classList.add('hidden');
    }
}

function getDownloadUrl() {
    if (!latestVersionInfo) return null;
    return appArch === 'arm64'
        ? latestVersionInfo.download_url_arm
        : latestVersionInfo.download_url_x64;
}

// dedicated update modal — supports changelog display and forced updates
function showUpdateModal(isForced) {
    const modal = $('#update-modal');
    const titleEl = $('#update-title');
    const changelogEl = $('#update-changelog');
    const closeBtn = $('#update-modal-close');
    const cancelBtn = $('#update-cancel');

    titleEl.textContent = '发现新版本';

    // show version comparison
    $('#update-version-info').textContent = `${appVersion} → ${latestVersionInfo.latest_version}`;

    // render changelog
    const changelog = latestVersionInfo.changelog;
    if (changelog) {
        changelogEl.textContent = changelog;
        changelogEl.classList.remove('hidden');
    } else {
        changelogEl.classList.add('hidden');
    }

    // forced update: hide close/cancel, block escape and backdrop click
    if (isForced) {
        closeBtn.classList.add('hidden');
        cancelBtn.classList.add('hidden');
    } else {
        closeBtn.classList.remove('hidden');
        cancelBtn.classList.remove('hidden');
    }

    modal.classList.remove('hidden');
}

function closeUpdateModal() {
    $('#update-modal').classList.add('hidden');
}

// update modal event bindings
$('#update-download').addEventListener('click', () => {
    const url = getDownloadUrl();
    if (url) openExternal(url);
});
$('#update-cancel').addEventListener('click', closeUpdateModal);
$('#update-modal-close').addEventListener('click', closeUpdateModal);
$('#update-modal').addEventListener('click', e => {
    // only close on backdrop if not forced
    if (e.target === $('#update-modal') && !$('#update-cancel').classList.contains('hidden')) {
        closeUpdateModal();
    }
});

async function checkForUpdates(silent = true) {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);
    try {
        const res = await fetch(VERSION_CHECK_URL, { cache: 'no-cache', signal: controller.signal });
        if (!res.ok) return;
        latestVersionInfo = await res.json();
        updateAboutVersionStatus();

        // nightly / dev / unknown builds: skip update prompts entirely
        if (!/^v?\d+\.\d+/.test(appVersion)) return;

        const hasUpdate = compareVersions(appVersion, latestVersionInfo.latest_version) > 0;

        if (hasUpdate) {
            const behavior = getUpdateBehavior();

            if (behavior === 'forced') {
                showUpdateModal(true);
            } else if (behavior === 'popup' || !silent) {
                // popup tier: show dismissible modal on startup
                // silent tier: only show when user manually checks
                showUpdateModal(false);
            }

            // badge hint for silent-tier updates
            if (behavior === 'silent') {
                showUpdateBadge();
            }
        } else if (!silent) {
            toast('当前已是最新版本', 'success');
        }
    } catch (e) {
        if (!silent) toast('检查更新失败，请检查网络连接', 'error');
    } finally {
        clearTimeout(timeout);
    }
}

$('#about-download').addEventListener('click', e => {
    e.preventDefault();
    const url = getDownloadUrl();
    if (url) openExternal(url);
});

$('#btn-check-update').addEventListener('click', () => checkForUpdates(false));


// ── announcements ─────────────────────────────────────────────────────────────

function getDismissedAnnouncements() {
    try {
        return JSON.parse(localStorage.getItem('dismissed_announcements') || '[]');
    } catch { return []; }
}

function dismissAnnouncement(id) {
    const dismissed = getDismissedAnnouncements();
    if (!dismissed.includes(id)) {
        dismissed.push(id);
        localStorage.setItem('dismissed_announcements', JSON.stringify(dismissed));
    }
}

async function checkAnnouncements() {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);
    try {
        const res = await fetch(ANNOUNCEMENTS_URL, { cache: 'no-cache', signal: controller.signal });
        if (!res.ok) return;
        const data = await res.json();
        const announcements = data.announcements || [];
        if (announcements.length === 0) return;

        const dismissed = getDismissedAnnouncements();
        const now = Date.now();
        const container = $('#announcement-container');
        container.innerHTML = '';

        const colorMap = {
            info: { border: 'border-spire-accent', bg: 'bg-spire-accent/10', text: 'text-spire-accent' },
            warning: { border: 'border-spire-warn', bg: 'bg-spire-warn/20', text: 'text-spire-warn' },
            error: { border: 'border-spire-danger', bg: 'bg-spire-danger/20', text: 'text-spire-danger' },
        };

        announcements.forEach(a => {
            // skip disabled, dismissed, or expired
            if (a.enabled === false) return;
            if (dismissed.includes(a.id)) return;
            if (a.expires_at && new Date(a.expires_at).getTime() < now) return;

            const colors = colorMap[a.type] || colorMap.info;
            const banner = document.createElement('div');
            banner.className = `announcement-banner rounded-lg border ${colors.border} ${colors.bg} p-2.5 flex items-start gap-2`;

            const content = document.createElement('div');
            content.className = 'flex-1 min-w-0';
            if (a.title) {
                const title = document.createElement('div');
                title.className = `text-sm font-bold ${colors.text}`;
                title.textContent = a.title;
                content.appendChild(title);
            }
            const body = document.createElement('div');
            body.className = 'text-xs text-spire-muted leading-relaxed announcement-body';
            body.textContent = a.content;
            content.appendChild(body);
            banner.appendChild(content);

            if (a.dismissible !== false) {
                const btn = document.createElement('button');
                btn.className = 'dismiss-btn text-spire-muted hover:text-spire-text text-lg leading-none transition-colors shrink-0 px-1';
                btn.innerHTML = '&times;';
                btn.addEventListener('click', () => {
                    dismissAnnouncement(a.id);
                    banner.style.opacity = '0';
                    banner.style.transition = 'opacity 0.3s';
                    setTimeout(() => banner.remove(), 300);
                });
                banner.appendChild(btn);
            }

            container.appendChild(banner);
        });
    } catch {
        // fail silently — announcements are non-critical
    } finally {
        clearTimeout(timeout);
    }
}


// ── title bar controls ──────────────────────────────────────────────────────

$('#titlebar-drag').addEventListener('mousedown', () => sendMessage('WINDOW_DRAG'));
$('#btn-minimize').addEventListener('click', () => sendMessage('WINDOW_MINIMIZE'));
$('#btn-maximize').addEventListener('click', () => sendMessage('WINDOW_MAXIMIZE'));
$('#btn-close').addEventListener('click', () => sendMessage('WINDOW_CLOSE'));

// double-click drag area to toggle maximize
$('#titlebar-drag').addEventListener('dblclick', () => sendMessage('WINDOW_MAXIMIZE'));


// ── help-tip tooltip (fixed position, viewport-clamped) ─────────────────────

(function () {
    const bubble = document.createElement('div');
    bubble.className = 'help-tip-bubble';
    document.body.appendChild(bubble);

    document.querySelectorAll('.help-tip').forEach(el => {
        el.addEventListener('mouseenter', () => {
            bubble.textContent = el.dataset.tip;
            bubble.classList.add('visible');

            // position above the icon, clamped within viewport
            const r = el.getBoundingClientRect();
            const bw = 260, pad = 8;
            let left = r.left + r.width / 2 - bw / 2;
            left = Math.max(pad, Math.min(left, window.innerWidth - bw - pad));
            bubble.style.left = left + 'px';
            bubble.style.top = (r.top - bubble.offsetHeight - 6) + 'px';
        });
        el.addEventListener('mouseleave', () => {
            bubble.classList.remove('visible');
        });
    });
})();


// ── bootstrap ────────────────────────────────────────────────────────────────

sendMessage('GET_VERSION');
sendMessage('GET_STATUS');
