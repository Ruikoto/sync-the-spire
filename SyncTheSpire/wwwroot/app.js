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
        toast(I18n.t('toast.nightlyBuild'), 'info');
    }
    checkForUpdates();
    checkAnnouncements();
});

on('GET_STATUS', data => {
    if (data.status === 'success') {
        const payload = data.payload;
        // H5 fix: verify this response is for the currently active workspace
        // (delayed responses from a previous workspace would corrupt state)
        if (!AppState.activeWorkspaceId) return;
        const ws = getWsState();

        // stash capabilities from backend
        if (payload.capabilities) {
            ws.capabilities = payload.capabilities;
        }

        if (!payload.isConfigured) {
            // setup page: request saved config for pre-fill
            $('#setup-subtitle').textContent = I18n.t('setup.firstTimeSubtitle');
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
                startRefreshSync();
            }
        }
    }
});

// track which workspace the latest REFRESH_SYNC was for
let _refreshSyncWsId = null;
let _refreshTimeout = null;

function startRefreshSync() {
    $('#refresh-icon').style.animation = 'spin 0.7s linear infinite';
    $('#refresh-label').textContent = I18n.t('main.refreshing');
    _refreshSyncWsId = AppState.activeWorkspaceId;
    if (_refreshTimeout) clearTimeout(_refreshTimeout);
    _refreshTimeout = setTimeout(() => {
        $('#refresh-icon').style.animation = '';
        $('#refresh-label').textContent = I18n.t('main.refresh');
        _refreshTimeout = null;
        toast(I18n.t('main.refreshTimeout'), 'warning');
    }, 60000);
    sendMessage('REFRESH_SYNC');
}

on('REFRESH_SYNC', data => {
    // always stop spinner regardless of status
    $('#refresh-icon').style.animation = '';
    $('#refresh-label').textContent = I18n.t('main.refresh');
    if (_refreshTimeout) { clearTimeout(_refreshTimeout); _refreshTimeout = null; }
    // H5 fix: ignore stale response if main page isn't visible or workspace changed
    if ($('#page-main').classList.contains('hidden')) return;
    if (!AppState.activeWorkspaceId) return;
    // skip follow-up fetches if we've switched workspace since the request was sent
    if (_refreshSyncWsId && _refreshSyncWsId !== AppState.activeWorkspaceId) {
        _refreshSyncWsId = null;
        return;
    }
    _refreshSyncWsId = null;
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
            $('#setup-subtitle').textContent = I18n.t('setup.editSubtitle');
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
    $('#mod-checkbox').disabled = false;
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
            data.payload?.message || I18n.t('modals.conflict.defaultMessage')
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
    $('#mod-checkbox').disabled = false;
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
    $('#redirect-checkbox').disabled = false;
    if (data.status === 'success') {
        toast(data.payload?.message || I18n.t('common.operationComplete'), 'success');
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
        btn.textContent = I18n.t('saveBackup.unlinkButton');
        toast(data.payload?.message || I18n.t('toast.unlinkDone'), 'success');
    } else {
        // show error inside the modal, keep it open
        btn.disabled = false;
        btn.textContent = I18n.t('saveBackup.unlinkButton');
        const errEl = $('#save-unlink-error');
        errEl.textContent = data.message || I18n.t('saveBackup.unlinkFailed');
        errEl.classList.remove('hidden');
    }
});

on('BACKUP_SAVES', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || I18n.t('toast.backupDone'), 'success');
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
        toast(data.payload?.message || I18n.t('toast.restoreDone'), 'success');
        closeBackupListModal();
        sendMessage('GET_SAVE_STATUS');
    }
});

on('DELETE_BACKUP', data => {
    if (data.status === 'success') {
        toast(data.payload?.message || I18n.t('toast.deleted'), 'success');
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
        toast(I18n.t('setup.nicknameRequired'), 'error');
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
        toast(I18n.t('toast.gamePathFound'), 'success');
    }
});

$('#btn-find-save').addEventListener('click', async () => {
    const result = await ipcCall('FIND_SAVE_PATH');
    if (result.status === 'success' && result.payload) {
        const path = await pickSteamAccount(result.payload);
        if (path) {
            $('#cfg-save').value = path;
            toast(I18n.t('toast.savePathFound'), 'success');
        }
    }
});

// refresh -- fetch remote and show sync status
guardClick($('#btn-refresh'), () => {
    startRefreshSync();
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

// mod toggle — disable during pending IPC to prevent rapid clicks
$('#mod-checkbox').addEventListener('change', (e) => {
    const cb = e.target;
    cb.disabled = true;
    if (cb.checked) {
        sendMessage('RESTORE_JUNCTION');
    } else {
        sendMessage('SWITCH_TO_VANILLA');
    }
});

// create branch
guardClick($('#btn-create'), () => {
    const name = $('#inp-branch').value.trim();
    if (!name) { toast(I18n.t('createBranch.nameRequired'), 'error'); return; }
    if (!isValidBranchName(name)) { toast(I18n.t('createBranch.nameInvalid'), 'error'); return; }
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
            I18n.t('main.pullConfirmMessage'),
            I18n.t('main.pullConfirmTitle')
        );
        if (!ok) return;
    }
    sendMessage('SYNC_OTHER_BRANCH', { branchName: ws.currentBranch });
});

// settings: go to setup page with config pre-filled
$('#btn-settings').addEventListener('click', () => {
    isEditMode = true;
    $('#setup-subtitle').textContent = I18n.t('setup.editSubtitle');
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

// save redirect toggle — disable during pending IPC
$('#redirect-checkbox').addEventListener('change', (e) => {
    e.target.disabled = true;
    sendMessage('SET_REDIRECT', { enabled: e.target.checked });
});


// ── save backup buttons ──────────────────────────────────────────────────────

// migration modal: one-click unlink
$('#btn-do-unlink').addEventListener('click', () => {
    const btn = $('#btn-do-unlink');
    btn.disabled = true;
    btn.textContent = I18n.t('common.processing');
    $('#save-unlink-error').classList.add('hidden');
    sendMessage('UNLINK_SAVES');
});

guardClick($('#btn-backup-saves'), async () => {
    const ok = await showConfirm(
        I18n.t('saveBackup.confirmMessage'),
        I18n.t('saveBackup.confirmTitle')
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
    const modals = ['#welcome-modal', '#backup-list-modal', '#settings-modal', '#conflict-modal', '#create-workspace-modal'];
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
$('#btn-titlebar-settings').addEventListener('click', () => openSettingsModal());
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

    // L10 fix: preserve scroll position across re-renders
    const prevScroll = tabBar.scrollLeft;

    tabBar.innerHTML = AppState.openTabs.map(id => {
        const ws = AppState.workspaces[id];
        if (!ws) return '';
        const isActive = id === AppState.activeWorkspaceId;
        return `
            <div class="tab-item${isActive ? ' active' : ''} h-full flex items-center gap-1.5 px-3 text-xs text-spire-muted shrink-0" data-tab="${escAttr(id)}">
                <span class="game-badge-${ws.gameType}" style="display:flex;">${gameIcon(ws.gameType, 12)}</span>
                <span class="truncate max-w-[120px]">${esc(ws.name)}</span>
                <button class="tab-close ml-1 text-spire-muted hover:text-spire-danger text-xs leading-none" data-tab-close="${escAttr(id)}" title="${escAttr(I18n.t('titlebar.closeTab'))}">&times;</button>
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

    // L10 fix: restore scroll position
    tabBar.scrollLeft = prevScroll;
}

async function switchToWorkspace(id) {
    try {
        const res = await ipcCall('SWITCH_WORKSPACE', { id });
        if (res.status !== 'success') throw new Error(res.message || I18n.t('main.switchFailed'));
        AppState.activeWorkspaceId = res.payload.id;
        renderTabBar();
        // clear stale branch data from previous workspace
        clearBranchCache();
        // M6 fix: clear the sync fade timer so it doesn't hide the new workspace's status
        if (typeof syncFadeTimer !== 'undefined' && syncFadeTimer) {
            clearTimeout(syncFadeTimer);
            syncFadeTimer = null;
        }
        // M7 fix: close any open modals from the previous workspace
        for (const sel of ['#branch-modal', '#backup-list-modal', '#welcome-modal', '#create-workspace-modal']) {
            const m = $(sel);
            if (m && !m.classList.contains('hidden')) m.classList.add('hidden');
        }
        sendMessage('GET_STATUS');
    } catch (err) {
        toast(I18n.t('main.switchFailedMsg', { message: err.message }), 'error');
    }
}

async function closeWorkspaceTab(id) {
    try {
        const res = await ipcCall('CLOSE_WORKSPACE_TAB', { id });
        if (res.status !== 'success') throw new Error(res.message || I18n.t('main.closeTabFailed'));
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
        toast(I18n.t('main.closeTabFailedMsg', { message: err.message }), 'error');
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
    await I18n.setLang(localStorage.getItem('sts-lang') || 'zh-CN');

    // sync language dropdown with current language
    const langSelect = $('#settings-language');
    if (langSelect) langSelect.value = I18n.getLang();

    // language change handler
    if (langSelect) {
        langSelect.addEventListener('change', () => {
            I18n.setLang(langSelect.value);
        });
    }

    // re-render dynamic UI when language changes
    I18n.onChange(() => {
        renderTabBar();
        renderWorkspaceGrid();
        // refresh status-dependent UI if a workspace is active
        if (AppState.activeWorkspaceId) {
            const ws = getWsState();
            updateSyncStatusLine();
            updateActionButtons();
            updateSaveBackupCard();
            if (ws.capabilities) updateDashboardForCapabilities(ws.capabilities);
        }
    });

    // fetch available game types
    try {
        const res = await ipcCall('GET_GAME_TYPES');
        cachedGameTypes = (res.status === 'success' && Array.isArray(res.payload)) ? res.payload : [];
    } catch { cachedGameTypes = []; }

    // fetch all workspaces and tab state
    try {
        const res = await ipcCall('GET_WORKSPACES');
        if (res.status !== 'success' || !res.payload) {
            throw new Error(res.message || I18n.t('home.loadFailed'));
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
        toast(I18n.t('home.loadFailedMsg', { message: err.message }), 'error');
        initHomePage();
        goHome();
    }
}

bootstrap();
