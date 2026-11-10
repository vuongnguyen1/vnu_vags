// VNU Academic Gateway (VAGS) - Background Engine (Manifest V3)
// Đồng bộ Real-time qua Server-Sent Events (SSE) & Cấu hình PAC HTTPS vags.vnu.edu.vn:443

const VAGS_BASE_URL = "https://vags.vnu.edu.vn";
let sseSource = null;
let reconnectTimer = null;

// 1. Kết nối Server-Sent Events (SSE) Real-time với Backend vags.vnu.edu.vn
function connectSSE() {
    if (sseSource) {
        try { sseSource.close(); } catch (e) {}
    }

    sseSource = new EventSource(`${VAGS_BASE_URL}/api/v1/rules/stream`);

    sseSource.onmessage = (event) => {
        try {
            const msg = JSON.parse(event.data);
            if (msg.type === "CONNECTED") {
                chrome.storage.local.set({
                    sseConnected: true,
                    lastConnectedAt: new Date().toISOString()
                });
            }
        } catch (e) {}
    };

    sseSource.addEventListener("RULES_UPDATED", (event) => {
        try {
            const newRules = JSON.parse(event.data);
            applyProxyRules(newRules);
        } catch (e) {
            console.error("[VAGS] Failed to parse RULES_UPDATED payload:", e);
        }
    });

    sseSource.onerror = () => {
        chrome.storage.local.set({ sseConnected: false });
        try { sseSource.close(); } catch (e) {}
        clearTimeout(reconnectTimer);
        reconnectTimer = setTimeout(connectSSE, 5000); // Tự động kết nối lại sau 5s nếu rớt mạng
    };
}

// 2. Cập nhật PAC Script vào Chrome Proxy API (Chạy qua cổng 443 HTTPS)
function applyProxyRules(rules) {
    const proxyScheme = rules.proxyScheme || "HTTPS";
    const proxyHost = rules.proxyHost || "vags.vnu.edu.vn";
    const proxyPort = rules.proxyPort || 443;
    const proxyServer = `${proxyScheme} ${proxyHost}:${proxyPort}`;

    const pacScript = `
        function FindProxyForURL(url, host) {
            var lHost = host.toLowerCase();

            // 1. Thư viện nội bộ ĐHQGHN (lic.vnu.edu.vn) -> Ưu tiên kiểm tra trước *.vnu.edu.vn
            var internalWL = ${JSON.stringify(rules.internalWhitelist || [])};
            for (var j = 0; j < internalWL.length; j++) {
                if (shExpMatch(lHost, internalWL[j])) return "${proxyServer}";
            }

            // 2. Blacklist giải trí & Cổng thông tin nội bộ *.vnu.edu.vn -> DIRECT
            var blacklist = ${JSON.stringify(rules.blacklist || [])};
            for (var i = 0; i < blacklist.length; i++) {
                if (shExpMatch(lHost, blacklist[i])) return "DIRECT";
            }

            // 3. CSDL & Tạp chí Khoa học Quốc tế -> PROXY qua cổng 443
            var academicWL = ${JSON.stringify(rules.globalAcademicWhitelist || [])};
            for (var k = 0; k < academicWL.length; k++) {
                if (shExpMatch(lHost, academicWL[k])) return "${proxyServer}";
            }

            return "DIRECT";
        }
    `;

    chrome.proxy.settings.set({
        value: { mode: "pac_script", pacScript: { data: pacScript } },
        scope: "regular"
    }, () => {
        chrome.storage.local.set({
            currentRules: rules,
            ruleVersion: rules.version,
            proxyEndpoint: proxyServer,
            lastSyncedAt: new Date().toISOString()
        });
    });
}

// 3. Startup Catch-up Check Version
async function checkVersionOnStartup() {
    try {
        let res = await fetch(`${VAGS_BASE_URL}/api/v1/rules/latest`, { cache: "no-store" });
        if (res.ok) {
            let rules = await res.json();
            applyProxyRules(rules);
        }
    } catch (e) {
        console.warn("[VAGS] Initial sync failed:", e);
    }
}

// Lắng nghe lệnh từ Popup (Force Sync / SSO Login / Toggle Proxy)
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.action === "SYNC_NOW") {
        checkVersionOnStartup().then(() => sendResponse({ ok: true }));
        return true;
    }
    if (msg.action === "RECONNECT_SSE") {
        connectSSE();
        sendResponse({ ok: true });
        return true;
    }
});

chrome.runtime.onStartup.addListener(() => {
    checkVersionOnStartup();
    connectSSE();
});

chrome.runtime.onInstalled.addListener(() => {
    checkVersionOnStartup();
    connectSSE();
    chrome.alarms.create("vagsKeepAlive", { periodInMinutes: 1 });
});

chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === "vagsKeepAlive") {
        if (!sseSource || sseSource.readyState === 2) {
            connectSSE();
        }
    }
});

checkVersionOnStartup();
connectSSE();

