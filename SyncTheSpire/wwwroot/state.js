'use strict';

// ── shared mutable state ─────────────────────────────────────────────────────
// accessed as AppState.xxx from all modules

const AppState = {
    // global (not workspace-scoped)
    appVersion: '',
    appArch: 'x64',
    appDistribution: 'direct', // 'store' (MSIX) or 'direct' (loose exe)

    // workspace management
    activeWorkspaceId: null,
    openTabs: [],          // ordered list of open workspace IDs
    workspaces: {},        // id → { id, name, gameType, gameDisplayName, isConfigured }

    // per-workspace cached state — keyed by workspace id
    workspaceStates: {},
};

// default shape for a per-workspace state entry
function _defaultWsState() {
    return {
        currentBranch: '',
        needsBranchSelection: false,
        isModEnabled: false,
        savePathConfigured: false,
        lastSyncStatus: null,
        lastHasLocalChanges: false,
        capabilities: null,  // filled by GET_STATUS response
        currentPage: null,   // 'setup' or 'main' — tracks which page this workspace is on
        isEditMode: false,   // whether setup page is in edit mode (vs first-time config)
    };
}

// get or create workspace state for the active workspace
// M5 fix: always store the created state so mutations persist
function getWsState(id) {
    const wsId = id || AppState.activeWorkspaceId;
    if (!wsId) {
        // no workspace active — store under a sentinel key so mutations aren't lost
        if (!AppState.workspaceStates['__none__']) {
            AppState.workspaceStates['__none__'] = _defaultWsState();
        }
        return AppState.workspaceStates['__none__'];
    }
    if (!AppState.workspaceStates[wsId]) {
        AppState.workspaceStates[wsId] = _defaultWsState();
    }
    return AppState.workspaceStates[wsId];
}

// clean up state when a workspace is deleted
function deleteWsState(id) {
    delete AppState.workspaceStates[id];
    delete AppState.workspaces[id];
}
