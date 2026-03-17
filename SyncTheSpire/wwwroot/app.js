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

function updateStatusCard(data) {
    const dot = $('#status-dot');
    const label = $('#status-label');
    const branch = $('#status-branch');

    branch.textContent = data.currentBranch || '—';

    if (data.isJunctionActive) {
        dot.className = 'w-2 h-2 rounded-full bg-spire-success';
        label.textContent = 'Mod 已连接';
    } else {
        dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
        label.textContent = '纯净模式 (Mod 未连接)';
    }
}


// ── event handlers (from backend) ────────────────────────────────────────────

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        if (!payload.isConfigured) {
            showPage('setup');
        } else {
            showPage('main');
            updateStatusCard(payload);
            // also grab branches for the dropdown
            sendMessage('GET_BRANCHES');
        }
    }
});

on('INIT_CONFIG', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || 'Done!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('GET_BRANCHES', data => {
    if (data.status === 'success') {
        const sel = $('#sel-branch');
        const branches = data.payload?.branches || [];
        const current = data.payload?.currentBranch || '';

        sel.innerHTML = '';
        if (branches.length === 0) {
            sel.innerHTML = '<option value="">无可用分支</option>';
            return;
        }

        branches.forEach(b => {
            const opt = document.createElement('option');
            opt.value = b;
            opt.textContent = b + (b === current ? ' (当前)' : '');
            sel.appendChild(opt);
        });
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


// ── UI event bindings ────────────────────────────────────────────────────────

// setup form
$('#setup-form').addEventListener('submit', e => {
    e.preventDefault();
    sendMessage('INIT_CONFIG', {
        repoUrl: $('#cfg-repo').value.trim(),
        username: $('#cfg-user').value.trim(),
        token: $('#cfg-token').value.trim(),
        gameModPath: $('#cfg-path').value.trim(),
    });
});

// refresh
$('#btn-refresh').addEventListener('click', () => sendMessage('GET_STATUS'));

// vanilla mode
$('#btn-vanilla').addEventListener('click', () => {
    if (confirm('确定要断开 Mod 连接吗？（不会删除 Mod 文件）'))
        sendMessage('SWITCH_TO_VANILLA');
});

// restore junction
$('#btn-restore').addEventListener('click', () => sendMessage('RESTORE_JUNCTION'));

// sync branch
$('#btn-sync').addEventListener('click', () => {
    const branch = $('#sel-branch').value;
    if (!branch) { toast('请先选择一个分支', 'error'); return; }
    if (confirm(`确定要强制同步到 ${branch}？本地改动将被覆盖。`))
        sendMessage('SYNC_OTHER_BRANCH', { branchName: branch });
});

// create branch
$('#btn-create').addEventListener('click', () => {
    const name = $('#inp-branch').value.trim();
    if (!name) { toast('请输入分支名称', 'error'); return; }
    sendMessage('CREATE_MY_BRANCH', { branchName: name });
});

// save & push
$('#btn-push').addEventListener('click', () => sendMessage('SAVE_AND_PUSH_MY_BRANCH'));

// settings: go back to setup page (keep it simple for now)
$('#btn-settings').addEventListener('click', () => showPage('setup'));


// ── bootstrap ────────────────────────────────────────────────────────────────

sendMessage('GET_STATUS');
