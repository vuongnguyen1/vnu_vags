// VNU Academic Gateway (VAGS) - Background Engine (Google Chrome & Microsoft Edge MV3)
const VAGS_BASE_URL = "https://vags.vnu.edu.vn";
let sseSource = null;
let reconnectTimer = null;
let sseAbortController = null;

let cachedRules = null;
let cachedUser = "";
let cachedToken = "";
let cachedMemberType = "";
let cachedProxyEnabled = false;

function shExpMatch(str, pattern) {
    if (!pattern) return false;
    const p = pattern.trim().toLowerCase();
    const s = (str || "").trim().toLowerCase();
    if (p === s) return true;
    if (p.startsWith("*.")) {
        const domain = p.substring(2);
        return s === domain || s.endsWith("." + domain);
    }
    return false;
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
    chrome.storage.local.get(["currentRules", "vagsUser", "vagsToken", "vagsMemberType", "proxyEnabled"], (storage) => {
        if (storage.currentRules) cachedRules = storage.currentRules;
        if (storage.vagsUser !== undefined) cachedUser = storage.vagsUser;
        if (storage.vagsToken !== undefined) cachedToken = storage.vagsToken;
        if (storage.vagsMemberType !== undefined) cachedMemberType = storage.vagsMemberType;
        const isLoggedIn = !!(storage.vagsUser && storage.vagsToken);
        const isStudent = storage.vagsMemberType === "sinhvien";
        const allowedUsers = storage.currentRules?.userOverrides?.allowed || [];
        const isOverride = storage.vagsUser && allowedUsers.includes(storage.vagsUser);

        if (!isLoggedIn || (isStudent && !isOverride)) {
            cachedProxyEnabled = false;
        } else {
            cachedProxyEnabled = storage.proxyEnabled === true;
        }
        updateExtensionActionUI(cachedProxyEnabled, isLoggedIn);
    });
}
updateLocalCache();

// 1. Ket noi SSE Real-time bang Fetch Streaming
async function connectSSE() {
    if (sseAbortController) {
        try { sseAbortController.abort(); } catch (e) {}
    }
    sseAbortController = new AbortController();

    try {
        const queryParams = new URLSearchParams();
        if (cachedUser) queryParams.set("user", cachedUser);
        queryParams.set("proxyEnabled", cachedProxyEnabled ? "true" : "false");
        const userParam = "?" + queryParams.toString();
        const response = await fetch(`${VAGS_BASE_URL}/api/v1/rules/stream${userParam}`, {
            headers: { "Accept": "text/event-stream" },
            cache: "no-store",
            signal: sseAbortController.signal
        });

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
                let eventData = "";
                for (const line of block.split("\n")) {
                    if (line.startsWith("event:")) eventType = line.slice(6).trim();
                    else if (line.startsWith("data:")) eventData = line.slice(5).trim();
                }

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
        if (err.name !== "AbortError") {
            console.warn("[VAGS Chrome] SSE error:", err);
        }
    }

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
            console.warn("[VAGS Chrome] ⚡ Nhận lệnh ép TẮT PROXY từ Quản trị viên!");
            cachedProxyEnabled = false;
            chrome.storage.local.set({ proxyEnabled: false }, () => {
                disableProxy(!!currentUser);
                updateLocalCache();
                if (sseAbortController) {
                    try { sseAbortController.abort(); } catch (e) {}
                }
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

    // Danh sach bo qua proxy (Bypass List)
    // QUAN TRỌNG: Máy chủ VAGS & IP cổng & Địa chỉ Loopback, Docker, Mạng nội bộ PHẢI LUÔN trong bypassList để không bao giờ bị lỗi hoặc rò rỉ tunnel
    let bypassList = [
        "<local>",
        "vags.vnu.edu.vn",
        "*.vags.vnu.edu.vn",
        "112.137.133.14",
        "localhost",
        "127.0.0.1",
        "::1",
        "10.*",
        "172.16.*", "172.17.*", "172.18.*", "172.19.*",
        "172.2*.*", "172.30.*", "172.31.*",
        "192.168.*",
        "*.docker.internal",
        "host.docker.internal",
        "*.local",
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
    if (rules.blacklist) bypassList = bypassList.concat(rules.blacklist);

    // Mạng nội bộ vnu: Nếu người dùng có đặc quyền cấu hình proxy thì áp dụng, các trang vnu khác bypass
    const userOverrides = (rules.userOverrides?.userSpecificRules?.[username]?.proxyDomains) || [];
    if (!userOverrides.includes("idp.vnu.edu.vn")) {
        bypassList.push("*.vnu.edu.vn");
    }

    if (isFullProxy) {
        // Che do Full Proxy: Dung fixed_servers truc tiep khong thong qua Javascript PAC isolate
        chrome.proxy.settings.set({
            value: {
                mode: "fixed_servers",
                rules: {
                    singleProxy: {
                        scheme: "https",
                        host: "vags.vnu.edu.vn",
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
        proxyEndpoint: "HTTPS vags.vnu.edu.vn:443",
        proxyEnabled: true,
        lastSyncedAt: new Date().toISOString()
    });
    updateExtensionActionUI(true, true);
}

function applyPacScript(rules, username) {
    let userSpecificProxy = [];
    if (username && rules.userOverrides?.userSpecificRules?.[username]?.proxyDomains) {
        userSpecificProxy = rules.userOverrides.userSpecificRules[username].proxyDomains;
    }

    const pacLines = [
        "function FindProxyForURL(url, host) {",
        "    var lHost = host.toLowerCase();",
        "    if (lHost === 'localhost' || lHost === '127.0.0.1' || lHost === '::1' || shExpMatch(lHost, '*.docker.internal') || lHost === 'host.docker.internal' || shExpMatch(lHost, '10.*') || shExpMatch(lHost, '172.16.*') || shExpMatch(lHost, '172.17.*') || shExpMatch(lHost, '172.18.*') || shExpMatch(lHost, '172.19.*') || shExpMatch(lHost, '172.2*.*') || shExpMatch(lHost, '172.30.*') || shExpMatch(lHost, '172.31.*') || shExpMatch(lHost, '192.168.*')) return 'DIRECT';",
        "    if (lHost === 'vags.vnu.edu.vn' || shExpMatch(lHost, '*.vags.vnu.edu.vn') || lHost === '112.137.133.14') return 'DIRECT';",
        "    if (shExpMatch(lHost, '*.google.com') || lHost === 'google.com' || shExpMatch(lHost, '*.googleapis.com') || shExpMatch(lHost, '*.gstatic.com') || shExpMatch(lHost, '*.googleusercontent.com')) return 'DIRECT';",
        "    var userWL = " + JSON.stringify(userSpecificProxy) + ";",
        "    for (var u = 0; u < userWL.length; u++) {",
        "        if (shExpMatch(lHost, userWL[u])) return 'HTTPS vags.vnu.edu.vn:443';",
        "    }",
        "    var blacklist = " + JSON.stringify(rules.blacklist || []) + ";",
        "    for (var i = 0; i < blacklist.length; i++) {",
        "        if (shExpMatch(lHost, blacklist[i])) return 'DIRECT';",
        "    }",
        "    var internalWL = " + JSON.stringify(rules.internalWhitelist || []) + ";",
        "    for (var j = 0; j < internalWL.length; j++) {",
        "        if (shExpMatch(lHost, internalWL[j])) return 'HTTPS vags.vnu.edu.vn:443';",
        "    }",
        "    if (shExpMatch(lHost, '*.vnu.edu.vn') || lHost === 'vnu.edu.vn') {",
        "        return 'DIRECT';",
        "    }",
        "    var academicWL = " + JSON.stringify(rules.globalAcademicWhitelist || []) + ";",
        "    for (var k = 0; k < academicWL.length; k++) {",
        "        if (shExpMatch(lHost, academicWL[k])) return 'HTTPS vags.vnu.edu.vn:443';",
        "    }",
        "    return 'DIRECT';",
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
                const isUserOverride = allowedUsers.includes(username);
                const isStudent = storage.vagsMemberType === "sinhvien";
                const isCanbo = (storage.vagsMemberType === "canbo" || !isStudent) && isLoggedIn;
                const isAllowed = (isCanbo || isUserOverride) && !isStudent;
                const isEnabled = storage.proxyEnabled === true;

                chrome.storage.local.set({ currentRules: rules, ruleVersion: rules.version });
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
        cachedMemberType = d.vagsMemberType || "canbo";

        // TÔN TRỌNG TUYỆT ĐỐI Ý CHÍ NGƯỜI DÙNG:
        // Đọc trạng thái proxyEnabled hiện tại trong storage. Nếu người dùng đã bấm Tắt (proxyEnabled === false),
        // thì reload trang TUYỆT ĐỐI KHÔNG ĐƯỢC TỰ BẬT LẠI!
        chrome.storage.local.get(["proxyEnabled", "currentRules"], (st) => {
            let shouldEnable = st.proxyEnabled;
            if (shouldEnable === undefined) {
                // Chỉ mặc định bật cho phiên đăng nhập lần đầu tiên
                shouldEnable = d.vagsMemberType === "canbo";
            }
            cachedProxyEnabled = shouldEnable === true;

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
                        headers: { 'Content-Type': 'application/json' },
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

            // Đồng bộ lập tức: Gửi thông báo đến tất cả các tab trang chủ ĐHQGHN để xóa sạch session
            if (chrome.tabs && chrome.tabs.query) {
                chrome.tabs.query({ url: "*://*.vnu.edu.vn/*" }, (tabs) => {
                    if (tabs && tabs.length > 0) {
                        tabs.forEach(t => {
                            try { chrome.tabs.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE" }); } catch (e) {}
                        });
                    }
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

            cachedProxyEnabled = !!msg.enabled;
            if (cachedProxyEnabled) {
                chrome.storage.local.set({ proxyEnabled: true }, () => {
                    if (storage.currentRules) applyProxyRules(storage.currentRules, storage.vagsUser);
                    else checkVersionOnStartup();
                    updateExtensionActionUI(true, true);
                    fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ user: storage.vagsUser, proxyEnabled: true })
                    }).catch(() => {});
                    sendResponse({ ok: true, enabled: true });
                });
            } else {
                chrome.storage.local.set({ proxyEnabled: false }, () => {
                    disableProxy(true);
                    updateExtensionActionUI(false, true);
                    fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
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
        const host = (msg.host || "").toLowerCase().trim();
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

        // 0. Loopback, Docker, Internal IP Bypass
        if (host === "localhost" || host === "127.0.0.1" || host === "::1" ||
            shExpMatch(host, "*.docker.internal") || host === "host.docker.internal" ||
            shExpMatch(host, "10.*") || shExpMatch(host, "172.16.*") || shExpMatch(host, "172.17.*") ||
            shExpMatch(host, "172.18.*") || shExpMatch(host, "172.19.*") || shExpMatch(host, "172.2*.*") ||
            shExpMatch(host, "172.30.*") || shExpMatch(host, "172.31.*") || shExpMatch(host, "192.168.*") ||
            host === "vags.vnu.edu.vn" || host.endsWith(".vags.vnu.edu.vn") || host === "112.137.133.14") {
            sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "internalBypass", label: "Địa chỉ nội bộ / Docker", reason: "Kết nối trực tiếp không qua proxy" });
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
        if (cachedUser && cachedRules?.userOverrides?.userSpecificRules) {
            const uConfig = cachedRules.userOverrides.userSpecificRules[cachedUser];
            for (const ud of (uConfig?.proxyDomains || [])) {
                if (shExpMatch(host, ud)) {
                    sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "userOverride", label: `Ngoại lệ (${cachedUser})`, reason: `Quyền định tuyến riêng cho ${cachedUser}` });
                    return true;
                }
            }
        }

        // 3. Internal Whitelist
        for (const iw of (cachedRules?.internalWhitelist || [])) {
            if (shExpMatch(host, iw)) {
                sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "lic", label: "Thư viện số LIC / Kiểm tra IP", reason: "Dịch vụ thư viện & công cụ nhận diện ĐHQGHN" });
                return true;
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

// 6. Tự động kiểm tra cập nhật khi trình duyệt mở lại hoặc Extension cài đặt
if (typeof chrome !== "undefined" && chrome.runtime) {
    chrome.runtime.onStartup.addListener(() => {
        console.log("[VAGS Chrome] Trình duyệt khởi động -> Tự động nạp rule mới");
        chrome.storage.local.get(["vagsUser", "vagsToken", "proxyEnabled"], (st) => {
            if (!st.vagsUser || !st.vagsToken || st.proxyEnabled !== true) {
                disableProxy();
            }
        });
        checkVersionOnStartup();
        connectSSE();
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

function sendHeartbeat() {
    if (cachedUser) {
        fetch(`${VAGS_BASE_URL}/api/v1/user/heartbeat`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ user: cachedUser, unit: 'VNU', memberType: cachedMemberType || 'canbo', proxyEnabled: cachedProxyEnabled })
        }).catch(() => {});
    }
}

// Khởi chạy an toàn: Nạp storage trước rồi mới kết nối SSE và gửi heartbeat
chrome.storage.local.get(["currentRules", "vagsUser", "vagsToken", "vagsMemberType", "proxyEnabled"], (storage) => {
    if (storage.currentRules) cachedRules = storage.currentRules;
    if (storage.vagsUser) cachedUser = storage.vagsUser;
    if (storage.vagsToken) cachedToken = storage.vagsToken;
    if (storage.vagsMemberType) cachedMemberType = storage.vagsMemberType;
    if (storage.proxyEnabled !== undefined) cachedProxyEnabled = storage.proxyEnabled === true;

    // QUAN TRỌNG: Nếu chưa đăng nhập VNU SSO hoặc proxy chưa bật -> Ép mode: direct ngay lập tức
    const isLoggedIn = !!(storage.vagsUser && storage.vagsToken);
    if (!isLoggedIn || storage.proxyEnabled !== true) {
        disableProxy();
    }

    checkVersionOnStartup();
    connectSSE();
    sendHeartbeat();
    forceCheckExtensionUpdate();
});

// Gui Heartbeat dinh ky de duy tri session on dinh, cap nhat IP client va bang thong tren SQLite
setInterval(() => {
    sendHeartbeat();
}, 120000);
