// VNU Academic Gateway (VAGS) - Popup Controller
const VAGS_BASE_URL = "https://thamkhao.vnu.edu.vn";

function setSafeContent(el, parts) {
    if (!el) return;
    el.textContent = "";
    parts.forEach(p => {
        if (typeof p === "string") {
            el.appendChild(document.createTextNode(p));
        } else if (p && p.tag) {
            const node = document.createElement(p.tag);
            if (p.text) node.textContent = p.text;
            if (p.style) Object.assign(node.style, p.style);
            if (p.className) node.className = p.className;
            el.appendChild(node);
        }
    });
}

function setSafeTestResult(resBox, isProxy, titleText, smallText) {
    if (!resBox) return;
    resBox.className = isProxy ? "result-box result-proxy" : "result-box result-direct";
    resBox.textContent = "";

    resBox.appendChild(document.createTextNode(isProxy ? "🟢 " : "⚪ "));
    const b = document.createElement("b");
    b.textContent = titleText;
    resBox.appendChild(b);

    resBox.appendChild(document.createElement("br"));

    const small = document.createElement("small");
    small.textContent = smallText;
    resBox.appendChild(small);
}

function getInitials(name) {
    if (!name) return "CB";
    const parts = name.trim().split(/\s+/);
    if (parts.length === 1) return parts[0].substring(0, 2).toUpperCase();
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

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

// Đo kiểm IP thực tế qua đường truyền của trình duyệt
async function verifyRealConnection() {
    const card = document.getElementById("liveConnCard");
    const icon = document.getElementById("liveConnIcon");
    const title = document.getElementById("liveConnTxt");
    const detail = document.getElementById("liveConnDetail");

    const storage = await new Promise(r => chrome.storage.local.get(["vagsToken", "vagsUser", "vagsMemberType", "proxyEnabled"], r));
    const isLoggedIn = !!(storage.vagsToken && storage.vagsUser);
    const isStudent = storage.vagsMemberType === "sinhvien";
    const isEnabled = storage.proxyEnabled === true;

    if (!isLoggedIn) {
        card.className = "live-conn-card";
        icon.textContent = "🔒";
        title.textContent = "CHƯA KÍCH HOẠT ỦY QUYỀN PROXY";
        setSafeContent(detail, ["Vui lòng bấm ", { tag: "b", text: "🔑 Đăng nhập VNU SSO" }, " ở trên để xác thực tài khoản Cán bộ ĐHQGHN."]);
        return;
    }

    if (isStudent) {
        card.className = "live-conn-card failed";
        icon.textContent = "⛔";
        title.textContent = "SINH VIÊN KHÔNG ĐƯỢC CẤP QUYỀN";
        detail.textContent = "Dịch vụ Proxy CSDL chỉ dành riêng cho Cán bộ ĐHQGHN. Tài khoản Sinh viên đi trực tiếp (DIRECT).";
        return;
    }

    if (!isEnabled) {
        card.className = "live-conn-card";
        icon.textContent = "⏸️";
        title.textContent = "ĐƯỜNG TRUYỀN PROXY ĐANG TẮT";
        setSafeContent(detail, ["Gạt công tắc ", { tag: "b", text: "Đường truyền Proxy" }, " phía trên sang BẬT để kết nối CSDL trường."]);
        return;
    }

    icon.textContent = "⏳";
    title.textContent = "Đang đo kiểm IP kết nối thực tế...";
    detail.textContent = "Đang gửi tín hiệu kiểm tra IP Egress...";
    card.className = "live-conn-card";

    try {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 4000);
        const res = await fetch("https://api4.ipify.org?format=json", {
            cache: "no-store",
            signal: controller.signal
        });
        clearTimeout(timeoutId);

        if (res.ok) {
            const data = await res.json();
            const ip = data.ip || "";
            const isVnuEgress = ip === "112.137.142.30" || ip === "112.137.133.14" || ip.startsWith("112.137.");
            if (isVnuEgress) {
                card.className = "live-conn-card success";
                icon.textContent = "🟢";
                title.textContent = "KẾT NỐI PROXY THÀNH CÔNG ✓";
                setSafeContent(detail, ["IP nhận diện thực tế: ", { tag: "b", text: ip, style: { color: "#34d399" } }, " (Mạng ĐHQGHN • Đạt yêu cầu)"]);
            } else {
                card.className = "live-conn-card failed";
                icon.textContent = "🔴";
                title.textContent = "CHƯA QUA PROXY ĐHQGHN";
                setSafeContent(detail, ["IP nhận diện: ", { tag: "b", text: ip, style: { color: "#f87171" } }, " (Mạng ngoài • Chưa chuyển tiếp)"]);
            }
            return;
        }
    } catch (e) {}

    // Dự phòng kiểm tra qua endpoint của VAGS
    try {
        const res2 = await fetch(`${VAGS_BASE_URL}/api/v1/ip-check`, { cache: "no-store" });
        if (res2.ok) {
            const d2 = await res2.json();
            const cIp = d2.clientIp || "";
            const isVnu = cIp === "112.137.142.30" || cIp === "112.137.133.14" || cIp.startsWith("112.137.") || d2.isVnuNetwork;
            if (isVnu) {
                card.className = "live-conn-card success";
                icon.textContent = "🟢";
                title.textContent = "KẾT NỐI PROXY THÀNH CÔNG ✓";
                setSafeContent(detail, ["IP nhận diện: ", { tag: "b", text: cIp || "112.137.142.30", style: { color: "#34d399" } }, " (Mạng ĐHQGHN)"]);
            } else {
                card.className = "live-conn-card failed";
                icon.textContent = "🔴";
                title.textContent = "CHƯA QUA PROXY ĐHQGHN";
                setSafeContent(detail, ["IP hiện tại: ", { tag: "b", text: cIp || "Không rõ", style: { color: "#f87171" } }, " (Mạng nhà)"]);
            }
            return;
        }
    } catch (e2) {}

    card.className = "live-conn-card failed";
    icon.textContent = "⚠️";
    title.textContent = "KHÔNG ĐO ĐƯỢC KẾT NỐI";
    detail.textContent = "Vui lòng kiểm tra lại kết nối mạng Internet của máy tính.";
}

function renderUI() {
    chrome.storage.local.get([
        "vagsToken", "vagsUser", "vagsName", "vagsUnit", "vagsMemberType", "vagsIsAdmin",
        "proxyEnabled", "ruleVersion", "sseConnected", "currentRules"
    ], (data) => {
        // 1. SSE Status
        const sseBadge = document.getElementById("sseStatus");
        if (data.sseConnected) {
            sseBadge.textContent = "● SSE Live";
            sseBadge.className = "badge";
        } else {
            sseBadge.textContent = "● Ngoại tuyến";
            sseBadge.className = "badge offline";
        }

        // 2. Rule Version
        if (data.ruleVersion) {
            document.getElementById("ruleVerTxt").textContent = "v" + data.ruleVersion.slice(-8);
        }

        // 3. User Login State
        const isLoggedIn = !!(data.vagsToken && data.vagsUser);
        const isStudent = data.vagsMemberType === "sinhvien";

        const loggedInBox = document.getElementById("loggedInBox");
        const loggedOutBox = document.getElementById("loggedOutBox");
        const studentWarning = document.getElementById("studentWarning");
        const btnLogout = document.getElementById("btnLogout");
        const proxyToggle = document.getElementById("proxyToggle");

        const liveCard = document.getElementById("liveConnCard");
        const liveIcon = document.getElementById("liveConnIcon");
        const liveTitle = document.getElementById("liveConnTxt");
        const liveDetail = document.getElementById("liveConnDetail");

        if (isLoggedIn) {
            loggedInBox.style.display = "flex";
            loggedOutBox.style.display = "none";
            btnLogout.style.display = "inline-block";

            const displayName = data.vagsName || data.vagsUser || "Cán bộ VNU";
            document.getElementById("userName").textContent = displayName;
            document.getElementById("avatarTxt").textContent = getInitials(displayName);
            document.getElementById("userDetail").textContent = `${data.vagsUser}@vnu.edu.vn • ${(data.vagsUnit || "VNU").toUpperCase()}`;

            const roleTag = document.getElementById("roleTag");
            const deniedUsers = (data.currentRules?.userOverrides?.denied || []).map(u => (u || '').toLowerCase());
            const isDenied = (data.vagsUser && deniedUsers.includes(data.vagsUser.toLowerCase())) || data.isLocked === true;
            const isOverride = (data.currentRules?.userOverrides?.allowed || []).includes(data.vagsUser);

            if (isDenied) {
                roleTag.textContent = "Bị Quản Trị Khóa";
                roleTag.className = "role-tag warn";
                studentWarning.style.display = "block";
                setSafeContent(studentWarning, ["⛔ ", { tag: "b", text: "Tài khoản bị KHOÁ:" }, " Quản trị viên ĐHQGHN đã khoá quyền sử dụng Proxy VAGS của tài khoản này."]);
                proxyToggle.disabled = true;
                proxyToggle.checked = false;
                updateProxySwitchUI(false, "Tài khoản đã bị Quản trị viên Khóa");

                liveCard.className = "live-conn-card failed";
                liveIcon.textContent = "🔒";
                liveTitle.textContent = "TÀI KHOẢN ĐÃ BỊ KHOÁ";
                liveDetail.textContent = "Bạn không thể kích hoạt Proxy. Vui lòng liên hệ Quản trị viên PQTKT ĐHQGHN để được mở khoá.";
            } else if (isStudent && !isOverride) {
                roleTag.textContent = "Sinh viên (Không có quyền)";
                roleTag.className = "role-tag warn";
                studentWarning.style.display = "block";
                setSafeContent(studentWarning, ["⚠️ ", { tag: "b", text: "Tài khoản Sinh viên:" }, " Không được phép sử dụng Proxy CSDL Quốc tế. Chỉ dành cho Cán bộ & Giảng viên ĐHQGHN."]);
                proxyToggle.disabled = true;
                proxyToggle.checked = false;
                updateProxySwitchUI(false, "Sinh viên không được cấp quyền");

                liveCard.className = "live-conn-card failed";
                liveIcon.textContent = "⛔";
                liveTitle.textContent = "SINH VIÊN KHÔNG ĐƯỢC CẤP QUYỀN";
                liveDetail.textContent = "Dịch vụ Proxy CSDL chỉ dành riêng cho Cán bộ ĐHQGHN. Tài khoản Sinh viên đi trực tiếp (DIRECT).";
            } else {
                roleTag.textContent = data.vagsIsAdmin ? "Quản trị viên (PQTKT)" : "Cán bộ VNU ✓";
                roleTag.className = "role-tag";
                studentWarning.style.display = "none";
                proxyToggle.disabled = false;
                const enabled = data.proxyEnabled === true;
                proxyToggle.checked = enabled;
                updateProxySwitchUI(enabled);
                // Cán bộ hợp lệ -> Đo kiểm kết nối thực tế
                if (enabled) {
                    verifyRealConnection();
                } else {
                    liveCard.className = "live-conn-card";
                    liveIcon.textContent = "⏸️";
                    liveTitle.textContent = "ĐƯỜNG TRUYỀN PROXY ĐANG TẮT";
                    setSafeContent(liveDetail, ["Gạt công tắc ", { tag: "b", text: "Đường truyền Proxy" }, " phía trên sang BẬT để kết nối CSDL trường."]);
                }
            }
        } else {
            // CHƯA ĐĂNG NHẬP VNU SSO: Khóa hoàn toàn tính năng Proxy!
            loggedInBox.style.display = "none";
            loggedOutBox.style.display = "block";
            studentWarning.style.display = "none";
            btnLogout.style.display = "none";

            proxyToggle.disabled = true;
            proxyToggle.checked = false;
            updateProxySwitchUI(false, "Chưa đăng nhập VNU SSO");

            liveCard.className = "live-conn-card";
            liveIcon.textContent = "🔒";
            liveTitle.textContent = "CHƯA KÍCH HOẠT ỦY QUYỀN PROXY";
            setSafeContent(liveDetail, ["Vui lòng bấm ", { tag: "b", text: "🔑 Đăng nhập VNU SSO" }, " ở trên để xác thực tài khoản Cán bộ ĐHQGHN."]);
        }
    });
}

function updateProxySwitchUI(enabled, customText) {
    const dot = document.getElementById("proxyDot");
    const txt = document.getElementById("proxyStateTxt");
    if (customText) {
        dot.className = "dot off";
        txt.textContent = `Đường truyền Proxy: ${customText}`;
    } else if (enabled) {
        dot.className = "dot";
        txt.textContent = "Đường truyền Proxy: Đang Bật";
    } else {
        dot.className = "dot off";
        txt.textContent = "Đường truyền Proxy: Đã Tắt";
    }
}

// Lắng nghe sự kiện Bật/Tắt Proxy
document.getElementById("proxyToggle").addEventListener("change", (e) => {
    const enabled = e.target.checked;
    updateProxySwitchUI(enabled);
    chrome.runtime.sendMessage({ action: "TOGGLE_PROXY", enabled: enabled }, () => {
        setTimeout(verifyRealConnection, 600);
    });
});

// Nút Đăng nhập VNU SSO
document.getElementById("btnOpenLogin").addEventListener("click", () => {
    chrome.tabs.create({ url: `${VAGS_BASE_URL}/` });
});

// Nút Đăng xuất
document.getElementById("btnLogout").addEventListener("click", () => {
    fetch(`${VAGS_BASE_URL}/api/v1/auth/logout`, { method: "POST" }).catch(() => {});
    chrome.runtime.sendMessage({ action: "CLEAR_SESSION" }, () => {
        renderUI();
    });
});

// Nút Đồng bộ ngay
document.getElementById("btnSyncNow").addEventListener("click", () => {
    chrome.runtime.sendMessage({ action: "SYNC_NOW" }, () => {
        setTimeout(() => {
            renderUI();
            verifyRealConnection();
        }, 500);
    });
});

// Nút Đo lại IP thực tế
document.getElementById("btnVerifyRealIp").addEventListener("click", () => {
    verifyRealConnection();
});

// 4. Công cụ Kiểm tra Định tuyến (Quick Route Tester)
function testRoute() {
    let inputVal = document.getElementById("testUrlInput").value.trim();
    if (!inputVal) return;

    let host = inputVal;
    try {
        if (!inputVal.includes("://")) inputVal = "https://" + inputVal;
        const u = new URL(inputVal);
        host = u.hostname;
    } catch (e) {
        host = inputVal.split("/")[0].split(":")[0];
    }
    host = host.toLowerCase();

    chrome.storage.local.get(["currentRules", "vagsUser"], (data) => {
        const rules = data.currentRules || {};
        const username = data.vagsUser || "";
        const userSpecific = (rules.userOverrides && rules.userOverrides.userSpecificRules && rules.userOverrides.userSpecificRules[username] && rules.userOverrides.userSpecificRules[username].proxyDomains) || [];
        const internalWL = rules.internalWhitelist || ["lic.vnu.edu.vn", "*.lic.vnu.edu.vn"];
        const blacklist = rules.blacklist || ["*.youtube.com", "youtube.com", "*.facebook.com", "facebook.com", "*.tiktok.com", "tiktok.com"];
        const academicWL = rules.globalAcademicWhitelist || ["*.sciencedirect.com", "*.ieee.org", "*.springer.com", "*.nature.com"];

        const resBox = document.getElementById("testResultBox");
        resBox.style.display = "block";

        // 0. Ngoại lệ riêng theo tài khoản (Ví dụ: phongnd -> idp.vnu.edu.vn)
        for (let pat of userSpecific) {
            if (shExpMatch(host, pat)) {
                setSafeTestResult(resBox, true, "Định tuyến: PROXY VAGS (:443)", `Khớp: ${pat} (Ngoại lệ tài khoản: ${username})`);
                return;
            }
        }

        // 1. Thư viện số VNU & Công cụ kiểm tra IP
        for (let pat of internalWL) {
            if (shExpMatch(host, pat)) {
                setSafeTestResult(resBox, true, "Định tuyến: PROXY VAGS (:443)", `Khớp: ${pat} (Thư viện số / Công cụ VNU)`);
                return;
            }
        }

        // 2. Blacklist (Băng thông cao)
        for (let pat of blacklist) {
            if (shExpMatch(host, pat)) {
                setSafeTestResult(resBox, false, "Định tuyến: DIRECT (Mạng ngoài)", `Khớp Blacklist: ${pat}`);
                return;
            }
        }

        // 3. Domain nội bộ *.vnu.edu.vn
        if (shExpMatch(host, "*.vnu.edu.vn") || host === "vnu.edu.vn") {
            setSafeTestResult(resBox, false, "Định tuyến: DIRECT (Mạng nội bộ)", "Domain nội bộ VNU không cần qua Proxy");
            return;
        }

        // 4. Whitelist CSDL Học thuật Quốc tế
        for (let pat of academicWL) {
            if (shExpMatch(host, pat)) {
                setSafeTestResult(resBox, true, "Định tuyến: PROXY VAGS (:443)", `Khớp CSDL Học thuật: ${pat}`);
                return;
            }
        }

        // 5. Chế độ Full Proxy
        if ((rules.proxyMode || "FULL_PROXY") === "FULL_PROXY") {
            setSafeTestResult(resBox, true, "Định tuyến: PROXY VAGS (:443)", "Chế độ Toàn phần (Full Proxy) đang kích hoạt");
            return;
        }

        // 6. Mặc định
        setSafeTestResult(resBox, false, "Định tuyến: DIRECT (Mạng ngoài)", "Tên miền không thuộc danh mục CSDL ủy quyền");
    });
}

document.getElementById("btnTestRoute").addEventListener("click", testRoute);
document.getElementById("testUrlInput").addEventListener("keypress", (e) => {
    if (e.key === "Enter") testRoute();
});

// Khởi chạy
renderUI();
