'use strict';

// open all markdown-rendered links in external browser
const renderer = new marked.Renderer();
renderer.link = ({ href, text }) =>
    `<a href="${href}" target="_blank" rel="noopener noreferrer">${text}</a>`;
marked.setOptions({ renderer });


// ── event handlers (from backend) ────────────────────────────────────────────

on('GET_VERSION', data => {
    if (data.status !== 'success') return;
    AppState.appVersion = data.payload?.version || 'unknown';
    AppState.appArch = data.payload?.arch || 'x64';
    AppState.appDistribution = data.payload?.distribution || 'direct';
    $('#about-version').textContent = AppState.appVersion;
    if (!/^v?\d+\.\d+/.test(AppState.appVersion)) {
        toast('当前为非正式构建版本，建议前往 About 页面下载最新正式版', 'info');
    }
    checkForUpdates();
    checkAnnouncements();
});

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        const ws = getWsState();

        // stash capabilities from backend
        if (payload.capabilities) {
            ws.capabilities = payload.capabilities;
        }

        if (!payload.isConfigured) {
            // setup page: request saved config for pre-fill
            $('#setup-subtitle').textContent = '首次配置 — 填写以下信息开始同步';
            updateSetupPageTitle();
            adaptSetupFormForGameType(ws.capabilities);
            sendMessage('GET_CONFIG');
            showPage('setup');
            // show welcome guide on first launch — only for sts2
            if (!isEditMode && ws.capabilities?.supportsAutoFind) {
                welcomeAutoOpened = true;
                showWelcomeModal();
            }
        } else {
            showPage('main');
            // update header with workspace info
            const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
            if (wsInfo) {
                const icon = $('#header-game-icon');
                icon.innerHTML = gameIcon(wsInfo.gameType, 18);
                icon.className = `flex items-center game-badge-${wsInfo.gameType}`;
                $('#header-ws-name').textContent = wsInfo.name;
            }
            updateStatusCard(payload);
            updateDashboardForCapabilities(ws.capabilities);
            // default to disabled until GET_SAVE_STATUS confirms save path
            ws.savePathConfigured = false;
            updateSaveBackupCard();
            sendMessage('GET_SAVE_STATUS');
            sendMessage('GET_REDIRECT_STATUS');
            // auto-refresh on startup if a branch is active
            if (!ws.needsBranchSelection && ws.currentBranch) {
                $('#refresh-icon').style.animation = 'spin 0.7s linear infinite';
                $('#refresh-label').textContent = '刷新中...';
                sendMessage('REFRESH_SYNC');
            }
        }
    }
});

on('REFRESH_SYNC', data => {
    // always stop spinner regardless of status
    $('#refresh-icon').style.animation = '';
    $('#refresh-label').textContent = '刷新';
    // stale response from a previous workspace — ignore if main page isn't visible
    if ($('#page-main').classList.contains('hidden')) return;
    if (data.status === 'success') {
        updateStatusCard(data.payload);
        // silently pre-fetch branches so the modal opens instantly later
        sendMessage('GET_BRANCHES');
        sendMessage('GET_SAVE_STATUS');
        sendMessage('GET_REDIRECT_STATUS');
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
        getWsState().lastSyncStatus = null;
        toast(data.payload?.message || 'Synced!', 'success');
        sendMessage('GET_STATUS');
    }
});

on('CREATE_MY_BRANCH', data => {
    if (data.status === 'success') {
        getWsState().lastSyncStatus = null;
        toast(data.payload?.message || 'Branch created!', 'success');
        $('#inp-branch').value = '';
        sendMessage('GET_STATUS');
    }
});

on('SAVE_AND_PUSH_MY_BRANCH', async data => {
    if (data.status === 'success') {
        getWsState().lastSyncStatus = null;
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

// ── save redirect handlers ───────────────────────────────────────────────────

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

// ── save management handlers ─────────────────────────────────────────────────

on('GET_SAVE_STATUS', data => {
    if (data.status !== 'success') return;
    const p = data.payload;
    getWsState().savePathConfigured = !!p.isConfigured;
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
    const nickname = $('#cfg-nickname').value.trim();

    if (!nickname) {
        toast('请填写昵称', 'error');
        $('#cfg-nickname').focus();
        return;
    }

    const payload = {
        nickname,
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

// steam auto-find buttons
$('#btn-find-game').addEventListener('click', async () => {
    const result = await ipcCall('FIND_GAME_PATH');
    if (result.status === 'success' && result.payload?.path) {
        $('#cfg-path').value = result.payload.path;
        toast('已自动检测到游戏安装路径', 'success');
    }
});

$('#btn-find-save').addEventListener('click', async () => {
    const result = await ipcCall('FIND_SAVE_PATH');
    if (result.status === 'success' && result.payload) {
        const path = await pickSteamAccount(result.payload);
        if (path) {
            $('#cfg-save').value = path;
            toast('已自动检测到存档路径', 'success');
        }
    }
});

// refresh -- fetch remote and show sync status
guardClick($('#btn-refresh'), () => {
    $('#refresh-icon').style.animation = 'spin 0.7s linear infinite';
    $('#refresh-label').textContent = '刷新中...';
    sendMessage('REFRESH_SYNC');
});

// click sync status to dismiss it
$('#sync-status-line').addEventListener('click', () => {
    const el = $('#sync-status-line');
    el.style.transition = 'opacity 0.15s';
    el.style.opacity = '0';
    setTimeout(() => { el.classList.add('hidden'); el.style.transition = 'none'; el.style.opacity = '1'; }, 150);
});

// folder dropdown
$('#btn-open-folder').addEventListener('click', (e) => {
    e.stopPropagation();
    const dd = $('#folder-dropdown');
    const isHidden = dd.classList.contains('hidden');
    dd.classList.toggle('hidden', !isHidden);

    if (isHidden) {
        // conditionally show/hide items
        const modItem = $('#folder-item-mod');
        const saveItem = $('#folder-item-save');
        modItem.classList.toggle('hidden', !$('#mod-checkbox').checked);
        saveItem.classList.toggle('hidden', !getWsState().savePathConfigured);
    }
});
document.addEventListener('click', () => {
    $('#folder-dropdown').classList.add('hidden');
});
$('#folder-dropdown').addEventListener('click', (e) => {
    const item = e.target.closest('.folder-item');
    if (!item) return;
    sendMessage('OPEN_FOLDER', { folderType: item.dataset.folder });
    $('#folder-dropdown').classList.add('hidden');
});

// mod toggle
$('#mod-checkbox').addEventListener('change', (e) => {
    if (e.target.checked) {
        sendMessage('RESTORE_JUNCTION');
    } else {
        sendMessage('SWITCH_TO_VANILLA');
    }
});

// create branch
guardClick($('#btn-create'), () => {
    const name = $('#inp-branch').value.trim();
    if (!name) { toast('请输入分支名称', 'error'); return; }
    if (!isValidBranchName(name)) { toast('分支名称格式无效（不能包含空格、..、~、^、:、\\、[ ] 等字符）', 'error'); return; }
    sendMessage('CREATE_MY_BRANCH', { branchName: name });
});

// push -- save local changes and upload to current branch
guardClick($('#btn-push'), async () => {
    if (!getWsState().currentBranch) return;
    sendMessage('SAVE_AND_PUSH_MY_BRANCH');
});

// pull -- fetch latest from remote, overwrite local
guardClick($('#btn-pull'), async () => {
    const ws = getWsState();
    if (!ws.currentBranch) return;
    if (ws.lastHasLocalChanges || (ws.lastSyncStatus && ws.lastSyncStatus.ahead > 0)) {
        const ok = await showConfirm(
            '本地有未上传的改动，拉取会覆盖这些改动。确定继续？',
            '拉取远端内容'
        );
        if (!ok) return;
    }
    sendMessage('SYNC_OTHER_BRANCH', { branchName: ws.currentBranch });
});

// settings: go to setup page with config pre-filled
$('#btn-settings').addEventListener('click', () => {
    isEditMode = true;
    $('#setup-subtitle').textContent = '编辑配置';
    updateSetupPageTitle();
    adaptSetupFormForGameType(getWsState().capabilities);
    sendMessage('GET_CONFIG');
    showPage('setup');
});

// back button on setup page — return to dashboard or home
$('#btn-setup-back').addEventListener('click', () => {
    if (isEditMode) {
        // was editing, go back to dashboard
        isEditMode = false;
        showPage('main');
    } else {
        // was in first-time setup, go back to home
        showPage('home');
    }
});

// auth type toggle: show/hide relevant fields + swap active tab style
document.querySelectorAll('input[name="authType"]').forEach(radio => {
    radio.addEventListener('change', () => {
        setAuthType(radio.value);
    });
});

// ── save redirect toggle ────────────────────────────────────────────────────

$('#redirect-checkbox').addEventListener('change', (e) => {
    sendMessage('SET_REDIRECT', { enabled: e.target.checked });
});


// ── save backup buttons ──────────────────────────────────────────────────────

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


// ── close any closeable modal on Escape ──────────────────────────────────────

document.addEventListener('keydown', e => {
    if (e.key !== 'Escape') return;
    // branch modal: if preview is showing, go back to list first
    const bm = $('#branch-modal');
    if (bm && !bm.classList.contains('hidden')) {
        if (!$('#branch-preview-view').classList.contains('hidden')) {
            showBranchListView();
        } else {
            closeBranchModal();
        }
        return;
    }
    const modals = ['#welcome-modal', '#backup-list-modal', '#about-modal', '#conflict-modal'];
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


// ── title bar controls ───────────────────────────────────────────────────────

$('#titlebar-drag').addEventListener('mousedown', () => sendMessage('WINDOW_DRAG'));
$('#btn-minimize').addEventListener('click', () => sendMessage('WINDOW_MINIMIZE'));
$('#btn-maximize').addEventListener('click', () => sendMessage('WINDOW_MAXIMIZE'));
$('#btn-close').addEventListener('click', () => sendMessage('WINDOW_CLOSE'));

// double-click drag area to toggle maximize
$('#titlebar-drag').addEventListener('dblclick', () => sendMessage('WINDOW_MAXIMIZE'));


// ── help-tip tooltip (fixed position, viewport-clamped) ──────────────────────

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


// ── tab bar rendering + workspace switching ──────────────────────────────────

function renderTabBar() {
    const tabBar = $('#tab-bar');
    if (!tabBar) return;

    tabBar.innerHTML = AppState.openTabs.map(id => {
        const ws = AppState.workspaces[id];
        if (!ws) return '';
        const isActive = id === AppState.activeWorkspaceId;
        return `
            <div class="tab-item${isActive ? ' active' : ''} h-full flex items-center gap-1.5 px-3 text-xs text-spire-muted shrink-0" data-tab="${escAttr(id)}">
                <span class="game-badge-${ws.gameType}" style="display:flex;">${gameIcon(ws.gameType, 12)}</span>
                <span class="truncate max-w-[120px]">${esc(ws.name)}</span>
                <button class="tab-close ml-1 text-spire-muted hover:text-spire-danger text-xs leading-none" data-tab-close="${escAttr(id)}" title="关闭标签页">&times;</button>
            </div>`;
    }).join('');

    // bind tab click events
    tabBar.querySelectorAll('.tab-item').forEach(tab => {
        tab.addEventListener('click', (e) => {
            if (e.target.closest('.tab-close')) return;
            const id = tab.dataset.tab;
            // always switch if we're not on the main/setup page for this workspace
            const currentPage = !$('#page-main').classList.contains('hidden') || !$('#page-setup').classList.contains('hidden');
            if (id !== AppState.activeWorkspaceId || !currentPage) {
                switchToWorkspace(id);
            }
        });
        // middle-click to close tab
        tab.addEventListener('auxclick', (e) => {
            if (e.button === 1) {
                e.preventDefault();
                closeWorkspaceTab(tab.dataset.tab);
            }
        });
    });

    // bind close buttons
    tabBar.querySelectorAll('[data-tab-close]').forEach(btn => {
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            closeWorkspaceTab(btn.dataset.tabClose);
        });
    });
}

async function switchToWorkspace(id) {
    try {
        const res = await ipcCall('SWITCH_WORKSPACE', { id });
        if (res.status !== 'success') throw new Error(res.message || '切换失败');
        AppState.activeWorkspaceId = res.payload.id;
        renderTabBar();
        // clear stale branch data from previous workspace
        clearBranchCache();
        sendMessage('GET_STATUS');
    } catch (err) {
        toast('切换失败：' + err.message, 'error');
    }
}

async function closeWorkspaceTab(id) {
    try {
        const res = await ipcCall('CLOSE_WORKSPACE_TAB', { id });
        if (res.status !== 'success') throw new Error(res.message || '关闭失败');
        AppState.openTabs = res.payload.openTabs || [];
        AppState.activeWorkspaceId = res.payload.activeWorkspace;
        renderTabBar();

        if (res.payload.activeWorkspace) {
            // clear cached branch data when switching context
            clearBranchCache();
            sendMessage('GET_STATUS');
        } else {
            // no more tabs open, go to home
            goHome();
        }
    } catch (err) {
        toast('关闭标签页失败：' + err.message, 'error');
    }
}

function goHome() {
    updateTabBarActive('home');
    renderWorkspaceGrid();
    showPage('home');
}

// bind home tab click
$('#tab-home')?.addEventListener('click', () => goHome());

// ── bootstrap ────────────────────────────────────────────────────────────────

async function bootstrap() {
    sendMessage('GET_VERSION');

    // fetch available game types
    try {
        const res = await ipcCall('GET_GAME_TYPES');
        cachedGameTypes = (res.status === 'success' && Array.isArray(res.payload)) ? res.payload : [];
    } catch { cachedGameTypes = []; }

    // fetch all workspaces and tab state
    try {
        const res = await ipcCall('GET_WORKSPACES');
        if (res.status !== 'success' || !res.payload) {
            throw new Error(res.message || '获取工作区失败');
        }
        const { workspaces, openTabs, activeWorkspace } = res.payload;

        // populate AppState
        AppState.openTabs = openTabs || [];
        AppState.activeWorkspaceId = activeWorkspace;
        AppState.workspaces = {};
        (workspaces || []).forEach(ws => {
            AppState.workspaces[ws.id] = ws;
        });

        // init home page listeners
        initHomePage();

        renderTabBar();

        if (activeWorkspace) {
            // load the active workspace
            sendMessage('GET_STATUS');
        } else {
            // no workspace — show home page
            goHome();
        }
    } catch (err) {
        toast('加载工作区失败：' + err.message, 'error');
        initHomePage();
        goHome();
    }
}

bootstrap();
