'use strict';

// ── dashboard: status card, action buttons, sync status, redirect, save backup ──

function updateActionButtons() {
    const pushBtn = $('#btn-push');
    const pullBtn = $('#btn-pull');

    if (AppState.needsBranchSelection || !AppState.isModEnabled) {
        pushBtn.disabled = true;
        pullBtn.disabled = true;
        pushBtn.classList.add('opacity-40', 'cursor-not-allowed');
        pullBtn.classList.add('opacity-40', 'cursor-not-allowed');
    } else {
        pushBtn.disabled = false;
        pullBtn.disabled = false;
        pushBtn.classList.remove('opacity-40', 'cursor-not-allowed');
        pullBtn.classList.remove('opacity-40', 'cursor-not-allowed');
    }
}

function updateStatusCard(data) {
    const dot = $('#status-dot');
    const label = $('#status-label');
    const branch = $('#status-branch');
    const modDot = $('#mod-dot');
    const modLabel = $('#mod-label');
    const modCheckbox = $('#mod-checkbox');

    AppState.currentBranch = data.currentBranch || '';
    AppState.needsBranchSelection = !!data.needsBranchSelection;

    if (AppState.needsBranchSelection) {
        branch.textContent = '未选择';
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = '请通过下方选择一个分支开始';
        modCheckbox.checked = false;
        modCheckbox.disabled = true;
        modDot.className = 'w-2 h-2 rounded-full bg-gray-500';
        modLabel.textContent = '请先选择分支';
    } else {
        branch.textContent = AppState.currentBranch || '—';
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

    // track sync-related state
    AppState.lastHasLocalChanges = !!data.hasLocalChanges;
    if (data.ahead !== undefined) {
        AppState.lastSyncStatus = { ahead: data.ahead, behind: data.behind, hasRemoteBranch: data.hasRemoteBranch };
    }
    AppState.isModEnabled = !AppState.needsBranchSelection && !!data.isJunctionActive;
    updateSyncStatusLine();
    updateActionButtons();
}

let syncFadeTimer = null;

function updateSyncStatusLine() {
    const container = $('#sync-status-line');
    const text = $('#sync-status-text');

    // clear any pending fade timer and reset transition state
    if (syncFadeTimer) { clearTimeout(syncFadeTimer); syncFadeTimer = null; }
    container.style.transition = 'none';
    container.style.opacity = '1';

    if (AppState.needsBranchSelection || !AppState.currentBranch || !AppState.lastSyncStatus) {
        container.classList.add('hidden');
        return;
    }

    container.classList.remove('hidden');
    const s = AppState.lastSyncStatus;

    if (!s.hasRemoteBranch) {
        text.textContent = '新分支，尚未推送到远端';
        text.className = 'text-xs text-spire-warn';
        return;
    }

    // combine uncommitted local changes + unpushed commits into a single "local" concept
    const hasLocal = AppState.lastHasLocalChanges || s.ahead > 0;
    const hasRemote = s.behind > 0;

    if (!hasLocal && !hasRemote) {
        text.textContent = '✓ 已是最新';
        text.className = 'text-xs text-spire-success';
        // fade out after 3s, then collapse
        syncFadeTimer = setTimeout(() => {
            container.style.transition = 'opacity 0.6s ease';
            container.style.opacity = '0';
            syncFadeTimer = setTimeout(() => {
                container.classList.add('hidden');
            }, 600);
        }, 3000);
        return;
    }

    const parts = [];
    if (hasRemote) parts.push(`↓ 远端有 ${s.behind} 处新改动`);
    if (hasLocal) parts.push('↑ 有本地改动未上传');
    text.textContent = parts.join('，');
    text.className = hasRemote ? 'text-xs text-spire-warn' : 'text-xs text-spire-accentHover';
}

// ── save redirect ────────────────────────────────────────────────────────────

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

    if (data.isEnabled) {
        dot.className = 'w-2 h-2 rounded-full bg-spire-success';
        label.textContent = '已启用';
        checkbox.checked = true;
    } else {
        dot.className = 'w-2 h-2 rounded-full bg-spire-muted';
        label.textContent = '未启用';
        checkbox.checked = false;
    }
}

// ── save backup card ─────────────────────────────────────────────────────────

function updateSaveBackupCard() {
    const btns = [$('#btn-backup-saves'), $('#btn-restore-saves')];
    const desc = $('#save-backup-desc');

    if (AppState.savePathConfigured) {
        btns.forEach(b => { b.disabled = false; b.classList.remove('opacity-50', 'cursor-not-allowed'); });
        if (desc) desc.textContent = '手动备份或恢复游戏存档';
    } else {
        btns.forEach(b => { b.disabled = true; b.classList.add('opacity-50', 'cursor-not-allowed'); });
        if (desc) desc.textContent = '未配置存档路径，请在 Settings 中设置后使用';
    }
}
