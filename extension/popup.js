const VAGS_BASE_URL = "https://vags.vnu.edu.vn";

function renderState() {
    chrome.storage.local.get(["sseConnected", "ruleVersion", "currentRules", "vagsUser", "vagsToken"], (data) => {
        const badge = document.getElementById("sseStatus");
        if (data.sseConnected) {
            badge.textContent = "SSE Live";
            badge.className = "badge";
        } else {
            badge.textContent = "Reconnecting";
            badge.className = "badge offline";
        }

        document.getElementById("ruleVersion").textContent = data.ruleVersion || "Chưa đồng bộ";
        const rules = data.currentRules || {};
        document.getElementById("authMode").textContent = (rules.authMode || "demo").toUpperCase();
        const total = (rules.globalAcademicWhitelist?.length || 0) + (rules.internalWhitelist?.length || 0);
        document.getElementById("domainCount").textContent = `${total} tên miền`;
        document.getElementById("userEmail").textContent = data.vagsUser || "Khách (Demo Mode)";
    });
}

document.getElementById("btnSync").addEventListener("click", () => {
    document.getElementById("msg").textContent = "Đang đồng bộ tập luật từ vags.vnu.edu.vn...";
    chrome.runtime.sendMessage({ action: "SYNC_NOW" }, () => {
        setTimeout(() => {
            renderState();
            document.getElementById("msg").textContent = "✅ Đã đồng bộ PAC Script mới nhất!";
        }, 400);
    });
});

document.getElementById("btnSso").addEventListener("click", async () => {
    document.getElementById("msg").textContent = "Đang xác thực với VAGS / Keycloak VNU...";
    try {
        const res = await fetch(`${VAGS_BASE_URL}/api/v1/auth/demo-token`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username: "giangvien@vnu.edu.vn" })
        });
        const data = await res.json();
        chrome.storage.local.set({
            vagsToken: data.accessToken,
            vagsUser: data.user
        }, () => {
            renderState();
            document.getElementById("msg").textContent = "✅ Đã nhận JWT Token từ VNU SSO!";
        });
    } catch (e) {
        document.getElementById("msg").textContent = "⚠️ Không thể kết nối máy chủ xác thực.";
    }
});

renderState();

