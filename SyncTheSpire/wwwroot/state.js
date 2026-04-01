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
    };
}

// get or create workspace state for the active workspace
function getWsState(id) {
    const wsId = id || AppState.activeWorkspaceId;
    if (!wsId) return _defaultWsState(); // shouldn't happen, but safe fallback
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
