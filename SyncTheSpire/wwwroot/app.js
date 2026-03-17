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
        handlers[event].forEach(fn => fn(data));
    }
});


// ── UI helpers ───────────────────────────────────────────────────────────────

const $ = (sel) => document.querySelector(sel);

let currentBranch = '';
let needsBranchSelection = false;

function showPage(name) {
    $('#page-setup').classList.add('hidden');
    $('#page-setup').classList.remove('flex');
    $('#page-main').classList.add('hidden');
    $('#page-main').classList.remove('flex');

    const el = $(`#page-${name}`);
    el.classList.remove('hidden');
    el.classList.add('flex');
}

function showLoading(text) {
    $('#loading-text').textContent = text;
    $('#loading-overlay').classList.remove('hidden');
}

function hideLoading() {
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

    el.className = `toast border rounded-lg px-4 py-2 text-sm max-w-xs ${bgMap[type] || bgMap.info}`;
    el.textContent = message;
    container.appendChild(el);

    // auto-dismiss
    setTimeout(() => {
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.3s';
        setTimeout(() => el.remove(), 300);
    }, 4000);
}

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

    currentBranch = data.currentBranch || '';
    needsBranchSelection = !!data.needsBranchSelection;

    if (needsBranchSelection) {
        branch.textContent = '未选择';
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = '请通过下方选择一个分支开始';
    } else {
        branch.textContent = currentBranch || '—';
        if (data.isJunctionActive) {
            dot.className = 'w-2 h-2 rounded-full bg-spire-success';
            label.textContent = 'Mod 已连接';
        } else {
            dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
            label.textContent = '纯净模式 (Mod 未连接)';
        }
    }

    updatePushButton();
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

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        if (!payload.isConfigured) {
            // first-time setup: request saved config for pre-fill
            sendMessage('GET_CONFIG');
            showPage('setup');
        } else {
            showPage('main');
            updateStatusCard(payload);
            // pre-fetch branches so the modal opens instantly
            sendMessage('GET_BRANCHES');
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

on('SAVE_AND_PUSH_MY_BRANCH', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Pushed!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('RESTORE_JUNCTION', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Restored!', 'success');
        sendMessage('GET_STATUS');
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
$('#btn-refresh').addEventListener('click', () => sendMessage('GET_STATUS'));

// vanilla mode
$('#btn-vanilla').addEventListener('click', async () => {
    const ok = await showConfirm(
        '确定要断开 Mod 连接吗？（不会删除 Mod 文件）',
        '切换到纯净模式'
    );
    if (ok) sendMessage('SWITCH_TO_VANILLA');
});

// restore junction
$('#btn-restore').addEventListener('click', () => sendMessage('RESTORE_JUNCTION'));

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
        return `<tr class="branch-row border-b border-spire-border/50 cursor-pointer transition-colors ${rowHighlight}" data-branch="${b.name}">
            <td class="px-4 py-2.5 font-mono text-xs">${b.name}${tag}</td>
            <td class="px-4 py-2.5 text-xs text-spire-muted">${b.author}</td>
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

// create branch
$('#btn-create').addEventListener('click', () => {
    const name = $('#inp-branch').value.trim();
    if (!name) { toast('请输入分支名称', 'error'); return; }
    sendMessage('CREATE_MY_BRANCH', { branchName: name });
});

// save & push (with themed confirmation dialog showing target branch)
$('#btn-push').addEventListener('click', async () => {
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
    sendMessage('GET_CONFIG');
    showPage('setup');
});

// auth type toggle: show/hide relevant fields + swap active tab style
document.querySelectorAll('input[name="authType"]').forEach(radio => {
    radio.addEventListener('change', () => {
        setAuthType(radio.value);
    });
});

// quick-open folder buttons
$('#btn-open-mod').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'mod' }));
$('#btn-open-save').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'save' }));
$('#btn-open-config').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'config' }));


// ── title bar controls ──────────────────────────────────────────────────────

$('#titlebar-drag').addEventListener('mousedown', () => sendMessage('WINDOW_DRAG'));
$('#btn-minimize').addEventListener('click', () => sendMessage('WINDOW_MINIMIZE'));
$('#btn-maximize').addEventListener('click', () => sendMessage('WINDOW_MAXIMIZE'));
$('#btn-close').addEventListener('click', () => sendMessage('WINDOW_CLOSE'));

// double-click drag area to toggle maximize
$('#titlebar-drag').addEventListener('dblclick', () => sendMessage('WINDOW_MAXIMIZE'));


// ── bootstrap ────────────────────────────────────────────────────────────────

sendMessage('GET_STATUS');
