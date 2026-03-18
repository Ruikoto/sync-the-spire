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
let saveMergeState = null;
let mergeCompareData = null;
let appVersion = '';

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

    el.className = `toast border rounded-lg px-4 py-2 text-sm max-w-xs cursor-pointer ${bgMap[type] || bgMap.info}`;
    el.textContent = message;
    container.appendChild(el);

    const dismiss = () => {
        el.style.opacity = '0';
        el.style.transition = 'opacity 0.3s';
        setTimeout(() => el.remove(), 300);
    };

    el.addEventListener('click', dismiss);

    // auto-dismiss
    const timer = setTimeout(dismiss, 4000);
    el.addEventListener('click', () => clearTimeout(timer));
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
        $('#btn-vanilla').classList.add('hidden');
        $('#btn-restore').classList.add('hidden');
    } else {
        branch.textContent = currentBranch || '—';
        if (data.isJunctionActive) {
            dot.className = 'w-2 h-2 rounded-full bg-spire-success';
            label.textContent = 'Mod 已连接';
            $('#btn-vanilla').classList.remove('hidden');
            $('#btn-restore').classList.add('hidden');
        } else {
            dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
            label.textContent = '纯净模式 (Mod 未连接)';
            $('#btn-vanilla').classList.add('hidden');
            $('#btn-restore').classList.remove('hidden');
        }
    }

    updatePushButton();
}

// ── save merge card helpers ──────────────────────────────────────────────────

function formatSize(bytes) {
    if (bytes == null) return '—';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(ms) {
    if (!ms) return '—';
    return new Date(ms).toLocaleString('zh-CN', {
        month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit'
    });
}

function updateSaveMergeCard(data) {
    const dot = $('#save-merge-dot');
    const label = $('#save-merge-label');
    const btnMerge = $('#btn-merge-saves');
    const btnUnlink = $('#btn-unlink-saves');

    if (!data || !data.isConfigured) {
        saveMergeState = null;
        dot.className = 'w-2 h-2 rounded-full bg-gray-500';
        label.textContent = '存档路径未配置';
        btnMerge.disabled = true;
        btnMerge.classList.add('opacity-50', 'cursor-not-allowed');
        btnMerge.classList.remove('hidden');
        btnUnlink.classList.add('hidden');
        return;
    }

    saveMergeState = data.mergeState;

    if (data.mergeState === 'linked') {
        dot.className = 'w-2 h-2 rounded-full bg-spire-success';
        label.textContent = '已合并 — 普通存档与 Mod 存档共享';
        btnMerge.classList.add('hidden');
        btnUnlink.classList.remove('hidden');
        btnUnlink.disabled = false;
        btnUnlink.classList.remove('opacity-50', 'cursor-not-allowed');
    } else if (data.mergeState === 'partial') {
        dot.className = 'w-2 h-2 rounded-full bg-spire-warn';
        label.textContent = '部分合并 — 存在异常状态';
        btnMerge.classList.remove('hidden');
        btnMerge.disabled = false;
        btnMerge.classList.remove('opacity-50', 'cursor-not-allowed');
        btnUnlink.classList.remove('hidden');
        btnUnlink.disabled = false;
        btnUnlink.classList.remove('opacity-50', 'cursor-not-allowed');
    } else {
        // "unlinked" or "no_modded"
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = data.mergeState === 'no_modded'
            ? '未合并 — Mod 存档文件夹不存在'
            : '未合并 — 普通存档与 Mod 存档独立';
        btnMerge.classList.remove('hidden');
        btnMerge.disabled = false;
        btnMerge.classList.remove('opacity-50', 'cursor-not-allowed');
        btnUnlink.classList.add('hidden');
    }
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

on('GET_VERSION', data => {
    if (data.status !== 'success') return;
    appVersion = data.payload?.version || 'unknown';
    $('#about-version').textContent = appVersion;
    if (appVersion.startsWith('nightly-')) {
        toast('当前为 Nightly 构建版本，建议前往 About 页面下载最新正式版', 'info');
    }
});

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        if (!payload.isConfigured) {
            // first-time setup: request saved config for pre-fill
            $('#btn-setup-back').classList.add('hidden');
            sendMessage('GET_CONFIG');
            showPage('setup');
        } else {
            showPage('main');
            updateStatusCard(payload);
            // pre-fetch branches so the modal opens instantly
            sendMessage('GET_BRANCHES');
            sendMessage('GET_SAVE_STATUS');
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

// ── save management handlers ────────────────────────────────────────────────

on('GET_SAVE_STATUS', data => {
    if (data.status === 'success') {
        updateSaveMergeCard(data.payload);
    }
});

on('ANALYZE_SAVE_MERGE', data => {
    if (data.status !== 'success') return;

    if (!data.payload.needsComparison) {
        // no modded data to compare, go straight with a confirm
        showConfirm(
            'Mod 存档文件夹不存在或为空，合并后将自动创建并链接到普通存档。\n操作前会自动备份所有存档，如不放心可在备份管理中手动备份。',
            '合并存档'
        ).then(ok => {
            if (ok) sendMessage('EXECUTE_SAVE_MERGE', {});
        });
    } else {
        mergeCompareData = data.payload.profiles;
        openMergeCompareModal();
    }
});

on('EXECUTE_SAVE_MERGE', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '合并完成', 'success');
        sendMessage('GET_SAVE_STATUS');
    }
});

on('UNLINK_SAVES', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || '已取消合并', 'success');
        sendMessage('GET_SAVE_STATUS');
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
$('#btn-open-save').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'save' }));
$('#btn-open-config').addEventListener('click', () => sendMessage('OPEN_FOLDER', { folderType: 'config' }));


// ── save merge & backup buttons ────────────────────────────────────────────

$('#btn-merge-saves').addEventListener('click', () => {
    sendMessage('ANALYZE_SAVE_MERGE');
});

$('#btn-unlink-saves').addEventListener('click', async () => {
    const ok = await showConfirm(
        '取消合并后，Mod 存档将变为普通存档的独立副本，两者不再共享数据。\n操作前会自动备份所有存档。',
        '取消存档合并'
    );
    if (ok) sendMessage('UNLINK_SAVES');
});

$('#btn-backup-saves').addEventListener('click', async () => {
    const ok = await showConfirm(
        '将备份整个存档文件夹到应用数据目录。\n可通过备份管理中的文件夹按钮查看备份位置。',
        '备份存档'
    );
    if (ok) sendMessage('BACKUP_SAVES');
});

$('#btn-restore-saves').addEventListener('click', () => {
    sendMessage('GET_BACKUP_LIST');
});

$('#btn-open-backup').addEventListener('click', () => {
    sendMessage('OPEN_FOLDER', { folderType: 'backup' });
});


// ── merge comparison modal ──────────────────────────────────────────────────

function openMergeCompareModal() {
    const tbody = $('#merge-compare-body');
    tbody.innerHTML = '';

    mergeCompareData.forEach(p => {
        const hasNormal = p.normal != null;
        const hasModded = p.modded != null;

        const normalLabel = hasNormal
            ? `${formatSize(p.normal.sizeBytes)}<br><span class="text-spire-muted">${formatDate(p.normal.lastModified)}</span>`
            : '<span class="text-spire-muted">不存在</span>';

        const moddedLabel = hasModded
            ? `${formatSize(p.modded.sizeBytes)}<br><span class="text-spire-muted">${formatDate(p.modded.lastModified)}</span>`
            : '<span class="text-spire-muted">不存在</span>';

        // highlight the newer one
        const normalNewer = hasNormal && hasModded && p.normal.lastModified >= p.modded.lastModified;
        const moddedNewer = hasNormal && hasModded && p.modded.lastModified > p.normal.lastModified;

        const canChoose = hasNormal && hasModded;
        const defaultChoice = p.recommendation || 'normal';

        const row = document.createElement('tr');
        row.className = 'border-b border-spire-border/50';
        row.innerHTML = `
            <td class="py-2.5 px-2 font-mono">${p.name}</td>
            <td class="py-2.5 px-2 ${normalNewer ? 'text-spire-success' : ''}">${normalLabel}</td>
            <td class="py-2.5 px-2 ${moddedNewer ? 'text-spire-success' : ''}">${moddedLabel}</td>
            <td class="py-2.5 px-2 text-center">
                ${canChoose ? `
                    <label class="inline-flex items-center gap-1 mr-2 cursor-pointer">
                        <input type="radio" name="merge-${p.name}" value="normal"
                            ${defaultChoice === 'normal' ? 'checked' : ''} />
                        <span>普通</span>
                    </label>
                    <label class="inline-flex items-center gap-1 cursor-pointer">
                        <input type="radio" name="merge-${p.name}" value="modded"
                            ${defaultChoice === 'modded' ? 'checked' : ''} />
                        <span>Mod</span>
                    </label>
                ` : `<span class="text-spire-muted">自动</span>`}
            </td>
        `;
        tbody.appendChild(row);
    });

    $('#merge-compare-modal').classList.remove('hidden');
}

function closeMergeCompareModal() {
    $('#merge-compare-modal').classList.add('hidden');
}

function collectMergeChoices() {
    const choices = {};
    mergeCompareData.forEach(p => {
        const radio = document.querySelector(`input[name="merge-${p.name}"]:checked`);
        choices[p.name] = radio ? radio.value : 'normal';
    });
    return choices;
}

$('#merge-compare-confirm').addEventListener('click', () => {
    const choices = collectMergeChoices();
    closeMergeCompareModal();
    sendMessage('EXECUTE_SAVE_MERGE', { choices });
});

$('#merge-compare-cancel').addEventListener('click', closeMergeCompareModal);
$('#merge-compare-close').addEventListener('click', closeMergeCompareModal);
$('#merge-compare-modal').addEventListener('click', e => {
    if (e.target === $('#merge-compare-modal')) closeMergeCompareModal();
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

        return `
            <div class="flex items-center justify-between py-2.5 group">
                <div class="flex items-center gap-2 flex-1 min-w-0 ${isSave ? 'backup-row cursor-pointer' : ''}" data-backup="${b.name}">
                    ${typeBadge}
                    <div class="min-w-0">
                        <div class="text-xs font-mono truncate">${b.name}</div>
                        <div class="text-[10px] text-spire-muted">${formatSize(b.sizeBytes)} · ${formatRelativeTime(b.createdAt)}</div>
                    </div>
                    ${isSave ? '<span class="text-[10px] text-spire-muted ml-auto shrink-0">点击恢复 →</span>' : ''}
                </div>
                <button class="backup-delete-btn text-spire-muted hover:text-spire-danger text-xs ml-2 opacity-0 group-hover:opacity-100 transition-opacity shrink-0" data-name="${b.name}" title="删除备份">
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

$('#about-repo').textContent = 'GitHub';
$('#about-author').textContent = 'Ruikoto（泡菜）';

// open external links via WebView2's built-in navigation handler
function openExternal(url) {
    window.open(url, '_blank');
}

$('#about-repo').addEventListener('click', e => { e.preventDefault(); openExternal(REPO_URL); });
$('#about-author').addEventListener('click', e => { e.preventDefault(); openExternal(AUTHOR_URL); });

$('#btn-about').addEventListener('click', () => {
    $('#about-modal').classList.remove('hidden');
});
$('#about-modal-close').addEventListener('click', () => {
    $('#about-modal').classList.add('hidden');
});
$('#about-modal').addEventListener('click', e => {
    if (e.target === $('#about-modal')) $('#about-modal').classList.add('hidden');
});


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
