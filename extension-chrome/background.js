// VNU Academic Gateway (VAGS) - Background Engine (Google Chrome & Microsoft Edge MV3)
const VAGS_BASE_URL = "https://thamkhao.vnu.edu.vn";
let sseSource = null;
let reconnectTimer = null;
let sseAbortController = null;

let cachedRules = null;
let cachedUser = "";
let cachedToken = "";
let cachedMemberType = "";
let cachedProxyEnabled = false;

function normalizeHost(rawHost) {
    return (rawHost || "").trim().toLowerCase().replace(/\.+$/, "");
}

function stripIpv6BracketsAndZone(rawHost) {
    let h = normalizeHost(rawHost);
    if (h.startsWith("[") && h.endsWith("]")) {
        h = h.slice(1, -1);
    }
    const pctIdx = h.indexOf("%");
    if (pctIdx !== -1) {
        h = h.slice(0, pctIdx);
    }
    return h;
}

function shExpMatch(str, pattern) {
    if (!pattern) return false;
    const p = normalizeHost(pattern);
    const s = normalizeHost(str);
    if (!p || !s) return false;
    if (p === s) return true;
    if (p.startsWith("*.")) {
        const domain = p.substring(2);
        if (s === domain || s.endsWith("." + domain)) return true;
    }
    const regexStr = "^" + p.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*/g, ".*").replace(/\?/g, ".") + "$";
    return new RegExp(regexStr).test(s);
}

function parseIPv4Octets(h) {
    const parts = h.split(".");
    if (parts.length !== 4) return null;
    const octets = [];
    for (let i = 0; i < 4; i++) {
        if (!/^\d{1,3}$/.test(parts[i])) return null;
        const n = Number(parts[i]);
        if (n < 0 || n > 255) return null;
        octets.push(n);
    }
    return octets;
}

function isForbiddenIPv4Octets(o) {
    if (!o) return false;
    const a = o[0], b = o[1];
    if (a === 127) return true;                          // 127.0.0.0/8 (127.*)
    if (a === 0) return true;                            // 0.0.0.0/8 (0.0.0.0)
    if (a === 10) return true;                           // 10.0.0.0/8 (10.*)
    if (a === 169 && b === 254) return true;             // 169.254.0.0/16 (169.254.*)
    if (a === 172 && b >= 16 && b <= 31) return true;    // 172.16.0.0/12
    if (a === 192 && b === 168) return true;             // 192.168.0.0/16
    if (a === 100 && b >= 64 && b <= 127) return true;   // 100.64.0.0/10 (CGNAT)
    if (a >= 224) return true;                           // Multicast / reserved
    return false;
}

function parseIPv6Words(addr) {
    if (!addr || addr.indexOf(":") === -1) return null;
    const dbl = addr.split("::");
    if (dbl.length > 2) return null;
    const leftRaw = dbl[0] ? dbl[0].split(":") : [];
    const rightRaw = dbl.length === 2 && dbl[1] ? dbl[1].split(":") : [];

    const parseGroupList = (arr, allowTailIpv4) => {
        const words = [];
        for (let i = 0; i < arr.length; i++) {
            const g = arr[i];
            if (allowTailIpv4 && i === arr.length - 1 && g.indexOf(".") !== -1) {
                const o = parseIPv4Octets(g);
                if (!o) return null;
                words.push((o[0] << 8) | o[1], (o[2] << 8) | o[3]);
            } else {
                if (!/^[0-9a-f]{1,4}$/.test(g)) return null;
                words.push(parseInt(g, 16));
            }
        }
        return words;
    };

    const left = parseGroupList(leftRaw, dbl.length === 1);
    const right = parseGroupList(rightRaw, dbl.length === 2);
    if (!left || !right) return null;

    if (dbl.length === 1) {
        return left.length === 8 ? left : null;
    }
    const missing = 8 - (left.length + right.length);
    if (missing < 1) return null;
    return left.concat(new Array(missing).fill(0), right);
}

function isInternalOrReservedHost(rawHost) {
    const h = stripIpv6BracketsAndZone(rawHost);
    if (!h) return true;
    if (h === "localhost" || h.endsWith(".localhost") ||
        h === "host.docker.internal" || h.endsWith(".docker.internal") ||
        h.endsWith(".local")) {
        return true;
    }
    const ipv4 = parseIPv4Octets(h);
    if (ipv4) {
        return isForbiddenIPv4Octets(ipv4);
    }
    const w = parseIPv6Words(h);
    if (w) {
        const z5 = w[0] === 0 && w[1] === 0 && w[2] === 0 && w[3] === 0 && w[4] === 0;
        if (z5 && w[5] === 0 && w[6] === 0 && (w[7] === 0 || w[7] === 1)) return true;
        if (z5 && w[5] === 0xffff) {
            return isForbiddenIPv4Octets([w[6] >> 8, w[6] & 0xff, w[7] >> 8, w[7] & 0xff]);
        }
        if ((w[0] & 0xfe00) === 0xfc00) return true; // fc00::/7
        if ((w[0] & 0xffc0) === 0xfe80) return true; // fe80::/10
        if ((w[0] & 0xff00) === 0xff00) return true; // ff00::/8
    }
    return false;
}

function isVagsSelfHost(rawHost) {
    const h = stripIpv6BracketsAndZone(rawHost);
    return h === "thamkhao.vnu.edu.vn" || h.endsWith(".thamkhao.vnu.edu.vn") || h === "112.137.142.30" ||
           h === "vags.vnu.edu.vn" || h.endsWith(".vags.vnu.edu.vn") || h === "112.137.133.14";
}

function isCloudBypassHost(rawHost) {
    const h = normalizeHost(rawHost);
    const cloudPatterns = [
        "google.com", "*.google.com", "*.googleapis.com", "*.gstatic.com",
        "*.googleusercontent.com", "*.googlevideo.com", "*.gvt1.com", "*.gvt2.com",
        "*.microsoft.com", "*.live.com", "*.office.com", "*.apple.com", "*.icloud.com"
    ];
    for (let i = 0; i < cloudPatterns.length; i++) {
        if (shExpMatch(h, cloudPatterns[i])) return true;
    }
    return false;
}

function getUserSpecificDomains(rules, username) {
    const map = rules?.userOverrides?.userSpecificRules;
    if (!map || !username) return [];
    if (map[username] && Array.isArray(map[username].proxyDomains)) {
        return map[username].proxyDomains;
    }
    const lower = username.trim().toLowerCase();
    const ciKey = Object.keys(map).find(k => (k || "").trim().toLowerCase() === lower);
    return (ciKey && Array.isArray(map[ciKey]?.proxyDomains)) ? map[ciKey].proxyDomains : [];
}

function normalizePatternList(list) {
    if (!Array.isArray(list)) return [];
    return list.map(p => normalizeHost(p)).filter(Boolean);
}

function buildAuthHeaders(tokenOverride) {
    const headers = { "Content-Type": "application/json" };
    const tok = tokenOverride || cachedToken;
    if (tok) {
        headers["Authorization"] = `Bearer ${tok}`;
    }
    return headers;
}

function updateExtensionActionUI(isEnabled, isLoggedIn) {
    if (typeof chrome === "undefined" || !chrome.action) return;

    if (isLoggedIn && isEnabled) {
        chrome.action.setIcon({
            path: {
                "16": "icon-16.png",
                "48": "icon-48.png",
                "128": "icon-128.png"
            }
        });
        chrome.action.setBadgeText({ text: "ON" });
        chrome.action.setBadgeBackgroundColor({ color: "#00803d" });
        chrome.action.setTitle({ title: "VAGS: Đang Bật Proxy CSDL (Mạng ĐHQGHN)" });
    } else {
        chrome.action.setIcon({
            path: {
                "16": "icon-16-off.png",
                "48": "icon-48-off.png",
                "128": "icon-128-off.png"
            }
        });
        chrome.action.setBadgeText({ text: !isLoggedIn ? "LOCK" : "OFF" });
        chrome.action.setBadgeBackgroundColor({ color: !isLoggedIn ? "#64748b" : "#ef4444" });
        chrome.action.setTitle({
            title: !isLoggedIn 
                ? "VAGS: Chưa đăng nhập VNU SSO (Bấm để đăng nhập)" 
                : "VAGS: Đã Tắt Proxy (Đi trực tiếp DIRECT)"
        });
    }
}

function updateLocalCache() {
    chrome.storage.local.get(["currentRules", "vagsUser", "vagsToken", "vagsMemberType", "proxyEnabled", "isLocked"], (storage) => {
        if (storage.currentRules) cachedRules = storage.currentRules;
        if (storage.vagsUser !== undefined) cachedUser = storage.vagsUser;
        if (storage.vagsToken !== undefined) cachedToken = storage.vagsToken;
        if (storage.vagsMemberType !== undefined) cachedMemberType = storage.vagsMemberType;
        const isLoggedIn = !!(storage.vagsUser && storage.vagsToken);
        const isStudent = storage.vagsMemberType === "sinhvien";
        const allowedUsers = storage.currentRules?.userOverrides?.allowed || [];
        const isOverride = storage.vagsUser && allowedUsers.includes(storage.vagsUser);
        const deniedUsers = (storage.currentRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
        const isDenied = (storage.vagsUser && deniedUsers.includes(storage.vagsUser.toLowerCase())) || storage.isLocked === true;

        if (!isLoggedIn || (isStudent && !isOverride) || isDenied) {
            cachedProxyEnabled = false;
        } else {
            cachedProxyEnabled = storage.proxyEnabled === true;
        }
        updateExtensionActionUI(cachedProxyEnabled, isLoggedIn && !isDenied);
    });
}
updateLocalCache();

// 1. Ket noi SSE Real-time bang Fetch Streaming
async function connectSSE() {
    if (sseAbortController) {
        try { sseAbortController.abort(); } catch (e) {}
    }
    const myController = new AbortController();
    sseAbortController = myController;
    clearTimeout(reconnectTimer);

    try {
        const queryParams = new URLSearchParams();
        if (cachedUser) queryParams.set("user", cachedUser);
        queryParams.set("proxyEnabled", cachedProxyEnabled ? "true" : "false");
        const userParam = "?" + queryParams.toString();
        const response = await fetch(`${VAGS_BASE_URL}/api/v1/rules/stream${userParam}`, {
            headers: { "Accept": "text/event-stream" },
            cache: "no-store",
            signal: myController.signal
        });

        if (sseAbortController !== myController) return;

        if (!response.ok || !response.body) {
            chrome.storage.local.set({ sseConnected: false });
            clearTimeout(reconnectTimer);
            reconnectTimer = setTimeout(connectSSE, 5000);
            return;
        }

        chrome.storage.local.set({ sseConnected: true, lastConnectedAt: new Date().toISOString() });

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });
            const parts = buffer.split("\n\n");
            buffer = parts.pop() || "";

            for (const block of parts) {
                if (!block.trim()) continue;
                let eventType = "message";
                let eventDataLines = [];
                for (const line of block.split("\n")) {
                    if (line.startsWith("event:")) eventType = line.slice(6).trim();
                    else if (line.startsWith("data:")) eventDataLines.push(line.slice(5).trim());
                    else if (eventDataLines.length > 0 && line.trim()) eventDataLines.push(line.trim());
                }
                const eventData = eventDataLines.join("\n");

                if (eventType === "CONNECTED") {
                    chrome.storage.local.set({ sseConnected: true });
                } else if (eventType === "RULES_UPDATED") {
                    try {
                        const newRules = JSON.parse(eventData);
                        handleRulesUpdated(newRules);
                    } catch (e) {
                        console.error("[VAGS Chrome] Failed to parse RULES_UPDATED:", e);
                    }
                } else if (eventType === "FORCE_UPDATE") {
                    try {
                        console.log("[VAGS Chrome] 📢 Nhận lệnh ép cập nhật từ máy chủ:", eventData);
                        forceCheckExtensionUpdate();
                    } catch (e) { }
                } else if (eventType === "FORCE_PROXY_OFF") {
                    try {
                        const payload = JSON.parse(eventData);
                        handleForceProxyOff(payload);
                    } catch (e) {
                        console.error("[VAGS Chrome] Failed to parse FORCE_PROXY_OFF:", e);
                    }
                } else if (eventType === "FORCE_LOGOUT") {
                    try {
                        const payload = JSON.parse(eventData);
                        handleForceLogout(payload);
                    } catch (e) {
                        console.error("[VAGS Chrome] Failed to parse FORCE_LOGOUT:", e);
                    }
                } else if (eventType === "FORCE_LOCK") {
                    try {
                        const payload = JSON.parse(eventData);
                        handleForceLock(payload);
                    } catch (e) {
                        console.error("[VAGS Chrome] Failed to parse FORCE_LOCK:", e);
                    }
                }
            }
        }
    } catch (err) {
        if (sseAbortController !== myController) return;
        if (err.name !== "AbortError") {
            console.warn("[VAGS Chrome] SSE error:", err);
        }
    }

    if (sseAbortController !== myController) return;
    chrome.storage.local.set({ sseConnected: false });
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectSSE, 5000);
}

function handleRulesUpdated(newRules) {
    cachedRules = newRules;
    chrome.storage.local.get(["proxyEnabled", "vagsMemberType", "vagsUser", "vagsToken"], (storage) => {
        const username = storage.vagsUser || "";
        const token = storage.vagsToken || "";
        const memberType = storage.vagsMemberType || "";
        const allowedUsers = newRules.userOverrides?.allowed || [];
        const deniedUsers = newRules.userOverrides?.denied || [];
        const isUserOverride = allowedUsers.includes(username);
        const isUserDenied = username && deniedUsers.map(u => (u || '').toLowerCase()).includes(username.toLowerCase());

        const isLoggedIn = !!(username && token);
        const isStudent = memberType === "sinhvien";
        const isCanbo = (memberType === "canbo" || memberType !== "sinhvien") && isLoggedIn;
        const isAllowed = (isCanbo || isUserOverride) && !isStudent && !isUserDenied;
        const isEnabled = storage.proxyEnabled === true;

        chrome.storage.local.set({ currentRules: newRules, ruleVersion: newRules.version });
        updateLocalCache();

        if (isLoggedIn && isAllowed && isEnabled) {
            applyProxyRules(newRules, username);
        } else {
            disableProxy();
        }
    });
}

function handleForceProxyOff(payload) {
    chrome.storage.local.get(["vagsUser"], (storage) => {
        const currentUser = (storage.vagsUser || cachedUser || "").trim().toLowerCase();
        const targetUser = (payload.user || "").trim().toLowerCase();
        const isMatch = payload.all === true || (targetUser && targetUser === currentUser);

        if (isMatch) {
            const reason = payload.reason || "";
            console.warn("[VAGS Chrome] ⚡ Nhận lệnh ép TẮT PROXY từ Hệ thống!", reason);
            cachedProxyEnabled = false;
            chrome.storage.local.set({
                proxyEnabled: false,
                lastForceOffReason: reason,
                lastForceOffAt: new Date().toISOString()
            }, () => {
                disableProxy(!!currentUser);
                updateLocalCache();
                connectSSE();
            });
        }
    });
}

function handleForceLogout(payload) {
    chrome.storage.local.get(["vagsUser"], (storage) => {
        const currentUser = (storage.vagsUser || cachedUser || "").trim().toLowerCase();
        const targetUser = (payload.user || "").trim().toLowerCase();
        const isMatch = payload.all === true || (targetUser && targetUser === currentUser);

        if (isMatch) {
            console.warn("[VAGS Chrome] 🚪 Nhận lệnh ép ĐĂNG XUẤT từ Quản trị viên!");
            disableProxy(false);
            if (sseAbortController) {
                try { sseAbortController.abort(); } catch (e) {}
            }
            chrome.storage.local.remove([
                "vagsUser", "vags_user", "vagsToken", "vags_access_token",
                "vagsMemberType", "vagsUnit", "vagsEmail", "vagsName",
                "vagsTokenExpires", "proxyEnabled", "vags_proxy_enabled"
            ], () => {
                cachedUser = "";
                cachedToken = "";
                cachedMemberType = "";
                cachedProxyEnabled = false;
                updateLocalCache();
                updateExtensionActionUI(false, false);
                connectSSE();

                chrome.tabs.query({}, (tabs) => {
                    (tabs || []).forEach(t => {
                        chrome.tabs.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE", type: "FORCE_LOGOUT_PAGE", reason: payload.reason || "Quản trị viên đã ngắt phiên và yêu cầu đăng xuất." }).catch(() => {});
                    });
                });
            });
        }
    });
}

function handleForceLock(payload) {
    chrome.storage.local.get(["vagsUser"], (storage) => {
        const currentUser = (storage.vagsUser || cachedUser || "").trim().toLowerCase();
        const targetUser = (payload.user || "").trim().toLowerCase();
        const isMatch = targetUser && targetUser === currentUser;

        if (isMatch) {
            console.warn("[VAGS Chrome] 🔒 Tài khoản đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS!");
            disableProxy(false);
            chrome.storage.local.remove([
                "vagsUser", "vags_user", "vagsToken", "vags_access_token",
                "vagsMemberType", "vagsUnit", "vagsEmail", "vagsName",
                "vagsTokenExpires", "proxyEnabled", "vags_proxy_enabled"
            ], () => {
                cachedUser = "";
                cachedToken = "";
                cachedMemberType = "";
                cachedProxyEnabled = false;
                chrome.storage.local.set({ isLocked: true });
                updateLocalCache();
                updateExtensionActionUI(false, false);

                chrome.tabs.query({}, (tabs) => {
                    (tabs || []).forEach(t => {
                        chrome.tabs.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE", type: "FORCE_LOGOUT_PAGE", reason: "Tài khoản của bạn đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS." }).catch(() => {});
                    });
                });
            });
        }
    });
}

// 2. Ap dung Proxy vao Chrome Network Stack
function applyProxyRules(rules, username) {
    if (typeof chrome === "undefined" || !chrome.proxy || !chrome.proxy.settings) return;

    // QUAN TRỌNG: Kiểm tra tài khoản bị khoá (Admin Lock)
    const deniedList = (rules.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
    if (username && deniedList.includes(username.toLowerCase())) {
        console.warn("[VAGS Chrome] 🔒 Tài khoản", username, "đang trong danh sách bị khoá (denied). Ép DIRECT ngay lập tức.");
        disableProxy(true);
        updateExtensionActionUI(false, true);
        return;
    }

    const isFullProxy = (rules.proxyMode || "FULL_PROXY") === "FULL_PROXY";
    const licEnabled = rules.enableLicProxy !== false;

    // Danh sach bo qua proxy (Bypass List)
    // QUAN TRỌNG: Máy chủ VAGS & IP cổng & Địa chỉ Loopback, Docker, Mạng nội bộ PHẢI LUÔN trong bypassList để không bao giờ bị lỗi hoặc rò rỉ tunnel
    let bypassList = [
        "<local>",
        "thamkhao.vnu.edu.vn",
        "*.thamkhao.vnu.edu.vn",
        "112.137.142.30",
        "vags.vnu.edu.vn",
        "*.vags.vnu.edu.vn",
        "112.137.133.14",
        "localhost",
        "*.localhost",
        "127.0.0.1",
        "127.*",
        "127.0.0.0/8",
        "0.0.0.0",
        "0.0.0.0/8",
        "::1",
        "::1/128",
        "::/128",
        "fc00::/7",
        "fe80::/10",
        "169.254.*",
        "169.254.0.0/16",
        "10.*",
        "10.0.0.0/8",
        "172.16.*", "172.17.*", "172.18.*", "172.19.*",
        "172.2*.*", "172.30.*", "172.31.*",
        "172.16.0.0/12",
        "192.168.*",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "*.docker.internal",
        "host.docker.internal",
        "*.local",
        "google.com",
        "*.google.com",
        "*.googleapis.com",
        "*.gstatic.com",
        "*.googleusercontent.com",
        "*.googlevideo.com",
        "*.gvt1.com",
        "*.gvt2.com",
        "*.microsoft.com",
        "*.live.com",
        "*.office.com",
        "*.apple.com",
        "*.icloud.com"
    ];
    if (rules.blacklist) bypassList = bypassList.concat(normalizePatternList(rules.blacklist));

    // Mạng nội bộ vnu: Luôn giữ vnu.edu.vn và *.vnu.edu.vn trong bypassList;
    // Chỉ dùng PAC khi có ngoại lệ proxy riêng trên *.vnu.edu.vn (userSpecificRules hoặc internalWhitelist khi enableLicProxy !== false) để giữ phần còn lại của *.vnu.edu.vn đi DIRECT.
    bypassList.push("vnu.edu.vn", "*.vnu.edu.vn");
    const userOverrides = normalizePatternList(getUserSpecificDomains(rules, username));
    const activeInternalWL = licEnabled ? normalizePatternList(rules.internalWhitelist) : [];
    const isVnuPattern = (p) => p === "vnu.edu.vn" || p.endsWith(".vnu.edu.vn");
    const hasVnuOverride = userOverrides.some(isVnuPattern) || activeInternalWL.some(isVnuPattern);

    if (isFullProxy && !hasVnuOverride) {
        // Che do Full Proxy: Dung fixed_servers truc tiep khong thong qua Javascript PAC isolate
        chrome.proxy.settings.set({
            value: {
                mode: "fixed_servers",
                rules: {
                    singleProxy: {
                        scheme: "https",
                        host: "thamkhao.vnu.edu.vn",
                        port: 443
                    },
                    bypassList: bypassList
                }
            },
            scope: "regular"
        }, () => {
            const err = chrome.runtime.lastError;
            if (err) {
                console.warn("[VAGS Chrome] fixed_servers error, trying pac_script:", err.message);
                applyPacScript(rules, username);
            }
        });
    } else {
        applyPacScript(rules, username);
    }

    chrome.storage.local.set({
        currentRules: rules,
        ruleVersion: rules.version,
        proxyEndpoint: "HTTPS thamkhao.vnu.edu.vn:443",
        proxyEnabled: true,
        lastSyncedAt: new Date().toISOString()
    });
    updateExtensionActionUI(true, true);
}

function applyPacScript(rules, username) {
    const licEnabled = rules.enableLicProxy !== false;
    const isFullProxy = (rules.proxyMode || "FULL_PROXY") === "FULL_PROXY";
    const userSpecificProxy = normalizePatternList(getUserSpecificDomains(rules, username));
    const blacklist = normalizePatternList(rules.blacklist);
    const internalWL = licEnabled ? normalizePatternList(rules.internalWhitelist) : [];
    const academicWL = normalizePatternList(rules.globalAcademicWhitelist);

    const pacLines = [
        "function vagsStripHost(rawHost) {",
        "    var h = (rawHost || '').toLowerCase().replace(/^\\s+|\\s+$/g, '').replace(/\\.+$/, '');",
        "    if (h.charAt(0) === '[' && h.charAt(h.length - 1) === ']') h = h.slice(1, -1);",
        "    var pct = h.indexOf('%');",
        "    if (pct !== -1) h = h.slice(0, pct);",
        "    return h;",
        "}",
        "function vagsParseIPv4(h) {",
        "    var parts = h.split('.');",
        "    if (parts.length !== 4) return null;",
        "    var out = [];",
        "    for (var i = 0; i < 4; i++) {",
        "        if (!/^\\d{1,3}$/.test(parts[i])) return null;",
        "        var n = Number(parts[i]);",
        "        if (n < 0 || n > 255) return null;",
        "        out.push(n);",
        "    }",
        "    return out;",
        "}",
        "function vagsIsForbiddenIPv4(o) {",
        "    if (!o) return false;",
        "    var a = o[0], b = o[1];",
        "    if (a === 127 || a === 0 || a === 10) return true;",
        "    if (a === 169 && b === 254) return true;",
        "    if (a === 172 && b >= 16 && b <= 31) return true;",
        "    if (a === 192 && b === 168) return true;",
        "    if (a === 100 && b >= 64 && b <= 127) return true;",
        "    if (a >= 224) return true;",
        "    return false;",
        "}",
        "function vagsParseIPv6(addr) {",
        "    if (!addr || addr.indexOf(':') === -1) return null;",
        "    var dbl = addr.split('::');",
        "    if (dbl.length > 2) return null;",
        "    var leftRaw = dbl[0] ? dbl[0].split(':') : [];",
        "    var rightRaw = (dbl.length === 2 && dbl[1]) ? dbl[1].split(':') : [];",
        "    function parseGroups(arr, allowTailV4) {",
        "        var words = [];",
        "        for (var i = 0; i < arr.length; i++) {",
        "            var g = arr[i];",
        "            if (allowTailV4 && i === arr.length - 1 && g.indexOf('.') !== -1) {",
        "                var o = vagsParseIPv4(g);",
        "                if (!o) return null;",
        "                words.push((o[0] << 8) | o[1], (o[2] << 8) | o[3]);",
        "            } else {",
        "                if (!/^[0-9a-f]{1,4}$/.test(g)) return null;",
        "                words.push(parseInt(g, 16));",
        "            }",
        "        }",
        "        return words;",
        "    }",
        "    var left = parseGroups(leftRaw, dbl.length === 1);",
        "    var right = parseGroups(rightRaw, dbl.length === 2);",
        "    if (!left || !right) return null;",
        "    if (dbl.length === 1) return left.length === 8 ? left : null;",
        "    var missing = 8 - (left.length + right.length);",
        "    if (missing < 1) return null;",
        "    var res = left.slice();",
        "    for (var z = 0; z < missing; z++) res.push(0);",
        "    return res.concat(right);",
        "}",
        "function vagsIsInternalHost(h) {",
        "    if (!h) return true;",
        "    if (h === 'localhost' || shExpMatch(h, '*.localhost') || h === 'host.docker.internal' || shExpMatch(h, '*.docker.internal') || shExpMatch(h, '*.local')) return true;",
        "    if (shExpMatch(h, '127.*') || shExpMatch(h, '169.254.*') || h === '0.0.0.0') return true;",
        "    var v4 = vagsParseIPv4(h);",
        "    if (v4) return vagsIsForbiddenIPv4(v4);",
        "    var w = vagsParseIPv6(h);",
        "    if (w) {",
        "        var z5 = (w[0] === 0 && w[1] === 0 && w[2] === 0 && w[3] === 0 && w[4] === 0);",
        "        if (z5 && w[5] === 0 && w[6] === 0 && (w[7] === 0 || w[7] === 1)) return true;",
        "        if (z5 && w[5] === 0xffff) return vagsIsForbiddenIPv4([w[6] >> 8, w[6] & 0xff, w[7] >> 8, w[7] & 0xff]);",
        "        if ((w[0] & 0xfe00) === 0xfc00) return true;",
        "        if ((w[0] & 0xffc0) === 0xfe80) return true;",
        "        if ((w[0] & 0xff00) === 0xff00) return true;",
        "    }",
        "    return false;",
        "}",
        "function vagsMatch(h, p) {",
        "    if (!p) return false;",
        "    if (p.indexOf('*.') === 0) {",
        "        var dom = p.substring(2);",
        "        if (h === dom || shExpMatch(h, '*.' + dom)) return true;",
        "    }",
        "    return h === p || shExpMatch(h, p);",
        "}",
        "function FindProxyForURL(url, host) {",
        "    var lHost = vagsStripHost(host);",
        "    if (vagsIsInternalHost(lHost)) return 'DIRECT';",
        "    if (lHost === 'thamkhao.vnu.edu.vn' || shExpMatch(lHost, '*.thamkhao.vnu.edu.vn') || lHost === '112.137.142.30' || lHost === 'vags.vnu.edu.vn' || shExpMatch(lHost, '*.vags.vnu.edu.vn') || lHost === '112.137.133.14') return 'DIRECT';",
        "    if (vagsMatch(lHost, '*.google.com') || vagsMatch(lHost, '*.googleapis.com') || vagsMatch(lHost, '*.gstatic.com') || vagsMatch(lHost, '*.googleusercontent.com') || vagsMatch(lHost, '*.googlevideo.com') || vagsMatch(lHost, '*.gvt1.com') || vagsMatch(lHost, '*.gvt2.com') || vagsMatch(lHost, '*.microsoft.com') || vagsMatch(lHost, '*.live.com') || vagsMatch(lHost, '*.office.com') || vagsMatch(lHost, '*.apple.com') || vagsMatch(lHost, '*.icloud.com')) return 'DIRECT';",
        "    var blacklist = " + JSON.stringify(blacklist) + ";",
        "    for (var i = 0; i < blacklist.length; i++) {",
        "        if (vagsMatch(lHost, blacklist[i])) return 'DIRECT';",
        "    }",
        "    var userWL = " + JSON.stringify(userSpecificProxy) + ";",
        "    for (var u = 0; u < userWL.length; u++) {",
        "        if (vagsMatch(lHost, userWL[u])) return 'HTTPS thamkhao.vnu.edu.vn:443';",
        "    }",
        "    var internalWL = " + JSON.stringify(internalWL) + ";",
        "    for (var j = 0; j < internalWL.length; j++) {",
        "        if (vagsMatch(lHost, internalWL[j])) return 'HTTPS thamkhao.vnu.edu.vn:443';",
        "    }",
        "    if (vagsMatch(lHost, '*.vnu.edu.vn')) {",
        "        return 'DIRECT';",
        "    }",
        "    var academicWL = " + JSON.stringify(academicWL) + ";",
        "    for (var k = 0; k < academicWL.length; k++) {",
        "        if (vagsMatch(lHost, academicWL[k])) return 'HTTPS thamkhao.vnu.edu.vn:443';",
        "    }",
        "    return " + (isFullProxy ? "'HTTPS thamkhao.vnu.edu.vn:443'" : "'DIRECT'") + ";",
        "}"
    ];
    const pacScript = pacLines.join("\n").replace(/[^\x00-\x7F]/g, "");

    chrome.proxy.settings.set({
        value: {
            mode: "pac_script",
            pacScript: {
                data: pacScript
            }
        },
        scope: "regular"
    }, () => {
        const err = chrome.runtime.lastError;
        if (err) {
            chrome.proxy.settings.set({
                value: {
                    mode: "pac_script",
                    pacScript: {
                        url: `${VAGS_BASE_URL}/vags.pac${username ? '?user=' + encodeURIComponent(username) : ''}`,
                        mandatory: false
                    }
                },
                scope: "regular"
            }, () => {});
        }
    });
}

function disableProxy(isLoggedIn = null) {
    if (typeof chrome !== "undefined" && chrome.proxy && chrome.proxy.settings) {
        chrome.proxy.settings.clear({ scope: "regular" }, () => {
            chrome.proxy.settings.set({
                value: { mode: "direct" },
                scope: "regular"
            }, () => {
                const err = chrome.runtime.lastError;
                if (err) {}
            });
        });
    }
    chrome.storage.local.set({ proxyEnabled: false });
    const logged = isLoggedIn !== null ? isLoggedIn : (!!cachedUser);
    updateExtensionActionUI(false, logged);
}

// 3. Catch-up tren Startup
async function checkVersionOnStartup() {
    try {
        let res = await fetch(`${VAGS_BASE_URL}/api/v1/rules/latest`, { cache: "no-store" });
        if (res.ok) {
            let rules = await res.json();
            cachedRules = rules;
            chrome.storage.local.get(["proxyEnabled", "vagsMemberType", "vagsUser", "vagsToken"], (storage) => {
                const username = storage.vagsUser || "";
                const token = storage.vagsToken || "";
                const isLoggedIn = !!(username && token);
                const allowedUsers = rules.userOverrides?.allowed || [];
                const deniedUsers = (rules.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
                const isUserOverride = allowedUsers.includes(username);
                const isUserDenied = username && deniedUsers.includes(username.toLowerCase());
                const isStudent = storage.vagsMemberType === "sinhvien";
                const isCanbo = (storage.vagsMemberType === "canbo" || !isStudent) && isLoggedIn;
                const isAllowed = (isCanbo || isUserOverride) && !isStudent && !isUserDenied;
                const isEnabled = storage.proxyEnabled === true;

                chrome.storage.local.set({ currentRules: rules, ruleVersion: rules.version, isLocked: isUserDenied });
                updateLocalCache();
                if (isLoggedIn && isAllowed && isEnabled) {
                    applyProxyRules(rules, username);
                } else {
                    disableProxy();
                }
            });
        }
    } catch (e) {
        console.warn("[VAGS Chrome] Sync failed:", e);
    }
}

// 4. Lang nghe Messages
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.action === "SYNC_SESSION") {
        const d = msg.data || {};
        cachedUser = d.vagsUser || "";
        cachedToken = d.vagsToken || "";
        cachedMemberType = d.vagsMemberType || "canbo";

        // TÔN TRỌNG TUYỆT ĐỐI Ý CHÍ NGƯỜI DÙNG:
        // Đọc trạng thái proxyEnabled hiện tại trong storage. Nếu người dùng đã bấm Tắt (proxyEnabled === false),
        // thì reload trang TUYỆT ĐỐI KHÔNG ĐƯỢC TỰ BẬT LẠI!
        chrome.storage.local.get(["proxyEnabled", "currentRules"], (st) => {
            const deniedUsers = (st.currentRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
            const isUserDenied = (d.vagsUser && deniedUsers.includes(d.vagsUser.toLowerCase()));
            let shouldEnable = st.proxyEnabled;
            if (isUserDenied) {
                shouldEnable = false;
            } else if (shouldEnable === undefined) {
                // Chỉ mặc định bật cho phiên đăng nhập lần đầu tiên
                shouldEnable = d.vagsMemberType === "canbo";
            }
            cachedProxyEnabled = shouldEnable === true && !isUserDenied;

            chrome.storage.local.set({
                vagsToken: d.vagsToken || "",
                vagsUser: d.vagsUser || "",
                vagsName: d.vagsName || d.vagsUser || "Cán bộ VNU",
                vagsUnit: d.vagsUnit || "",
                vagsMemberType: d.vagsMemberType || "canbo",
                vagsIsAdmin: d.vagsIsAdmin === true,
                proxyEnabled: cachedProxyEnabled
            }, () => {
                if (cachedUser) {
                    fetch(`${VAGS_BASE_URL}/api/v1/user/heartbeat`, {
                        method: 'POST',
                        headers: buildAuthHeaders(d.vagsToken),
                        body: JSON.stringify({ user: cachedUser, unit: d.vagsUnit || 'VNU', memberType: cachedMemberType || 'canbo', proxyEnabled: cachedProxyEnabled })
                    }).catch(() => {});
                }
                connectSSE();
                if (cachedProxyEnabled) {
                    if (st.currentRules) applyProxyRules(st.currentRules, cachedUser);
                    else checkVersionOnStartup();
                } else {
                    disableProxy(true);
                }
                updateExtensionActionUI(cachedProxyEnabled, true);
                sendResponse({ ok: true, verified: true, proxyEnabled: cachedProxyEnabled });
            });
        });
        return true;
    }

    if (msg.action === "CLEAR_SESSION") {
        cachedUser = "";
        cachedToken = "";
        cachedMemberType = "";
        cachedProxyEnabled = false;

        if (sseAbortController) {
            try { sseAbortController.abort(); } catch (e) {}
        }

        // Gọi hủy đăng ký IP trên máy chủ VAGS Backend
        fetch(`${VAGS_BASE_URL}/api/v1/auth/logout`, { method: "POST" }).catch(() => {});

        chrome.storage.local.remove([
            "vagsToken", "vagsUser", "vagsName", "vagsUnit", "vagsMemberType", "vagsIsAdmin", "proxyEnabled"
        ], () => {
            disableProxy(false);
            updateExtensionActionUI(false, false);
            connectSSE();

            // Đồng bộ lập tức: Gửi thông báo đến tất cả các tab để xóa sạch session
            if (chrome.tabs && chrome.tabs.query) {
                chrome.tabs.query({}, (tabs) => {
                    (tabs || []).forEach(t => {
                        try { chrome.tabs.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE" }); } catch (e) {}
                    });
                });
            }
            sendResponse({ ok: true });
        });
        return true;
    }

    if (msg.action === "TOGGLE_PROXY") {
        chrome.storage.local.get(["vagsUser", "vagsToken", "vagsMemberType", "currentRules"], (storage) => {
            const isLoggedIn = !!(storage.vagsUser && storage.vagsToken);
            const isStudent = storage.vagsMemberType === "sinhvien";
            const isOverride = (storage.currentRules?.userOverrides?.allowed || []).includes(storage.vagsUser);
            const deniedUsers = (storage.currentRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
            const isDenied = storage.vagsUser && deniedUsers.includes(storage.vagsUser.toLowerCase());

            if (!isLoggedIn) {
                disableProxy(false);
                updateExtensionActionUI(false, false);
                sendResponse({ ok: false, error: "Yêu cầu đăng nhập VNU SSO trước khi bật Proxy" });
                return;
            }
            if (isDenied) {
                disableProxy(true);
                updateExtensionActionUI(false, true);
                sendResponse({ ok: false, error: "Tài khoản của bạn đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS" });
                return;
            }
            if (isStudent && !isOverride) {
                disableProxy(true);
                updateExtensionActionUI(false, true);
                sendResponse({ ok: false, error: "Tài khoản Sinh viên không được phép bật Proxy" });
                return;
            }

            const activeToken = storage.vagsToken || cachedToken || "";
            if (activeToken) cachedToken = activeToken;

            cachedProxyEnabled = !!msg.enabled;
            if (cachedProxyEnabled) {
                chrome.storage.local.set({ proxyEnabled: true }, () => {
                    if (storage.currentRules) applyProxyRules(storage.currentRules, storage.vagsUser);
                    else checkVersionOnStartup();
                    updateExtensionActionUI(true, true);
                    connectSSE();
                    fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                        method: 'POST',
                        headers: buildAuthHeaders(activeToken),
                        body: JSON.stringify({ user: storage.vagsUser, proxyEnabled: true })
                    }).catch(() => {});
                    sendResponse({ ok: true, enabled: true });
                });
            } else {
                chrome.storage.local.set({ proxyEnabled: false }, () => {
                    disableProxy(true);
                    updateExtensionActionUI(false, true);
                    connectSSE();
                    fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                        method: 'POST',
                        headers: buildAuthHeaders(activeToken),
                        body: JSON.stringify({ user: storage.vagsUser, proxyEnabled: false })
                    }).catch(() => {});
                    sendResponse({ ok: true, enabled: false });
                });
            }
        });
        return true;
    }

    if (msg.action === "SYNC_NOW") {
        checkVersionOnStartup().then(() => sendResponse({ ok: true }));
        return true;
    }

    if (msg.action === "RECONNECT_SSE") {
        connectSSE();
        sendResponse({ ok: true });
        return true;
    }

    if (msg.action === "GET_STATUS") {
        sendResponse({
            enabled: cachedProxyEnabled,
            user: cachedUser,
            memberType: cachedMemberType,
            version: chrome.runtime.getManifest().version
        });
        return true;
    }

    if (msg.action === "GET_PAGE_ROUTE_INFO") {
        const host = normalizeHost(msg.host);
        const enabled = cachedProxyEnabled;
        const isLoggedIn = !!(cachedUser && (cachedToken || cachedMemberType));

        if (!enabled || !isLoggedIn) {
            let isAcademic = false;
            for (const aw of (cachedRules?.globalAcademicWhitelist || [])) {
                if (shExpMatch(host, aw)) { isAcademic = true; break; }
            }
            sendResponse({
                enabled: enabled,
                isLoggedIn: isLoggedIn,
                action: "DIRECT",
                category: !isLoggedIn ? "unauthenticated" : "disabled",
                isAcademic: isAcademic,
                label: !isLoggedIn ? "Chưa đăng nhập" : "Tiện ích đang tắt",
                reason: !isLoggedIn ? "Vui lòng đăng nhập SSO ĐHQGHN" : "Chế độ Proxy đang tắt trong Extension"
            });
            return true;
        }

        // 0. Loopback, Docker, Internal IP Bypass & VAGS Self-Host
        if (isInternalOrReservedHost(host) || isVagsSelfHost(host)) {
            sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "internalBypass", label: "Địa chỉ nội bộ / Docker", reason: "Kết nối trực tiếp không qua proxy" });
            return true;
        }

        // 0.5 Cloud / OS Bypass
        if (isCloudBypassHost(host)) {
            sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "default", label: "Mạng ngoài", reason: "Dịch vụ đám mây / xác thực kết nối trực tiếp" });
            return true;
        }

        // 1. Blacklist
        for (const b of (cachedRules?.blacklist || [])) {
            if (shExpMatch(host, b)) {
                sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "blacklist", label: "Băng thông cao (Blacklist)", reason: "Không đi qua Proxy trường" });
                return true;
            }
        }

        // 2. User Specific
        for (const ud of getUserSpecificDomains(cachedRules, cachedUser)) {
            if (shExpMatch(host, ud)) {
                sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "userOverride", label: `Ngoại lệ (${cachedUser})`, reason: `Quyền định tuyến riêng cho ${cachedUser}` });
                return true;
            }
        }

        // 3. Internal Whitelist (chỉ áp dụng khi enableLicProxy !== false)
        if (cachedRules?.enableLicProxy !== false) {
            for (const iw of (cachedRules?.internalWhitelist || [])) {
                if (shExpMatch(host, iw)) {
                    sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "lic", label: "Thư viện số LIC / Kiểm tra IP", reason: "Dịch vụ thư viện & công cụ nhận diện ĐHQGHN" });
                    return true;
                }
            }
        }

        // 4. Mạng nội bộ
        if (shExpMatch(host, "*.vnu.edu.vn") || host === "vnu.edu.vn") {
            sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "vnu", label: "Mạng Nội bộ VNU", reason: "Mạng nội bộ ĐHQGHN kết nối trực tiếp" });
            return true;
        }

        // 5. CSDL Học thuật
        for (const aw of (cachedRules?.globalAcademicWhitelist || [])) {
            if (shExpMatch(host, aw)) {
                sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "academic", label: "CSDL Khoa Học Quốc Tế", reason: "Thuê bao điện tử được cấp phép bởi ĐHQGHN" });
                return true;
            }
        }

        // 6. Full Proxy
        if ((cachedRules?.proxyMode || "FULL_PROXY") === "FULL_PROXY") {
            sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "fullProxy", label: "Định Tuyến Toàn Phần", reason: "Chuyển tiếp qua máy chủ ĐHQGHN (Chống rò rỉ IP)" });
            return true;
        }

        sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "default", label: "Mạng ngoài", reason: "Truy cập trực tiếp thông thường" });
        return true;
    }
});

if (typeof chrome !== "undefined" && chrome.proxy && chrome.proxy.onProxyError) {
    chrome.proxy.onProxyError.addListener((details) => {
        console.error("[VAGS Chrome Proxy Error]", details);
    });
}

// 5. TỰ ĐỘNG ĐỒNG BỘ ĐỊNH KỲ 2 TIẾNG CHO TOÀN BỘ CÁN BỘ (Google Workspace & Enterprise)
// Sử dụng chrome.alarms: Dù Service Worker có ngủ (idle), Chrome vẫn tự đánh thức để fetch rule mới
if (typeof chrome !== "undefined" && chrome.alarms) {
    chrome.alarms.create("VAGS_2HR_AUTO_SYNC", {
        delayInMinutes: 2,
        periodInMinutes: 120 // 120 phút = 2 tiếng đồng bộ tự động một lần
    });

    chrome.alarms.onAlarm.addListener((alarm) => {
        if (alarm.name === "VAGS_2HR_AUTO_SYNC") {
            console.log("[VAGS Chrome] ⏰ Chu kỳ 2 tiếng: Tự động tải và áp dụng rule mới từ máy chủ...");
            checkVersionOnStartup();
        }
    });
}

let startupEnforcementPromise = null;

function enforceBrowserStartupProxyOff(callback) {
    cachedProxyEnabled = false;
    chrome.storage.local.get(["vagsUser", "vagsToken", "vagsMemberType", "currentRules"], (st) => {
        if (st.currentRules) cachedRules = st.currentRules;
        cachedUser = st.vagsUser || "";
        cachedToken = st.vagsToken || "";
        cachedMemberType = st.vagsMemberType || "";
        const isLoggedIn = !!(cachedUser && cachedToken);

        if (isLoggedIn) {
            chrome.storage.local.set({ proxyEnabled: false }, () => {
                disableProxy(true);
                fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                    method: "POST",
                    headers: buildAuthHeaders(cachedToken),
                    body: JSON.stringify({ user: cachedUser, proxyEnabled: false })
                }).catch(() => {});
                if (typeof callback === "function") callback();
            });
        } else {
            if (typeof chrome !== "undefined" && chrome.proxy && chrome.proxy.settings) {
                chrome.proxy.settings.clear({ scope: "regular" }, () => {
                    chrome.proxy.settings.set({ value: { mode: "direct" }, scope: "regular" }, () => {});
                });
            }
            updateExtensionActionUI(false, false);
            if (typeof callback === "function") callback();
        }
    });
}

function ensureBrowserStartupProxyReset(reasonTag, onReady) {
    if (startupEnforcementPromise) {
        startupEnforcementPromise.then(() => {
            if (typeof onReady === "function") onReady(false);
        });
        return;
    }

    const sessApi = (typeof chrome !== "undefined" && chrome.storage && chrome.storage.session) ? chrome.storage.session : null;
    startupEnforcementPromise = new Promise((resolve) => {
        if (!sessApi) {
            if (reasonTag === "onStartup") {
                enforceBrowserStartupProxyOff(() => resolve(true));
            } else {
                resolve(false);
            }
            return;
        }
        sessApi.get(["vagsBrowserSessionInit"], (sess) => {
            if (sess && sess.vagsBrowserSessionInit === true) {
                resolve(false);
            } else {
                sessApi.set({ vagsBrowserSessionInit: true }, () => {
                    enforceBrowserStartupProxyOff(() => resolve(true));
                });
            }
        });
    });

    startupEnforcementPromise.then((didForceOff) => {
        if (typeof onReady === "function") onReady(didForceOff);
    });
}

// 6. Tự động kiểm tra cập nhật khi trình duyệt mở lại hoặc Extension cài đặt
if (typeof chrome !== "undefined" && chrome.runtime) {
    chrome.runtime.onStartup.addListener(() => {
        console.log("[VAGS Chrome] Trình duyệt mở mới -> Đưa Proxy về OFF (DIRECT) nhưng giữ đăng nhập VNU SSO");
        const sessApi = (chrome.storage && chrome.storage.session) ? chrome.storage.session : null;
        if (sessApi) {
            sessApi.set({ vagsBrowserSessionInit: true });
        }
        startupEnforcementPromise = new Promise((resolve) => {
            enforceBrowserStartupProxyOff(() => resolve(true));
        });
        startupEnforcementPromise.then(() => {
            checkVersionOnStartup();
            connectSSE();
        });
    });
    chrome.runtime.onInstalled.addListener(() => {
        console.log("[VAGS Chrome] Extension cài đặt/cập nhật -> Đảm bảo DIRECT nếu chưa đăng nhập");
        chrome.storage.local.get(["vagsUser", "vagsToken", "proxyEnabled"], (st) => {
            if (!st.vagsUser || !st.vagsToken || st.proxyEnabled !== true) {
                disableProxy();
            }
        });
        checkVersionOnStartup();
        connectSSE();
    });
}

// 7. Hỗ trợ Google Workspace Managed Policy (Chính sách Quản trị Google Admin)
if (typeof chrome !== "undefined" && chrome.storage && chrome.storage.managed) {
    chrome.storage.managed.get(null, (managedPolicy) => {
        if (managedPolicy && Object.keys(managedPolicy).length > 0) {
            console.log("[VAGS Chrome] Nhận chính sách Google Workspace Enterprise:", managedPolicy);
        }
    });
    chrome.storage.onChanged.addListener((changes, area) => {
        if (area === "managed") {
            console.log("[VAGS Chrome] Chính sách Google Admin thay đổi -> Cập nhật cấu hình ngay:", changes);
            checkVersionOnStartup();
        }
    });
}

// 8. Tự động kiểm tra bản cập nhật mới từ Google Workspace XML manifest
function forceCheckExtensionUpdate() {
    if (typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.requestUpdateCheck) {
        chrome.runtime.requestUpdateCheck((status, details) => {
            console.log("[VAGS Chrome] Trạng thái kiểm tra cập nhật:", status, details);
            if (status === "update_available") {
                console.log("[VAGS Chrome] Phát hiện bản nâng cấp mới v" + (details ? details.version : "") + ", đang nạp...");
                if (chrome.runtime.reload) chrome.runtime.reload();
            }
        });
    }
}

// Kiểm tra cập nhật định kỳ mỗi 60 phút qua chrome.alarms
if (typeof chrome !== "undefined" && chrome.alarms) {
    chrome.alarms.create("VAGS_UPDATE_ALARM", { periodInMinutes: 60 });
    chrome.alarms.onAlarm.addListener((alarm) => {
        if (alarm && alarm.name === "VAGS_UPDATE_ALARM") {
            forceCheckExtensionUpdate();
        }
    });
}

async function sendHeartbeat() {
    if (!cachedUser) return;
    try {
        const res = await fetch(`${VAGS_BASE_URL}/api/v1/user/heartbeat`, {
            method: 'POST',
            headers: buildAuthHeaders(),
            body: JSON.stringify({ user: cachedUser, unit: 'VNU', memberType: cachedMemberType || 'canbo', proxyEnabled: cachedProxyEnabled })
        });
        if (!res.ok) return;
        const data = await res.json().catch(() => null);
        if (data && data.forceProxyOff === true) {
            handleForceProxyOff({ user: cachedUser, ip: data.clientIp, reason: data.reason });
        }
    } catch (e) {}
}

// Khởi chạy an toàn: Đặt DIRECT khi mở trình duyệt mới; giữ trạng thái đang bật nếu Worker tỉnh lại giữa phiên
ensureBrowserStartupProxyReset("boot", () => {
    chrome.storage.local.get(["currentRules", "vagsUser", "vagsToken", "vagsMemberType", "proxyEnabled"], (storage) => {
        if (storage.currentRules) cachedRules = storage.currentRules;
        if (storage.vagsUser) cachedUser = storage.vagsUser;
        if (storage.vagsToken) cachedToken = storage.vagsToken;
        if (storage.vagsMemberType) cachedMemberType = storage.vagsMemberType;
        if (storage.proxyEnabled !== undefined) cachedProxyEnabled = storage.proxyEnabled === true;

        const isLoggedIn = !!(storage.vagsUser && storage.vagsToken);
        if (!isLoggedIn || storage.proxyEnabled !== true) {
            disableProxy();
        }

        checkVersionOnStartup();
        connectSSE();
        sendHeartbeat();
        forceCheckExtensionUpdate();
    });
});

// Gui Heartbeat dinh ky de duy tri session on dinh, cap nhat IP client va bang thong tren SQLite
setInterval(() => {
    sendHeartbeat();
}, 120000);
