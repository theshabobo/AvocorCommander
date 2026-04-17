/* =============================================================================
   Avocor Commander -- SPA Router + All Page Renderers
   ============================================================================= */

(function () {
    'use strict';

    // =========================================================================
    //  UTILITIES
    // =========================================================================

    function $(sel, root) { return (root || document).querySelector(sel); }
    function $$(sel, root) { return Array.from((root || document).querySelectorAll(sel)); }
    function esc(s) { var d = document.createElement('div'); d.textContent = s || ''; return d.innerHTML; }
    function formatTime(ts) {
        if (!ts) return '--';
        try { var d = new Date(ts); return d.toLocaleString(); } catch { return ts; }
    }
    function timeNow() { return new Date().toLocaleTimeString('en-GB', { hour12: false }); }
    function uid() { return '_' + Math.random().toString(36).substr(2, 9); }

    // Toast notifications
    function toast(message, type, duration) {
        type = type || 'info';
        duration = duration || 4000;
        var container = document.getElementById('toast-container');
        var icons = { success: '\u2714', error: '\u2718', warning: '\u26A0', info: '\u2139' };
        var el = document.createElement('div');
        el.className = 'toast toast-' + type;
        el.innerHTML = '<span class="toast-icon">' + (icons[type] || '') + '</span><span>' + esc(message) + '</span>';
        container.appendChild(el);
        setTimeout(function () {
            el.classList.add('removing');
            setTimeout(function () { el.remove(); }, 300);
        }, duration);
    }

    // Modal helper
    function showModal(title, bodyHtml, footerHtml, opts) {
        opts = opts || {};
        var overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        overlay.innerHTML =
            '<div class="modal ' + (opts.large ? 'modal-lg' : '') + '">' +
                '<div class="modal-header">' +
                    '<div class="modal-title">' + esc(title) + '</div>' +
                    '<button class="modal-close" data-close>&times;</button>' +
                '</div>' +
                '<div class="modal-body">' + bodyHtml + '</div>' +
                (footerHtml ? '<div class="modal-footer">' + footerHtml + '</div>' : '') +
            '</div>';
        document.body.appendChild(overlay);

        var close = function () { overlay.remove(); };
        overlay.querySelector('[data-close]').addEventListener('click', close);
        overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });
        var allCloseButtons = overlay.querySelectorAll('[data-close]');
        for (var i = 0; i < allCloseButtons.length; i++) {
            allCloseButtons[i].addEventListener('click', close);
        }

        return { overlay: overlay, close: close, el: overlay.querySelector('.modal') };
    }

    // Confirm dialog
    function confirmDialog(title, message) {
        return new Promise(function (resolve) {
            var modal = showModal(title,
                '<p style="color:var(--text-secondary)">' + esc(message) + '</p>',
                '<button class="btn btn-secondary" data-action="cancel">Cancel</button>' +
                '<button class="btn btn-danger" data-action="confirm">Confirm</button>'
            );
            var overlay = modal.overlay;
            overlay.addEventListener('click', function (e) {
                var action = e.target.dataset.action;
                if (action === 'confirm') { modal.close(); resolve(true); }
                else if (action === 'cancel') { modal.close(); resolve(false); }
            });
        });
    }

    // Sleep helper for Test All
    function sleep(ms) {
        return new Promise(function (resolve) { setTimeout(resolve, ms); });
    }

    // =========================================================================
    //  THEME
    // =========================================================================

    function applyTheme(theme) {
        document.body.className = 'theme-' + theme;
        localStorage.setItem('theme', theme);
    }

    function getCurrentTheme() {
        return localStorage.getItem('theme') || 'avocor';
    }

    applyTheme(getCurrentTheme());

    // =========================================================================
    //  APP ROUTER
    // =========================================================================

    var pages = {
        login:      { title: 'Login',           icon: '\uD83D\uDD12', render: renderLogin,       wire: wireLogin,       nav: false },
        dashboard:  { title: 'Dashboard',       icon: '\uD83D\uDCCA', render: renderDashboard,   wire: wireDashboard,   nav: true },
        devices:    { title: 'Devices',         icon: '\uD83D\uDCF1', render: renderDevices,     wire: wireDevices,     nav: true },
        rooms:      { title: 'Rooms',           icon: '\uD83C\uDFE2', render: renderRooms,       wire: wireRooms,       nav: true },
        control:    { title: 'Device Control',  icon: '\uD83C\uDFAE', render: renderControl,     wire: wireControl,     nav: true },
        macros:     { title: 'Macros',          icon: '\u26A1',       render: renderMacros,      wire: wireMacros,      nav: true },
        scheduler:  { title: 'Scheduler',       icon: '\uD83D\uDD52', render: renderScheduler,   wire: wireScheduler,   nav: true },
        database:   { title: 'Database',        icon: '\uD83D\uDDC4', render: renderDatabase,    wire: wireDatabase,    nav: true },
        audit:      { title: 'Audit Log',       icon: '\uD83D\uDCCB', render: renderAudit,       wire: wireAudit,       nav: true },
        settings:   { title: 'Settings',        icon: '\u2699\uFE0F', render: renderSettings,    wire: wireSettings,    nav: true }
    };

    var navIcons = {
        dashboard: '\uD83D\uDCCA',
        devices:   '\uD83D\uDCF1',
        rooms:     '\uD83C\uDFE2',
        control:   '\uD83C\uDFAE',
        macros:    '\u26A1',
        scheduler: '\uD83D\uDD52',
        database:  '\uD83D\uDDC4',
        audit:     '\uD83D\uDCCB',
        settings:  '\u2699\uFE0F'
    };

    var mobileNav = ['dashboard', 'control', 'devices', 'settings', 'audit'];

    var currentPage = null;
    var cachedData = {};
    var wsCleanups = [];

    // Persistent expanded rooms across re-renders
    var expandedRooms = new Set();

    var app = {
        navigate: function (page) {
            wsCleanups.forEach(function (fn) { fn(); });
            wsCleanups = [];

            if (!api.isAuthenticated && page !== 'login') {
                page = 'login';
            }
            if (api.isAuthenticated && page === 'login') {
                page = 'dashboard';
            }

            currentPage = page;
            var pageInfo = pages[page];
            if (!pageInfo) { currentPage = 'dashboard'; return this.navigate('dashboard'); }

            var container = document.getElementById('app');
            if (page === 'login') {
                container.innerHTML = pageInfo.render();
            } else {
                container.innerHTML = renderShell(page, pageInfo);
            }

            pageInfo.wire();

            if (location.hash !== '#' + page) {
                history.replaceState(null, '', '#' + page);
            }
        }
    };

    window.__app = app;

    // -- Shell (sidebar + content area) ---------------------------------------

    function renderShell(page, pageInfo) {
        var user = api.getUser();
        var navItems = '';
        var keys = Object.keys(pages);
        for (var i = 0; i < keys.length; i++) {
            var key = keys[i];
            var p = pages[key];
            if (!p.nav) continue;
            navItems += '<button class="nav-item ' + (key === page ? 'active' : '') + '" data-nav="' + key + '">' +
                '<span class="nav-icon">' + (navIcons[key] || '') + '</span>' +
                '<span>' + p.title + '</span>' +
            '</button>';
        }

        var bottomItems = '';
        for (var j = 0; j < mobileNav.length; j++) {
            var mk = mobileNav[j];
            var mp = pages[mk];
            bottomItems += '<button class="bottom-nav-item ' + (mk === page ? 'active' : '') + '" data-nav="' + mk + '">' +
                '<span class="bnav-icon">' + (navIcons[mk] || '') + '</span>' +
                '<span>' + mp.title + '</span>' +
            '</button>';
        }

        return '' +
            '<aside class="sidebar">' +
                '<div class="sidebar-logo">' +
                    '<img src="/img/logo.png" alt="" onerror="this.style.display=\'none\'">' +
                    '<div class="sidebar-logo-text">AVOCOR<br>COMMANDER</div>' +
                '</div>' +
                '<nav class="sidebar-nav">' + navItems + '</nav>' +
                '<div class="sidebar-footer">' +
                    '<div class="sidebar-user">' + esc(user.username) + ' &middot; ' + esc(user.role) + '</div>' +
                    '<button class="nav-item" data-action="logout">' +
                        '<span class="nav-icon">\uD83D\uDEAA</span>' +
                        '<span>Logout</span>' +
                    '</button>' +
                '</div>' +
            '</aside>' +
            '<div class="main-content">' +
                '<div class="page-header">' +
                    '<div><div class="page-title">' + pageInfo.title + '</div></div>' +
                    '<div id="page-header-actions"></div>' +
                '</div>' +
                '<div class="page-body" id="page-body">' +
                    pageInfo.render() +
                '</div>' +
            '</div>' +
            '<nav class="bottom-nav">' + bottomItems + '</nav>';
    }

    // -- Global nav wiring ----------------------------------------------------

    function wireGlobalNav() {
        document.addEventListener('click', function (e) {
            var navBtn = e.target.closest('[data-nav]');
            if (navBtn) {
                app.navigate(navBtn.dataset.nav);
                return;
            }
            var logoutBtn = e.target.closest('[data-action="logout"]');
            if (logoutBtn) {
                api.logout();
                app.navigate('login');
            }
        });
    }

    // =========================================================================
    //  PAGE: LOGIN
    // =========================================================================

    function renderLogin() {
        return '' +
            '<div class="login-page">' +
                '<div class="login-card">' +
                    '<div class="login-logo">' +
                        '<img src="/img/logo.png" alt="" onerror="this.style.display=\'none\';this.nextElementSibling.style.display=\'block\'">' +
                        '<div class="login-logo-text" style="display:block">AVOCOR COMMANDER</div>' +
                    '</div>' +
                    '<div class="login-error" id="login-error"></div>' +
                    '<form id="login-form">' +
                        '<div class="form-group">' +
                            '<label class="form-label">Username</label>' +
                            '<input type="text" class="form-input" id="login-user" autocomplete="username" required autofocus>' +
                        '</div>' +
                        '<div class="form-group">' +
                            '<label class="form-label">Password</label>' +
                            '<input type="password" class="form-input" id="login-pass" autocomplete="current-password" required>' +
                        '</div>' +
                        '<div class="form-group">' +
                            '<label class="form-check">' +
                                '<input type="checkbox" id="login-remember" checked>' +
                                '<span>Remember me</span>' +
                            '</label>' +
                        '</div>' +
                        '<button type="submit" class="btn btn-primary btn-block btn-lg" id="login-btn">Sign In</button>' +
                    '</form>' +
                '</div>' +
            '</div>';
    }

    function wireLogin() {
        var form = $('#login-form');
        if (!form) return;

        form.addEventListener('submit', async function (e) {
            e.preventDefault();
            var user = $('#login-user').value.trim();
            var pass = $('#login-pass').value;
            var remember = $('#login-remember').checked;
            var errEl = $('#login-error');
            var btn = $('#login-btn');

            if (!user || !pass) {
                errEl.textContent = 'Please enter username and password.';
                errEl.classList.add('visible');
                return;
            }

            btn.disabled = true;
            btn.innerHTML = '<span class="spinner"></span> Signing in...';
            errEl.classList.remove('visible');

            try {
                await api.login(user, pass, remember);
                api.connectWebSocket();
                app.navigate('dashboard');
            } catch (err) {
                errEl.textContent = err.message || 'Login failed.';
                errEl.classList.add('visible');
                btn.disabled = false;
                btn.textContent = 'Sign In';
            }
        });
    }

    // =========================================================================
    //  PAGE: DASHBOARD
    // =========================================================================

    function renderDashboard() {
        return '' +
            '<div class="summary-bar" id="dash-summary">' +
                '<div class="summary-stat"><span class="summary-value" id="dash-total">--</span><span class="summary-label">devices</span></div>' +
                '<div class="summary-stat"><span class="summary-value" id="dash-online" style="color:var(--success)">--</span><span class="summary-label">online</span></div>' +
                '<div class="summary-stat"><span class="summary-value" id="dash-connected" style="color:rgb(74,234,220)">--</span><span class="summary-label">connected</span></div>' +
            '</div>' +
            '<div id="dash-rooms">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading devices...</span></div>' +
            '</div>';
    }

    async function wireDashboard() {
        try {
            var results = await Promise.all([api.getDevices(), api.getGroups()]);
            cachedData.devices = results[0];
            cachedData.groups = results[1];
            renderDashboardData(results[0], results[1]);
        } catch (err) {
            toast(err.message, 'error');
            $('#dash-rooms').innerHTML = '<div class="empty-state"><div class="empty-state-icon">\u26A0\uFE0F</div><div class="empty-state-title">Failed to load</div><p class="text-muted">' + esc(err.message) + '</p></div>';
        }

        var unsub = api.on('ws:connection.changed', function (data) {
            var dev = cachedData.devices ? cachedData.devices.find(function (d) { return d.id === data.deviceId; }) : null;
            if (dev) {
                dev.isConnected = data.isConnected;
                renderDashboardData(cachedData.devices, cachedData.groups || []);
            }
        });
        wsCleanups.push(unsub);
    }

    function renderDashboardData(devices, groups) {
        var total = devices.length;
        var connected = devices.filter(function (d) { return d.isConnected; }).length;

        var totalEl = $('#dash-total');
        var onlineEl = $('#dash-online');
        var connEl = $('#dash-connected');
        if (totalEl) totalEl.textContent = total;
        if (onlineEl) onlineEl.textContent = total;
        if (connEl) connEl.textContent = connected;

        var container = $('#dash-rooms');
        if (!container) return;

        if (groups.length === 0) {
            container.innerHTML = '<div class="device-grid">' + devices.map(renderDeviceTile).join('') + '</div>';
            wireDeviceTileActions(container);
            return;
        }

        var assignedIds = new Set();
        groups.forEach(function (g) { (g.memberDeviceIds || []).forEach(function (id) { assignedIds.add(id); }); });
        var ungrouped = devices.filter(function (d) { return !assignedIds.has(d.id); });

        var html = '';
        for (var i = 0; i < groups.length; i++) {
            var g = groups[i];
            var members = (g.memberDeviceIds || []).map(function (id) { return devices.find(function (d) { return d.id === id; }); }).filter(Boolean);
            var onlineCount = members.filter(function (d) { return d.isConnected; }).length;
            var isExpanded = expandedRooms.has(g.id);
            html += '' +
                '<div class="room-card' + (isExpanded ? ' expanded' : '') + '" data-room="' + g.id + '">' +
                    '<div class="room-header" data-toggle-room="' + g.id + '">' +
                        '<div class="room-title"><span>\uD83C\uDFE2</span><span>' + esc(g.groupName) + '</span></div>' +
                        '<div class="flex items-center gap-16">' +
                            '<div class="room-stats">' +
                                '<span class="room-stat">' + members.length + ' device' + (members.length !== 1 ? 's' : '') + '</span>' +
                                '<span class="room-stat"><span class="status-dot ' + (onlineCount > 0 ? 'online' : 'offline') + '"></span> ' + onlineCount + ' connected</span>' +
                            '</div>' +
                            '<span class="room-chevron">\u25BC</span>' +
                        '</div>' +
                    '</div>' +
                    '<div class="room-body"><div class="device-grid">' + members.map(renderDeviceTile).join('') + '</div></div>' +
                '</div>';
        }

        if (ungrouped.length > 0) {
            var isUngroupedExpanded = expandedRooms.has('ungrouped');
            html += '' +
                '<div class="room-card' + (isUngroupedExpanded ? ' expanded' : '') + '" data-room="ungrouped">' +
                    '<div class="room-header" data-toggle-room="ungrouped">' +
                        '<div class="room-title"><span>\uD83D\uDCE6</span> Ungrouped</div>' +
                        '<div class="flex items-center gap-16">' +
                            '<div class="room-stats"><span class="room-stat">' + ungrouped.length + ' device' + (ungrouped.length !== 1 ? 's' : '') + '</span></div>' +
                            '<span class="room-chevron">\u25BC</span>' +
                        '</div>' +
                    '</div>' +
                    '<div class="room-body"><div class="device-grid">' + ungrouped.map(renderDeviceTile).join('') + '</div></div>' +
                '</div>';
        }

        container.innerHTML = html;

        // Toggle rooms and track in expandedRooms Set
        container.addEventListener('click', function (e) {
            var toggle = e.target.closest('[data-toggle-room]');
            if (toggle) {
                var roomId = toggle.dataset.toggleRoom;
                var card = toggle.closest('.room-card');
                card.classList.toggle('expanded');
                if (card.classList.contains('expanded')) {
                    expandedRooms.add(isNaN(parseInt(roomId)) ? roomId : parseInt(roomId));
                } else {
                    expandedRooms.delete(isNaN(parseInt(roomId)) ? roomId : parseInt(roomId));
                }
            }
        });

        wireDeviceTileActions(container);
    }

    // Three-state device status
    function renderDeviceTile(d) {
        var statusClass, statusText;
        if (d.isConnected) {
            statusClass = 'online';
            statusText = 'Online - Connected';
        } else {
            statusClass = 'offline';
            statusText = 'Offline';
        }
        return '' +
            '<div class="device-tile" data-device-id="' + d.id + '">' +
                '<div class="device-tile-header">' +
                    '<span class="status-dot ' + statusClass + '"></span>' +
                    '<span class="device-tile-name">' + esc(d.deviceName) + '</span>' +
                '</div>' +
                '<div class="device-tile-info">' +
                    '<span>' + esc(d.modelNumber || 'Unknown model') + '</span>' +
                    '<span>' + esc(d.ipAddress) + ':' + d.port + '</span>' +
                    '<span>' + statusText + (d.series ? ' \u00b7 ' + esc(d.series) : '') + '</span>' +
                '</div>' +
                '<div class="device-tile-actions">' +
                    '<button class="btn btn-sm btn-secondary" data-wake="' + d.id + '" title="Wake on LAN">\u23FB Wake</button>' +
                    (d.isConnected
                        ? '<button class="btn btn-sm btn-ghost" data-disconnect="' + d.id + '">Disconnect</button>'
                        : '<button class="btn btn-sm btn-success" data-connect="' + d.id + '">Connect</button>') +
                    (!d.isConnected ? '<button class="btn btn-sm btn-secondary" data-check="' + d.id + '">Check</button>' : '') +
                '</div>' +
            '</div>';
    }

    function wireDeviceTileActions(container) {
        container.addEventListener('click', async function (e) {
            var wakeBtn = e.target.closest('[data-wake]');
            if (wakeBtn) {
                wakeBtn.disabled = true;
                try {
                    var res = await api.wakeDevice(parseInt(wakeBtn.dataset.wake));
                    toast(res.detail || 'Wake sent', 'success');
                } catch (err) { toast(err.message, 'error'); }
                wakeBtn.disabled = false;
                return;
            }

            var checkBtn = e.target.closest('[data-check]');
            if (checkBtn) {
                var checkId = parseInt(checkBtn.dataset.check);
                checkBtn.disabled = true;
                checkBtn.textContent = 'Checking...';
                try {
                    await api.connectDevice(checkId);
                    toast('Device is online and connected', 'success');
                    if (currentPage === 'dashboard') {
                        var cr = await Promise.all([api.getDevices(), api.getGroups()]);
                        cachedData.devices = cr[0]; cachedData.groups = cr[1];
                        renderDashboardData(cr[0], cr[1]);
                    } else if (currentPage === 'devices') {
                        var devs = await api.getDevices();
                        cachedData.devices = devs;
                        renderDevicesList(devs);
                    }
                } catch (err) {
                    toast('Device is offline: ' + err.message, 'warning');
                    checkBtn.disabled = false;
                    checkBtn.textContent = 'Check';
                }
                return;
            }

            var connBtn = e.target.closest('[data-connect]');
            if (connBtn) {
                connBtn.disabled = true;
                connBtn.textContent = 'Connecting...';
                try {
                    await api.connectDevice(parseInt(connBtn.dataset.connect));
                    toast('Connected', 'success');
                    if (currentPage === 'dashboard') {
                        var r2 = await Promise.all([api.getDevices(), api.getGroups()]);
                        cachedData.devices = r2[0]; cachedData.groups = r2[1];
                        renderDashboardData(r2[0], r2[1]);
                    } else if (currentPage === 'devices') {
                        var devs2 = await api.getDevices();
                        cachedData.devices = devs2;
                        renderDevicesList(devs2);
                    }
                } catch (err) { toast(err.message, 'error'); connBtn.disabled = false; connBtn.textContent = 'Connect'; }
                return;
            }
            var dcBtn = e.target.closest('[data-disconnect]');
            if (dcBtn) {
                dcBtn.disabled = true;
                try {
                    await api.disconnectDevice(parseInt(dcBtn.dataset.disconnect));
                    toast('Disconnected', 'info');
                    if (currentPage === 'dashboard') {
                        var r3 = await Promise.all([api.getDevices(), api.getGroups()]);
                        cachedData.devices = r3[0]; cachedData.groups = r3[1];
                        renderDashboardData(r3[0], r3[1]);
                    } else if (currentPage === 'devices') {
                        var devs3 = await api.getDevices();
                        cachedData.devices = devs3;
                        renderDevicesList(devs3);
                    }
                } catch (err) { toast(err.message, 'error'); dcBtn.disabled = false; }
            }
        });
    }

    // =========================================================================
    //  PAGE: DEVICES (CRUD)
    // =========================================================================

    function renderDevices() {
        return '' +
            '<div class="flex items-center justify-between mb-16">' +
                '<div class="text-muted text-sm" id="devices-count"></div>' +
                '<div class="flex gap-8">' +
                    '<button class="btn btn-secondary btn-sm" id="scan-network-btn">\uD83D\uDD0D Import from Scan (Desktop)</button>' +
                    '<button class="btn btn-primary btn-sm" id="add-device-btn">+ Add Device</button>' +
                '</div>' +
            '</div>' +
            '<div id="devices-list">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading devices...</span></div>' +
            '</div>';
    }

    async function wireDevices() {
        try {
            var devices = await api.getDevices();
            cachedData.devices = devices;
            renderDevicesList(devices);
        } catch (err) {
            toast(err.message, 'error');
            $('#devices-list').innerHTML = '<div class="empty-state"><div class="empty-state-icon">\u26A0\uFE0F</div><div class="empty-state-title">Failed to load devices</div></div>';
        }

        $('#add-device-btn').addEventListener('click', function () { showDeviceModal(null); });

        // Scan Network button
        $('#scan-network-btn').addEventListener('click', function () { showScanNetworkModal(); });
    }

    function renderDevicesList(devices) {
        var countEl = $('#devices-count');
        if (countEl) countEl.textContent = devices.length + ' device' + (devices.length !== 1 ? 's' : '');

        var container = $('#devices-list');
        if (devices.length === 0) {
            container.innerHTML = '<div class="empty-state"><div class="empty-state-icon">\uD83D\uDCF1</div><div class="empty-state-title">No devices</div><p class="text-muted">Add a device to get started.</p></div>';
            return;
        }

        container.innerHTML = '<div class="device-grid">' + devices.map(function (d) {
            var statusClass, statusText;
            if (d.isConnected) {
                statusClass = 'online';
                statusText = 'Online - Connected';
            } else {
                statusClass = 'offline';
                statusText = 'Offline';
            }
            return '' +
                '<div class="device-tile">' +
                    '<div class="device-tile-header">' +
                        '<span class="status-dot ' + statusClass + '"></span>' +
                        '<span class="device-tile-name">' + esc(d.deviceName) + '</span>' +
                    '</div>' +
                    '<div class="device-tile-info">' +
                        '<span>' + esc(d.modelNumber || 'Unknown') + (d.series ? ' (' + esc(d.series) + ')' : '') + '</span>' +
                        '<span>' + esc(d.ipAddress || '--') + ':' + (d.port || '--') + '</span>' +
                        '<span>' + statusText + '</span>' +
                        (d.connectionType ? '<span>Type: ' + esc(d.connectionType) + '</span>' : '') +
                    '</div>' +
                    '<div class="device-tile-actions">' +
                        '<button class="btn btn-sm btn-secondary" data-edit-device="' + d.id + '">Edit</button>' +
                        '<button class="btn btn-sm btn-danger" data-delete-device="' + d.id + '">Delete</button>' +
                        (!d.isConnected ? '<button class="btn btn-sm btn-secondary" data-check="' + d.id + '">Check</button>' : '') +
                    '</div>' +
                '</div>';
        }).join('') + '</div>';

        wireDeviceTileActions(container);

        container.addEventListener('click', async function (e) {
            var editBtn = e.target.closest('[data-edit-device]');
            if (editBtn) {
                var id = parseInt(editBtn.dataset.editDevice);
                var device = devices.find(function (d) { return d.id === id; });
                if (device) showDeviceModal(device);
                return;
            }
            var delBtn = e.target.closest('[data-delete-device]');
            if (delBtn) {
                var did = parseInt(delBtn.dataset.deleteDevice);
                var ok = await confirmDialog('Delete Device', 'Are you sure you want to delete this device?');
                if (!ok) return;
                try {
                    await api.deleteDevice(did);
                    toast('Device deleted', 'success');
                    app.navigate('devices');
                } catch (err) { toast(err.message, 'error'); }
            }
        });
    }

    // Scan Network modal
    function showScanNetworkModal() {
        var body = '' +
            '<div style="padding:24px;text-align:center">' +
                '<div style="font-size:48px;margin-bottom:16px">\uD83D\uDD0D</div>' +
                '<h3 style="margin-bottom:12px;color:var(--text-primary)">Network Scanning</h3>' +
                '<p style="color:var(--text-secondary);margin-bottom:16px">Scan Network requires the desktop application. It uses ICMP pings and TCP probes to discover Avocor displays on the network.</p>' +
                '<p style="color:var(--text-secondary)">Devices discovered via scan in the desktop app will appear here automatically.</p>' +
            '</div>';

        showModal('Import from Scan (Desktop)', body,
            '<button class="btn btn-secondary" data-close>Close</button>',
            { large: false }
        );
    }

    // Device modal with room membership section
    async function showDeviceModal(device) {
        var isEdit = !!device;
        var models = [];
        var groups = [];
        try { models = await api.getModels(); } catch (err) { /* ok */ }
        try { groups = await api.getGroups(); } catch (err) { /* ok */ }

        var modelOptions = '<option value="">Select model...</option>';
        if (Array.isArray(models)) {
            models.forEach(function (m) {
                var name = typeof m === 'string' ? m : (m.modelNumber || m.name || '');
                var selected = device && device.modelNumber === name ? ' selected' : '';
                modelOptions += '<option value="' + esc(name) + '"' + selected + '>' + esc(name) + '</option>';
            });
        }

        // Determine which groups this device currently belongs to
        var deviceGroupIds = new Set();
        if (device && Array.isArray(groups)) {
            groups.forEach(function (g) {
                if ((g.memberDeviceIds || []).indexOf(device.id) >= 0) {
                    deviceGroupIds.add(g.id);
                }
            });
        }

        var roomCheckboxes = '';
        if (Array.isArray(groups) && groups.length > 0) {
            roomCheckboxes = groups.map(function (g) {
                var checked = deviceGroupIds.has(g.id) ? ' checked' : '';
                return '<label class="form-check">' +
                    '<input type="checkbox" data-group-id="' + g.id + '"' + checked + '>' +
                    '<span>' + esc(g.groupName) + '</span></label>';
            }).join('');
        } else {
            roomCheckboxes = '<p class="text-muted text-sm">No rooms available. Create rooms first.</p>';
        }

        var body = '' +
            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">Device Name</label>' +
                    '<input type="text" class="form-input" id="dev-name" value="' + esc(device ? device.deviceName : '') + '" required></div>' +
                '<div class="form-group"><label class="form-label">Model</label>' +
                    '<select class="form-input" id="dev-model">' + modelOptions + '</select></div>' +
            '</div>' +
            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">IP Address</label>' +
                    '<input type="text" class="form-input" id="dev-ip" value="' + esc(device ? device.ipAddress : '') + '" placeholder="192.168.1.100"></div>' +
                '<div class="form-group"><label class="form-label">Port</label>' +
                    '<input type="number" class="form-input" id="dev-port" value="' + (device ? device.port || '' : '') + '" placeholder="5000"></div>' +
            '</div>' +
            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">Baud Rate</label>' +
                    '<input type="number" class="form-input" id="dev-baud" value="' + (device && device.baudRate ? device.baudRate : '') + '" placeholder="9600"></div>' +
                '<div class="form-group"><label class="form-label">COM Port</label>' +
                    '<input type="text" class="form-input" id="dev-com" value="' + esc(device && device.comPort ? device.comPort : '') + '" placeholder="COM3"></div>' +
            '</div>' +
            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">MAC Address</label>' +
                    '<input type="text" class="form-input" id="dev-mac" value="' + esc(device && device.macAddress ? device.macAddress : '') + '" placeholder="AA:BB:CC:DD:EE:FF"></div>' +
                '<div class="form-group"><label class="form-label">Connection Type</label>' +
                    '<select class="form-input" id="dev-conntype">' +
                        '<option value="TCP"' + (device && device.connectionType === 'TCP' ? ' selected' : '') + '>TCP</option>' +
                        '<option value="Serial"' + (device && device.connectionType === 'Serial' ? ' selected' : '') + '>Serial</option>' +
                    '</select></div>' +
            '</div>' +
            '<div class="form-group"><label class="form-label">Notes</label>' +
                '<textarea class="form-input" id="dev-notes" rows="2">' + esc(device && device.notes ? device.notes : '') + '</textarea></div>' +
            '<div class="form-group"><label class="form-check">' +
                '<input type="checkbox" id="dev-autoconnect"' + (device && device.autoConnect ? ' checked' : '') + '>' +
                '<span>Auto-connect on startup</span></label></div>' +
            '<hr style="border-color:var(--border);margin:16px 0">' +
            '<div class="form-group">' +
                '<label class="form-label">Room Membership</label>' +
                '<div class="flex flex-col gap-8" id="dev-room-checkboxes">' + roomCheckboxes + '</div>' +
            '</div>';

        var modal = showModal(isEdit ? 'Edit Device' : 'Add Device', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="dev-save">' + (isEdit ? 'Save Changes' : 'Add Device') + '</button>'
        );

        modal.el.querySelector('#dev-save').addEventListener('click', async function () {
            var data = {
                deviceName: modal.el.querySelector('#dev-name').value.trim(),
                modelNumber: modal.el.querySelector('#dev-model').value,
                ipAddress: modal.el.querySelector('#dev-ip').value.trim(),
                port: parseInt(modal.el.querySelector('#dev-port').value) || 0,
                baudRate: parseInt(modal.el.querySelector('#dev-baud').value) || 0,
                comPort: modal.el.querySelector('#dev-com').value.trim(),
                macAddress: modal.el.querySelector('#dev-mac').value.trim(),
                connectionType: modal.el.querySelector('#dev-conntype').value,
                notes: modal.el.querySelector('#dev-notes').value.trim(),
                autoConnect: modal.el.querySelector('#dev-autoconnect').checked
            };

            if (!data.deviceName) { toast('Device name is required', 'warning'); return; }

            var btn = modal.el.querySelector('#dev-save');
            btn.disabled = true;

            try {
                var savedDevice;
                if (isEdit) {
                    savedDevice = await api.updateDevice(device.id, data);
                    toast('Device updated', 'success');
                } else {
                    savedDevice = await api.addDevice(data);
                    toast('Device added', 'success');
                }

                // Update room membership
                var deviceId = isEdit ? device.id : (savedDevice && savedDevice.id ? savedDevice.id : null);
                if (deviceId && Array.isArray(groups) && groups.length > 0) {
                    var checkedGroupEls = modal.el.querySelectorAll('[data-group-id]');
                    for (var gi = 0; gi < checkedGroupEls.length; gi++) {
                        var cb = checkedGroupEls[gi];
                        var gId = parseInt(cb.dataset.groupId);
                        var grp = groups.find(function (g) { return g.id === gId; });
                        if (!grp) continue;

                        var currentMembers = (grp.memberDeviceIds || []).slice();
                        var isCurrentlyMember = currentMembers.indexOf(deviceId) >= 0;

                        if (cb.checked && !isCurrentlyMember) {
                            currentMembers.push(deviceId);
                            try { await api.setGroupMembers(gId, currentMembers); } catch (err) { /* ok */ }
                        } else if (!cb.checked && isCurrentlyMember) {
                            currentMembers = currentMembers.filter(function (mid) { return mid !== deviceId; });
                            try { await api.setGroupMembers(gId, currentMembers); } catch (err) { /* ok */ }
                        }
                    }
                }

                modal.close();
                app.navigate('devices');
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // =========================================================================
    //  PAGE: ROOMS (Groups CRUD)
    // =========================================================================

    function renderRooms() {
        return '' +
            '<div class="flex items-center justify-between mb-16">' +
                '<div class="text-muted text-sm" id="rooms-count"></div>' +
                '<button class="btn btn-primary btn-sm" id="add-room-btn">+ New Room</button>' +
            '</div>' +
            '<div id="rooms-list">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading rooms...</span></div>' +
            '</div>';
    }

    async function wireRooms() {
        try {
            var results = await Promise.all([api.getGroups(), api.getDevices()]);
            cachedData.groups = results[0];
            cachedData.devices = results[1];
            renderRoomsList(results[0], results[1]);
        } catch (err) {
            toast(err.message, 'error');
            $('#rooms-list').innerHTML = '<div class="empty-state"><div class="empty-state-icon">\u26A0\uFE0F</div><div class="empty-state-title">Failed to load rooms</div></div>';
        }

        $('#add-room-btn').addEventListener('click', function () { showRoomModal(null); });
    }

    function renderRoomsList(groups, devices) {
        var countEl = $('#rooms-count');
        if (countEl) countEl.textContent = groups.length + ' room' + (groups.length !== 1 ? 's' : '');

        var container = $('#rooms-list');
        if (groups.length === 0) {
            container.innerHTML = '<div class="empty-state"><div class="empty-state-icon">\uD83C\uDFE2</div><div class="empty-state-title">No rooms</div><p class="text-muted">Create a room to group devices together.</p></div>';
            return;
        }

        container.innerHTML = groups.map(function (g) {
            var memberNames = (g.memberDeviceIds || []).map(function (id) {
                var d = devices.find(function (dd) { return dd.id === id; });
                return d ? d.deviceName : 'Unknown';
            });
            return '' +
                '<div class="item-card">' +
                    '<div class="item-card-body">' +
                        '<div class="item-card-title">' + esc(g.groupName) + '</div>' +
                        '<div class="item-card-sub">' + (g.memberDeviceIds || []).length + ' member' + ((g.memberDeviceIds || []).length !== 1 ? 's' : '') +
                            (memberNames.length > 0 ? ' -- ' + esc(memberNames.join(', ')) : '') +
                            (g.notes ? ' -- ' + esc(g.notes) : '') +
                        '</div>' +
                    '</div>' +
                    '<div class="item-card-actions">' +
                        '<button class="btn btn-sm btn-secondary" data-members-room="' + g.id + '">Members</button>' +
                        '<button class="btn btn-sm btn-secondary" data-edit-room="' + g.id + '">Edit</button>' +
                        '<button class="btn btn-sm btn-danger" data-delete-room="' + g.id + '">Delete</button>' +
                    '</div>' +
                '</div>';
        }).join('');

        container.addEventListener('click', async function (e) {
            var editBtn = e.target.closest('[data-edit-room]');
            if (editBtn) {
                var gid = parseInt(editBtn.dataset.editRoom);
                var grp = groups.find(function (g) { return g.id === gid; });
                if (grp) showRoomModal(grp);
                return;
            }
            var delBtn = e.target.closest('[data-delete-room]');
            if (delBtn) {
                var did = parseInt(delBtn.dataset.deleteRoom);
                var ok = await confirmDialog('Delete Room', 'Are you sure you want to delete this room?');
                if (!ok) return;
                try {
                    await api.deleteGroup(did);
                    toast('Room deleted', 'success');
                    app.navigate('rooms');
                } catch (err) { toast(err.message, 'error'); }
                return;
            }
            var memBtn = e.target.closest('[data-members-room]');
            if (memBtn) {
                var mid = parseInt(memBtn.dataset.membersRoom);
                var mgrp = groups.find(function (g) { return g.id === mid; });
                if (mgrp) showRoomMembersModal(mgrp, devices);
            }
        });
    }

    function showRoomModal(group) {
        var isEdit = !!group;
        var body = '' +
            '<div class="form-group"><label class="form-label">Room Name</label>' +
                '<input type="text" class="form-input" id="room-name" value="' + esc(group ? group.groupName : '') + '" required></div>' +
            '<div class="form-group"><label class="form-label">Notes</label>' +
                '<textarea class="form-input" id="room-notes" rows="2">' + esc(group && group.notes ? group.notes : '') + '</textarea></div>';

        var modal = showModal(isEdit ? 'Edit Room' : 'New Room', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="room-save">' + (isEdit ? 'Save Changes' : 'Create Room') + '</button>'
        );

        modal.el.querySelector('#room-save').addEventListener('click', async function () {
            var data = {
                groupName: modal.el.querySelector('#room-name').value.trim(),
                notes: modal.el.querySelector('#room-notes').value.trim()
            };
            if (!data.groupName) { toast('Room name is required', 'warning'); return; }

            var btn = modal.el.querySelector('#room-save');
            btn.disabled = true;

            try {
                if (isEdit) {
                    await api.updateGroup(group.id, data);
                    toast('Room updated', 'success');
                } else {
                    await api.addGroup(data);
                    toast('Room created', 'success');
                }
                modal.close();
                app.navigate('rooms');
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    function showRoomMembersModal(group, devices) {
        var currentIds = new Set(group.memberDeviceIds || []);
        var checkboxes = devices.map(function (d) {
            return '<label class="form-check">' +
                '<input type="checkbox" value="' + d.id + '"' + (currentIds.has(d.id) ? ' checked' : '') + '>' +
                '<span>' + esc(d.deviceName) + ' (' + esc(d.ipAddress || '--') + ')</span></label>';
        }).join('');

        var body = '<p class="text-muted mb-16">Select devices to include in this room:</p>' +
            '<div class="flex flex-col gap-8">' + checkboxes + '</div>';

        var modal = showModal('Manage Members -- ' + esc(group.groupName), body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="members-save">Save Members</button>',
            { large: false }
        );

        modal.el.querySelector('#members-save').addEventListener('click', async function () {
            var checked = modal.el.querySelectorAll('input[type="checkbox"]:checked');
            var ids = [];
            for (var i = 0; i < checked.length; i++) {
                ids.push(parseInt(checked[i].value));
            }

            var btn = modal.el.querySelector('#members-save');
            btn.disabled = true;

            try {
                await api.setGroupMembers(group.id, ids);
                toast('Members updated', 'success');
                modal.close();
                app.navigate('rooms');
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // =========================================================================
    //  PAGE: DEVICE CONTROL
    //  FIX #6: Test All Commands button in Advanced tab
    // =========================================================================

    var controlLog = [];
    var advAllCommands = []; // All commands for the selected device's series

    function renderControl() {
        return '' +
            '<div class="tabs">' +
                '<button class="tab-btn active" data-tab="quick">Quick Control</button>' +
                '<button class="tab-btn" data-tab="advanced">Advanced</button>' +
            '</div>' +

            '<div class="target-selector" id="control-target">' +
                '<label>Target:</label>' +
                '<select class="form-input" id="target-type" style="min-width:120px;width:auto">' +
                    '<option value="device">Single Device</option>' +
                    '<option value="group">Room / Group</option>' +
                '</select>' +
                '<select class="form-input" id="target-id"><option value="">Loading...</option></select>' +
            '</div>' +

            '<div class="tab-content active" id="tab-quick">' +
                '<div class="control-section">' +
                    '<div class="control-section-title">Power</div>' +
                    '<div class="control-grid" id="ctrl-power">' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Power On"><span class="btn-touch-icon">\u23FB</span>On</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Power Off"><span class="btn-touch-icon">\u23FE</span>Off</button>' +
                        '<button class="btn btn-touch btn-secondary" data-wake-target><span class="btn-touch-icon">\uD83D\uDCA4</span>Wake</button>' +
                    '</div>' +
                '</div>' +

                '<div class="control-section">' +
                    '<div class="control-section-title">Source / Input</div>' +
                    '<div class="control-grid" id="ctrl-source">' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="HDMI 1"><span class="btn-touch-icon">\uD83D\uDD0C</span>HDMI 1</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="HDMI 2"><span class="btn-touch-icon">\uD83D\uDD0C</span>HDMI 2</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="HDMI 3"><span class="btn-touch-icon">\uD83D\uDD0C</span>HDMI 3</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="DisplayPort"><span class="btn-touch-icon">\uD83D\uDD0C</span>DP</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="USB-C"><span class="btn-touch-icon">\uD83D\uDD0C</span>USB-C</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Home"><span class="btn-touch-icon">\uD83C\uDFE0</span>Home</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="OPS"><span class="btn-touch-icon">\uD83D\uDCBB</span>OPS</button>' +
                    '</div>' +
                '</div>' +

                '<div class="control-section">' +
                    '<div class="control-section-title">Volume</div>' +
                    '<div class="slider-row">' +
                        '<span class="slider-label">\uD83D\uDD0A Volume</span>' +
                        '<input type="range" min="0" max="100" step="25" value="50" id="ctrl-volume">' +
                        '<span class="slider-value" id="ctrl-volume-val">50</span>' +
                    '</div>' +
                    '<div class="control-grid mt-8">' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Mute On"><span class="btn-touch-icon">\uD83D\uDD07</span>Mute</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Mute Off"><span class="btn-touch-icon">\uD83D\uDD0A</span>Unmute</button>' +
                    '</div>' +
                '</div>' +

                '<div class="control-section">' +
                    '<div class="control-section-title">Brightness</div>' +
                    '<div class="slider-row">' +
                        '<span class="slider-label">\u2600 Brightness</span>' +
                        '<input type="range" min="0" max="100" step="25" value="50" id="ctrl-brightness">' +
                        '<span class="slider-value" id="ctrl-brightness-val">50</span>' +
                    '</div>' +
                '</div>' +

                '<div class="control-section">' +
                    '<div class="control-section-title">Picture Mode</div>' +
                    '<div class="control-grid" id="ctrl-picture">' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Picture Standard">Standard</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Picture Bright">Bright</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Picture Soft">Soft</button>' +
                        '<button class="btn btn-touch btn-secondary" data-qcmd="Picture Customer">Customer</button>' +
                    '</div>' +
                '</div>' +
            '</div>' +

            '<div class="tab-content" id="tab-advanced">' +
                '<div class="form-row mb-16">' +
                    '<div class="form-group">' +
                        '<label class="form-label">Category</label>' +
                        '<select class="form-input" id="adv-category"><option value="">-- Select category --</option></select>' +
                    '</div>' +
                    '<div class="form-group">' +
                        '<label class="form-label">Command</label>' +
                        '<select class="form-input" id="adv-command"><option value="">-- Select command --</option></select>' +
                    '</div>' +
                    '<div style="display:flex;align-items:flex-end;gap:8px">' +
                        '<button class="btn btn-primary" id="adv-send">Send</button>' +
                        '<button class="btn btn-warning" id="adv-test-all">\uD83E\uDDEA Test All</button>' +
                    '</div>' +
                '</div>' +

                '<div class="flex items-center justify-between mb-8">' +
                    '<div class="control-section-title" style="margin:0">Session Log</div>' +
                    '<div class="toolbar">' +
                        '<button class="btn btn-sm btn-ghost" id="log-copy">\uD83D\uDCCB Copy</button>' +
                        '<button class="btn btn-sm btn-ghost" id="log-export">\uD83D\uDCE5 Export</button>' +
                        '<button class="btn btn-sm btn-ghost" id="log-clear">\uD83D\uDDD1 Clear</button>' +
                    '</div>' +
                '</div>' +
                '<div class="session-log" id="session-log">' +
                    '<div class="text-muted text-sm" style="padding:8px">No commands sent yet.</div>' +
                '</div>' +
            '</div>';
    }

    async function wireControl() {
        try {
            var results = await Promise.all([api.getDevices(), api.getGroups()]);
            cachedData.devices = results[0];
            cachedData.groups = results[1];
            populateTargetSelector(results[0], results[1]);
        } catch (err) {
            toast('Failed to load devices: ' + err.message, 'error');
        }

        var body = $('#page-body');

        // Tab switching
        body.addEventListener('click', function (e) {
            var tabBtn = e.target.closest('.tab-btn[data-tab]');
            if (tabBtn) {
                $$('.tab-btn', body).forEach(function (b) { b.classList.remove('active'); });
                $$('.tab-content', body).forEach(function (c) { c.classList.remove('active'); });
                tabBtn.classList.add('active');
                var tgt = $('#tab-' + tabBtn.dataset.tab, body);
                if (tgt) tgt.classList.add('active');
            }
        });

        // Target type switching
        $('#target-type').addEventListener('change', function () {
            populateTargetSelector(cachedData.devices || [], cachedData.groups || []);
            loadAdvancedCommands();
        });

        // Target device switching -- reload commands for the new device's series
        $('#target-id').addEventListener('change', function () {
            loadAdvancedCommands();
        });

        // Quick control command buttons (name-only, no category)
        body.addEventListener('click', async function (e) {
            var cmdBtn = e.target.closest('[data-qcmd]');
            if (cmdBtn) {
                var cmdName = cmdBtn.dataset.qcmd;
                await sendQuickCommand(cmdName);
                return;
            }

            var wakeBtn = e.target.closest('[data-wake-target]');
            if (wakeBtn) {
                var targetType = $('#target-type').value;
                var targetId = parseInt($('#target-id').value);
                if (!targetId) { toast('Select a target first', 'warning'); return; }
                if (targetType === 'device') {
                    try {
                        var res = await api.wakeDevice(targetId);
                        addLogEntry('info', 'WAKE', res.detail || 'Wake sent');
                        toast(res.detail || 'Wake sent', 'success');
                    } catch (err) { toast(err.message, 'error'); addLogEntry('error', 'WAKE', err.message); }
                } else {
                    toast('Wake is only supported for individual devices', 'warning');
                }
                return;
            }
        });

        // Volume slider
        var volSlider = $('#ctrl-volume');
        var volVal = $('#ctrl-volume-val');
        if (volSlider) {
            volSlider.addEventListener('input', function () { volVal.textContent = volSlider.value; });
            volSlider.addEventListener('change', async function () {
                await sendQuickCommand('Set Volume ' + volSlider.value);
            });
        }

        // Brightness slider
        var briSlider = $('#ctrl-brightness');
        var briVal = $('#ctrl-brightness-val');
        if (briSlider) {
            briSlider.addEventListener('input', function () { briVal.textContent = briSlider.value; });
            briSlider.addEventListener('change', async function () {
                await sendQuickCommand('Set Brightness ' + briSlider.value);
            });
        }

        // Load advanced commands for selected device's series
        loadAdvancedCommands();

        // Category changes -> filter command dropdown
        var advCat = $('#adv-category');
        if (advCat) {
            advCat.addEventListener('change', function () {
                var cat = advCat.value;
                var cmdSel = $('#adv-command');
                cmdSel.innerHTML = '<option value="">-- Select command --</option>';
                if (cat) {
                    var filtered = advAllCommands.filter(function (c) {
                        return (c.commandCategory || c.category || 'Other') === cat;
                    });
                    filtered.forEach(function (c) {
                        var name = c.commandName || c.name || '';
                        cmdSel.innerHTML += '<option value="' + esc(name) + '">' + esc(name) + '</option>';
                    });
                }
            });
        }

        // Advanced send
        var advSend = $('#adv-send');
        if (advSend) {
            advSend.addEventListener('click', async function () {
                var cmd = $('#adv-command').value;
                if (!cmd) { toast('Select a command', 'warning'); return; }
                await sendControlCommand(cmd);
            });
        }

        // FIX #6: Test All Commands button
        var testAllBtn = $('#adv-test-all');
        if (testAllBtn) {
            testAllBtn.addEventListener('click', function () {
                runTestAllCommands();
            });
        }

        // Log actions
        var logCopy = $('#log-copy');
        if (logCopy) logCopy.addEventListener('click', function () {
            var text = controlLog.map(function (e) { return e.time + ' ' + e.dir + ' ' + e.msg; }).join('\n');
            navigator.clipboard.writeText(text).then(function () { toast('Copied to clipboard', 'success'); });
        });

        var logExport = $('#log-export');
        if (logExport) logExport.addEventListener('click', function () {
            var text = controlLog.map(function (e) { return e.time + ' ' + e.dir + ' ' + e.msg; }).join('\n');
            downloadFile('session-log.txt', text, 'text/plain');
        });

        var logClear = $('#log-clear');
        if (logClear) logClear.addEventListener('click', function () {
            controlLog = [];
            var log = $('#session-log');
            if (log) log.innerHTML = '<div class="text-muted text-sm" style="padding:8px">Log cleared.</div>';
        });
    }

    // Properly fetch commands for series, group by category
    async function loadAdvancedCommands() {
        var catSel = $('#adv-category');
        var cmdSel = $('#adv-command');
        if (!catSel) return;

        // Determine series from selected device
        var series = '';
        var targetType = $('#target-type') ? $('#target-type').value : 'device';
        if (targetType === 'device') {
            var devId = parseInt($('#target-id') ? $('#target-id').value : '');
            if (devId && cachedData.devices) {
                var dev = cachedData.devices.find(function (d) { return d.id === devId; });
                if (dev && dev.series) series = dev.series;
            }
        }

        try {
            var commands = await api.getCommands(series);
            advAllCommands = Array.isArray(commands) ? commands : [];

            var catSet = {};
            advAllCommands.forEach(function (c) {
                var cat = c.commandCategory || c.category || 'Other';
                catSet[cat] = true;
            });

            catSel.innerHTML = '<option value="">-- Select category --</option>';
            Object.keys(catSet).sort().forEach(function (cat) {
                catSel.innerHTML += '<option value="' + esc(cat) + '">' + esc(cat) + '</option>';
            });

            cmdSel.innerHTML = '<option value="">-- Select command --</option>';
        } catch (err) {
            catSel.innerHTML = '<option value="">-- No commands available --</option>';
            advAllCommands = [];
        }
    }

    function populateTargetSelector(devices, groups) {
        var typeVal = $('#target-type').value;
        var sel = $('#target-id');
        sel.innerHTML = '';

        if (typeVal === 'device') {
            if (devices.length === 0) {
                sel.innerHTML = '<option value="">No devices</option>';
            } else {
                devices.forEach(function (d) {
                    var opt = document.createElement('option');
                    opt.value = d.id;
                    opt.textContent = d.deviceName + ' (' + d.ipAddress + ')';
                    sel.appendChild(opt);
                });
            }
        } else {
            if (groups.length === 0) {
                sel.innerHTML = '<option value="">No groups</option>';
            } else {
                groups.forEach(function (g) {
                    var opt = document.createElement('option');
                    opt.value = g.id;
                    opt.textContent = g.groupName + ' (' + (g.memberDeviceIds || []).length + ' devices)';
                    sel.appendChild(opt);
                });
            }
        }
    }

    // Quick command: send by name only, no category
    async function sendQuickCommand(commandName) {
        var targetType = $('#target-type') ? $('#target-type').value : '';
        var targetId = parseInt($('#target-id') ? $('#target-id').value : '');

        if (!targetId) { toast('Select a target first', 'warning'); return; }

        addLogEntry('info', '\u25B6', 'Sending: ' + commandName);

        try {
            var result;
            if (targetType === 'group') {
                result = await api.sendGroupCommand(targetId, commandName);
                if (Array.isArray(result)) {
                    result.forEach(function (r) {
                        addLogEntry(r.success ? 'success' : 'error',
                            r.success ? '\u2714' : '\u2718',
                            r.deviceName + ': ' + r.response);
                    });
                    toast('Group command sent to ' + result.length + ' device(s)', 'success');
                }
            } else {
                result = await api.sendCommandByName(targetId, commandName);
                addLogEntry(result.success ? 'success' : 'error',
                    result.success ? '\u2714' : '\u2718',
                    'Response: ' + result.response + (result.hex ? ' [' + result.hex + ']' : ''));
                toast(result.response || 'Command sent', result.success ? 'success' : 'error');
            }
        } catch (err) {
            addLogEntry('error', '\u2718', 'Error: ' + err.message);
            toast(err.message, 'error');
        }
    }

    // Advanced command: send by name (POST /api/devices/{id}/command with {command: name})
    async function sendControlCommand(command) {
        var targetType = $('#target-type') ? $('#target-type').value : '';
        var targetId = parseInt($('#target-id') ? $('#target-id').value : '');

        if (!targetId) { toast('Select a target first', 'warning'); return; }

        addLogEntry('info', '\u25B6', 'Sending: ' + command);

        try {
            var result;
            if (targetType === 'group') {
                result = await api.sendGroupCommand(targetId, command);
                if (Array.isArray(result)) {
                    result.forEach(function (r) {
                        addLogEntry(r.success ? 'success' : 'error',
                            r.success ? '\u2714' : '\u2718',
                            r.deviceName + ': ' + r.response);
                    });
                    toast('Group command sent to ' + result.length + ' device(s)', 'success');
                }
            } else {
                result = await api.sendCommandByName(targetId, command);
                addLogEntry(result.success ? 'success' : 'error',
                    result.success ? '\u2714' : '\u2718',
                    'Response: ' + result.response + (result.hex ? ' [' + result.hex + ']' : ''));
                toast(result.response || 'Command sent', result.success ? 'success' : 'error');
            }
        } catch (err) {
            addLogEntry('error', '\u2718', 'Error: ' + err.message);
            toast(err.message, 'error');
        }
    }

    // FIX #6: Test All Commands
    var DANGEROUS_COMMANDS = [
        'factory reset', 'power off', 'standby', 'backlight off', 'screen off',
        'reset', 'shutdown'
    ];

    function isDangerousCommand(name) {
        var lower = (name || '').toLowerCase();
        for (var i = 0; i < DANGEROUS_COMMANDS.length; i++) {
            if (lower.indexOf(DANGEROUS_COMMANDS[i]) >= 0) return true;
        }
        return false;
    }

    function hasVariableBytes(code) {
        var c = (code || '').toUpperCase();
        return c.indexOf('XX') >= 0 || c.indexOf('YY') >= 0;
    }

    async function runTestAllCommands() {
        var targetType = $('#target-type') ? $('#target-type').value : '';
        var targetId = parseInt($('#target-id') ? $('#target-id').value : '');

        if (targetType !== 'device' || !targetId) {
            toast('Test All requires a single device target', 'warning');
            return;
        }

        // Use GET /api/commands?series=X to get all commands for the device's series
        var device = (cachedData.devices || []).find(function (d) { return d.id === targetId; });
        var series = device ? (device.series || '') : '';

        var testCommands = [];
        try {
            testCommands = await api.getCommands(series);
            if (!Array.isArray(testCommands)) testCommands = [];
        } catch (err) {
            toast('Failed to load commands: ' + err.message, 'error');
            return;
        }

        if (testCommands.length === 0) {
            toast('No commands loaded for this device series', 'warning');
            return;
        }

        var ok = await confirmDialog('Test All Commands',
            'This will send every command to the selected device with 3-second delays between each. ' +
            'Dangerous commands (Factory Reset, Power Off, Standby, Backlight Off, Screen Off) and ' +
            'commands with variable bytes (XX/YY) will be skipped. Continue?');
        if (!ok) return;

        // Build list of testable commands
        var testable = [];
        var skippedCount = 0;
        for (var i = 0; i < testCommands.length; i++) {
            var cmd = testCommands[i];
            var cmdName = cmd.commandName || cmd.name || '';
            var cmdCode = cmd.commandCode || cmd.code || '';
            if (isDangerousCommand(cmdName)) {
                skippedCount++;
                continue;
            }
            if (hasVariableBytes(cmdCode)) {
                skippedCount++;
                continue;
            }
            testable.push(cmd);
        }

        addLogEntry('info', '\uD83E\uDDEA', 'TEST ALL: Starting ' + testable.length + ' commands, skipping ' + skippedCount + ' (dangerous/variable)');

        // Show cancel button
        var testAllBtn = $('#adv-test-all');
        var origText = testAllBtn.textContent;
        testAllBtn.textContent = 'Cancel Test';
        testAllBtn.classList.remove('btn-warning');
        testAllBtn.classList.add('btn-danger');
        var aborted = false;

        var cancelHandler = function () {
            aborted = true;
            testAllBtn.textContent = origText;
            testAllBtn.classList.remove('btn-danger');
            testAllBtn.classList.add('btn-warning');
            testAllBtn.removeEventListener('click', cancelHandler);
            addLogEntry('warning', '\u26A0', 'TEST ALL: Cancelled by user');
        };
        // Replace the test-all click with cancel
        testAllBtn.replaceWith(testAllBtn.cloneNode(true));
        testAllBtn = $('#adv-test-all');
        testAllBtn.textContent = 'Cancel Test';
        testAllBtn.classList.remove('btn-warning');
        testAllBtn.classList.add('btn-danger');
        testAllBtn.addEventListener('click', cancelHandler);

        var passCount = 0;
        var failCount = 0;

        for (var j = 0; j < testable.length; j++) {
            if (aborted) break;

            var tc = testable[j];
            var tcName = tc.commandName || tc.name || '';

            addLogEntry('info', '\u25B6', 'TEST [' + (j + 1) + '/' + testable.length + '] ' + tcName);

            try {
                // Send command by name via POST /api/devices/{id}/command with {command: name}
                var result = await api.sendCommandByName(targetId, tcName);
                if (result.success) {
                    passCount++;
                    addLogEntry('success', '\u2714', 'PASS: ' + tcName + ' -> ' + (result.response || 'OK'));
                } else {
                    failCount++;
                    addLogEntry('error', '\u2718', 'FAIL: ' + tcName + ' -> ' + (result.response || 'No response'));
                }
            } catch (err) {
                failCount++;
                addLogEntry('error', '\u2718', 'FAIL: ' + tcName + ' -> ' + err.message);
            }

            // 3-second delay between commands (unless last or aborted)
            if (j < testable.length - 1 && !aborted) {
                await sleep(3000);
            }
        }

        // Restore button
        testAllBtn.textContent = origText;
        testAllBtn.classList.remove('btn-danger');
        testAllBtn.classList.add('btn-warning');
        testAllBtn.removeEventListener('click', cancelHandler);

        // Re-attach the original handler
        testAllBtn.addEventListener('click', function () { runTestAllCommands(); });

        addLogEntry('info', '\uD83C\uDFC1',
            'TEST ALL COMPLETE: Pass = ' + passCount + ', Fail = ' + failCount + ', Skipped = ' + skippedCount +
            (aborted ? ' (CANCELLED)' : ''));
        toast('Test complete: Pass=' + passCount + ' Fail=' + failCount + ' Skip=' + skippedCount, passCount > failCount ? 'success' : 'warning');
    }

    function addLogEntry(type, dir, msg) {
        var entry = { time: timeNow(), dir: dir, msg: msg, type: type };
        controlLog.push(entry);

        var log = $('#session-log');
        if (!log) return;

        var placeholder = log.querySelector('.text-muted');
        if (placeholder) placeholder.remove();

        var div = document.createElement('div');
        div.className = 'log-entry log-' + type;
        div.innerHTML = '<span class="log-time">' + entry.time + '</span><span class="log-dir">' + esc(dir) + '</span><span class="log-msg">' + esc(msg) + '</span>';
        log.appendChild(div);
        log.scrollTop = log.scrollHeight;
    }

    // =========================================================================
    //  PAGE: MACROS (full CRUD)
    //  FIX #3: Macro run -- smart auto-run based on seriesPattern
    //  FIX #4: Macro edit -- load existing steps from API
    //  FIX #5: Save actual CommandId integers
    // =========================================================================

    function renderMacros() {
        return '' +
            '<div class="flex items-center justify-between mb-16">' +
                '<div class="text-muted text-sm" id="macro-count"></div>' +
                '<button class="btn btn-primary btn-sm" id="add-macro-btn">+ New Macro</button>' +
            '</div>' +
            '<div id="macro-list">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading macros...</span></div>' +
            '</div>';
    }

    async function wireMacros() {
        try {
            var macros = await api.getMacros();
            cachedData.macros = macros;
            renderMacroList(macros);
        } catch (err) {
            toast(err.message, 'error');
            $('#macro-list').innerHTML = '<div class="empty-state"><div class="empty-state-icon">\u26A0\uFE0F</div><div class="empty-state-title">Failed to load macros</div></div>';
        }

        $('#add-macro-btn').addEventListener('click', function () { showMacroEditModal(null); });

        var unsub1 = api.on('ws:macro.step', function (data) {
            toast('Step: ' + data.result, 'info', 2000);
        });
        var unsub2 = api.on('ws:macro.completed', function () {
            toast('Macro completed', 'success');
        });
        var unsub3 = api.on('ws:macro.failed', function (data) {
            toast('Macro failed: ' + data.reason, 'error');
        });
        // Macro prompt — show a modal and respond via API
        var unsub4 = api.on('ws:macro.prompt', function (data) {
            var modal = showModal(
                'Macro Paused',
                '<div style="margin-bottom:16px">' +
                    '<p style="font-size:15px;margin-bottom:8px">' + esc(data.message || 'Continue?') + '</p>' +
                    '<p class="text-muted text-sm">Macro: ' + esc(data.macroName || '') + '</p>' +
                    '<p class="text-muted text-sm">Device: ' + esc(data.deviceName || '') + '</p>' +
                '</div>' +
                '<div style="display:flex;gap:8px;justify-content:center">' +
                    '<button class="btn btn-success" id="prompt-continue">Continue</button>' +
                    '<button class="btn btn-danger" id="prompt-cancel">Cancel</button>' +
                '</div>'
            );
            var contBtn = modal.el.querySelector('#prompt-continue');
            var cancelBtn = modal.el.querySelector('#prompt-cancel');
            if (contBtn) contBtn.onclick = function () {
                api.post('/api/macros/prompt/' + data.promptId, { "continue": true });
                modal.close();
            };
            if (cancelBtn) cancelBtn.onclick = function () {
                api.post('/api/macros/prompt/' + data.promptId, { "continue": false });
                modal.close();
            };
        });
        wsCleanups.push(unsub1, unsub2, unsub3, unsub4);
    }

    function renderMacroList(macros) {
        var countEl = $('#macro-count');
        if (countEl) countEl.textContent = macros.length + ' macro' + (macros.length !== 1 ? 's' : '');

        var container = $('#macro-list');
        if (macros.length === 0) {
            container.innerHTML = '<div class="empty-state"><div class="empty-state-icon">\u26A1</div><div class="empty-state-title">No macros defined</div><p class="text-muted">Create a macro to automate commands.</p></div>';
            return;
        }

        container.innerHTML = macros.map(function (m) {
            return '' +
                '<div class="item-card">' +
                    '<div class="item-card-body">' +
                        '<div class="item-card-title">' + esc(m.macroName) + '</div>' +
                        '<div class="item-card-sub">' + (m.stepCount || (m.steps ? m.steps.length : 0)) + ' step' + (((m.stepCount || (m.steps ? m.steps.length : 0))) !== 1 ? 's' : '') + (m.notes ? ' -- ' + esc(m.notes) : '') + '</div>' +
                    '</div>' +
                    '<div class="item-card-actions">' +
                        '<button class="btn btn-sm btn-success" data-run-macro="' + m.id + '">\u25B6 Run</button>' +
                        '<button class="btn btn-sm btn-secondary" data-edit-macro="' + m.id + '">Edit</button>' +
                        '<button class="btn btn-sm btn-secondary" data-dup-macro="' + m.id + '">Duplicate</button>' +
                        '<button class="btn btn-sm btn-danger" data-delete-macro="' + m.id + '">Delete</button>' +
                    '</div>' +
                '</div>';
        }).join('');

        container.addEventListener('click', async function (e) {
            var runBtn = e.target.closest('[data-run-macro]');
            if (runBtn) {
                var macroId = parseInt(runBtn.dataset.runMacro);
                var macro = macros.find(function (m) { return m.id === macroId; });
                showMacroRunModal(macroId, macro);
                return;
            }
            var editBtn = e.target.closest('[data-edit-macro]');
            if (editBtn) {
                var mid = parseInt(editBtn.dataset.editMacro);
                var macro2 = macros.find(function (m) { return m.id === mid; });
                if (macro2) showMacroEditModal(macro2);
                return;
            }
            var dupBtn = e.target.closest('[data-dup-macro]');
            if (dupBtn) {
                var did = parseInt(dupBtn.dataset.dupMacro);
                var orig = macros.find(function (m) { return m.id === did; });
                if (orig) {
                    try {
                        await api.addMacro({
                            macroName: orig.macroName + ' (Copy)',
                            notes: orig.notes || '',
                            steps: (orig.steps || []).map(function (s) {
                                return {
                                    stepType: s.stepType || 'command',
                                    commandId: s.commandId || 0,
                                    delayAfterMs: s.delayAfterMs || 0,
                                    promptText: s.promptText || ''
                                };
                            })
                        });
                        toast('Macro duplicated', 'success');
                        app.navigate('macros');
                    } catch (err) { toast(err.message, 'error'); }
                }
                return;
            }
            var delBtn = e.target.closest('[data-delete-macro]');
            if (delBtn) {
                var delId = parseInt(delBtn.dataset.deleteMacro);
                var ok = await confirmDialog('Delete Macro', 'Are you sure you want to delete this macro?');
                if (!ok) return;
                try {
                    await api.deleteMacro(delId);
                    toast('Macro deleted', 'success');
                    app.navigate('macros');
                } catch (err) { toast(err.message, 'error'); }
            }
        });
    }

    // FIX #4: Macro edit modal -- loads existing steps, shows full details
    async function showMacroEditModal(macro) {
        var isEdit = !!macro;
        // Deep-clone existing steps and add display labels from API data
        var steps = [];
        if (macro && Array.isArray(macro.steps)) {
            steps = macro.steps.map(function (s) {
                return {
                    stepType: s.stepType || 'command',
                    commandId: parseInt(s.commandId) || 0,
                    delayAfterMs: parseInt(s.delayAfterMs) || 0,
                    promptText: s.promptText || '',
                    // Display labels from the API response
                    _commandLabel: s.commandName || '',
                    _seriesPattern: s.seriesPattern || '',
                    _categoryLabel: '',
                    _deviceLabel: ''
                };
            });
        }

        // Preload devices for step builder device dropdown
        var devices = cachedData.devices || [];
        try {
            if (devices.length === 0) devices = await api.getDevices();
            cachedData.devices = devices;
        } catch (err) { /* ok */ }

        // Cache: deviceId -> { series, commands[] }
        var deviceCommandsCache = {};

        // Helper: get series from device model
        function getDeviceSeries(device) {
            return device ? (device.series || '') : '';
        }

        // Helper: load commands for a device (by its series)
        async function loadCommandsForDevice(deviceId) {
            if (deviceCommandsCache[deviceId]) return deviceCommandsCache[deviceId];
            var device = devices.find(function (d) { return d.id === deviceId; });
            var series = getDeviceSeries(device);
            if (!series) {
                deviceCommandsCache[deviceId] = { series: '', commands: [] };
                return deviceCommandsCache[deviceId];
            }
            try {
                var commands = await api.getCommands(series);
                deviceCommandsCache[deviceId] = { series: series, commands: Array.isArray(commands) ? commands : [] };
            } catch (err) {
                deviceCommandsCache[deviceId] = { series: series, commands: [] };
            }
            return deviceCommandsCache[deviceId];
        }

        function renderSteps() {
            if (steps.length === 0) return '<p class="text-muted text-sm">No steps added yet.</p>';
            return steps.map(function (s, idx) {
                var desc;
                if (s.stepType === 'prompt') {
                    desc = '<strong>' + (idx + 1) + '.</strong> [Prompt] ' + esc(s.promptText || 'Continue?');
                } else {
                    var cmdLabel = s._commandLabel || ('CommandId: ' + (s.commandId || 0));
                    var seriesLabel = s._seriesPattern || '';
                    desc = '<strong>' + (idx + 1) + '.</strong> ' + esc(cmdLabel) +
                        (seriesLabel ? ' <span class="text-muted">(' + esc(seriesLabel) + ')</span>' : '') +
                        ' <span class="text-muted">delay: ' + (s.delayAfterMs || 0) + 'ms</span>';
                }
                return '<div class="step-item" data-step-idx="' + idx + '" style="display:flex;align-items:center;gap:8px;padding:6px 8px;border:1px solid var(--border);border-radius:var(--radius-sm);margin-bottom:4px">' +
                    '<span class="flex-1 text-sm">' + desc + '</span>' +
                    (idx > 0 ? '<button class="btn btn-sm btn-ghost" data-move-step-up="' + idx + '" title="Move up">\u25B2</button>' : '') +
                    (idx < steps.length - 1 ? '<button class="btn btn-sm btn-ghost" data-move-step-down="' + idx + '" title="Move down">\u25BC</button>' : '') +
                    '<button class="btn btn-sm btn-ghost btn-danger" data-remove-step="' + idx + '">Remove</button>' +
                '</div>';
            }).join('');
        }

        // Device options for the step builder
        var deviceOptions = devices.map(function (d) {
            return '<option value="' + d.id + '">' + esc(d.deviceName) + ' (' + esc(d.series || d.modelNumber || '') + ')</option>';
        }).join('');

        var body = '' +
            '<div class="form-group"><label class="form-label">Macro Name</label>' +
                '<input type="text" class="form-input" id="macro-name" value="' + esc(macro ? macro.macroName : '') + '" required></div>' +
            '<div class="form-group"><label class="form-label">Notes</label>' +
                '<textarea class="form-input" id="macro-notes" rows="2">' + esc(macro && macro.notes ? macro.notes : '') + '</textarea></div>' +
            '<div class="form-group">' +
                '<label class="form-label">Steps</label>' +
                '<div id="macro-steps-list">' + renderSteps() + '</div>' +
                '<div style="border:1px solid var(--border);border-radius:var(--radius-sm);padding:12px;margin-top:12px" id="step-builder">' +
                    '<div class="text-sm fw-600 mb-8">Add Command Step</div>' +
                    '<div class="form-row">' +
                        '<div class="form-group"><label class="form-label text-sm">Device</label>' +
                            '<select class="form-input" id="step-device"><option value="">-- Select Device --</option>' + deviceOptions + '</select></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Category</label>' +
                            '<select class="form-input" id="step-category"><option value="">-- Category --</option></select></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Command</label>' +
                            '<select class="form-input" id="step-command"><option value="">-- Command --</option></select></div>' +
                    '</div>' +
                    '<div class="form-row">' +
                        '<div class="form-group"><label class="form-label text-sm">Delay After (ms)</label>' +
                            '<input type="number" class="form-input" id="step-delay" value="0" min="0"></div>' +
                        '<div style="display:flex;align-items:flex-end;gap:8px">' +
                            '<button class="btn btn-sm btn-primary" id="step-add-cmd">Add Command</button>' +
                            '<button class="btn btn-sm btn-secondary" id="step-add-prompt">Add Prompt</button>' +
                        '</div>' +
                    '</div>' +
                '</div>' +
            '</div>';

        var modal = showModal(isEdit ? 'Edit Macro' : 'Create Macro', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="macro-save">' + (isEdit ? 'Save Changes' : 'Create Macro') + '</button>',
            { large: true }
        );

        function refreshStepsList() {
            var el = modal.el.querySelector('#macro-steps-list');
            if (el) el.innerHTML = renderSteps();
        }

        // Remove step + reorder via event delegation
        modal.el.addEventListener('click', function (e) {
            var rmBtn = e.target.closest('[data-remove-step]');
            if (rmBtn) {
                steps.splice(parseInt(rmBtn.dataset.removeStep), 1);
                refreshStepsList();
                return;
            }
            var upBtn = e.target.closest('[data-move-step-up]');
            if (upBtn) {
                var idx = parseInt(upBtn.dataset.moveStepUp);
                if (idx > 0) {
                    var tmp = steps[idx];
                    steps[idx] = steps[idx - 1];
                    steps[idx - 1] = tmp;
                    refreshStepsList();
                }
                return;
            }
            var downBtn = e.target.closest('[data-move-step-down]');
            if (downBtn) {
                var idx2 = parseInt(downBtn.dataset.moveStepDown);
                if (idx2 < steps.length - 1) {
                    var tmp2 = steps[idx2];
                    steps[idx2] = steps[idx2 + 1];
                    steps[idx2 + 1] = tmp2;
                    refreshStepsList();
                }
            }
        });

        // Device change -> auto-detect series, load categories
        var stepDeviceSel = modal.el.querySelector('#step-device');
        var stepCatSel = modal.el.querySelector('#step-category');
        var stepCmdSel = modal.el.querySelector('#step-command');

        stepDeviceSel.addEventListener('change', async function () {
            var deviceId = parseInt(stepDeviceSel.value);
            stepCatSel.innerHTML = '<option value="">-- Category --</option>';
            stepCmdSel.innerHTML = '<option value="">-- Command --</option>';
            if (!deviceId) return;

            var data = await loadCommandsForDevice(deviceId);
            var commands = data.commands;
            var cats = {};
            commands.forEach(function (c) {
                var cat = c.commandCategory || c.category || 'Other';
                cats[cat] = true;
            });
            Object.keys(cats).sort().forEach(function (cat) {
                stepCatSel.innerHTML += '<option value="' + esc(cat) + '">' + esc(cat) + '</option>';
            });
        });

        // Category change -> filter commands
        stepCatSel.addEventListener('change', function () {
            var deviceId = parseInt(stepDeviceSel.value);
            var cat = stepCatSel.value;
            stepCmdSel.innerHTML = '<option value="">-- Command --</option>';
            if (!deviceId || !cat) return;

            var data = deviceCommandsCache[deviceId];
            if (!data) return;
            var commands = data.commands;
            commands.filter(function (c) {
                return (c.commandCategory || c.category || 'Other') === cat;
            }).forEach(function (c) {
                var name = c.commandName || c.name || '';
                var id = c.id || 0;
                // FIX #5: Store the command's database ID as the option value
                stepCmdSel.innerHTML += '<option value="' + id + '" data-cmd-name="' + esc(name) + '" data-series="' + esc(data.series || '') + '">' + esc(name) + '</option>';
            });
        });

        // FIX #5: Add command step - store actual commandId integer
        modal.el.querySelector('#step-add-cmd').addEventListener('click', function () {
            var deviceId = parseInt(stepDeviceSel.value);
            var cat = stepCatSel.value;
            var cmdOption = stepCmdSel.options[stepCmdSel.selectedIndex];
            var commandId = parseInt(stepCmdSel.value);
            var commandName = cmdOption ? (cmdOption.dataset.cmdName || cmdOption.textContent) : '';
            var seriesPattern = cmdOption ? (cmdOption.dataset.series || '') : '';
            var delay = parseInt(modal.el.querySelector('#step-delay').value) || 0;

            if (!deviceId || !cat || !commandId) {
                toast('Select device, category, and command', 'warning');
                return;
            }

            steps.push({
                stepType: 'command',
                commandId: commandId,
                delayAfterMs: delay,
                promptText: '',
                // Display-only labels (not sent to API, prefixed with _)
                _commandLabel: commandName,
                _seriesPattern: seriesPattern,
                _categoryLabel: cat,
                _deviceLabel: ''
            });
            refreshStepsList();
        });

        // Add prompt step
        modal.el.querySelector('#step-add-prompt').addEventListener('click', function () {
            steps.push({
                stepType: 'prompt',
                commandId: 0,
                delayAfterMs: 0,
                promptText: 'Continue?',
                _commandLabel: '',
                _seriesPattern: '',
                _categoryLabel: '',
                _deviceLabel: ''
            });
            refreshStepsList();
        });

        // Save macro - send clean steps without display labels
        modal.el.querySelector('#macro-save').addEventListener('click', async function () {
            var cleanSteps = steps.map(function (s) {
                return {
                    stepType: s.stepType || 'command',
                    commandId: parseInt(s.commandId) || 0,
                    delayAfterMs: parseInt(s.delayAfterMs) || 0,
                    promptText: s.promptText || ''
                };
            });

            var data = {
                macroName: modal.el.querySelector('#macro-name').value.trim(),
                notes: modal.el.querySelector('#macro-notes').value.trim(),
                steps: cleanSteps
            };
            if (!data.macroName) { toast('Macro name is required', 'warning'); return; }

            var btn = modal.el.querySelector('#macro-save');
            btn.disabled = true;

            try {
                if (isEdit) {
                    await api.updateMacro(macro.id, data);
                    toast('Macro updated', 'success');
                } else {
                    await api.addMacro(data);
                    toast('Macro created', 'success');
                }
                modal.close();
                app.navigate('macros');
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // FIX #3: Smart macro run -- check seriesPattern, auto-run if single matching device
    async function showMacroRunModal(macroId, macro) {
        var devices = cachedData.devices || [];
        try {
            if (devices.length === 0) devices = await api.getDevices();
            cachedData.devices = devices;
        } catch (err) { /* ok */ }

        // Determine the seriesPattern from the first command step
        var seriesPattern = '';
        if (macro && Array.isArray(macro.steps)) {
            for (var i = 0; i < macro.steps.length; i++) {
                var step = macro.steps[i];
                if (step.stepType === 'command' && step.seriesPattern) {
                    seriesPattern = step.seriesPattern;
                    break;
                }
            }
        }

        // Find matching connected devices
        var matchingDevices = [];
        if (seriesPattern) {
            matchingDevices = devices.filter(function (d) {
                return d.series === seriesPattern && d.isConnected;
            });
        }

        // If exactly one matching connected device, run immediately
        if (matchingDevices.length === 1) {
            var autoDevice = matchingDevices[0];
            toast('Running macro on ' + autoDevice.deviceName + '...', 'info');
            try {
                await api.runMacro(macroId, autoDevice.id, null);
                toast('Macro started on ' + autoDevice.deviceName, 'success');
            } catch (err) {
                toast(err.message, 'error');
            }
            return;
        }

        // Otherwise show a dropdown of matching devices (or all if no series match)
        var displayDevices = seriesPattern
            ? devices.filter(function (d) { return d.series === seriesPattern; })
            : devices;

        // If no series-filtered devices, fall back to all devices
        if (displayDevices.length === 0) {
            displayDevices = devices;
        }

        var deviceOptions = displayDevices.map(function (d) {
            var connLabel = d.isConnected ? '' : ' [offline]';
            return '<option value="' + d.id + '"' + (!d.isConnected ? ' disabled' : '') + '>' +
                esc(d.deviceName) + ' (' + esc(d.ipAddress || '') + ')' + connLabel + '</option>';
        }).join('');

        var infoText = seriesPattern
            ? '<p class="text-muted text-sm mb-8">Showing devices matching series: <strong>' + esc(seriesPattern) + '</strong></p>'
            : '';

        var body = '' +
            infoText +
            '<div class="form-group">' +
                '<label class="form-label">Run on device:</label>' +
                '<select class="form-input" id="macro-run-device">' +
                    (deviceOptions || '<option value="">No devices available</option>') +
                '</select>' +
            '</div>';

        var modal = showModal('Run Macro', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-success" id="macro-run-confirm">\u25B6 Run</button>'
        );

        modal.el.querySelector('#macro-run-confirm').addEventListener('click', async function () {
            var deviceId = parseInt(modal.el.querySelector('#macro-run-device').value);
            if (!deviceId) { toast('Select a device', 'warning'); return; }

            var btn = modal.el.querySelector('#macro-run-confirm');
            btn.disabled = true;
            btn.innerHTML = '<span class="spinner"></span> Running...';

            try {
                await api.runMacro(macroId, deviceId, null);
                toast('Macro started', 'success');
                modal.close();
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
                btn.textContent = '\u25B6 Run';
            }
        });
    }

    // =========================================================================
    //  PAGE: SCHEDULER (full CRUD)
    // =========================================================================

    function renderScheduler() {
        return '' +
            '<div class="flex items-center justify-between mb-16">' +
                '<div class="text-muted text-sm" id="sched-count"></div>' +
                '<button class="btn btn-primary btn-sm" id="add-schedule-btn">+ New Schedule</button>' +
            '</div>' +
            '<div id="sched-list">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading schedules...</span></div>' +
            '</div>';
    }

    async function wireScheduler() {
        try {
            var schedules = await api.getSchedules();
            cachedData.schedules = schedules;
            renderScheduleList(schedules);
        } catch (err) {
            $('#sched-list').innerHTML = '<div class="empty-state"><div class="empty-state-icon">\uD83D\uDD52</div><div class="empty-state-title">No schedules</div><p class="text-muted">Create a schedule to automate commands at specific times.</p></div>';
            var countEl = $('#sched-count');
            if (countEl) countEl.textContent = '0 schedules';
        }

        $('#add-schedule-btn').addEventListener('click', function () { showScheduleModal(null); });

        var unsub1 = api.on('ws:scheduler.fired', function (data) {
            toast('Rule fired: ' + data.ruleName, 'info');
        });
        var unsub2 = api.on('ws:scheduler.failed', function (data) {
            toast('Scheduler error: ' + data.detail, 'error');
        });
        wsCleanups.push(unsub1, unsub2);
    }

    function renderScheduleList(schedules) {
        var countEl = $('#sched-count');
        if (countEl) countEl.textContent = schedules.length + ' schedule' + (schedules.length !== 1 ? 's' : '');

        var container = $('#sched-list');
        if (schedules.length === 0) {
            container.innerHTML = '<div class="empty-state"><div class="empty-state-icon">\uD83D\uDD52</div><div class="empty-state-title">No schedules</div><p class="text-muted">Create a schedule to automate commands at specific times.</p></div>';
            return;
        }

        container.innerHTML = schedules.map(function (s) {
            var enabledBadge = s.enabled
                ? '<span class="badge badge-success">Enabled</span>'
                : '<span class="badge badge-error">Disabled</span>';
            return '' +
                '<div class="item-card">' +
                    '<div class="item-card-body">' +
                        '<div class="item-card-title">' + esc(s.name || s.ruleName || 'Schedule #' + s.id) + ' ' + enabledBadge + '</div>' +
                        '<div class="item-card-sub">' +
                            (s.time || '--') + ' -- ' + (s.recurrence || s.frequency || 'Once') +
                            ' -- ' + (s.command || '') +
                            (s.notes ? ' -- ' + esc(s.notes) : '') +
                        '</div>' +
                    '</div>' +
                    '<div class="item-card-actions">' +
                        '<button class="btn btn-sm btn-success" data-run-schedule="' + s.id + '">\u25B6 Run Now</button>' +
                        '<button class="btn btn-sm btn-secondary" data-edit-schedule="' + s.id + '">Edit</button>' +
                        '<button class="btn btn-sm btn-danger" data-delete-schedule="' + s.id + '">Delete</button>' +
                    '</div>' +
                '</div>';
        }).join('');

        container.addEventListener('click', async function (e) {
            var runBtn = e.target.closest('[data-run-schedule]');
            if (runBtn) {
                var rid = parseInt(runBtn.dataset.runSchedule);
                runBtn.disabled = true;
                try {
                    await api.runScheduleNow(rid);
                    toast('Schedule executed', 'success');
                } catch (err) { toast(err.message, 'error'); }
                runBtn.disabled = false;
                return;
            }
            var editBtn = e.target.closest('[data-edit-schedule]');
            if (editBtn) {
                var eid = parseInt(editBtn.dataset.editSchedule);
                var sched = schedules.find(function (s) { return s.id === eid; });
                if (sched) showScheduleModal(sched);
                return;
            }
            var delBtn = e.target.closest('[data-delete-schedule]');
            if (delBtn) {
                var did = parseInt(delBtn.dataset.deleteSchedule);
                var ok = await confirmDialog('Delete Schedule', 'Are you sure you want to delete this schedule?');
                if (!ok) return;
                try {
                    await api.deleteSchedule(did);
                    toast('Schedule deleted', 'success');
                    app.navigate('scheduler');
                } catch (err) { toast(err.message, 'error'); }
            }
        });
    }

    // Schedule modal with command dropdowns and expanded recurrence
    async function showScheduleModal(schedule) {
        var isEdit = !!schedule;
        var devices = cachedData.devices || [];
        var groups = cachedData.groups || [];
        try {
            if (devices.length === 0) devices = await api.getDevices();
            if (groups.length === 0) groups = await api.getGroups();
            cachedData.devices = devices;
            cachedData.groups = groups;
        } catch (err) { /* use empty */ }

        // Load series list
        var seriesList = [];
        try { seriesList = await api.getCommandSeries(); } catch (err) { /* ok */ }
        if (!Array.isArray(seriesList)) seriesList = [];
        seriesList = seriesList.map(function (s) { return typeof s === 'string' ? s : (s.name || s.series || ''); }).filter(Boolean);

        // Determine current target type
        var currentTargetType = 'device';
        if (schedule && schedule.groupId) currentTargetType = 'room';

        var deviceOptions = devices.map(function (d) {
            var sel = (schedule && schedule.deviceId === d.id) ? ' selected' : '';
            return '<option value="' + d.id + '"' + sel + '>' + esc(d.deviceName) + '</option>';
        }).join('');

        var roomOptions = groups.map(function (g) {
            var sel = (schedule && schedule.groupId === g.id) ? ' selected' : '';
            return '<option value="' + g.id + '"' + sel + '>' + esc(g.groupName) + '</option>';
        }).join('');

        var seriesOptions = seriesList.map(function (s) {
            var sel = (schedule && schedule.series === s) ? ' selected' : '';
            return '<option value="' + esc(s) + '"' + sel + '>' + esc(s) + '</option>';
        }).join('');

        // Recurrence options
        var recurrenceOptions = [
            'Once', 'Daily', 'Weekdays', 'Weekends',
            'Every 1 minute', 'Every 5 minutes', 'Every 10 minutes', 'Every 15 minutes', 'Every 30 minutes',
            'Every hour', 'Every 2 hours', 'Every 4 hours', 'Every 6 hours', 'Every 12 hours'
        ];
        var recurrenceSelect = recurrenceOptions.map(function (r) {
            var sel = (schedule && (schedule.recurrence === r || schedule.frequency === r)) ? ' selected' : '';
            return '<option value="' + esc(r) + '"' + sel + '>' + esc(r) + '</option>';
        }).join('');

        var body = '' +
            '<div class="form-group"><label class="form-label">Name</label>' +
                '<input type="text" class="form-input" id="sched-name" value="' + esc(schedule ? (schedule.name || schedule.ruleName || '') : '') + '" required></div>' +

            '<div class="form-group">' +
                '<label class="form-label">Target</label>' +
                '<div class="flex gap-16 mb-8">' +
                    '<label class="form-check"><input type="radio" name="sched-target-type" value="device"' + (currentTargetType === 'device' ? ' checked' : '') + '><span>Device</span></label>' +
                    '<label class="form-check"><input type="radio" name="sched-target-type" value="room"' + (currentTargetType === 'room' ? ' checked' : '') + '><span>Room</span></label>' +
                '</div>' +
                '<select class="form-input" id="sched-target">' +
                    (currentTargetType === 'device' ? deviceOptions : roomOptions) +
                '</select>' +
            '</div>' +

            '<div class="form-group"><label class="form-label">Series</label>' +
                '<select class="form-input" id="sched-series"><option value="">-- Auto-detect or select --</option>' + seriesOptions + '</select></div>' +

            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">Category</label>' +
                    '<select class="form-input" id="sched-category"><option value="">-- Select category --</option></select></div>' +
                '<div class="form-group"><label class="form-label">Command</label>' +
                    '<select class="form-input" id="sched-command"><option value="">-- Select command --</option></select></div>' +
            '</div>' +

            '<div class="form-row">' +
                '<div class="form-group"><label class="form-label">Time (HH:MM)</label>' +
                    '<input type="time" class="form-input" id="sched-time" value="' + (schedule && schedule.time ? schedule.time : '08:00') + '"></div>' +
                '<div class="form-group"><label class="form-label">Recurrence</label>' +
                    '<select class="form-input" id="sched-recurrence">' + recurrenceSelect + '</select></div>' +
            '</div>' +

            '<div class="form-group"><label class="form-check">' +
                '<input type="checkbox" id="sched-enabled"' + (schedule ? (schedule.enabled ? ' checked' : '') : ' checked') + '>' +
                '<span>Enabled</span></label></div>' +
            '<div class="form-group"><label class="form-label">Notes</label>' +
                '<textarea class="form-input" id="sched-notes" rows="2">' + esc(schedule && schedule.notes ? schedule.notes : '') + '</textarea></div>';

        var modal = showModal(isEdit ? 'Edit Schedule' : 'New Schedule', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="sched-save">' + (isEdit ? 'Save Changes' : 'Create Schedule') + '</button>'
        );

        // Command cascading state
        var schedCommandsCache = {};

        // Target type radio toggle
        var radios = modal.el.querySelectorAll('input[name="sched-target-type"]');
        for (var ri = 0; ri < radios.length; ri++) {
            radios[ri].addEventListener('change', function (ev) {
                var targetSel = modal.el.querySelector('#sched-target');
                if (ev.target.value === 'device') {
                    targetSel.innerHTML = deviceOptions;
                } else {
                    targetSel.innerHTML = roomOptions;
                }
                autoDetectSeries();
            });
        }

        // Auto-detect series from device
        function autoDetectSeries() {
            var targetTypeRadio = modal.el.querySelector('input[name="sched-target-type"]:checked');
            var seriesSel = modal.el.querySelector('#sched-series');
            if (targetTypeRadio && targetTypeRadio.value === 'device') {
                var devIdVal = parseInt(modal.el.querySelector('#sched-target').value);
                if (devIdVal && devices.length > 0) {
                    var dev = devices.find(function (d) { return d.id === devIdVal; });
                    if (dev && dev.series) {
                        seriesSel.value = dev.series;
                        loadScheduleCategories(seriesSel.value);
                        return;
                    }
                }
            }
        }

        modal.el.querySelector('#sched-target').addEventListener('change', function () {
            autoDetectSeries();
        });

        // Series change -> load categories
        async function loadScheduleCategories(series) {
            var catSel = modal.el.querySelector('#sched-category');
            var cmdSel = modal.el.querySelector('#sched-command');
            catSel.innerHTML = '<option value="">-- Select category --</option>';
            cmdSel.innerHTML = '<option value="">-- Select command --</option>';
            if (!series) return;

            try {
                if (!schedCommandsCache[series]) {
                    schedCommandsCache[series] = await api.getCommands(series);
                }
                var commands = schedCommandsCache[series] || [];
                var cats = {};
                commands.forEach(function (c) {
                    var cat = c.commandCategory || c.category || 'Other';
                    cats[cat] = true;
                });
                Object.keys(cats).sort().forEach(function (cat) {
                    catSel.innerHTML += '<option value="' + esc(cat) + '">' + esc(cat) + '</option>';
                });
            } catch (err) { /* ok */ }
        }

        modal.el.querySelector('#sched-series').addEventListener('change', function () {
            loadScheduleCategories(modal.el.querySelector('#sched-series').value);
        });

        // Category change -> filter commands
        modal.el.querySelector('#sched-category').addEventListener('change', function () {
            var series = modal.el.querySelector('#sched-series').value;
            var cat = modal.el.querySelector('#sched-category').value;
            var cmdSel = modal.el.querySelector('#sched-command');
            cmdSel.innerHTML = '<option value="">-- Select command --</option>';
            if (!series || !cat) return;

            var commands = schedCommandsCache[series] || [];
            commands.filter(function (c) {
                return (c.commandCategory || c.category || 'Other') === cat;
            }).forEach(function (c) {
                var name = c.commandName || c.name || '';
                cmdSel.innerHTML += '<option value="' + esc(name) + '">' + esc(name) + '</option>';
            });
        });

        // If editing, try to set the command dropdown to the existing value
        if (schedule && schedule.command) {
            setTimeout(function () {
                autoDetectSeries();
                if (schedule.series) {
                    modal.el.querySelector('#sched-series').value = schedule.series;
                    loadScheduleCategories(schedule.series).then(function () {
                        if (schedule.category) {
                            modal.el.querySelector('#sched-category').value = schedule.category;
                            var changeEvent = new Event('change');
                            modal.el.querySelector('#sched-category').dispatchEvent(changeEvent);
                            setTimeout(function () {
                                modal.el.querySelector('#sched-command').value = schedule.command;
                            }, 100);
                        }
                    });
                }
            }, 100);
        } else {
            autoDetectSeries();
        }

        // Save
        modal.el.querySelector('#sched-save').addEventListener('click', async function () {
            var targetTypeRadio = modal.el.querySelector('input[name="sched-target-type"]:checked');
            var targetType = targetTypeRadio ? targetTypeRadio.value : 'device';
            var targetId = parseInt(modal.el.querySelector('#sched-target').value);
            var command = modal.el.querySelector('#sched-command').value;
            var category = modal.el.querySelector('#sched-category').value;
            var series = modal.el.querySelector('#sched-series').value;

            var data = {
                name: modal.el.querySelector('#sched-name').value.trim(),
                command: command,
                category: category,
                series: series,
                time: modal.el.querySelector('#sched-time').value,
                recurrence: modal.el.querySelector('#sched-recurrence').value,
                enabled: modal.el.querySelector('#sched-enabled').checked,
                notes: modal.el.querySelector('#sched-notes').value.trim(),
                deviceId: targetType === 'device' ? targetId : null,
                groupId: targetType === 'room' ? targetId : null
            };

            if (!data.name) { toast('Schedule name is required', 'warning'); return; }
            if (!data.command) { toast('Command is required', 'warning'); return; }

            var btn = modal.el.querySelector('#sched-save');
            btn.disabled = true;

            try {
                if (isEdit) {
                    await api.updateSchedule(schedule.id, data);
                    toast('Schedule updated', 'success');
                } else {
                    await api.addSchedule(data);
                    toast('Schedule created', 'success');
                }
                modal.close();
                app.navigate('scheduler');
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // =========================================================================
    //  PAGE: DATABASE MANAGEMENT (4 tabs)
    //  FIX #1: Event delegation for category headers
    //  FIX #2: OUI tab fetches from /api/oui
    // =========================================================================

    function renderDatabase() {
        return '' +
            '<div class="tabs">' +
                '<button class="tab-btn active" data-dbtab="commands">Commands</button>' +
                '<button class="tab-btn" data-dbtab="models">Models</button>' +
                '<button class="tab-btn" data-dbtab="oui">OUI Table</button>' +
                '<button class="tab-btn" data-dbtab="groups">Groups</button>' +
            '</div>' +

            // Commands tab
            '<div class="tab-content active" id="dbtab-commands">' +
                '<div style="display:grid;grid-template-columns:1fr 350px;gap:16px;min-height:400px">' +
                    '<div>' +
                        '<div class="flex items-center justify-between mb-8">' +
                            '<select class="form-input" id="dbcmd-series-filter" style="width:auto;min-width:180px">' +
                                '<option value="">All Series</option>' +
                            '</select>' +
                        '</div>' +
                        '<div id="dbcmd-table" style="max-height:500px;overflow-y:auto"><div class="text-muted">Select a series or view all commands.</div></div>' +
                    '</div>' +
                    '<div style="border:1px solid var(--border);border-radius:var(--radius-sm);padding:16px">' +
                        '<div class="fw-600 mb-12">Command Details</div>' +
                        '<p class="text-muted text-sm mb-12" style="padding:6px;background:var(--bg);border-radius:4px">Read-only in web portal. Use the desktop app to edit commands.</p>' +
                        '<div class="form-group"><label class="form-label text-sm">Series</label>' +
                            '<input type="text" class="form-input" id="dbcmd-series" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Category</label>' +
                            '<input type="text" class="form-input" id="dbcmd-category" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Name</label>' +
                            '<input type="text" class="form-input" id="dbcmd-name" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Code</label>' +
                            '<input type="text" class="form-input" id="dbcmd-code" readonly style="font-family:monospace"></div>' +
                        '<div class="form-row">' +
                            '<div class="form-group"><label class="form-label text-sm">Port</label>' +
                                '<input type="text" class="form-input" id="dbcmd-port" readonly></div>' +
                            '<div class="form-group"><label class="form-label text-sm">Format</label>' +
                                '<input type="text" class="form-input" id="dbcmd-format" readonly></div>' +
                        '</div>' +
                        '<div class="form-group"><label class="form-label text-sm">Notes</label>' +
                            '<textarea class="form-input" id="dbcmd-notes" rows="2" readonly></textarea></div>' +
                    '</div>' +
                '</div>' +
            '</div>' +

            // Models tab
            '<div class="tab-content" id="dbtab-models">' +
                '<div style="display:grid;grid-template-columns:1fr 350px;gap:16px;min-height:400px">' +
                    '<div id="dbmodel-table"><div class="loading-center"><div class="spinner"></div></div></div>' +
                    '<div style="border:1px solid var(--border);border-radius:var(--radius-sm);padding:16px">' +
                        '<div class="fw-600 mb-12">Model Details</div>' +
                        '<p class="text-muted text-sm mb-12" style="padding:6px;background:var(--bg);border-radius:4px">Read-only in web portal. Use the desktop app to edit models.</p>' +
                        '<div class="form-group"><label class="form-label text-sm">Model Number</label>' +
                            '<input type="text" class="form-input" id="dbmodel-number" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Series Pattern</label>' +
                            '<input type="text" class="form-input" id="dbmodel-series" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Baud Rate</label>' +
                            '<input type="text" class="form-input" id="dbmodel-baud" readonly></div>' +
                    '</div>' +
                '</div>' +
            '</div>' +

            // OUI tab
            '<div class="tab-content" id="dbtab-oui">' +
                '<div style="display:grid;grid-template-columns:1fr 350px;gap:16px;min-height:400px">' +
                    '<div id="dboui-table"><div class="loading-center"><div class="spinner"></div></div></div>' +
                    '<div style="border:1px solid var(--border);border-radius:var(--radius-sm);padding:16px">' +
                        '<div class="fw-600 mb-12">OUI Details</div>' +
                        '<p class="text-muted text-sm mb-12" style="padding:6px;background:var(--bg);border-radius:4px">Read-only in web portal. Use the desktop app to edit OUI entries.</p>' +
                        '<div class="form-group"><label class="form-label text-sm">OUI Prefix</label>' +
                            '<input type="text" class="form-input" id="dboui-prefix" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Series Label</label>' +
                            '<input type="text" class="form-input" id="dboui-label" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Series Pattern</label>' +
                            '<input type="text" class="form-input" id="dboui-pattern" readonly></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Notes</label>' +
                            '<textarea class="form-input" id="dboui-notes" rows="2" readonly></textarea></div>' +
                    '</div>' +
                '</div>' +
            '</div>' +

            // Groups tab (CRUD)
            '<div class="tab-content" id="dbtab-groups">' +
                '<div style="display:grid;grid-template-columns:1fr 350px;gap:16px;min-height:400px">' +
                    '<div id="dbgroup-table"><div class="loading-center"><div class="spinner"></div></div></div>' +
                    '<div style="border:1px solid var(--border);border-radius:var(--radius-sm);padding:16px">' +
                        '<div class="fw-600 mb-12">Group Details</div>' +
                        '<div class="form-group"><label class="form-label text-sm">Group Name</label>' +
                            '<input type="text" class="form-input" id="dbgroup-name"></div>' +
                        '<div class="form-group"><label class="form-label text-sm">Notes</label>' +
                            '<textarea class="form-input" id="dbgroup-notes" rows="2"></textarea></div>' +
                        '<div class="flex gap-8 mt-12">' +
                            '<button class="btn btn-sm btn-primary" id="dbgroup-save">Save</button>' +
                            '<button class="btn btn-sm btn-secondary" id="dbgroup-new">New</button>' +
                            '<button class="btn btn-sm btn-danger" id="dbgroup-delete">Delete</button>' +
                        '</div>' +
                    '</div>' +
                '</div>' +
            '</div>';
    }

    async function wireDatabase() {
        var body = $('#page-body');

        // Tab switching
        body.addEventListener('click', function (e) {
            var tabBtn = e.target.closest('.tab-btn[data-dbtab]');
            if (tabBtn) {
                $$('.tab-btn[data-dbtab]', body).forEach(function (b) { b.classList.remove('active'); });
                $$('[id^="dbtab-"]', body).forEach(function (c) { c.classList.remove('active'); });
                tabBtn.classList.add('active');
                var target = $('#dbtab-' + tabBtn.dataset.dbtab, body);
                if (target) target.classList.add('active');

                if (tabBtn.dataset.dbtab === 'models') loadDbModels();
                if (tabBtn.dataset.dbtab === 'oui') loadDbOui();
                if (tabBtn.dataset.dbtab === 'groups') loadDbGroups();
            }
        });

        // Load commands tab initially
        loadDbCommandsTab();

        // Groups CRUD
        var selectedGroupId = null;

        var dbGroupSave = $('#dbgroup-save');
        var dbGroupNew = $('#dbgroup-new');
        var dbGroupDelete = $('#dbgroup-delete');

        if (dbGroupNew) dbGroupNew.addEventListener('click', function () {
            selectedGroupId = null;
            $('#dbgroup-name').value = '';
            $('#dbgroup-notes').value = '';
            // Deselect rows
            $$('#dbgroup-table tr.selected').forEach(function (r) { r.classList.remove('selected'); });
        });

        if (dbGroupSave) dbGroupSave.addEventListener('click', async function () {
            var name = $('#dbgroup-name').value.trim();
            if (!name) { toast('Group name is required', 'warning'); return; }
            var data = { groupName: name, notes: $('#dbgroup-notes').value.trim() };
            try {
                if (selectedGroupId) {
                    await api.updateGroup(selectedGroupId, data);
                    toast('Group updated', 'success');
                } else {
                    await api.addGroup(data);
                    toast('Group created', 'success');
                }
                selectedGroupId = null;
                loadDbGroups();
            } catch (err) { toast(err.message, 'error'); }
        });

        if (dbGroupDelete) dbGroupDelete.addEventListener('click', async function () {
            if (!selectedGroupId) { toast('Select a group first', 'warning'); return; }
            var ok = await confirmDialog('Delete Group', 'Are you sure you want to delete this group?');
            if (!ok) return;
            try {
                await api.deleteGroup(selectedGroupId);
                toast('Group deleted', 'success');
                selectedGroupId = null;
                $('#dbgroup-name').value = '';
                $('#dbgroup-notes').value = '';
                loadDbGroups();
            } catch (err) { toast(err.message, 'error'); }
        });

        // Expose selectedGroupId setter for table click handler
        body._setSelectedGroupId = function (id) { selectedGroupId = id; };
        body._getSelectedGroupId = function () { return selectedGroupId; };
    }

    // -- Database: Commands Tab -----------------------------------------------
    // FIX #1: Uses event delegation on #dbcmd-table for category header toggling

    async function loadDbCommandsTab() {
        var filterEl = $('#dbcmd-series-filter');
        if (!filterEl) return;

        try {
            var seriesList = await api.getCommandSeries();
            filterEl.innerHTML = '<option value="">All Series</option>';
            if (Array.isArray(seriesList)) {
                seriesList.forEach(function (s) {
                    var name = typeof s === 'string' ? s : (s.name || s.series || '');
                    filterEl.innerHTML += '<option value="' + esc(name) + '">' + esc(name) + '</option>';
                });
            }
        } catch (err) { /* ok */ }

        filterEl.addEventListener('change', function () {
            loadDbCommandsTable(filterEl.value);
        });

        loadDbCommandsTable('');
    }

    async function loadDbCommandsTable(series) {
        var el = $('#dbcmd-table');
        if (!el) return;
        el.innerHTML = '<div class="loading-center"><div class="spinner"></div></div>';

        try {
            var commands = await api.getCommands(series);
            if (!Array.isArray(commands) || commands.length === 0) {
                el.innerHTML = '<p class="text-muted">No commands found.</p>';
                return;
            }

            // Group commands by category with collapsible sections
            var grouped = {};
            commands.forEach(function (c) {
                var cat = c.commandCategory || c.category || 'Other';
                if (!grouped[cat]) grouped[cat] = [];
                grouped[cat].push(c);
            });

            var html = '';
            var sortedCats = Object.keys(grouped).sort();
            sortedCats.forEach(function (cat) {
                var cmds = grouped[cat];
                html += '' +
                    '<div class="db-category-section" style="margin-bottom:4px">' +
                        '<div class="db-category-header" data-toggle-dbcat style="cursor:pointer;padding:8px 12px;background:var(--bg);border:1px solid var(--border);border-radius:var(--radius-sm);display:flex;justify-content:space-between;align-items:center">' +
                            '<span class="fw-600 text-sm">' + esc(cat) + ' <span class="text-muted">(' + cmds.length + ')</span></span>' +
                            '<span class="room-chevron" style="font-size:10px">\u25BC</span>' +
                        '</div>' +
                        '<div class="db-category-body" style="display:block;border:1px solid var(--border);border-top:none">' +
                            '<table style="width:100%;font-size:13px">' +
                                '<tbody>' + cmds.map(function (c) {
                                    return '<tr class="db-cmd-row" data-cmd-id="' + (c.id || 0) + '" style="cursor:pointer">' +
                                        '<td style="padding:4px 8px">' + esc(c.commandName || c.name || '') + '</td>' +
                                        '<td style="padding:4px 8px"><code style="font-size:11px">' + esc(c.commandCode || c.code || '') + '</code></td>' +
                                    '</tr>';
                                }).join('') +
                            '</tbody></table>' +
                        '</div>' +
                    '</div>';
            });

            el.innerHTML = html;

            // FIX #1: Use onclick assignment (not addEventListener) to avoid stacking
            // duplicate handlers when the series filter changes and re-renders the table.
            el.onclick = function (e) {
                // Category header toggle
                var header = e.target.closest('.db-category-header');
                if (header) {
                    var bodyEl = header.nextElementSibling;
                    if (bodyEl) {
                        var isVisible = bodyEl.style.display !== 'none';
                        bodyEl.style.display = isVisible ? 'none' : 'block';
                    }
                    return;
                }

                // Command row click -> populate detail form
                var row = e.target.closest('.db-cmd-row');
                if (row) {
                    var cmdId = parseInt(row.dataset.cmdId);
                    var cmd = commands.find(function (c) { return c.id === cmdId; });
                    if (cmd) {
                        // Clear previous selection styles
                        $$('.db-cmd-row', el).forEach(function (r) {
                            r.classList.remove('selected');
                            r.style.background = '';
                        });
                        row.classList.add('selected');
                        row.style.background = 'var(--primary-alpha, rgba(74,234,220,0.1))';

                        $('#dbcmd-series').value = cmd.seriesPattern || cmd.series || '';
                        $('#dbcmd-category').value = cmd.commandCategory || cmd.category || '';
                        $('#dbcmd-name').value = cmd.commandName || cmd.name || '';
                        $('#dbcmd-code').value = cmd.commandCode || cmd.code || '';
                        $('#dbcmd-port').value = cmd.port || '';
                        $('#dbcmd-format').value = cmd.commandFormat || cmd.format || '';
                        $('#dbcmd-notes').value = cmd.notes || '';
                    }
                }
            };
        } catch (err) {
            el.innerHTML = '<p class="text-muted">' + esc(err.message) + '</p>';
        }
    }

    // -- Database: Models Tab -------------------------------------------------

    async function loadDbModels() {
        var el = $('#dbmodel-table');
        if (!el) return;
        el.innerHTML = '<div class="loading-center"><div class="spinner"></div></div>';

        try {
            var models = await api.getModels();
            if (!Array.isArray(models) || models.length === 0) {
                el.innerHTML = '<p class="text-muted">No models found.</p>';
                return;
            }

            el.innerHTML = '' +
                '<div class="table-wrap" style="max-height:450px;overflow-y:auto"><table style="font-size:13px">' +
                    '<thead><tr><th>Model Number</th><th>Series</th><th>Baud Rate</th></tr></thead>' +
                    '<tbody>' + models.map(function (m) {
                        var modelNum = typeof m === 'string' ? m : (m.modelNumber || m.name || '');
                        var seriesVal = typeof m === 'object' ? (m.seriesPattern || m.series || '') : '';
                        var baud = typeof m === 'object' ? (m.baudRate || '') : '';
                        return '<tr class="db-model-row" style="cursor:pointer" data-model="' + esc(modelNum) + '">' +
                            '<td style="padding:6px 8px" class="fw-600">' + esc(modelNum) + '</td>' +
                            '<td style="padding:6px 8px">' + esc(seriesVal) + '</td>' +
                            '<td style="padding:6px 8px">' + esc(String(baud)) + '</td>' +
                        '</tr>';
                    }).join('') +
                    '</tbody></table></div>';

            el.addEventListener('click', function (e) {
                var row = e.target.closest('.db-model-row');
                if (row) {
                    var modelNum = row.dataset.model;
                    var model = models.find(function (m) {
                        return (typeof m === 'string' ? m : (m.modelNumber || m.name || '')) === modelNum;
                    });
                    if (model) {
                        $$('.db-model-row', el).forEach(function (r) {
                            r.classList.remove('selected');
                            r.style.background = '';
                        });
                        row.classList.add('selected');
                        row.style.background = 'var(--primary-alpha, rgba(74,234,220,0.1))';

                        if (typeof model === 'string') {
                            $('#dbmodel-number').value = model;
                            $('#dbmodel-series').value = '';
                            $('#dbmodel-baud').value = '';
                        } else {
                            $('#dbmodel-number').value = model.modelNumber || model.name || '';
                            $('#dbmodel-series').value = model.seriesPattern || model.series || '';
                            $('#dbmodel-baud').value = model.baudRate || '';
                        }
                    }
                }
            });
        } catch (err) {
            el.innerHTML = '<p class="text-muted">' + esc(err.message) + '</p>';
        }
    }

    // -- Database: OUI Tab ----------------------------------------------------
    // FIX #2: Fetches from /api/oui endpoint, displays table with details panel

    async function loadDbOui() {
        var el = $('#dboui-table');
        if (!el) return;
        el.innerHTML = '<div class="loading-center"><div class="spinner"></div></div>';

        try {
            var ouiData = await api.getOuiTable();
            if (!Array.isArray(ouiData) || ouiData.length === 0) {
                el.innerHTML = '<p class="text-muted">No OUI entries found.</p>';
                return;
            }

            el.innerHTML = '' +
                '<div class="table-wrap" style="max-height:450px;overflow-y:auto"><table style="font-size:13px">' +
                    '<thead><tr><th>OUI Prefix</th><th>Series Label</th><th>Series Pattern</th><th>Notes</th></tr></thead>' +
                    '<tbody>' + ouiData.map(function (o) {
                        return '<tr class="db-oui-row" style="cursor:pointer" data-oui="' + esc(o.ouiPrefix || o.prefix || '') + '">' +
                            '<td style="padding:6px 8px" class="fw-600"><code>' + esc(o.ouiPrefix || o.prefix || '') + '</code></td>' +
                            '<td style="padding:6px 8px">' + esc(o.seriesLabel || o.label || '') + '</td>' +
                            '<td style="padding:6px 8px">' + esc(o.seriesPattern || o.pattern || '') + '</td>' +
                            '<td style="padding:6px 8px" class="text-muted">' + esc(o.notes || '') + '</td>' +
                        '</tr>';
                    }).join('') +
                    '</tbody></table></div>';

            el.addEventListener('click', function (e) {
                var row = e.target.closest('.db-oui-row');
                if (row) {
                    var prefix = row.dataset.oui;
                    var oui = ouiData.find(function (o) { return (o.ouiPrefix || o.prefix || '') === prefix; });
                    if (oui) {
                        $$('.db-oui-row', el).forEach(function (r) {
                            r.classList.remove('selected');
                            r.style.background = '';
                        });
                        row.classList.add('selected');
                        row.style.background = 'var(--primary-alpha, rgba(74,234,220,0.1))';

                        $('#dboui-prefix').value = oui.ouiPrefix || oui.prefix || '';
                        $('#dboui-label').value = oui.seriesLabel || oui.label || '';
                        $('#dboui-pattern').value = oui.seriesPattern || oui.pattern || '';
                        $('#dboui-notes').value = oui.notes || '';
                    }
                }
            });
        } catch (err) {
            el.innerHTML = '<div style="padding:24px;text-align:center"><p class="text-muted">OUI data not available: ' + esc(err.message) + '</p></div>';
        }
    }

    // -- Database: Groups Tab (full CRUD) -------------------------------------

    async function loadDbGroups() {
        var el = $('#dbgroup-table');
        if (!el) return;
        el.innerHTML = '<div class="loading-center"><div class="spinner"></div></div>';

        try {
            var groups = await api.getGroups();
            cachedData.groups = groups;

            if (!Array.isArray(groups) || groups.length === 0) {
                el.innerHTML = '<p class="text-muted">No groups. Create one using the form.</p>';
                return;
            }

            el.innerHTML = '' +
                '<div class="table-wrap" style="max-height:450px;overflow-y:auto"><table style="font-size:13px">' +
                    '<thead><tr><th>Group Name</th><th>Notes</th><th>Members</th></tr></thead>' +
                    '<tbody>' + groups.map(function (g) {
                        return '<tr class="db-group-row" style="cursor:pointer" data-group-id="' + g.id + '">' +
                            '<td style="padding:6px 8px" class="fw-600">' + esc(g.groupName) + '</td>' +
                            '<td style="padding:6px 8px" class="text-muted">' + esc(g.notes || '') + '</td>' +
                            '<td style="padding:6px 8px">' + (g.memberDeviceIds || []).length + '</td>' +
                        '</tr>';
                    }).join('') +
                    '</tbody></table></div>';

            el.addEventListener('click', function (e) {
                var row = e.target.closest('.db-group-row');
                if (row) {
                    var gid = parseInt(row.dataset.groupId);
                    var grp = groups.find(function (g) { return g.id === gid; });
                    if (grp) {
                        $$('.db-group-row', el).forEach(function (r) {
                            r.classList.remove('selected');
                            r.style.background = '';
                        });
                        row.classList.add('selected');
                        row.style.background = 'var(--primary-alpha, rgba(74,234,220,0.1))';

                        $('#dbgroup-name').value = grp.groupName || '';
                        $('#dbgroup-notes').value = grp.notes || '';

                        // Store selected group ID
                        var body = $('#page-body');
                        if (body && body._setSelectedGroupId) body._setSelectedGroupId(gid);
                    }
                }
            });
        } catch (err) {
            el.innerHTML = '<p class="text-muted">' + esc(err.message) + '</p>';
        }
    }

    // =========================================================================
    //  PAGE: AUDIT LOG
    // =========================================================================

    var auditFilter = { status: '', search: '' };

    function renderAudit() {
        return '' +
            '<div class="filter-bar">' +
                '<input type="text" class="form-input" id="audit-search" placeholder="Search commands...">' +
                '<select class="form-input" id="audit-status-filter" style="width:auto;min-width:120px">' +
                    '<option value="">All Status</option>' +
                    '<option value="success">Success</option>' +
                    '<option value="fail">Failed</option>' +
                '</select>' +
                '<div class="toolbar" style="margin-left:auto">' +
                    '<button class="btn btn-sm btn-ghost" id="audit-export-csv">\uD83D\uDCE5 CSV</button>' +
                    '<button class="btn btn-sm btn-ghost" id="audit-export-json">\uD83D\uDCE5 JSON</button>' +
                    '<button class="btn btn-sm btn-ghost" id="audit-clear">\uD83D\uDDD1 Clear</button>' +
                '</div>' +
            '</div>' +
            '<div id="audit-table">' +
                '<div class="loading-center"><div class="spinner spinner-lg"></div><span>Loading audit log...</span></div>' +
            '</div>';
    }

    async function wireAudit() {
        renderAuditTable();

        var searchEl = $('#audit-search');
        if (searchEl) searchEl.addEventListener('input', function (e) {
            auditFilter.search = e.target.value.toLowerCase();
            renderAuditTable();
        });

        var statusEl = $('#audit-status-filter');
        if (statusEl) statusEl.addEventListener('change', function (e) {
            auditFilter.status = e.target.value;
            renderAuditTable();
        });

        var csvBtn = $('#audit-export-csv');
        if (csvBtn) csvBtn.addEventListener('click', function () {
            var rows = [['Time', 'Direction', 'Message', 'Type']];
            getFilteredAuditEntries().forEach(function (e) { rows.push([e.time, e.dir, e.msg, e.type]); });
            var csv = rows.map(function (r) { return r.map(function (c) { return '"' + (c || '').replace(/"/g, '""') + '"'; }).join(','); }).join('\n');
            downloadFile('audit-log.csv', csv, 'text/csv');
            toast('CSV exported', 'success');
        });

        var jsonBtn = $('#audit-export-json');
        if (jsonBtn) jsonBtn.addEventListener('click', function () {
            downloadFile('audit-log.json', JSON.stringify(getFilteredAuditEntries(), null, 2), 'application/json');
            toast('JSON exported', 'success');
        });

        var clearBtn = $('#audit-clear');
        if (clearBtn) clearBtn.addEventListener('click', async function () {
            var ok = await confirmDialog('Clear Audit Log', 'Clear all session log entries?');
            if (ok) {
                controlLog = [];
                renderAuditTable();
                toast('Audit log cleared', 'info');
            }
        });

        var unsub = api.on('ws:message', function () {
            if (currentPage === 'audit') renderAuditTable();
        });
        wsCleanups.push(unsub);
    }

    function getFilteredAuditEntries() {
        var entries = controlLog.slice();
        if (auditFilter.status === 'success') entries = entries.filter(function (e) { return e.type === 'success'; });
        else if (auditFilter.status === 'fail') entries = entries.filter(function (e) { return e.type === 'error'; });
        if (auditFilter.search) entries = entries.filter(function (e) {
            return e.msg.toLowerCase().indexOf(auditFilter.search) >= 0 || e.dir.toLowerCase().indexOf(auditFilter.search) >= 0;
        });
        return entries;
    }

    function renderAuditTable() {
        var el = $('#audit-table');
        if (!el) return;

        var entries = getFilteredAuditEntries();
        if (entries.length === 0) {
            el.innerHTML = '<div class="empty-state"><div class="empty-state-icon">\uD83D\uDCCB</div><div class="empty-state-title">No audit entries</div><p class="text-muted">Commands sent from the Device Control page will appear here.</p></div>';
            return;
        }

        var reversed = entries.slice().reverse();
        el.innerHTML = '' +
            '<div class="table-wrap"><table>' +
                '<thead><tr><th>Time</th><th>Status</th><th>Details</th></tr></thead>' +
                '<tbody>' + reversed.map(function (e) {
                    var isTest = e.msg.toLowerCase().indexOf('test') >= 0;
                    var isMacro = e.msg.toLowerCase().indexOf('macro') >= 0;
                    var rowClass = isTest ? 'audit-test' : isMacro ? 'audit-macro' : '';
                    var statusIcon = e.type === 'success' ? '\u2714' : e.type === 'error' ? '\u2718' : '\u2139';
                    var statusColor = e.type === 'success' ? 'var(--success)' : e.type === 'error' ? 'var(--error)' : 'var(--info)';
                    return '<tr class="' + rowClass + '">' +
                        '<td class="nowrap text-muted">' + esc(e.time) + '</td>' +
                        '<td><span style="color:' + statusColor + ';font-size:16px">' + statusIcon + '</span></td>' +
                        '<td>' + esc(e.msg) + '</td></tr>';
                }).join('') +
                '</tbody></table></div>';
    }

    // =========================================================================
    //  PAGE: SETTINGS (System, Users, Keys, Theme)
    // =========================================================================

    function renderSettings() {
        return '' +
            '<div class="tabs">' +
                '<button class="tab-btn active" data-stab="system">System</button>' +
                '<button class="tab-btn" data-stab="users">Users</button>' +
                '<button class="tab-btn" data-stab="keys">API Keys</button>' +
                '<button class="tab-btn" data-stab="theme">Theme</button>' +
            '</div>' +

            // System tab
            '<div class="tab-content active" id="stab-system">' +
                '<div class="card mb-16">' +
                    '<div class="card-title mb-16">API Server Status</div>' +
                    '<div id="system-status"><div class="loading-center"><div class="spinner"></div></div></div>' +
                '</div>' +
                '<div class="card mb-16">' +
                    '<div class="card-title mb-16">Database Stats</div>' +
                    '<div id="system-db-stats"><div class="text-muted">Statistics loaded from server status.</div></div>' +
                '</div>' +
                '<div class="card">' +
                    '<div class="card-title mb-16">Maintenance</div>' +
                    '<div class="flex gap-8 flex-wrap">' +
                        '<button class="btn btn-sm btn-secondary" id="sys-export-backup">\uD83D\uDCE5 Export Backup</button>' +
                        '<button class="btn btn-sm btn-secondary" id="sys-clear-audit">\uD83D\uDDD1 Clear Audit Log</button>' +
                    '</div>' +
                '</div>' +
            '</div>' +

            // Users tab
            '<div class="tab-content" id="stab-users">' +
                '<div class="flex items-center justify-between mb-16">' +
                    '<div class="card-title">User Management</div>' +
                    '<button class="btn btn-primary btn-sm" id="add-user-btn">+ New User</button>' +
                '</div>' +
                '<div id="users-table"><div class="loading-center"><div class="spinner"></div></div></div>' +
            '</div>' +

            // API Keys tab
            '<div class="tab-content" id="stab-keys">' +
                '<div class="flex items-center justify-between mb-16">' +
                    '<div class="card-title">API Key Management</div>' +
                    '<button class="btn btn-primary btn-sm" id="add-key-btn">+ New Key</button>' +
                '</div>' +
                '<div id="keys-table"><div class="loading-center"><div class="spinner"></div></div></div>' +
            '</div>' +

            // Theme tab
            '<div class="tab-content" id="stab-theme">' +
                '<div class="card mb-16" style="max-width:500px">' +
                    '<div class="card-title mb-16">Appearance</div>' +
                    '<div class="flex items-center justify-between mb-16">' +
                        '<span>Theme</span>' +
                        '<select class="form-input" id="theme-select" style="width:auto;min-width:180px">' +
                            '<option value="avocor"' + (getCurrentTheme() === 'avocor' ? ' selected' : '') + '>Avocor Colors (Gradients)</option>' +
                            '<option value="dark"' + (getCurrentTheme() === 'dark' ? ' selected' : '') + '>Dark Mode (Simple)</option>' +
                        '</select>' +
                    '</div>' +
                '</div>' +
                '<div class="card" style="max-width:500px">' +
                    '<div class="card-title mb-16">Logo</div>' +
                    '<div class="flex items-center gap-16 mb-16">' +
                        '<img src="/img/logo.png" alt="Current logo" id="logo-preview" style="width:64px;height:64px;object-fit:contain;background:var(--bg);border-radius:var(--radius-sm);padding:8px" onerror="this.style.display=\'none\'">' +
                        '<div>' +
                            '<input type="file" accept="image/*" id="logo-upload" style="display:none">' +
                            '<button class="btn btn-sm btn-secondary" id="logo-upload-btn">Upload Logo</button>' +
                            '<p class="text-muted text-sm mt-8">Accepts PNG, JPG, SVG</p>' +
                        '</div>' +
                    '</div>' +
                '</div>' +
            '</div>';
    }

    async function wireSettings() {
        var body = $('#page-body');

        // Tab switching
        body.addEventListener('click', function (e) {
            var tabBtn = e.target.closest('.tab-btn[data-stab]');
            if (tabBtn) {
                $$('.tab-btn[data-stab]', body).forEach(function (b) { b.classList.remove('active'); });
                $$('[id^="stab-"]', body).forEach(function (c) { c.classList.remove('active'); });
                tabBtn.classList.add('active');
                var target = $('#stab-' + tabBtn.dataset.stab, body);
                if (target) target.classList.add('active');

                if (tabBtn.dataset.stab === 'users') loadUsers();
                if (tabBtn.dataset.stab === 'keys') loadKeys();
            }
        });

        // Load system status
        loadSystemStatus();

        // User management
        var addUserBtn = $('#add-user-btn');
        if (addUserBtn) addUserBtn.addEventListener('click', showCreateUserModal);

        // Key management
        var addKeyBtn = $('#add-key-btn');
        if (addKeyBtn) addKeyBtn.addEventListener('click', showCreateKeyModal);

        // Theme switching
        var themeSelect = $('#theme-select');
        if (themeSelect) {
            themeSelect.addEventListener('change', function () {
                applyTheme(themeSelect.value);
                toast('Theme updated', 'success');
            });
        }

        // Logo upload
        var logoUploadBtn = $('#logo-upload-btn');
        var logoUploadInput = $('#logo-upload');
        if (logoUploadBtn && logoUploadInput) {
            logoUploadBtn.addEventListener('click', function () { logoUploadInput.click(); });
            logoUploadInput.addEventListener('change', async function () {
                var file = logoUploadInput.files[0];
                if (!file) return;
                try {
                    await api.uploadLogo(file);
                    toast('Logo uploaded', 'success');
                    var preview = $('#logo-preview');
                    if (preview) {
                        preview.style.display = '';
                        preview.src = '/img/logo.png?t=' + Date.now();
                    }
                } catch (err) {
                    toast(err.message, 'error');
                }
            });
        }

        // Maintenance buttons
        var exportBtn = $('#sys-export-backup');
        if (exportBtn) exportBtn.addEventListener('click', function () {
            toast('Export backup not yet implemented on server', 'info');
        });

        var clearAuditBtn = $('#sys-clear-audit');
        if (clearAuditBtn) clearAuditBtn.addEventListener('click', async function () {
            var ok = await confirmDialog('Clear Audit Log', 'Are you sure you want to clear the session audit log?');
            if (ok) {
                controlLog = [];
                toast('Audit log cleared', 'info');
            }
        });
    }

    async function loadSystemStatus() {
        var el = $('#system-status');
        if (!el) return;
        try {
            var health = await api.getHealth();
            var upHours = Math.floor(health.uptime / 3600);
            var upMins = Math.floor((health.uptime % 3600) / 60);
            var upSecs = Math.floor(health.uptime % 60);
            el.innerHTML = '' +
                '<div style="display:grid;grid-template-columns:auto 1fr;gap:8px 20px;align-items:center">' +
                    '<span class="text-muted">Status:</span><span><span class="badge badge-success">' + esc(health.status) + '</span></span>' +
                    '<span class="text-muted">Version:</span><span class="fw-600">' + esc(health.version) + '</span>' +
                    '<span class="text-muted">Uptime:</span><span>' + upHours + 'h ' + upMins + 'm ' + upSecs + 's</span>' +
                    '<span class="text-muted">WebSocket Clients:</span><span>' + health.wsClients + '</span>' +
                    '<span class="text-muted">Port:</span><span>' + (location.port || (location.protocol === 'https:' ? '443' : '80')) + '</span>' +
                '</div>';
        } catch (err) {
            el.innerHTML = '<span class="badge badge-error">Unreachable</span><p class="text-muted mt-8">' + esc(err.message) + '</p>';
        }
    }

    // -- Users Tab -------------------------------------------------------------

    async function loadUsers() {
        var el = $('#users-table');
        if (!el) return;
        try {
            var users = await api.getUsers();
            if (users.length === 0) {
                el.innerHTML = '<p class="text-muted">No users found.</p>';
                return;
            }
            el.innerHTML = '' +
                '<div class="table-wrap"><table>' +
                    '<thead><tr><th>Username</th><th>Role</th><th>Created</th><th>Last Login</th><th></th></tr></thead>' +
                    '<tbody>' + users.map(function (u) {
                        return '<tr>' +
                            '<td class="fw-600">' + esc(u.username) + '</td>' +
                            '<td><span class="badge ' + (u.role === 'Admin' ? 'badge-warning' : u.role === 'Operator' ? 'badge-info' : 'badge-success') + '">' + esc(u.role) + '</span></td>' +
                            '<td class="text-muted">' + formatTime(u.createdAt) + '</td>' +
                            '<td class="text-muted">' + formatTime(u.lastLogin) + '</td>' +
                            '<td><button class="btn btn-sm btn-ghost btn-danger" data-delete-user="' + u.id + '" title="Delete">\uD83D\uDDD1</button></td>' +
                        '</tr>';
                    }).join('') +
                    '</tbody></table></div>';

            el.onclick = null;
            el.addEventListener('click', async function handler(e) {
                var delBtn = e.target.closest('[data-delete-user]');
                if (delBtn) {
                    var ok = await confirmDialog('Delete User', 'Are you sure you want to delete this user?');
                    if (!ok) return;
                    try {
                        await api.deleteUser(parseInt(delBtn.dataset.deleteUser));
                        toast('User deleted', 'success');
                        loadUsers();
                    } catch (err) { toast(err.message, 'error'); }
                }
            });
        } catch (err) {
            el.innerHTML = '<p class="text-muted">' + esc(err.message) + '</p>';
        }
    }

    function showCreateUserModal() {
        var body = '' +
            '<div class="form-group"><label class="form-label">Username</label>' +
                '<input type="text" class="form-input" id="new-username" required></div>' +
            '<div class="form-group"><label class="form-label">Password</label>' +
                '<input type="password" class="form-input" id="new-password" required></div>' +
            '<div class="form-group"><label class="form-label">Role</label>' +
                '<select class="form-input" id="new-role">' +
                    '<option value="Viewer">Viewer</option>' +
                    '<option value="Operator" selected>Operator</option>' +
                    '<option value="Admin">Admin</option>' +
                '</select></div>';

        var modal = showModal('Create User', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="create-user-confirm">Create</button>'
        );

        modal.el.querySelector('#create-user-confirm').addEventListener('click', async function () {
            var username = modal.el.querySelector('#new-username').value.trim();
            var password = modal.el.querySelector('#new-password').value;
            var role = modal.el.querySelector('#new-role').value;

            if (!username || !password) { toast('Username and password are required', 'warning'); return; }

            var btn = modal.el.querySelector('#create-user-confirm');
            btn.disabled = true;

            try {
                await api.createUser(username, password, role);
                toast('User created', 'success');
                modal.close();
                loadUsers();
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // -- API Keys Tab ---------------------------------------------------------

    async function loadKeys() {
        var el = $('#keys-table');
        if (!el) return;
        try {
            var keys = await api.getKeys();
            if (keys.length === 0) {
                el.innerHTML = '<p class="text-muted">No API keys. Create one to enable API access.</p>';
                return;
            }
            el.innerHTML = '' +
                '<div class="table-wrap"><table>' +
                    '<thead><tr><th>Label</th><th>Key Prefix</th><th>Created</th><th>Last Used</th><th></th></tr></thead>' +
                    '<tbody>' + keys.map(function (k) {
                        return '<tr>' +
                            '<td class="fw-600">' + esc(k.label) + '</td>' +
                            '<td><code>' + esc(k.keyPrefix) + '...</code></td>' +
                            '<td class="text-muted">' + formatTime(k.createdAt) + '</td>' +
                            '<td class="text-muted">' + formatTime(k.lastUsedAt) + '</td>' +
                            '<td><button class="btn btn-sm btn-ghost btn-danger" data-delete-key="' + k.id + '" title="Delete">\uD83D\uDDD1</button></td>' +
                        '</tr>';
                    }).join('') +
                    '</tbody></table></div>';

            el.onclick = null;
            el.addEventListener('click', async function (e) {
                var delBtn = e.target.closest('[data-delete-key]');
                if (delBtn) {
                    var ok = await confirmDialog('Delete API Key', 'Are you sure? Applications using this key will lose access.');
                    if (!ok) return;
                    try {
                        await api.deleteKey(parseInt(delBtn.dataset.deleteKey));
                        toast('API key deleted', 'success');
                        loadKeys();
                    } catch (err) { toast(err.message, 'error'); }
                }
            });
        } catch (err) {
            el.innerHTML = '<p class="text-muted">' + esc(err.message) + '</p>';
        }
    }

    function showCreateKeyModal() {
        var body = '' +
            '<div class="form-group"><label class="form-label">Label</label>' +
                '<input type="text" class="form-input" id="new-key-label" placeholder="e.g. Home Assistant Integration" required></div>';

        var modal = showModal('Create API Key', body,
            '<button class="btn btn-secondary" data-close>Cancel</button>' +
            '<button class="btn btn-primary" id="create-key-confirm">Create</button>'
        );

        modal.el.querySelector('#create-key-confirm').addEventListener('click', async function () {
            var label = modal.el.querySelector('#new-key-label').value.trim();
            if (!label) { toast('Label is required', 'warning'); return; }

            var btn = modal.el.querySelector('#create-key-confirm');
            btn.disabled = true;

            try {
                var result = await api.createKey(label);
                modal.close();
                var keyModal = showModal('API Key Created',
                    '<p class="text-muted mb-8">Copy this key now. It will not be shown again.</p>' +
                    '<div style="background:var(--bg);padding:12px;border-radius:var(--radius-sm);font-family:monospace;word-break:break-all;font-size:13px;user-select:all">' + esc(result.key) + '</div>',
                    '<button class="btn btn-primary" id="copy-key-btn">\uD83D\uDCCB Copy Key</button>' +
                    '<button class="btn btn-secondary" data-close>Done</button>'
                );
                var copyBtn = keyModal.el.querySelector('#copy-key-btn');
                if (copyBtn) copyBtn.addEventListener('click', function () {
                    navigator.clipboard.writeText(result.key).then(function () { toast('Key copied', 'success'); });
                });
                loadKeys();
            } catch (err) {
                toast(err.message, 'error');
                btn.disabled = false;
            }
        });
    }

    // =========================================================================
    //  DOWNLOAD HELPER
    // =========================================================================

    function downloadFile(filename, content, mimeType) {
        var blob = new Blob([content], { type: mimeType });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 100);
    }

    // =========================================================================
    //  INIT
    // =========================================================================

    function init() {
        wireGlobalNav();

        if (api.isAuthenticated) {
            api.connectWebSocket();
        }

        var hash = location.hash.replace('#', '') || (api.isAuthenticated ? 'dashboard' : 'login');
        app.navigate(hash);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

})();
