/* =============================================================================
   Avocor Commander -- API Client + WebSocket Manager
   ============================================================================= */

class CommanderApi {
    constructor(baseUrl = '') {
        this.baseUrl = baseUrl;
        this.token = localStorage.getItem('commander_token') || sessionStorage.getItem('commander_token');
        this.ws = null;
        this.wsReconnectTimer = null;
        this.wsListeners = {};
        this._wsReconnectDelay = 2000;
    }

    // -- Auth helpers ---------------------------------------------------------

    get isAuthenticated() {
        return !!this.token;
    }

    _headers(isFormData) {
        const h = {};
        if (!isFormData) h['Content-Type'] = 'application/json';
        if (this.token) h['Authorization'] = `Bearer ${this.token}`;
        return h;
    }

    async _fetch(method, path, body, isFormData) {
        const opts = { method, headers: this._headers(isFormData) };
        if (body !== undefined) {
            opts.body = isFormData ? body : JSON.stringify(body);
        }

        let res;
        try {
            res = await fetch(`${this.baseUrl}${path}`, opts);
        } catch (err) {
            throw new ApiError('Network error', `Cannot reach server: ${err.message}`, 0);
        }

        if (res.status === 401) {
            this.logout();
            if (window.__app) window.__app.navigate('login');
            throw new ApiError('Unauthorized', 'Session expired. Please log in again.', 401);
        }

        let data;
        try {
            data = await res.json();
        } catch {
            data = null;
        }

        if (!res.ok) {
            const msg = data?.message || data?.error || res.statusText;
            throw new ApiError(data?.error || 'Error', msg, res.status);
        }

        return data;
    }

    // -- Generic HTTP helpers -------------------------------------------------

    async get(url) { return this._fetch('GET', url); }
    async post(url, data) { return this._fetch('POST', url, data); }
    async put(url, data) { return this._fetch('PUT', url, data); }
    async del(url) { return this._fetch('DELETE', url); }
    async postForm(url, formData) { return this._fetch('POST', url, formData, true); }

    // -- Auth -----------------------------------------------------------------

    async login(username, password, remember = false) {
        const data = await this._fetch('POST', '/api/auth/login', { username, password });
        this.token = data.token;
        if (remember) {
            localStorage.setItem('commander_token', data.token);
        } else {
            sessionStorage.setItem('commander_token', data.token);
        }
        localStorage.setItem('commander_user', data.username);
        localStorage.setItem('commander_role', data.role);
        return data;
    }

    logout() {
        this.token = null;
        localStorage.removeItem('commander_token');
        sessionStorage.removeItem('commander_token');
        localStorage.removeItem('commander_user');
        localStorage.removeItem('commander_role');
        this.disconnectWebSocket();
    }

    getUser() {
        return {
            username: localStorage.getItem('commander_user') || 'unknown',
            role: localStorage.getItem('commander_role') || 'Viewer'
        };
    }

    // -- Devices --------------------------------------------------------------

    async getDevices() { return this.get('/api/devices'); }
    async getDevice(id) { return this.get(`/api/devices/${id}`); }
    async addDevice(data) { return this.post('/api/devices', data); }
    async updateDevice(id, data) { return this.put(`/api/devices/${id}`, data); }
    async deleteDevice(id) { return this.del(`/api/devices/${id}`); }

    async sendCommand(deviceId, category, command) {
        return this._fetch('POST', `/api/devices/${deviceId}/command`, { category, command });
    }

    async sendCommandByName(deviceId, command) {
        return this._fetch('POST', `/api/devices/${deviceId}/command`, { command });
    }

    async sendCommandById(deviceId, commandId) {
        return this._fetch('POST', `/api/devices/${deviceId}/command`, { commandId });
    }

    async wakeDevice(deviceId) {
        return this._fetch('POST', `/api/devices/${deviceId}/wake`);
    }

    async connectDevice(deviceId) {
        return this._fetch('POST', `/api/devices/${deviceId}/connect`);
    }

    async disconnectDevice(deviceId) {
        return this._fetch('POST', `/api/devices/${deviceId}/disconnect`);
    }

    async getDeviceCommands(deviceId) {
        return this._fetch('GET', `/api/devices/${deviceId}/commands`);
    }

    async discoverApps(deviceId) {
        return this._fetch('POST', `/api/devices/${deviceId}/discover`);
    }

    // -- Groups / Rooms -------------------------------------------------------

    async getGroups() { return this.get('/api/groups'); }
    async addGroup(data) { return this.post('/api/groups', data); }
    async updateGroup(id, data) { return this.put(`/api/groups/${id}`, data); }
    async deleteGroup(id) { return this.del(`/api/groups/${id}`); }
    async setGroupMembers(id, deviceIds) { return this.put(`/api/groups/${id}/members`, { deviceIds }); }

    async sendGroupCommand(groupId, command) {
        return this._fetch('POST', `/api/groups/${groupId}/command`, { command });
    }

    // -- Macros ---------------------------------------------------------------

    async getMacros() { return this.get('/api/macros'); }
    async addMacro(data) { return this.post('/api/macros', data); }
    async updateMacro(id, data) { return this.put(`/api/macros/${id}`, data); }
    async deleteMacro(id) { return this.del(`/api/macros/${id}`); }

    async runMacro(macroId, deviceId, groupId) {
        return this._fetch('POST', `/api/macros/${macroId}/run`, { deviceId, groupId });
    }

    // -- Scheduler ------------------------------------------------------------

    async getSchedules() { return this.get('/api/schedules'); }
    async addSchedule(data) { return this.post('/api/schedules', data); }
    async updateSchedule(id, data) { return this.put(`/api/schedules/${id}`, data); }
    async deleteSchedule(id) { return this.del(`/api/schedules/${id}`); }
    async runScheduleNow(id) { return this.post(`/api/schedules/${id}/run`); }

    // -- Commands Browser -----------------------------------------------------

    async getCommands(series) {
        return this.get(`/api/commands${series ? '?series=' + encodeURIComponent(series) : ''}`);
    }

    async getCommandSeries() { return this.get('/api/commands/series'); }
    async getCommandCategories(series) {
        return this.get('/api/commands/categories?series=' + encodeURIComponent(series));
    }
    async getModels() { return this.get('/api/models'); }

    // -- Commands CRUD --------------------------------------------------------

    async addCommand(data) { return this.post('/api/commands', data); }
    async updateCommand(id, data) { return this.put(`/api/commands/${id}`, data); }
    async deleteCommand(id) { return this.del(`/api/commands/${id}`); }

    // -- Models CRUD ----------------------------------------------------------

    async addModel(data) { return this.post('/api/models', data); }
    async updateModel(id, data) { return this.put(`/api/models/${id}`, data); }
    async deleteModel(id) { return this.del(`/api/models/${id}`); }

    // -- CSV Export/Import ----------------------------------------------------

    async exportCommandsCsv() {
        const res = await fetch('/api/commands/export', { headers: this._headers() });
        if (!res.ok) throw new ApiError('Export failed', 'Failed to export commands.', res.status);
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'commands_export.csv';
        a.click();
        URL.revokeObjectURL(url);
    }

    async importCommandsCsv(file) {
        const fd = new FormData();
        fd.append('file', file);
        return this.postForm('/api/commands/import', fd);
    }

    // -- Audit Log ------------------------------------------------------------

    async getLogs(params) {
        var qs = [];
        if (params) {
            if (params.device) qs.push('device=' + encodeURIComponent(params.device));
            if (params.from) qs.push('from=' + encodeURIComponent(params.from));
            if (params.to) qs.push('to=' + encodeURIComponent(params.to));
            if (params.success !== undefined && params.success !== null) qs.push('success=' + params.success);
            if (params.search) qs.push('search=' + encodeURIComponent(params.search));
            if (params.limit !== undefined) qs.push('limit=' + params.limit);
            if (params.offset !== undefined) qs.push('offset=' + params.offset);
        }
        return this.get('/api/logs' + (qs.length ? '?' + qs.join('&') : ''));
    }

    async clearLogs() { return this.del('/api/logs'); }

    // -- Firmware -------------------------------------------------------------

    async getDeviceFirmware(id) { return this.get(`/api/devices/${id}/firmware`); }

    // -- Network Scan ---------------------------------------------------------

    async startScan(startIp, endIp) { return this.post('/api/scan', { startIp, endIp }); }
    async getScanStatus(scanId) { return this.get(`/api/scan/${scanId}`); }

    // -- OUI Table ------------------------------------------------------------

    async getOuiTable() { return this.get('/api/oui'); }
    async addOui(data) { return this.post('/api/oui', data); }
    async updateOui(id, data) { return this.put(`/api/oui/${id}`, data); }
    async deleteOui(id) { return this.del(`/api/oui/${id}`); }

    // -- Status / Health ------------------------------------------------------

    async getStatus() { return this.get('/api/status'); }
    async getHealth() { return this.get('/health'); }

    // -- Users (Admin) --------------------------------------------------------

    async getUsers() { return this.get('/api/users'); }

    async createUser(username, password, role) {
        return this.post('/api/users', { username, password, role });
    }

    async deleteUser(id) { return this.del(`/api/users/${id}`); }

    // -- API Keys (Admin) -----------------------------------------------------

    async getKeys() { return this.get('/api/keys'); }
    async createKey(label) { return this.post('/api/keys', { label }); }
    async deleteKey(id) { return this.del(`/api/keys/${id}`); }

    // -- Settings -------------------------------------------------------------

    async uploadLogo(file) {
        const fd = new FormData();
        fd.append('logo', file);
        return this.postForm('/api/settings/logo', fd);
    }

    // -- WebSocket ------------------------------------------------------------

    connectWebSocket() {
        if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
            return;
        }

        if (!this.token) return;

        const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const url = `${proto}//${location.host}/ws?token=${encodeURIComponent(this.token)}`;

        try {
            this.ws = new WebSocket(url);
        } catch {
            return;
        }

        this.ws.onopen = () => {
            this._wsReconnectDelay = 2000;
            this._emit('ws:open');
        };

        this.ws.onmessage = (e) => {
            try {
                const evt = JSON.parse(e.data);
                if (evt.type) {
                    this._emit(`ws:${evt.type}`, evt.data);
                    this._emit('ws:message', evt);
                }
            } catch { /* ignore malformed */ }
        };

        this.ws.onclose = () => {
            this._emit('ws:close');
            this._scheduleReconnect();
        };

        this.ws.onerror = () => {
            /* onclose will fire after */
        };
    }

    disconnectWebSocket() {
        if (this.wsReconnectTimer) {
            clearTimeout(this.wsReconnectTimer);
            this.wsReconnectTimer = null;
        }
        if (this.ws) {
            this.ws.onclose = null;
            this.ws.onerror = null;
            try { this.ws.close(); } catch { /* ok */ }
            this.ws = null;
        }
    }

    _scheduleReconnect() {
        if (this.wsReconnectTimer) return;
        if (!this.token) return;

        this.wsReconnectTimer = setTimeout(() => {
            this.wsReconnectTimer = null;
            this.connectWebSocket();
        }, this._wsReconnectDelay);

        this._wsReconnectDelay = Math.min(this._wsReconnectDelay * 1.5, 30000);
    }

    // -- Event Emitter --------------------------------------------------------

    on(event, fn) {
        if (!this.wsListeners[event]) this.wsListeners[event] = [];
        this.wsListeners[event].push(fn);
        return () => this.off(event, fn);
    }

    off(event, fn) {
        if (!this.wsListeners[event]) return;
        this.wsListeners[event] = this.wsListeners[event].filter(f => f !== fn);
    }

    _emit(event, data) {
        const fns = this.wsListeners[event];
        if (fns) fns.forEach(fn => { try { fn(data); } catch (e) { console.error('WS listener error:', e); } });
    }
}

// -- Error class --------------------------------------------------------------

class ApiError extends Error {
    constructor(title, message, status) {
        super(message);
        this.title = title;
        this.status = status;
    }
}

// -- Global instance ----------------------------------------------------------
const api = new CommanderApi();
