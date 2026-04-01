'use strict';

// ── home page — workspace grid, search, create flow ─────────────────────────

// cached from GET_GAME_TYPES on bootstrap
let cachedGameTypes = [];

function renderWorkspaceGrid() {
    const grid = $('#workspace-grid');
    const empty = $('#workspace-empty');
    const searchEl = $('#workspace-search');
    const query = (searchEl?.value || '').trim().toLowerCase();

    const allWs = Object.values(AppState.workspaces);
    const filtered = query
        ? allWs.filter(ws => ws.name.toLowerCase().includes(query) || ws.gameDisplayName.toLowerCase().includes(query))
        : allWs;

    if (filtered.length === 0) {
        grid.innerHTML = '';
        empty.classList.remove('hidden');
        return;
    }
    empty.classList.add('hidden');

    grid.innerHTML = filtered.map(ws => {
        const statusText = ws.isConfigured ? '已配置' : '未配置';
        const statusDot = ws.isConfigured ? 'bg-spire-success' : 'bg-gray-500';
        return `
            <div class="workspace-card bg-spire-card rounded-xl border border-spire-border p-4" data-ws-id="${escAttr(ws.id)}">
                <div class="flex items-center justify-between mb-2">
                    <span class="ws-name text-sm font-medium truncate">${esc(ws.name)}</span>
                    <div class="flex items-center gap-1">
                        <button class="ws-settings-btn text-spire-muted hover:text-spire-accent transition-colors p-1" data-ws-id="${escAttr(ws.id)}" title="工作区设置">
                            <svg class="w-3.5 h-3.5" style="width:14px;height:14px;" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 011.37.49l1.296 2.247a1.125 1.125 0 01-.26 1.431l-1.003.827c-.293.24-.438.613-.431.992a6.759 6.759 0 010 .255c-.007.378.138.75.43.99l1.005.828c.424.35.534.954.26 1.43l-1.298 2.247a1.125 1.125 0 01-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.57 6.57 0 01-.22.128c-.331.183-.581.495-.644.869l-.213 1.28c-.09.543-.56.941-1.11.941h-2.594c-.55 0-1.02-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 01-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 01-1.369-.49l-1.297-2.247a1.125 1.125 0 01.26-1.431l1.004-.827c.292-.24.437-.613.43-.992a6.932 6.932 0 010-.255c.007-.378-.138-.75-.43-.99l-1.004-.828a1.125 1.125 0 01-.26-1.43l1.297-2.247a1.125 1.125 0 011.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.087.22-.128.332-.183.582-.495.644-.869l.214-1.281z"/><path stroke-linecap="round" stroke-linejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/></svg>
                        </button>
                        <button class="ws-delete-btn text-spire-muted hover:text-spire-danger transition-colors p-1" data-ws-id="${escAttr(ws.id)}" title="删除工作区">
                            <svg class="w-3.5 h-3.5" style="width:14px;height:14px;" fill="none" stroke="currentColor" stroke-width="2" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"/></svg>
                        </button>
                    </div>
                </div>
                <div class="flex items-center gap-2">
                    <span class="flex items-center gap-1.5">
                        <span class="game-badge-${ws.gameType}" style="display:flex;">${gameIcon(ws.gameType, 12)}</span>
                        <span class="game-badge game-badge-${ws.gameType}">${esc(ws.gameDisplayName)}</span>
                    </span>
                    <span class="flex items-center gap-1 text-[10px] text-spire-muted">
                        <span class="w-1.5 h-1.5 rounded-full ${statusDot}"></span>
                        ${statusText}
                    </span>
                </div>
            </div>`;
    }).join('');

    // bind card clicks (open tab + switch)
    grid.querySelectorAll('.workspace-card').forEach(card => {
        card.addEventListener('click', (e) => {
            // don't trigger on action buttons
            if (e.target.closest('.ws-delete-btn') || e.target.closest('.ws-settings-btn')) return;
            const id = card.dataset.wsId;
            openWorkspaceTab(id);
        });
    });

    // bind settings buttons — open workspace and go to setup page
    grid.querySelectorAll('.ws-settings-btn').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.stopPropagation();
            const id = btn.dataset.wsId;
            // ensure tab is open + backend context switched
            const tabRes = await ipcCall('OPEN_WORKSPACE_TAB', { id });
            if (tabRes.status !== 'success') return;
            AppState.openTabs = tabRes.payload.openTabs || [];
            AppState.activeWorkspaceId = tabRes.payload.activeWorkspace;
            const switchRes = await ipcCall('SWITCH_WORKSPACE', { id });
            if (switchRes.status !== 'success') return;
            AppState.activeWorkspaceId = switchRes.payload.id;
            renderTabBar();
            clearBranchCache();
            // go straight to setup page
            isEditMode = true;
            $('#setup-subtitle').textContent = '编辑配置';
            updateSetupPageTitle();
            adaptSetupFormForGameType(getWsState().capabilities);
            sendMessage('GET_CONFIG');
            showPage('setup');
        });
    });

    // bind delete buttons
    grid.querySelectorAll('.ws-delete-btn').forEach(btn => {
        btn.addEventListener('click', async (e) => {
            e.stopPropagation();
            const id = btn.dataset.wsId;
            const ws = AppState.workspaces[id];
            if (!ws) return;
            const ok = await showConfirm(`确认删除工作区"${ws.name}"？\n此操作不可恢复，工作区的所有数据（仓库、备份等）将被永久删除。`, '删除工作区');
            if (!ok) return;
            const res = await ipcCall('DELETE_WORKSPACE', { id });
            if (res.status !== 'success') { toast('删除失败：' + (res.message || '未知错误'), 'error'); return; }
            deleteWsState(id);
            AppState.openTabs = res.payload.openTabs || [];
            AppState.activeWorkspaceId = res.payload.activeWorkspace;
            renderTabBar();
            renderWorkspaceGrid();
        });
    });
}

// open a workspace: add tab if needed, switch to it, load status
async function openWorkspaceTab(id) {
    // open tab first (ensures it's in the tab list)
    const tabRes = await ipcCall('OPEN_WORKSPACE_TAB', { id });
    if (tabRes.status !== 'success') { toast('打开标签页失败：' + (tabRes.message || '未知错误'), 'error'); return; }
    AppState.openTabs = tabRes.payload.openTabs || [];
    AppState.activeWorkspaceId = tabRes.payload.activeWorkspace;

    // switch backend context to this workspace
    const switchRes = await ipcCall('SWITCH_WORKSPACE', { id });
    if (switchRes.status !== 'success') { toast('切换工作区失败：' + (switchRes.message || '未知错误'), 'error'); return; }
    AppState.activeWorkspaceId = switchRes.payload.id;

    renderTabBar();
    clearBranchCache();

    // load workspace status (will route to setup or main)
    sendMessage('GET_STATUS');
}

function openCreateWorkspaceModal() {
    const modal = $('#create-workspace-modal');
    const nameInput = $('#new-ws-name');
    const selector = $('#game-type-selector');

    nameInput.value = '';

    // render game type cards
    selector.innerHTML = cachedGameTypes.map((gt, i) => `
        <div class="game-type-card${i === 0 ? ' selected' : ''}" data-type="${escAttr(gt.typeKey)}">
            <div class="flex items-center gap-2">
                <span class="game-badge-${gt.typeKey}" style="display:flex;">${gameIcon(gt.typeKey, 16)}</span>
                <span class="text-sm font-medium">${esc(gt.displayName)}</span>
            </div>
        </div>
    `).join('');

    // bind selection
    selector.querySelectorAll('.game-type-card').forEach(card => {
        card.addEventListener('click', () => {
            selector.querySelectorAll('.game-type-card').forEach(c => c.classList.remove('selected'));
            card.classList.add('selected');
        });
    });

    modal.classList.remove('hidden');
    nameInput.focus();
}

async function handleCreateWorkspace() {
    const name = $('#new-ws-name').value.trim();
    const selectedCard = document.querySelector('#game-type-selector .game-type-card.selected');
    const gameType = selectedCard?.dataset.type || 'sts2';

    if (!name) {
        toast('请输入工作区名称', 'error');
        return;
    }

    const res = await ipcCall('CREATE_WORKSPACE', { name, gameType });
    if (res.status !== 'success') { toast('创建失败：' + (res.message || '未知错误'), 'error'); return; }

    const p = res.payload;
    // update local state
    AppState.workspaces[p.id] = {
        id: p.id,
        name: p.name,
        gameType: p.gameType,
        gameDisplayName: p.gameDisplayName,
        isConfigured: false,
    };
    AppState.openTabs = p.openTabs || [];
    AppState.activeWorkspaceId = p.activeWorkspace;

    // close modal
    $('#create-workspace-modal').classList.add('hidden');

    renderTabBar();
    renderWorkspaceGrid();

    // switch to the new workspace's setup
    sendMessage('GET_STATUS');
}

// ── home page event bindings (called once at bootstrap) ─────────────────────

// M9 fix: guard against double initialization
let _homePageInitialized = false;

function initHomePage() {
    if (_homePageInitialized) return;
    _homePageInitialized = true;
    $('#btn-create-workspace')?.addEventListener('click', openCreateWorkspaceModal);
    $('#create-ws-confirm')?.addEventListener('click', handleCreateWorkspace);
    $('#create-ws-cancel')?.addEventListener('click', () => {
        $('#create-workspace-modal').classList.add('hidden');
    });

    // close modal on backdrop click
    $('#create-workspace-modal')?.addEventListener('click', (e) => {
        if (e.target === e.currentTarget) {
            $('#create-workspace-modal').classList.add('hidden');
        }
    });

    // enter key in name input triggers create
    $('#new-ws-name')?.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            handleCreateWorkspace();
        }
    });

    // live search
    $('#workspace-search')?.addEventListener('input', () => {
        renderWorkspaceGrid();
    });
}
