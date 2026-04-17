'use strict';

// ── mod manager: local mod browsing, install, delete, branch copy ────────────

let mmMods = [];
let mmGhosts = [];
let mmLocalModIds = new Set(); // for branch copy "already exists" check
let mmSelectedBranch = ''; // currently selected branch in copy modal

// ── open / close ─────────────────────────────────────────────────────────────

function openModManager() {
    const modal = $('#mod-manager-modal');
    modal.classList.remove('hidden');
    $('#mm-grid').innerHTML = '';
    $('#mm-grid-scroll').classList.add('hidden');
    $('#mm-loading').classList.remove('hidden');
    $('#mm-search').value = '';
    lucide.createIcons({ nodes: [modal] });
    refreshModList();
}

function closeModManager() {
    $('#mod-manager-modal').classList.add('hidden');
}

async function refreshModList() {
    try {
        const res = await ipcCall('GET_LOCAL_MODS_DETAILED');
        mmMods = res.payload?.mods || [];
        mmGhosts = res.payload?.ghosts || [];
        mmLocalModIds = new Set(mmMods.map(m => m.id?.toLowerCase()).filter(Boolean));
        $('#mm-loading').classList.add('hidden');
        $('#mm-grid-scroll').classList.remove('hidden');
        renderMmGrid();
    } catch (err) {
        $('#mm-loading').classList.add('hidden');
        $('#mm-grid-scroll').classList.remove('hidden');
        $('#mm-grid').innerHTML = `<div class="text-xs text-spire-muted py-8 text-center">加载失败：${esc(err.message || '未知错误')}</div>`;
    }
}

// ── main grid rendering ──────────────────────────────────────────────────────

function renderMmGrid() {
    const grid = $('#mm-grid');
    const query = $('#mm-search').value.trim().toLowerCase();

    // filter by search
    const filteredMods = query
        ? mmMods.filter(m =>
            (m.name || '').toLowerCase().includes(query) ||
            (m.id || '').toLowerCase().includes(query) ||
            (m.author || '').toLowerCase().includes(query))
        : mmMods;

    const filteredGhosts = query
        ? mmGhosts.filter(g =>
            (g.id || '').toLowerCase().includes(query) ||
            (g.dependedBy || []).some(d => d.toLowerCase().includes(query)))
        : mmGhosts;

    let html = '';

    // ghost cards first
    for (const g of filteredGhosts) {
        const neededBy = (g.dependedBy || []).map(d => esc(d)).join('、');
        html += `
            <div class="mm-ghost-card bg-red-500/5 border border-red-500/30 rounded-lg p-3 mb-3 break-inside-avoid">
                <div class="flex items-center gap-1.5 mb-1">
                    <span class="text-red-400 text-xs font-medium">⚠ 前置 MOD 缺失</span>
                </div>
                <div class="text-xs text-spire-text font-mono">${esc(g.id || '')}</div>
                <div class="text-xs text-spire-muted mt-1">需要此 MOD：${neededBy}</div>
            </div>`;
    }

    // sort normal mods by name
    const sorted = [...filteredMods].sort((a, b) => (a.name || '').localeCompare(b.name || ''));

    for (const m of sorted) {
        const hasMissingDeps = (m.dependencies || []).some(d => !mmLocalModIds.has(d.toLowerCase()));
        const isFramework = (m.dependedBy || []).length > 0;
        const hasMissingFiles = (m.missingFiles || []).length > 0;

        let tags = '';
        if (isFramework) tags += `<span class="inline-block text-xs px-1.5 py-0.5 rounded bg-green-500/15 text-green-400">前置 MOD</span>`;
        if ((m.dependencies || []).length > 0) tags += `<span class="inline-block text-xs px-1.5 py-0.5 rounded bg-spire-accent/15 text-spire-accent">${m.dependencies.length} 个依赖</span>`;
        if (hasMissingDeps) tags += `<span class="inline-block text-xs px-1.5 py-0.5 rounded bg-red-500/15 text-red-400">⚠ 缺失依赖</span>`;
        if (hasMissingFiles) tags += `<span class="inline-block text-xs px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-400">⚠ 文件缺失</span>`;

        html += `
            <div class="mm-mod-card mod-card bg-spire-card border border-spire-border rounded-lg p-3 mb-3 cursor-pointer break-inside-avoid hover:border-spire-accent/40 transition-colors"
                 data-mod-id="${escAttr(m.id || '')}">
                <div class="flex items-center justify-between mb-1">
                    <span class="text-sm font-medium text-spire-text truncate">${esc(m.name || m.id || '')}</span>
                    <span class="text-xs text-spire-muted shrink-0 ml-2">${esc(m.version || '')}</span>
                </div>
                ${m.author ? `<div class="text-xs text-spire-muted mb-1">${esc(m.author)}</div>` : ''}
                ${m.description ? `<div class="text-xs text-spire-muted mb-2 line-clamp-2">${esc(m.description)}</div>` : ''}
                <div class="flex items-center gap-1.5 flex-wrap">
                    ${tags}
                    <span class="text-xs text-spire-muted ml-auto">${formatSize(m.sizeBytes)}</span>
                </div>
            </div>`;
    }

    if (!html) {
        html = `<div class="text-xs text-spire-muted py-8 text-center col-span-full">
            ${query ? '没有匹配的 MOD' : '没有找到本地 MOD'}
        </div>`;
    }

    grid.innerHTML = html;
    $('#mm-mod-count').textContent = `${mmMods.length} 个 MOD`;
}

// ── detail modal ─────────────────────────────────────────────────────────────

function openModDetail(modId) {
    // look in both real mods and ghosts
    const mod = mmMods.find(m => m.id === modId) || mmGhosts.find(g => g.id === modId);
    if (!mod) return;

    const isGhost = !mod.folderName;
    const container = $('#mm-detail-content');

    if (isGhost) {
        // ghost mod detail — minimal info
        const neededBy = (mod.dependedBy || []).map(d => {
            const exists = mmLocalModIds.has(d.toLowerCase());
            return exists
                ? `<div class="mm-dep-row clickable py-1 px-2 rounded text-xs text-spire-accent cursor-pointer" data-mod-id="${escAttr(d)}">${esc(d)}</div>`
                : `<div class="py-1 px-2 text-xs text-spire-muted">${esc(d)}</div>`;
        }).join('');

        container.innerHTML = `
            <div class="flex items-center justify-between mb-3">
                <span class="text-red-400 font-medium">⚠ 前置 MOD 缺失</span>
                <button class="mm-detail-close text-spire-muted hover:text-spire-text p-1"><i data-lucide="x" style="width:16px;height:16px"></i></button>
            </div>
            <div class="font-mono text-sm text-spire-text mb-3">${esc(mod.id || '')}</div>
            <div class="border-t border-spire-border pt-3">
                <div class="text-xs text-spire-muted mb-2">被以下 MOD 依赖：</div>
                ${neededBy}
            </div>`;
    } else {
        // full mod detail
        let depsHtml = '';
        if ((mod.dependencies || []).length > 0) {
            depsHtml = `<div class="border-t border-spire-border pt-3 mt-3">
                <div class="text-xs text-spire-muted mb-2">🔗 依赖项</div>
                ${mod.dependencies.map(d => {
                    const exists = mmLocalModIds.has(d.toLowerCase());
                    return exists
                        ? `<div class="mm-dep-row clickable py-1 px-2 rounded text-xs flex items-center gap-1.5" data-mod-id="${escAttr(d)}"><span class="text-green-400">✓</span> <span class="text-spire-accent">${esc(d)}</span></div>`
                        : `<div class="py-1 px-2 text-xs flex items-center gap-1.5"><span class="text-red-400">✗</span> <span class="text-red-400">${esc(d)}</span> <span class="text-spire-muted">(未安装)</span></div>`;
                }).join('')}
            </div>`;
        }

        let depByHtml = '';
        if ((mod.dependedBy || []).length > 0) {
            depByHtml = `<div class="border-t border-spire-border pt-3 mt-3">
                <div class="text-xs text-spire-muted mb-2">📦 被以下 MOD 依赖</div>
                ${mod.dependedBy.map(d => {
                    const exists = mmLocalModIds.has(d.toLowerCase());
                    return exists
                        ? `<div class="mm-dep-row clickable py-1 px-2 rounded text-xs text-spire-accent" data-mod-id="${escAttr(d)}">${esc(d)}</div>`
                        : `<div class="py-1 px-2 text-xs text-spire-muted">${esc(d)}</div>`;
                }).join('')}
            </div>`;
        }

        let missingHtml = '';
        if ((mod.missingFiles || []).length > 0) {
            missingHtml = `<div class="border-t border-spire-border pt-3 mt-3">
                <div class="text-xs text-amber-400 mb-1">⚠ 文件缺失警告</div>
                <div class="text-xs text-spire-muted">${mod.missingFiles.map(f => `缺少 ${esc(f)} 文件`).join('、')}</div>
            </div>`;
        }

        let filesHtml = '';
        if ((mod.files || []).length > 0) {
            const maxShow = 20;
            const fileList = mod.files.slice(0, maxShow).map(f => `<div class="text-xs text-spire-muted font-mono py-0.5">${esc(f)}</div>`).join('');
            const moreCount = mod.files.length - maxShow;
            filesHtml = `<div class="border-t border-spire-border pt-3 mt-3">
                <div class="text-xs text-spire-muted mb-2">📁 文件列表 (${mod.files.length})</div>
                ${fileList}
                ${moreCount > 0 ? `<div class="text-xs text-spire-muted py-0.5">...还有 ${moreCount} 个文件</div>` : ''}
            </div>`;
        }

        container.innerHTML = `
            <div class="flex items-center justify-between mb-1">
                <span class="text-base font-semibold text-spire-text">${esc(mod.name || mod.id || '')}</span>
                <div class="flex items-center gap-2">
                    <span class="text-xs text-spire-muted">${esc(mod.version || '')}</span>
                    <button class="mm-detail-close text-spire-muted hover:text-spire-text p-1"><i data-lucide="x" style="width:16px;height:16px"></i></button>
                </div>
            </div>
            ${mod.author ? `<div class="text-xs text-spire-muted mb-3">by ${esc(mod.author)}</div>` : '<div class="mb-3"></div>'}
            ${mod.description ? `<div class="border-t border-spire-border pt-3"><div class="text-xs text-spire-text leading-relaxed">${esc(mod.description)}</div></div>` : ''}
            <div class="border-t border-spire-border pt-3 mt-3">
                <div class="text-xs text-spire-muted mb-2">📊 信息</div>
                <div class="text-xs text-spire-muted space-y-1">
                    <div>ID: <span class="font-mono text-spire-text">${esc(mod.id || '')}</span></div>
                    <div>文件夹: <span class="font-mono text-spire-text">${esc(mod.folderName || '')}</span></div>
                    <div>大小: <span class="text-spire-text">${formatSize(mod.sizeBytes)}</span></div>
                </div>
            </div>
            ${depsHtml}
            ${depByHtml}
            ${missingHtml}
            ${filesHtml}
            <div class="border-t border-spire-border pt-4 mt-4">
                <button class="mm-delete-btn w-full text-xs py-2 rounded-lg border border-red-500/30 text-red-400 hover:bg-red-500/10 transition-colors"
                    data-folder="${escAttr(mod.folderName || '')}">
                    🗑 删除此 MOD
                </button>
            </div>`;
    }

    $('#mm-detail-modal').classList.remove('hidden');
    lucide.createIcons({ nodes: [container] });
}

function closeModDetail() {
    $('#mm-detail-modal').classList.add('hidden');
}

// ── branch copy modal ────────────────────────────────────────────────────────

let mmBranchMods = [];

async function openBranchCopyModal() {
    const modal = $('#mm-branch-copy-modal');
    modal.classList.remove('hidden');
    $('#mm-branch-grid').innerHTML = '';
    $('#mm-branch-grid-scroll').classList.add('hidden');
    $('#mm-branch-loading').classList.add('hidden');
    $('#mm-branch-empty').classList.remove('hidden');
    $('#mm-branch-search').value = '';
    mmSelectedBranch = '';
    $('#mm-branch-picker-label').textContent = '选择分支...';
    $('#mm-branch-picker-dropdown').classList.add('hidden');

    // populate branch dropdown
    try {
        const res = await ipcCall('GET_BRANCHES');
        const branches = res.payload?.branches || [];
        const list = $('#mm-branch-picker-list');
        list.innerHTML = branches.map(b =>
            `<div class="mm-branch-option px-3 py-2 text-xs text-spire-text hover:bg-spire-accent/10 cursor-pointer truncate transition-colors" data-branch="${escAttr(b.name)}">${esc(b.name)}</div>`
        ).join('');
        if (branches.length === 0) {
            list.innerHTML = '<div class="px-3 py-2 text-xs text-spire-muted">没有可用分支</div>';
        }
    } catch { /* branches load failed */ }

    lucide.createIcons({ nodes: [modal] });
}

function closeBranchCopyModal() {
    $('#mm-branch-copy-modal').classList.add('hidden');
}

async function loadBranchMods(branchName) {
    if (!branchName) {
        $('#mm-branch-grid-scroll').classList.add('hidden');
        $('#mm-branch-loading').classList.add('hidden');
        $('#mm-branch-empty').classList.remove('hidden');
        return;
    }

    $('#mm-branch-empty').classList.add('hidden');
    $('#mm-branch-grid-scroll').classList.add('hidden');
    $('#mm-branch-loading').classList.remove('hidden');

    try {
        const res = await ipcCall('GET_BRANCH_MODS_FOR_COPY', { branchName });
        mmBranchMods = res.payload?.mods || [];
        $('#mm-branch-loading').classList.add('hidden');
        $('#mm-branch-grid-scroll').classList.remove('hidden');
        renderBranchCopyGrid();
    } catch (err) {
        $('#mm-branch-loading').classList.add('hidden');
        $('#mm-branch-grid-scroll').classList.remove('hidden');
        $('#mm-branch-grid').innerHTML = `<div class="text-xs text-spire-muted py-8 text-center">加载失败：${esc(err.message || '')}</div>`;
    }
}

function renderBranchCopyGrid() {
    const grid = $('#mm-branch-grid');
    const query = $('#mm-branch-search').value.trim().toLowerCase();

    const filtered = query
        ? mmBranchMods.filter(m =>
            (m.name || '').toLowerCase().includes(query) ||
            (m.id || '').toLowerCase().includes(query))
        : mmBranchMods;

    if (filtered.length === 0) {
        grid.innerHTML = `<div class="text-xs text-spire-muted py-8 text-center">${query ? '没有匹配的 MOD' : '此分支没有 MOD'}</div>`;
        return;
    }

    let html = '';
    for (const m of filtered) {
        const exists = mmLocalModIds.has((m.id || '').toLowerCase());
        // show dep count if any
        const depCount = (m.dependencies || []).length;
        const depTag = depCount > 0
            ? `<span class="inline-block text-xs px-1.5 py-0.5 rounded bg-spire-accent/15 text-spire-accent mr-auto">${depCount} 个依赖</span>`
            : '';
        html += `
            <div class="bg-spire-card border border-spire-border rounded-lg p-3 mb-3 break-inside-avoid">
                <div class="flex items-center justify-between mb-1">
                    <span class="text-sm font-medium text-spire-text truncate">${esc(m.name || m.id || '')}</span>
                    <span class="text-xs text-spire-muted shrink-0 ml-2">${esc(m.version || '')}</span>
                </div>
                ${m.author ? `<div class="text-xs text-spire-muted mb-1">${esc(m.author)}</div>` : ''}
                ${m.description ? `<div class="text-xs text-spire-muted mb-2 line-clamp-2">${esc(m.description)}</div>` : ''}
                <div class="flex items-center gap-1.5">
                    ${depTag}
                    ${exists
                        ? `<span class="text-xs text-spire-muted px-2 py-1 rounded border border-spire-border opacity-50 ml-auto">✓ 已存在</span>`
                        : `<button class="mm-copy-btn text-xs px-3 py-1 rounded bg-spire-accent hover:bg-spire-accentHover text-white transition-colors ml-auto"
                            data-branch="${escAttr(mmSelectedBranch)}" data-folder="${escAttr(m.folderName || '')}" data-mod-id="${escAttr(m.id || '')}">
                            📋 拷贝
                        </button>`
                    }
                </div>
            </div>`;
    }

    grid.innerHTML = html;
}

// ── event delegation ─────────────────────────────────────────────────────────

// main grid: click mod card -> open detail
$('#mm-grid').addEventListener('click', e => {
    const card = e.target.closest('[data-mod-id]');
    if (card) openModDetail(card.dataset.modId);
});

// detail modal: click dep row -> navigate to that mod's detail
$('#mm-detail-content').addEventListener('click', e => {
    const depRow = e.target.closest('.mm-dep-row.clickable[data-mod-id]');
    if (depRow) {
        openModDetail(depRow.dataset.modId);
        return;
    }
    // close button
    if (e.target.closest('.mm-detail-close')) {
        closeModDetail();
        return;
    }
    // delete button
    const delBtn = e.target.closest('.mm-delete-btn');
    if (delBtn) {
        const folderName = delBtn.dataset.folder;
        handleDeleteMod(folderName);
    }
});

// close detail by clicking backdrop
$('#mm-detail-modal').addEventListener('click', e => {
    if (e.target === e.currentTarget) closeModDetail();
});

// branch copy grid: click copy button — with recursive dependency detection
$('#mm-branch-grid').addEventListener('click', async e => {
    const btn = e.target.closest('.mm-copy-btn');
    if (!btn) return;
    const branchName = btn.dataset.branch;
    const folderName = btn.dataset.folder;
    const modId = btn.dataset.modId;

    // collect all missing dependencies (recursive) from branch mods
    const branchModById = new Map(mmBranchMods.map(m => [(m.id || '').toLowerCase(), m]));
    const missingDeps = [];

    function collectMissingDeps(id) {
        const mod = branchModById.get((id || '').toLowerCase());
        if (!mod) return;
        for (const depId of (mod.dependencies || [])) {
            const depLower = depId.toLowerCase();
            // not locally installed and not already in our copy list
            if (!mmLocalModIds.has(depLower) && !missingDeps.some(d => d.id.toLowerCase() === depLower)) {
                const depMod = branchModById.get(depLower);
                if (depMod) {
                    missingDeps.push(depMod);
                    // recurse into dep's own deps
                    collectMissingDeps(depId);
                }
            }
        }
    }
    collectMissingDeps(modId);

    // if there are missing deps, ask user whether to copy them too
    let copyDeps = false;
    if (missingDeps.length > 0) {
        const depNames = missingDeps.map(d => d.name || d.id).join('、');
        copyDeps = await showConfirm(
            `此 MOD 依赖以下未安装的前置 MOD：\n${depNames}\n\n是否一起拷贝？`,
            '拷贝依赖'
        );
    }

    // build the list of mods to copy
    const toCopy = [{ folderName, modId }];
    if (copyDeps) {
        for (const dep of missingDeps) {
            toCopy.push({ folderName: dep.folderName, modId: dep.id });
        }
    }

    btn.disabled = true;
    btn.textContent = '拷贝中...';

    let successCount = 0;
    for (const item of toCopy) {
        try {
            await ipcCall('COPY_MOD_FROM_BRANCH', { branchName, folderName: item.folderName });
            successCount++;
            // mark as local so UI updates
            mmLocalModIds.add((item.modId || '').toLowerCase());
        } catch (err) {
            toast(`拷贝 ${item.folderName} 失败：${err.message || ''}`, 'error');
        }
    }

    if (successCount > 0) {
        const msg = successCount === 1 ? 'MOD 拷贝成功' : `已拷贝 ${successCount} 个 MOD`;
        toast(msg, 'success');
    }

    btn.textContent = '✓ 已拷贝';
    btn.className = 'text-xs px-3 py-1 rounded border border-spire-border text-spire-muted opacity-50';
    // re-render grid so dep buttons also show "已存在"
    renderBranchCopyGrid();
});

// header buttons
$('#mm-btn-close').addEventListener('click', closeModManager);
$('#mm-btn-branch-copy').addEventListener('click', openBranchCopyModal);

// open mod folder in file explorer
$('#mm-btn-open-folder').addEventListener('click', () => {
    sendMessage('OPEN_FOLDER', { folderType: 'mod' });
});

// refresh with spin animation
$('#mm-btn-refresh').addEventListener('click', async () => {
    const icon = $('#mm-btn-refresh').querySelector('i, svg');
    if (icon) {
        icon.style.transition = 'transform 0.5s ease';
        icon.style.transform = 'rotate(360deg)';
        // reset after animation completes so it can spin again next click
        setTimeout(() => {
            icon.style.transition = 'none';
            icon.style.transform = 'rotate(0deg)';
        }, 520);
    }
    await refreshModList();
    toast('已刷新', 'success');
});

// dropzone click -> open file picker to install
$('#mm-dropzone').addEventListener('click', async () => {
    try {
        const res = await ipcCall('PICK_MOD_ARCHIVE');
        if (res.payload?.cancelled) return;
        const names = res.payload?.installed || [];
        if (names.length > 0) {
            toast(`已安装：${names.join('、')}`, 'success');
            refreshModList();
        }
    } catch (err) {
        toast(`安装失败：${err.message || ''}`, 'error');
    }
});

// search input
$('#mm-search').addEventListener('input', () => renderMmGrid());

// branch copy modal events
$('#mm-branch-close').addEventListener('click', () => {
    closeBranchCopyModal();
    // refresh local list in case we copied mods
    refreshModList();
});
$('#mm-branch-copy-modal').addEventListener('click', e => {
    if (e.target === e.currentTarget) {
        closeBranchCopyModal();
        refreshModList();
    }
});
// custom branch picker dropdown
$('#mm-branch-picker-btn').addEventListener('click', e => {
    e.stopPropagation();
    $('#mm-branch-picker-dropdown').classList.toggle('hidden');
});
$('#mm-branch-picker-list').addEventListener('click', e => {
    const opt = e.target.closest('.mm-branch-option');
    if (!opt) return;
    mmSelectedBranch = opt.dataset.branch;
    $('#mm-branch-picker-label').textContent = mmSelectedBranch;
    $('#mm-branch-picker-dropdown').classList.add('hidden');
    // highlight selected
    $('#mm-branch-picker-list').querySelectorAll('.mm-branch-option').forEach(el => {
        el.classList.toggle('bg-spire-accent/15', el.dataset.branch === mmSelectedBranch);
    });
    loadBranchMods(mmSelectedBranch);
});
// close dropdown when clicking elsewhere
document.addEventListener('click', e => {
    if (!e.target.closest('#mm-branch-picker')) {
        $('#mm-branch-picker-dropdown').classList.add('hidden');
    }
});
$('#mm-branch-search').addEventListener('input', () => renderBranchCopyGrid());

// main modal backdrop close
$('#mod-manager-modal').addEventListener('click', e => {
    if (e.target === e.currentTarget) closeModManager();
});

// entry button on dashboard
$('#btn-open-mod-manager').addEventListener('click', openModManager);

// ── delete mod ───────────────────────────────────────────────────────────────

async function handleDeleteMod(folderName) {
    if (!folderName) return;
    const ok = await showConfirm(`确定要删除 MOD 文件夹「${folderName}」吗？\n此操作不可撤销。`, '删除 MOD');
    if (!ok) return;

    const res = await ipcCall('DELETE_MOD', { folderName });
    if (res.status === 'error') {
        toast(`删除失败：${res.message || ''}`, 'error');
        return;
    }
    toast('MOD 已删除', 'success');
    closeModDetail();
    refreshModList();
}

// ── drag-drop install ───────────────────────────────────────────────────────
// WebView2 doesn't expose file paths to JS, so we read everything as base64
// and send structured data to C# for processing.
// supports: archives (.zip/.rar/.7z), folders, loose files, and any mix

function arrayBufferToBase64(buffer) {
    const bytes = new Uint8Array(buffer);
    let binary = '';
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(binary);
}

// promisified file reader
function readFileAsBase64(file) {
    return file.arrayBuffer().then(buf => arrayBufferToBase64(buf));
}

// recursively enumerate all files in a FileSystemDirectoryEntry
function readDirectoryEntries(dirEntry, basePath) {
    return new Promise((resolve, reject) => {
        const reader = dirEntry.createReader();
        const allEntries = [];

        // readEntries may return batches of <= 100, need to call until empty
        function readBatch() {
            reader.readEntries(entries => {
                if (entries.length === 0) {
                    resolve(allEntries);
                    return;
                }
                allEntries.push(...entries);
                readBatch();
            }, reject);
        }
        readBatch();
    }).then(async entries => {
        const results = [];
        for (const entry of entries) {
            const entryPath = basePath ? `${basePath}/${entry.name}` : entry.name;
            if (entry.isFile) {
                const file = await new Promise((res, rej) => entry.file(res, rej));
                results.push({ path: entryPath, file });
            } else if (entry.isDirectory) {
                const sub = await readDirectoryEntries(entry, entryPath);
                results.push(...sub);
            }
        }
        return results;
    });
}

// prevent default drag behavior everywhere to stop WebView2 navigation
document.addEventListener('dragover', e => e.preventDefault());
document.addEventListener('drop', e => e.preventDefault());

// the whole mod manager modal is a valid drop target
const mmModal = $('#mod-manager-modal');
const mmDropzone = $('#mm-dropzone');

mmModal.addEventListener('dragover', e => {
    e.preventDefault();
    e.stopPropagation();
    mmDropzone.classList.add('drag-over');
});
mmModal.addEventListener('dragleave', e => {
    // only remove highlight when actually leaving the modal
    if (!mmModal.contains(e.relatedTarget)) {
        mmDropzone.classList.remove('drag-over');
    }
});
mmModal.addEventListener('drop', async e => {
    e.preventDefault();
    e.stopPropagation();
    mmDropzone.classList.remove('drag-over');

    const items = e.dataTransfer.items;
    if (!items || items.length === 0) return;

    showLoading('正在读取文件...');

    try {
        const archives = [];  // { name, data }
        const folders = [];   // { name, entries: [{ path, data }] }
        const looseFiles = []; // { name, data }

        // use webkitGetAsEntry to distinguish files from folders
        const entryList = [];
        for (let i = 0; i < items.length; i++) {
            const entry = items[i].webkitGetAsEntry?.();
            if (entry) entryList.push(entry);
        }

        for (const entry of entryList) {
            if (entry.isDirectory) {
                // recursively read all files in the folder
                const dirFiles = await readDirectoryEntries(entry, '');
                if (dirFiles.length === 0) continue;

                const folderEntries = [];
                for (const { path, file } of dirFiles) {
                    const data = await readFileAsBase64(file);
                    folderEntries.push({ path, data });
                }
                folders.push({ name: entry.name, entries: folderEntries });
            } else if (entry.isFile) {
                const file = await new Promise((res, rej) => entry.file(res, rej));
                const data = await readFileAsBase64(file);

                if (/\.(zip|rar|7z)$/i.test(file.name)) {
                    archives.push({ name: file.name, data });
                } else {
                    looseFiles.push({ name: file.name, data });
                }
            }
        }

        if (archives.length === 0 && folders.length === 0 && looseFiles.length === 0) {
            toast('未检测到可安装的内容', 'error');
            return;
        }

        showLoading('正在安装 MOD...');
        const res = await ipcCall('INSTALL_MOD_DROPPED', { archives, folders, looseFiles }, 120000);
        const names = res.payload?.installed || [];
        if (names.length > 0) {
            toast(`已安装：${names.join('、')}`, 'success');
            refreshModList();
        }
    } catch (err) {
        toast(`安装失败：${err.message || ''}`, 'error');
    } finally {
        hideLoading();
    }
});
