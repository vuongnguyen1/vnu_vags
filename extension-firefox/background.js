// VNU Academic Gateway (VAGS) - Background Engine (Mozilla Firefox MV3)
// Dung native WebExtension API: browser.proxy.onRequest de dinh tuyen 100% truc tiep vao Firefox Network Stack

const VAGS_BASE_URL = "https://thamkhao.vnu.edu.vn";
let sseSource = null;
let reconnectTimer = null;

let cachedRules = null;
let cachedUser = "";
let cachedToken = "";
let cachedMemberType = "";
let cachedProxyEnabled = false; // Mặc định tắt khi chưa đăng nhập VNU SSO

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

function buildAuthHeaders(tokenOverride) {
    const headers = { "Content-Type": "application/json" };
    const tok = tokenOverride || cachedToken;
    if (tok) {
        headers["Authorization"] = `Bearer ${tok}`;
    }
    return headers;
}

function updateExtensionActionUI(isEnabled, isLoggedIn) {
    const act = (typeof browser !== "undefined" && browser.browserAction) ? browser.browserAction : ((typeof chrome !== "undefined" && chrome.action) ? chrome.action : (typeof chrome !== "undefined" && chrome.browserAction ? chrome.browserAction : null));
    if (!act) return;

    if (isLoggedIn && isEnabled) {
        if (act.setIcon) {
            act.setIcon({
                path: {
                    "16": "icon-16.png",
                    "48": "icon-48.png",
                    "128": "icon-128.png"
                }
            });
        }
        if (act.setBadgeText) act.setBadgeText({ text: "ON" });
        if (act.setBadgeBackgroundColor) act.setBadgeBackgroundColor({ color: "#00803d" });
        if (act.setTitle) act.setTitle({ title: "VAGS: Đang Bật Proxy CSDL (Mạng ĐHQGHN)" });
    } else {
        if (act.setIcon) {
            act.setIcon({
                path: {
                    "16": "icon-16-off.png",
                    "48": "icon-48-off.png",
                    "128": "icon-128-off.png"
                }
            });
        }
        if (act.setBadgeText) act.setBadgeText({ text: !isLoggedIn ? "LOCK" : "OFF" });
        if (act.setBadgeBackgroundColor) act.setBadgeBackgroundColor({ color: !isLoggedIn ? "#64748b" : "#ef4444" });
        if (act.setTitle) {
            act.setTitle({
                title: !isLoggedIn 
                    ? "VAGS: Chưa đăng nhập VNU SSO (Bấm để đăng nhập)" 
                    : "VAGS: Đã Tắt Proxy (Đi trực tiếp DIRECT)"
            });
        }
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

// 1. Dinh tuyen Proxy Native tren Firefox bang browser.proxy.onRequest
if (typeof browser !== "undefined" && browser.proxy && browser.proxy.onRequest) {
    browser.proxy.onRequest.addListener(
        (requestInfo) => {
            // 0. BẮT BUỘC ĐĂNG NHẬP VNU SSO: Chưa đăng nhập -> 100% DIRECT (Không qua proxy)
            if (!cachedUser || !cachedToken) {
                return { type: "direct" };
            }

            // 1. Phân quyền: Cán bộ (canbo) là đối tượng được phép, Sinh viên (sinhvien) bị LOẠI BỎ
            const isStudent = cachedMemberType === "sinhvien";
            const allowedUsers = cachedRules?.userOverrides?.allowed || [];
            const deniedUsers = cachedRules?.userOverrides?.denied || [];
            const isOverrideUser = cachedUser && allowedUsers.includes(cachedUser);
            const isDeniedUser = cachedUser && deniedUsers.map(u => (u || '').toLowerCase()).includes(cachedUser.toLowerCase());
            if (isDeniedUser || (isStudent && !isOverrideUser)) {
                return { type: "direct" };
            }

            // 2. Nếu người dùng tắt công tắc Proxy -> DIRECT
            if (!cachedProxyEnabled) {
                return { type: "direct" };
            }

            let targetUrl;
            try {
                targetUrl = new URL(requestInfo.url);
            } catch (e) {
                return { type: "direct" };
            }
            const host = normalizeHost(targetUrl.hostname);

            // 0.0. Loopback, Docker, Reserved CIDR & Mạng nội bộ / VAGS Self-Host -> LUÔN 100% DIRECT (Fail-safe, không qua proxy)
            if (isInternalOrReservedHost(host) || isVagsSelfHost(host)) {
                return { type: "direct" };
            }

            // Google Workspace & Dịch vụ xác thực: Luôn kết nối trực tiếp (DIRECT)
            if (isCloudBypassHost(host)) {
                return { type: "direct" };
            }

            // 1. Blacklist (Băng thông cao: YouTube, Facebook, TikTok...) -> Luon DIRECT
            const blacklist = cachedRules?.blacklist || [];
            for (const b of blacklist) {
                if (shExpMatch(host, b)) return { type: "direct" };
            }

            // 2. Ngoại lệ Cán bộ riêng biệt
            const currentU = cachedUser || "";
            for (const ud of getUserSpecificDomains(cachedRules, currentU)) {
                if (shExpMatch(host, ud)) {
                    return { type: "https", host: "thamkhao.vnu.edu.vn", port: 443 };
                }
            }

            // 3. Thư viện số VNU LIC & Công cụ nhận diện IP (chỉ áp dụng khi enableLicProxy !== false)
            if (cachedRules?.enableLicProxy !== false) {
                const internalWL = cachedRules?.internalWhitelist || [];
                for (const iw of internalWL) {
                    if (shExpMatch(host, iw)) {
                        return { type: "https", host: "thamkhao.vnu.edu.vn", port: 443 };
                    }
                }
            }

            // 4. Mạng nội bộ *.vnu.edu.vn -> DIRECT
            if (shExpMatch(host, "*.vnu.edu.vn") || host === "vnu.edu.vn") {
                return { type: "direct" };
            }

            // 5. CSDL Khoa học Quốc tế (ScienceDirect, IEEE, Springer, Nature, Wiley...)
            const academicWL = cachedRules?.globalAcademicWhitelist || [];
            for (const aw of academicWL) {
                if (shExpMatch(host, aw)) {
                    return { type: "https", host: "thamkhao.vnu.edu.vn", port: 443 };
                }
            }

            // 6. Chế độ Định tuyến Toàn phần (Full Proxy Mode)
            if ((cachedRules?.proxyMode || "FULL_PROXY") === "FULL_PROXY") {
                return { type: "https", host: "thamkhao.vnu.edu.vn", port: 443 };
            }

            return { type: "direct" };
        },
        { urls: ["<all_urls>"] }
    );

    if (browser.proxy.onError) {
        browser.proxy.onError.addListener((err) => {
            console.error("[VAGS Firefox Proxy Error]", err);
        });
    }
}

// 2. Ket noi SSE Real-time tren Firefox
function connectSSE() {
    if (typeof EventSource === "undefined") return;
    try {
        if (sseSource) { try { sseSource.close(); } catch (e) {} }
        const queryParams = new URLSearchParams();
        if (cachedUser) queryParams.set("user", cachedUser);
        queryParams.set("proxyEnabled", cachedProxyEnabled ? "true" : "false");
        const userParam = "?" + queryParams.toString();
        sseSource = new EventSource(`${VAGS_BASE_URL}/api/v1/rules/stream${userParam}`);

        sseSource.onopen = () => {
            chrome.storage.local.set({ sseConnected: true, lastConnectedAt: new Date().toISOString() });
        };

        sseSource.onmessage = (event) => {
            try {
                const msg = JSON.parse(event.data);
                if (msg.type === "CONNECTED") {
                    chrome.storage.local.set({ sseConnected: true });
                }
            } catch (e) {}
        };

        sseSource.addEventListener("RULES_UPDATED", (event) => {
            try {
                const newRules = JSON.parse(event.data);
                cachedRules = newRules;
                chrome.storage.local.set({ currentRules: newRules, ruleVersion: newRules.version });
                updateLocalCache();
            } catch (e) {
                console.error("[VAGS Firefox] Failed to parse RULES_UPDATED:", e);
            }
        });

        // 3. Lang nghe lenh EP CAP NHAT TU XA (Remote Force Update)
        sseSource.addEventListener("FORCE_UPDATE", (event) => {
            try {
                const payload = JSON.parse(event.data || "{}");
                console.log("[VAGS Firefox] Nhan tin hieu FORCE_UPDATE tu May chu:", payload);
                if (typeof browser !== "undefined" && browser.runtime && browser.runtime.reload) {
                    browser.runtime.reload();
                }
            } catch (e) {
                console.error("[VAGS Firefox] Loi xu ly FORCE_UPDATE:", e);
            }
        });

        // 4. Lắng nghe lệnh FORCE_PROXY_OFF
        sseSource.addEventListener("FORCE_PROXY_OFF", (event) => {
            try {
                const payload = JSON.parse(event.data || "{}");
                handleForceProxyOff(payload);
            } catch (e) {
                console.error("[VAGS Firefox] Loi xu ly FORCE_PROXY_OFF:", e);
            }
        });

        // 5. Lắng nghe lệnh FORCE_LOGOUT
        sseSource.addEventListener("FORCE_LOGOUT", (event) => {
            try {
                const payload = JSON.parse(event.data || "{}");
                handleForceLogout(payload);
            } catch (e) {
                console.error("[VAGS Firefox] Loi xu ly FORCE_LOGOUT:", e);
            }
        });

        // 6. Lắng nghe lệnh FORCE_LOCK
        sseSource.addEventListener("FORCE_LOCK", (event) => {
            try {
                const payload = JSON.parse(event.data || "{}");
                handleForceLock(payload);
            } catch (e) {
                console.error("[VAGS Firefox] Loi xu ly FORCE_LOCK:", e);
            }
        });

        sseSource.onerror = () => {
            chrome.storage.local.set({ sseConnected: false });
            try { sseSource.close(); } catch (e) {}
            clearTimeout(reconnectTimer);
            reconnectTimer = setTimeout(connectSSE, 5000);
        };
    } catch (e) {
        chrome.storage.local.set({ sseConnected: false });
    }
}

function handleForceProxyOff(payload) {
    chrome.storage.local.get(["vagsUser"], (storage) => {
        const currentUser = (storage.vagsUser || cachedUser || "").trim().toLowerCase();
        const targetUser = (payload.user || "").trim().toLowerCase();
        const isMatch = payload.all === true || (targetUser && targetUser === currentUser);

        if (isMatch) {
            console.warn("[VAGS Firefox] ⚡ Nhận lệnh ép TẮT PROXY từ Quản trị viên!");
            cachedProxyEnabled = false;
            chrome.storage.local.set({ proxyEnabled: false }, () => {
                updateLocalCache();
                if (sseSource) { try { sseSource.close(); } catch (e) {} }
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
            console.warn("[VAGS Firefox] 🚪 Nhận lệnh ép ĐĂNG XUẤT từ Quản trị viên!");
            if (sseSource) { try { sseSource.close(); } catch (e) {} }
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
            console.warn("[VAGS Firefox] 🔒 Tài khoản đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS!");
            if (sseSource) { try { sseSource.close(); } catch (e) {} }
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
                connectSSE();

                chrome.tabs.query({}, (tabs) => {
                    (tabs || []).forEach(t => {
                        chrome.tabs.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE", type: "FORCE_LOGOUT_PAGE", reason: "Tài khoản của bạn đã bị Quản trị viên khoá quyền sử dụng Proxy VAGS." }).catch(() => {});
                    });
                });
            });
        }
    });
}

async function checkVersionOnStartup() {
    try {
        let res = await fetch(`${VAGS_BASE_URL}/api/v1/rules/latest`, { cache: "no-store" });
        if (res.ok) {
            let rules = await res.json();
            cachedRules = rules;
            chrome.storage.local.set({ currentRules: rules, ruleVersion: rules.version });
            updateLocalCache();
        }
    } catch (e) {
        console.warn("[VAGS Firefox] Sync rules error:", e);
    }
}

// 3. Lang nghe Messages tu Popup & Content Script
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.action === "SYNC_SESSION") {
        const d = msg.data || {};
        cachedUser = d.vagsUser || "";
        cachedToken = d.vagsToken || "";
        cachedMemberType = d.vagsMemberType || "canbo";

        // TÔN TRỌNG TUYỆT ĐỐI Ý CHÍ NGƯỜI DÙNG:
        // Đọc proxyEnabled trong storage. Nếu người dùng đã bấm Tắt, reload trang không tự động bật lại!
        chrome.storage.local.get(["proxyEnabled", "currentRules"], (st) => {
            const deniedUsers = (st.currentRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
            const isUserDenied = (d.vagsUser && deniedUsers.includes(d.vagsUser.toLowerCase()));
            let shouldEnable = st.proxyEnabled;
            if (isUserDenied) {
                shouldEnable = false;
            } else if (shouldEnable === undefined) {
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
                checkVersionOnStartup();
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

        if (sseSource) { try { sseSource.close(); } catch (e) {} }

        // Gọi hủy đăng ký IP trên máy chủ VAGS Backend
        fetch(`${VAGS_BASE_URL}/api/v1/auth/logout`, { method: "POST" }).catch(() => {});

        chrome.storage.local.remove(["vagsToken", "vagsUser", "vagsName", "vagsUnit", "vagsMemberType", "vagsIsAdmin", "proxyEnabled"], () => {
            updateExtensionActionUI(false, false);
            connectSSE();

            // Đồng bộ lập tức tới các tab
            const tabsApi = (typeof browser !== "undefined" && browser.tabs) ? browser.tabs : ((typeof chrome !== "undefined" && chrome.tabs) ? chrome.tabs : null);
            if (tabsApi && tabsApi.query) {
                tabsApi.query({}, (tabs) => {
                    if (tabs && tabs.length > 0) {
                        tabs.forEach(t => {
                            try { tabsApi.sendMessage(t.id, { action: "FORCE_LOGOUT_PAGE" }); } catch (e) {}
                        });
                    }
                });
            }
            sendResponse({ ok: true });
        });
        return true;
    }

    if (msg.action === "TOGGLE_PROXY") {
        if (!cachedUser || !cachedToken) {
            cachedProxyEnabled = false;
            chrome.storage.local.set({ proxyEnabled: false });
            updateExtensionActionUI(false, false);
            sendResponse({ ok: false, error: "Yêu cầu đăng nhập VNU SSO trước khi bật Proxy" });
            return true;
        }
        const deniedUsers = (cachedRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
        if (cachedUser && deniedUsers.includes(cachedUser.toLowerCase())) {
            cachedProxyEnabled = false;
            chrome.storage.local.set({ proxyEnabled: false });
            updateExtensionActionUI(false, true);
            sendResponse({ ok: false, error: "Tài khoản của bạn đã bị Quản trị viên KHOÁ quyền sử dụng Proxy VAGS" });
            return true;
        }
        if (cachedMemberType === "sinhvien" && !(cachedRules?.userOverrides?.allowed || []).includes(cachedUser)) {
            cachedProxyEnabled = false;
            chrome.storage.local.set({ proxyEnabled: false });
            updateExtensionActionUI(false, true);
            sendResponse({ ok: false, error: "Tài khoản Sinh viên không được phép bật Proxy" });
            return true;
        }
        cachedProxyEnabled = !!msg.enabled;
        chrome.storage.local.set({ proxyEnabled: cachedProxyEnabled }, () => {
            updateExtensionActionUI(cachedProxyEnabled, true);
            fetch(`${VAGS_BASE_URL}/api/v1/user/status`, {
                method: 'POST',
                headers: buildAuthHeaders(),
                body: JSON.stringify({ user: cachedUser, proxyEnabled: cachedProxyEnabled })
            }).catch(() => {});
            sendResponse({ ok: true, enabled: cachedProxyEnabled });
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
            version: (chrome.runtime.getManifest && chrome.runtime.getManifest().version) || "2.1.4"
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
                sendResponse({ enabled: true, isLoggedIn: true, action: "DIRECT", category: "blacklist", label: "Băng thông cao (Blacklist)", reason: "Không qua Proxy để bảo toàn băng thông" });
                return true;
            }
        }

        // 2. User Specific
        for (const ud of getUserSpecificDomains(cachedRules, cachedUser)) {
            if (shExpMatch(host, ud)) {
                sendResponse({ enabled: true, isLoggedIn: true, action: "PROXY", category: "userOverride", label: `Ngoại lệ (${cachedUser})`, reason: `Cấp quyền định tuyến riêng cho ${cachedUser}` });
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

// 4. TỰ ĐỘNG ĐỒNG BỘ ĐỊNH KỲ 2 TIẾNG CHO TOÀN BỘ CÁN BỘ (Mozilla Firefox)
if (typeof browser !== "undefined" && browser.alarms) {
    browser.alarms.create("VAGS_2HR_AUTO_SYNC", {
        delayInMinutes: 2,
        periodInMinutes: 120 // 120 phút = 2 tiếng đồng bộ tự động một lần
    });

    browser.alarms.onAlarm.addListener((alarm) => {
        if (alarm.name === "VAGS_2HR_AUTO_SYNC") {
            console.log("[VAGS Firefox] ⏰ Chu kỳ 2 tiếng: Tự động tải và áp dụng rule mới từ máy chủ...");
            checkVersionOnStartup();
        }
    });
}

// 5. Tự động kiểm tra cập nhật khi trình duyệt khởi động
if (typeof browser !== "undefined" && browser.runtime && browser.runtime.onStartup) {
    browser.runtime.onStartup.addListener(() => {
        checkVersionOnStartup();
        connectSSE();
    });
}

checkVersionOnStartup();
connectSSE();

// Gửi Heartbeat định kỳ duy trì session và cập nhật IP client trên SQLite
setInterval(() => {
    if (cachedUser) {
        fetch(`${VAGS_BASE_URL}/api/v1/user/heartbeat`, {
            method: 'POST',
            headers: buildAuthHeaders(),
            body: JSON.stringify({ user: cachedUser, unit: 'VNU', memberType: cachedMemberType || 'canbo', proxyEnabled: cachedProxyEnabled })
        }).catch(() => {});
    }
}, 120000);
