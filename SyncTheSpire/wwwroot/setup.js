'use strict';

// ── setup page: auth toggle, form prefill, form submit, folder pickers, auto-find ──

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

// isEditMode is now per-workspace: getWsState().isEditMode

function prefillConfigForm(cfg) {
    // always clear ALL fields first to prevent cross-workspace leaking
    $('#cfg-repo').value = '';
    $('#cfg-nickname').value = '';
    $('#cfg-user').value = '';
    $('#cfg-token').value = '';
    $('#cfg-ssh-key').value = '';
    $('#cfg-ssh-pass').value = '';
    $('#cfg-path').value = '';
    $('#cfg-save').value = '';

    if (!cfg) { setAuthType('anonymous'); return; }
    if (cfg.repoUrl) $('#cfg-repo').value = cfg.repoUrl;
    // auto-fill nickname: saved value first, then git global user.name as hint
    if (cfg.nickname) {
        $('#cfg-nickname').value = cfg.nickname;
    } else if (cfg.gitUserName) {
        $('#cfg-nickname').value = cfg.gitUserName;
    }
    if (cfg.username) $('#cfg-user').value = cfg.username;
    if (cfg.sshKeyPath) $('#cfg-ssh-key').value = cfg.sshKeyPath;
    if (cfg.gameInstallPath) $('#cfg-path').value = cfg.gameInstallPath;
    if (cfg.saveFolderPath) $('#cfg-save').value = cfg.saveFolderPath;
    // password fields are intentionally left blank — backend merges them
    setAuthType(cfg.authType || 'anonymous');

    // file size limit settings
    const mode = cfg.maxFileSizeMode || 'auto';
    const radio = document.querySelector(`input[name="file-size-mode"][value="${mode}"]`);
    if (radio) radio.checked = true;
    const mibInput = $('#file-size-mib');
    if (mibInput && cfg.maxFileSizeManualMib != null) mibInput.value = cfg.maxFileSizeManualMib;
    // update visibility
    const manualRow = $('#file-size-manual-row');
    const warnEl = $('#file-size-warn');
    if (manualRow) manualRow.classList.toggle('hidden', mode !== 'manual');
    if (warnEl) warnEl.classList.toggle('hidden', mode === 'auto');

    // LFS status
    const ws = getWsState();
    ws.lfsEnabled = !!cfg.lfsEnabled;
    ws.lfsTrackedPatterns = cfg.lfsTrackedPatterns || [];
    updateLfsStatus(ws.lfsEnabled, ws.lfsTrackedPatterns);
}

// ── steam account picker — returns Promise<string|null> (full save path) ────

function pickSteamAccount(payload) {
    const { basePath, accounts } = payload;
    const valid = accounts.filter(a => a.hasSave);

    // single valid account — skip the modal
    if (valid.length === 1) {
        return Promise.resolve(basePath + '\\' + valid[0].steamId);
    }
    if (valid.length === 0) {
        toast(I18n.t('modals.steam.notFound'), 'error');
        return Promise.resolve(null);
    }

    return new Promise(resolve => {
        const list = $('#steam-account-list');
        list.innerHTML = '';

        accounts.forEach(a => {
            const btn = document.createElement('button');
            btn.type = 'button';
            const disabled = !a.hasSave;

            btn.className = disabled
                ? 'w-full text-left px-3 py-2 rounded-lg text-xs opacity-40 cursor-not-allowed border border-spire-border/50'
                : 'w-full text-left px-3 py-2 rounded-lg text-xs text-spire-text border border-spire-border hover:border-spire-accent hover:bg-spire-accent/10 transition-colors cursor-pointer';
            btn.disabled = disabled;

            let label = esc(a.personaName);
            if (a.mostRecent) label += ` <span class="text-spire-accent text-[10px] ml-1">${esc(I18n.t('modals.steam.currentAccount'))}</span>`;
            if (disabled) label += ` <span class="text-[10px] ml-1">${esc(I18n.t('modals.steam.noSave'))}</span>`;
            btn.innerHTML = `<div>${label}</div><div class="text-[10px] text-spire-muted mt-0.5">${esc(a.steamId)}</div>`;

            if (!disabled) {
                btn.addEventListener('click', () => { cleanup(); resolve(basePath + '\\' + a.steamId); });
            }
            list.appendChild(btn);
        });

        const modal = $('#steam-account-modal');
        modal.classList.remove('hidden');

        function cleanup() {
            modal.classList.add('hidden');
            $('#steam-account-cancel').removeEventListener('click', onCancel);
            modal.removeEventListener('click', onBackdrop);
            document.removeEventListener('keydown', onKeydown);
        }
        function onCancel() { cleanup(); resolve(null); }
        function onBackdrop(e) { if (e.target === modal) { cleanup(); resolve(null); } }
        function onKeydown(e) { if (e.key === 'Escape') { cleanup(); resolve(null); } }

        $('#steam-account-cancel').addEventListener('click', onCancel);
        modal.addEventListener('click', onBackdrop);
        document.addEventListener('keydown', onKeydown);
    });
}

// ── adapt setup form based on game type capabilities ─────────────────────────

function adaptSetupFormForGameType(capabilities) {
    if (!capabilities) return;

    const supportsAutoFind = capabilities.supportsAutoFind;
    const supportsSaveBackup = capabilities.supportsSaveBackup;

    // auto-find buttons: only show for games that support steam discovery
    const btnFindGame = $('#btn-find-game');
    const btnFindSave = $('#btn-find-save');
    if (btnFindGame) btnFindGame.classList.toggle('hidden', !supportsAutoFind);
    if (btnFindSave) btnFindSave.classList.toggle('hidden', !supportsAutoFind);

    // save path group: hide for games that don't support save backup
    const savePath = $('#save-path-group');
    if (savePath) savePath.classList.toggle('hidden', !supportsSaveBackup);

    // label adjustments for generic game type
    const labelGamePath = $('#label-game-path');
    const tipGamePath = document.querySelector('#game-path-group .help-tip');
    if (labelGamePath) {
        labelGamePath.textContent = supportsAutoFind ? I18n.t('setup.gamePathLabel') : I18n.t('setup.syncFolderLabel');
    }
    if (tipGamePath) {
        tipGamePath.dataset.tip = supportsAutoFind
            ? I18n.t('setup.gamePathTip')
            : I18n.t('setup.syncFolderTip');
    }
}

// ── setup page title (editable workspace name) ─────────────────────────────

function updateSetupPageTitle() {
    const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
    const nameEl = $('#setup-ws-name');
    const icon = $('#setup-game-icon');
    const display = $('#setup-ws-name-display');
    const input = $('#setup-ws-name-input');

    if (wsInfo) {
        nameEl.textContent = wsInfo.name;
        icon.innerHTML = gameIcon(wsInfo.gameType, 20);
        icon.className = `flex items-center game-badge-${wsInfo.gameType}`;
        icon.classList.remove('hidden');
        display.style.cursor = 'pointer';
    } else {
        nameEl.textContent = 'Sync the Spire';
        icon.classList.add('hidden');
        display.style.cursor = 'default';
    }
    // always ensure display mode
    display.classList.remove('hidden');
    input.classList.add('hidden');
}

// click to edit workspace name
$('#setup-ws-name-display')?.addEventListener('click', () => {
    const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
    if (!wsInfo) return;
    const display = $('#setup-ws-name-display');
    const input = $('#setup-ws-name-input');
    input.value = wsInfo.name;
    display.classList.add('hidden');
    input.classList.remove('hidden');
    input.focus();
    input.select();
});

// blur always cancels — only Enter commits
$('#setup-ws-name-input')?.addEventListener('blur', () => {
    updateSetupPageTitle();
});

$('#setup-ws-name-input')?.addEventListener('keydown', async (e) => {
    if (e.key === 'Enter') {
        e.preventDefault();
        const wsInfo = AppState.workspaces[AppState.activeWorkspaceId];
        if (!wsInfo) { updateSetupPageTitle(); return; }
        const newName = $('#setup-ws-name-input').value.trim();
        if (newName && newName !== wsInfo.name) {
            const res = await ipcCall('RENAME_WORKSPACE', { id: wsInfo.id, name: newName });
            if (res.status === 'success') {
                wsInfo.name = newName;
                renderTabBar();
                renderWorkspaceGrid();
            }
        }
        updateSetupPageTitle();
    }
    if (e.key === 'Escape') {
        e.preventDefault();
        updateSetupPageTitle();
    }
});

async function promptAutoFind() {
    await new Promise(r => setTimeout(r, 300));
    const ok = await showConfirm(
        I18n.t('setup.autoDetectConfirm'),
        I18n.t('setup.autoDetectTitle')
    );
    if (!ok) return;

    const gameResult = await ipcCall('FIND_GAME_PATH');
    if (gameResult.status === 'success' && gameResult.payload?.path) {
        $('#cfg-path').value = gameResult.payload.path;
    }

    const saveResult = await ipcCall('FIND_SAVE_PATH');
    if (saveResult.status === 'success' && saveResult.payload) {
        const savePath = await pickSteamAccount(saveResult.payload);
        if (savePath) $('#cfg-save').value = savePath;
    }

    const found = [];
    if ($('#cfg-path').value) found.push(I18n.t('setup.gamePath'));
    if ($('#cfg-save').value) found.push(I18n.t('setup.savePath'));
    if (found.length > 0) {
        toast(I18n.t('setup.autoFillResult', { paths: found.join(I18n.t('setup.autoFillAnd')) }), 'success');
    } else {
        toast(I18n.t('setup.autoFillFailed'), 'info');
    }
}

// ── config copy/paste ───────────────────────────────────────────────────────

const CONFIG_EXPORT_VERSION = 1;

// gather sharable fields. options gate the optional credential/path groups.
function gatherExportableConfig({ credentials, paths }) {
    const ws = AppState.workspaces[AppState.activeWorkspaceId];
    const wsState = getWsState();
    const authType = document.querySelector('input[name="authType"]:checked')?.value || 'anonymous';

    const cfg = {
        gameType: ws?.gameType,
        repoUrl: $('#cfg-repo').value.trim(),
        nickname: $('#cfg-nickname').value.trim(),
        authType,
        maxFileSizeMode: document.querySelector('input[name="file-size-mode"]:checked')?.value || 'auto',
        maxFileSizeManualMib: parseInt($('#file-size-mib')?.value || '99', 10),
        lfsEnabled: !!wsState.lfsEnabled,
        lfsTrackedPatterns: wsState.lfsTrackedPatterns || [],
    };
    if (authType === 'https') cfg.username = $('#cfg-user').value.trim();

    if (credentials) {
        if (authType === 'https') cfg.token = $('#cfg-token').value;
        if (authType === 'ssh')   cfg.sshPassphrase = $('#cfg-ssh-pass').value;
    }
    if (paths) {
        cfg.gameInstallPath = $('#cfg-path').value.trim();
        cfg.saveFolderPath  = $('#cfg-save').value.trim();
        if (authType === 'ssh') cfg.sshKeyPath = $('#cfg-ssh-key').value.trim();
    }
    return cfg;
}

// JSON → gzip → base64url
async function encodeConfig(obj) {
    const json = JSON.stringify({ v: CONFIG_EXPORT_VERSION, config: obj });
    const stream = new Blob([json]).stream().pipeThrough(new CompressionStream('gzip'));
    const buf = new Uint8Array(await new Response(stream).arrayBuffer());
    let bin = '';
    for (let i = 0; i < buf.length; i += 0x8000) {
        bin += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
    }
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

async function decodeConfig(str) {
    const norm = str.trim().replace(/-/g, '+').replace(/_/g, '/');
    const padded = norm + '='.repeat((4 - norm.length % 4) % 4);
    const bytes = Uint8Array.from(atob(padded), c => c.charCodeAt(0));
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
    const json = await new Response(stream).text();
    const env = JSON.parse(json);
    if (!env || env.v == null || !env.config) throw new Error('invalid envelope');
    if (env.v > CONFIG_EXPORT_VERSION) throw new Error('version too new');
    return env.config;
}

// merge: fields absent from cfg leave the form untouched (so unticked groups don't clobber)
function applyImportedConfig(cfg) {
    if (cfg.repoUrl != null)  $('#cfg-repo').value = cfg.repoUrl;
    if (cfg.nickname != null) $('#cfg-nickname').value = cfg.nickname;
    if (cfg.authType) setAuthType(cfg.authType);
    if (cfg.username != null) $('#cfg-user').value = cfg.username;

    if (cfg.token != null)         $('#cfg-token').value = cfg.token;
    if (cfg.sshPassphrase != null) $('#cfg-ssh-pass').value = cfg.sshPassphrase;

    if (cfg.gameInstallPath != null) $('#cfg-path').value = cfg.gameInstallPath;
    if (cfg.saveFolderPath != null)  $('#cfg-save').value = cfg.saveFolderPath;
    if (cfg.sshKeyPath != null)      $('#cfg-ssh-key').value = cfg.sshKeyPath;

    if (cfg.maxFileSizeMode) {
        const mode = cfg.maxFileSizeMode;
        const radio = document.querySelector(`input[name="file-size-mode"][value="${mode}"]`);
        if (radio) radio.checked = true;
        $('#file-size-manual-row')?.classList.toggle('hidden', mode !== 'manual');
        $('#file-size-warn')?.classList.toggle('hidden', mode === 'auto');
    }
    if (cfg.maxFileSizeManualMib != null) $('#file-size-mib').value = cfg.maxFileSizeManualMib;

    if (cfg.lfsEnabled != null || cfg.lfsTrackedPatterns != null) {
        const wsState = getWsState();
        if (cfg.lfsEnabled != null)         wsState.lfsEnabled = !!cfg.lfsEnabled;
        if (cfg.lfsTrackedPatterns != null) wsState.lfsTrackedPatterns = cfg.lfsTrackedPatterns;
        updateLfsStatus(wsState.lfsEnabled, wsState.lfsTrackedPatterns);
    }
}

// open the import/export modal. handles export auto-regen on option changes,
// import parsing + cross-gameType confirm + form merge — fully self-contained.
async function showConfigIoModal() {
    const modal = $('#config-io-modal');
    const tabExport = $('#config-io-tab-export');
    const tabImport = $('#config-io-tab-import');
    const panelExport = $('#config-io-panel-export');
    const panelImport = $('#config-io-panel-import');
    const credCb = $('#export-include-credentials');
    const pathCb = $('#export-include-paths');
    const exportOut = $('#config-io-export-output');
    const exportSize = $('#config-io-export-size');
    const importIn = $('#config-io-import-input');
    const btnCopy = $('#config-io-copy');
    const btnPasteClip = $('#config-io-paste-clipboard');
    const btnImport = $('#config-io-import');
    const btnClose = $('#config-io-close');

    // reset to a clean state every open
    credCb.checked = true;
    pathCb.checked = false;
    importIn.value = '';
    exportOut.value = '';
    exportSize.textContent = '';
    selectTab('export');

    async function regenExport() {
        try {
            const encoded = await encodeConfig(gatherExportableConfig({
                credentials: credCb.checked,
                paths: pathCb.checked,
            }));
            exportOut.value = encoded;
            exportSize.textContent = `${encoded.length} ${I18n.t('setup.exportSizeChars')}`;
        } catch {
            exportOut.value = '';
            exportSize.textContent = '';
        }
    }

    function selectTab(name) {
        const isExport = name === 'export';
        tabExport.classList.toggle('active', isExport);
        tabExport.classList.toggle('text-spire-muted', !isExport);
        tabImport.classList.toggle('active', !isExport);
        tabImport.classList.toggle('text-spire-muted', isExport);
        panelExport.classList.toggle('hidden', !isExport);
        panelImport.classList.toggle('hidden', isExport);
        if (isExport) regenExport();
        else setTimeout(() => importIn.focus(), 0);
    }

    async function onCopy() {
        if (!exportOut.value) return;
        try {
            await navigator.clipboard.writeText(exportOut.value);
            toast(I18n.t('setup.copySuccess'), 'success');
        } catch {
            toast(I18n.t('setup.copyFailed'), 'error');
        }
    }

    async function onPasteClip() {
        try { importIn.value = await navigator.clipboard.readText(); }
        catch { toast(I18n.t('setup.clipboardReadFailed'), 'error'); }
    }

    async function onImport() {
        const raw = importIn.value;
        if (!raw || !raw.trim()) return;
        let cfg;
        try { cfg = await decodeConfig(raw); }
        catch { toast(I18n.t('setup.importInvalid'), 'error'); return; }

        const ws = AppState.workspaces[AppState.activeWorkspaceId];
        if (cfg.gameType && ws?.gameType && cfg.gameType !== ws.gameType) {
            const ok = await showConfirm(
                I18n.t('setup.importGameTypeMismatch', { from: cfg.gameType, to: ws.gameType }),
                I18n.t('setup.importTitle')
            );
            if (!ok) return;
        }
        applyImportedConfig(cfg);
        toast(I18n.t('setup.importSuccess'), 'success');
        close();
    }

    function close() {
        modal.classList.add('hidden');
        tabExport.removeEventListener('click', onTabExport);
        tabImport.removeEventListener('click', onTabImport);
        credCb.removeEventListener('change', regenExport);
        pathCb.removeEventListener('change', regenExport);
        btnCopy.removeEventListener('click', onCopy);
        btnPasteClip.removeEventListener('click', onPasteClip);
        btnImport.removeEventListener('click', onImport);
        btnClose.removeEventListener('click', close);
        modal.removeEventListener('click', onBackdrop);
        document.removeEventListener('keydown', onKey);
    }
    function onTabExport() { selectTab('export'); }
    function onTabImport() { selectTab('import'); }
    function onBackdrop(e) { if (e.target === modal) close(); }
    function onKey(e) { if (e.key === 'Escape') close(); }

    tabExport.addEventListener('click', onTabExport);
    tabImport.addEventListener('click', onTabImport);
    credCb.addEventListener('change', regenExport);
    pathCb.addEventListener('change', regenExport);
    btnCopy.addEventListener('click', onCopy);
    btnPasteClip.addEventListener('click', onPasteClip);
    btnImport.addEventListener('click', onImport);
    btnClose.addEventListener('click', close);
    modal.addEventListener('click', onBackdrop);
    document.addEventListener('keydown', onKey);

    modal.classList.remove('hidden');
}
