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

let isEditMode = false; // true when coming from Settings button, false for first-time setup

function prefillConfigForm(cfg) {
    // always clear password fields first
    $('#cfg-token').value = '';
    $('#cfg-ssh-pass').value = '';

    if (!cfg) return;
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
        toast('未找到有效的存档文件夹', 'error');
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
            if (a.mostRecent) label += ' <span class="text-spire-accent text-[10px] ml-1">当前账号</span>';
            if (disabled) label += ' <span class="text-[10px] ml-1">无存档</span>';
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
        labelGamePath.textContent = supportsAutoFind ? '游戏安装路径' : '同步文件夹路径';
    }
    if (tipGamePath) {
        tipGamePath.dataset.tip = supportsAutoFind
            ? '在 Steam 中右键游戏 → 管理 → 浏览本地文件，打开的路径即为游戏安装路径。自动查找仅支持 Steam 平台。'
            : '你要同步的文件夹';
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
        '是否自动检测游戏安装路径和存档路径？（仅支持 Steam 平台）',
        '自动检测'
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
    if ($('#cfg-path').value) found.push('游戏路径');
    if ($('#cfg-save').value) found.push('存档路径');
    if (found.length > 0) {
        toast('已自动填入' + found.join('和'), 'success');
    } else {
        toast('未能自动检测到路径，请手动填写', 'info');
    }
}
