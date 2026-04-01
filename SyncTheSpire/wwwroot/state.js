'use strict';

// ── shared mutable state ─────────────────────────────────────────────────────
// accessed as AppState.xxx from all modules

const AppState = {
    currentBranch: '',
    needsBranchSelection: false,
    isModEnabled: false,
    appVersion: '',
    appArch: 'x64',
    appDistribution: 'direct', // 'store' (MSIX) or 'direct' (loose exe)
    savePathConfigured: false,
    lastSyncStatus: null,
    lastHasLocalChanges: false,
};
