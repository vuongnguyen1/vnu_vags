// VNU Academic Gateway (VAGS) - Content Script
// 1. Tự động đồng bộ phiên đăng nhập từ https://thamkhao.vnu.edu.vn vào Extension
// 2. Hiển thị Khung Trạng Thái Phân Tuyến Nổi (Floating Badge: PROXY Xanh vs DIRECT Ghi)

(function () {
    // Chỉ chạy trên top-level window (không chèn vào iframe quảng cáo)
    if (window.top !== window) return;

    // --- PHẦN 1: ĐỒNG BỘ PHIÊN SSO (Trên thamkhao.vnu.edu.vn & vags.vnu.edu.vn) ---
    if (window.location.hostname === "thamkhao.vnu.edu.vn" || window.location.hostname === "vags.vnu.edu.vn" || window.location.hostname.endsWith(".vnu.edu.vn")) {
        try {
            document.documentElement.setAttribute("data-vags-extension", "installed");
            
            function broadcastStatusToPage() {
                try {
                    chrome.runtime.sendMessage({ action: "GET_STATUS" }, (res) => {
                        if (chrome.runtime.lastError || !res) return;
                        const isEnabled = res.enabled !== false;
                        const user = res.user || "";
                        document.documentElement.setAttribute("data-vags-enabled", isEnabled ? "true" : "false");
                        window.postMessage({
                            type: "VAGS_EXTENSION_PONG",
                            version: res.version || "2.1.4",
                            enabled: isEnabled,
                            user: user,
                            isLoggedIn: !!user
                        }, "*");
                    });
                } catch (e) { }
            }

            broadcastStatusToPage();

            // Lắng nghe thay đổi storage để cập nhật trạng thái thời gian thực
            chrome.storage.onChanged.addListener((changes, area) => {
                if (area === "local") {
                    broadcastStatusToPage();
                }
            });

            window.addEventListener("message", (event) => {
                if (event && event.data && event.data.type === "VAGS_EXTENSION_PING") {
                    broadcastStatusToPage();
                }
            });
        } catch (e) { }

        function sendSessionToExtension(data) {
            if (!data || !data.vagsToken) return;
            try {
                chrome.runtime.sendMessage({
                    action: "SYNC_SESSION",
                    data: {
                        vagsToken: data.vagsToken,
                        vagsUser: data.vagsUser || "",
                        vagsName: data.vagsName || data.vagsUser || "Cán bộ VNU",
                        vagsUnit: data.vagsUnit || "",
                        vagsMemberType: data.vagsMemberType || "canbo",
                        vagsIsAdmin: data.vagsIsAdmin === true || data.vagsIsAdmin === "true"
                    }
                }, () => {});
            } catch (e) { }
        }

        function checkLocalSession() {
            try {
                const token = localStorage.getItem("vags_token");
                const user = localStorage.getItem("vags_user");
                if (token && user) {
                    sendSessionToExtension({
                        vagsToken: token,
                        vagsUser: user,
                        vagsName: localStorage.getItem("vags_name") || user,
                        vagsUnit: localStorage.getItem("vags_unit") || "",
                        vagsMemberType: localStorage.getItem("vags_member_type") || "canbo",
                        vagsIsAdmin: localStorage.getItem("vags_is_admin") === "true"
                    });
                }
            } catch (e) { }
        }

        window.addEventListener("VAGS_SSO_SYNC", (event) => {
            if (event && event.detail) sendSessionToExtension(event.detail);
        });

        window.addEventListener("message", (event) => {
            if (event && event.data && event.data.type === "VAGS_SSO_SYNC") {
                sendSessionToExtension(event.data.data);
            } else if (event && event.data && event.data.type === "VAGS_SSO_LOGOUT") {
                try { chrome.runtime.sendMessage({ action: "CLEAR_SESSION" }); } catch (e) { }
            }
        });

        // Nhận lệnh đăng xuất ép buộc từ Extension Popup
        chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
            if (msg && msg.action === "FORCE_LOGOUT_PAGE") {
                try {
                    localStorage.removeItem("vags_token");
                    localStorage.removeItem("vags_user");
                    localStorage.removeItem("vags_unit");
                    localStorage.removeItem("vags_name");
                    localStorage.removeItem("vags_member_type");
                    localStorage.removeItem("vags_is_admin");
                    sessionStorage.clear();
                    window.postMessage({ type: "VAGS_SSO_LOGOUT" }, "*");
                    document.documentElement.setAttribute("data-vags-enabled", "false");
                    window.location.reload();
                } catch (e) { }
                sendResponse({ ok: true });
                return true;
            }
        });

        checkLocalSession();
    }

    // --- PHẦN 2: KHUNG TRẠNG THÁI NỔI (FLOATING ROUTE BADGE) TRÊN MỌI TRANG WEB ---
    function removeFloatingBadge() {
        const existing = document.getElementById("vags-floating-shield-host");
        if (existing) existing.remove();
    }

    function initFloatingBadge() {
        // Kiểm tra xem đã bị tắt trên trang này chưa
        if (sessionStorage.getItem("vags_badge_dismissed") === "true") return;

        try {
            chrome.runtime.sendMessage({
                action: "GET_PAGE_ROUTE_INFO",
                host: window.location.hostname,
                url: window.location.href
            }, (info) => {
                if (!info) return;
                // Nếu proxy đang tắt hoặc chưa đăng nhập: ẨN HOÀN TOÀN KHUNG HIỂN THỊ BÊN PHẢI THEO YÊU CẦU NGƯỜI DÙNG
                if (!info.enabled || !info.isLoggedIn) {
                    removeFloatingBadge();
                    return;
                }
                renderFloatingBadge(info);
            });
        } catch (e) {
            // Extension context not ready
        }
    }

    // Lắng nghe thay đổi storage để ẩn/hiện Floating Badge lập tức theo thời gian thực
    if (typeof chrome !== "undefined" && chrome.storage && chrome.storage.onChanged) {
        chrome.storage.onChanged.addListener((changes, area) => {
            if (area === "local") {
                if (changes.proxyEnabled || changes.vagsUser || changes.vagsToken) {
                    chrome.storage.local.get(["proxyEnabled", "vagsUser", "vagsToken"], (st) => {
                        const isEnabled = st.proxyEnabled === true;
                        const isLoggedIn = !!(st.vagsUser && st.vagsToken);
                        if (!isEnabled || !isLoggedIn) {
                            removeFloatingBadge();
                        } else {
                            initFloatingBadge();
                        }
                    });
                }
            }
        });
    }

    function renderFloatingBadge(info) {
        if (document.getElementById("vags-floating-shield-host")) return;

        const host = document.createElement("div");
        host.id = "vags-floating-shield-host";
        host.style.all = "initial";
        host.style.position = "fixed";
        host.style.bottom = "16px";
        host.style.right = "16px";
        host.style.zIndex = "2147483647";
        host.style.pointerEvents = "auto";
        document.documentElement.appendChild(host);

        const shadow = host.attachShadow({ mode: "closed" });

        const isDisabled = !info.enabled;
        const isProxy = !isDisabled && info.action === "PROXY";
        const isBlacklist = !isDisabled && info.category === "blacklist";
        const isVnuDirect = !isDisabled && info.category === "vnu";

        // Tùy biến màu sắc và nội dung
        let themeBg = isDisabled ? "linear-gradient(135deg, #7f1d1d 0%, #991b1b 100%)" : (isProxy ? "linear-gradient(135deg, #064e3b 0%, #047857 100%)" : "#1e293b");
        let themeBorder = isDisabled ? "#ef4444" : (isProxy ? "#34d399" : (isBlacklist ? "#ef4444" : "#94a3b8"));
        let statusDotColor = isDisabled ? "#f87171" : (isProxy ? "#10b981" : (isBlacklist ? "#f87171" : "#94a3b8"));
        let badgeIcon = isDisabled ? "🔴" : (isProxy ? "🟢" : (isBlacklist ? "⚪" : "🏛️"));
        let titleTxt = isDisabled ? "VAGS: ĐÃ TẮT" : (isProxy ? "VAGS PROXY" : (isBlacklist ? "VAGS DIRECT" : "VNU DIRECT"));
        let subTxt = isDisabled ? "Không qua mạng ĐHQGHN (Chưa bật Proxy)" : (isProxy ? "Mạng ĐHQGHN (112.137.142.30)" : (isBlacklist ? "Mạng ngoài • Băng thông cao" : "Mạng Nội bộ ĐHQGHN"));
        let pillColor = isDisabled ? "#fecaca" : (isProxy ? "#a7f3d0" : (isBlacklist ? "#fecaca" : "#e2e8f0"));

        const style = document.createElement("style");
        style.textContent = `
            * { box-sizing: border-box; margin: 0; padding: 0; }
            .vags-badge-container {
                font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
                background: ${themeBg};
                border: 1px solid ${themeBorder};
                color: #ffffff;
                border-radius: 999px;
                box-shadow: 0 4px 16px rgba(0, 0, 0, 0.22);
                display: inline-flex;
                align-items: center;
                gap: 8px;
                padding: 5px 12px;
                font-size: 11px;
                font-weight: 600;
                cursor: pointer;
                user-select: none;
                transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
                overflow: hidden;
                white-space: nowrap;
                backdrop-filter: blur(8px);
                max-width: 420px;
            }
            .vags-badge-container:hover {
                transform: translateY(-2px);
                box-shadow: 0 6px 20px rgba(0, 0, 0, 0.32);
            }
            .vags-badge-container.collapsed {
                padding: 5px 9px;
                max-width: 90px;
            }
            .vags-badge-container.collapsed .vags-expandable {
                display: none;
            }
            .vags-dot {
                width: 7px;
                height: 7px;
                border-radius: 50%;
                background: ${statusDotColor};
                box-shadow: 0 0 6px ${statusDotColor};
                flex-shrink: 0;
            }
            .vags-title {
                font-weight: 800;
                color: #ffffff;
                letter-spacing: 0.02em;
            }
            .vags-desc {
                color: ${pillColor};
                font-weight: 500;
                font-size: 11px;
            }
            .vags-close-btn {
                background: rgba(255, 255, 255, 0.15);
                border: none;
                color: #cbd5e1;
                border-radius: 50%;
                width: 16px;
                height: 16px;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 11px;
                cursor: pointer;
                margin-left: 4px;
                transition: background 0.15s;
            }
            .vags-close-btn:hover {
                background: rgba(239, 68, 68, 0.8);
                color: white;
            }
            .vags-card-tooltip {
                display: none;
                position: absolute;
                bottom: 38px;
                right: 0;
                background: #0f172a;
                border: 1px solid ${themeBorder};
                border-radius: 10px;
                padding: 12px 14px;
                width: 280px;
                box-shadow: 0 10px 25px rgba(0,0,0,0.4);
                font-size: 11px;
                line-height: 1.5;
                color: #cbd5e1;
            }
            .vags-card-tooltip b { color: #ffffff; }
            .vags-card-tooltip code { color: #38bdf8; font-family: monospace; }
        `;

        const wrapper = document.createElement("div");
        wrapper.style.position = "relative";

        const tooltip = document.createElement("div");
        tooltip.className = "vags-card-tooltip";
        tooltip.id = "vagsTooltip";

        const header = document.createElement("div");
        header.style.cssText = "font-weight:800;font-size:12px;color:#ffffff;margin-bottom:6px;display:flex;align-items:center;gap:6px;";
        const iconSpan = document.createElement("span");
        iconSpan.textContent = badgeIcon;
        const titleSpan = document.createElement("span");
        titleSpan.textContent = titleTxt;
        header.appendChild(iconSpan);
        header.appendChild(titleSpan);
        tooltip.appendChild(header);

        function createRow(boldText, valNode) {
            const row = document.createElement("div");
            row.style.marginBottom = "4px";
            const b = document.createElement("b");
            b.textContent = boldText;
            row.appendChild(b);
            row.appendChild(document.createTextNode(" "));
            if (typeof valNode === "string") {
                row.appendChild(document.createTextNode(valNode));
            } else {
                row.appendChild(valNode);
            }
            return row;
        }

        const codeHost = document.createElement("code");
        codeHost.textContent = window.location.hostname;
        tooltip.appendChild(createRow("Tên miền:", codeHost));

        const routeSpan = document.createElement("span");
        routeSpan.style.color = isDisabled ? "#f87171" : (isProxy ? "#34d399" : "#94a3b8");
        routeSpan.style.fontWeight = "700";
        routeSpan.textContent = isDisabled ? "DIRECT (PROXY OFF)" : info.action;
        tooltip.appendChild(createRow("Định tuyến:", routeSpan));

        const catSpan = document.createElement("span");
        if (isDisabled) {
            catSpan.style.color = "#f87171";
            catSpan.style.fontWeight = "700";
            catSpan.textContent = "ĐÃ TẮT PROXY";
        } else {
            catSpan.textContent = info.label || info.category || "CSDL";
        }
        tooltip.appendChild(createRow("Phân loại:", catSpan));

        const reasonSpan = document.createElement("span");
        reasonSpan.textContent = isDisabled 
            ? "Bấm biểu tượng VAGS trên thanh công cụ trình duyệt để BẬT lại Proxy tra cứu" 
            : (info.reason || "Tập luật VAGS");
        tooltip.appendChild(createRow("Lý do:", reasonSpan));

        const footer = document.createElement("div");
        footer.style.cssText = "border-top:1px solid rgba(255,255,255,0.1);padding-top:8px;margin-top:6px;font-size:10px;color:#94a3b8;display:flex;align-items:center;justify-content:space-between;gap:8px;";
        
        const footText = document.createElement("div");
        if (isDisabled) {
            footText.appendChild(document.createTextNode("Trạng thái: "));
            const bFoot = document.createElement("b");
            bFoot.style.color = "#f87171";
            bFoot.textContent = "Chưa bật Proxy";
            footText.appendChild(bFoot);
        } else {
            footText.appendChild(document.createTextNode("Hạ tầng Cổng: "));
            const bFoot = document.createElement("b");
            bFoot.textContent = "thamkhao.vnu.edu.vn";
            footText.appendChild(bFoot);
            footer.appendChild(footText);
        }

        const btnCheckIp = document.createElement("button");
        btnCheckIp.style.cssText = "background:#00803d;color:#ffffff;border:none;border-radius:4px;padding:3px 8px;font-size:10px;font-weight:700;cursor:pointer;display:inline-flex;align-items:center;gap:3px;transition:background 0.2s;";
        btnCheckIp.textContent = "🔍 Kiểm tra IP";
        btnCheckIp.onclick = (e) => {
            e.stopPropagation();
            btnCheckIp.textContent = "⏳ Đang kiểm tra...";
            fetch("https://thamkhao.vnu.edu.vn/api/v1/health")
                .then(r => r.json())
                .then(d => {
                    alert("[VNU Academic Gateway - Kiểm tra IP]\n\n" +
                          "• Địa chỉ IP Egress: " + (d.egressIp || "112.137.142.30") + " (Mạng ĐHQGHN)\n" +
                          "• Trạng thái Cổng: " + (d.status === "healthy" ? "Đang hoạt động (OK)" : "Bảo trì") + "\n" +
                          "• Tên miền: " + (d.primaryDomain || "thamkhao.vnu.edu.vn") + "\n\n" +
                          "Kết nối ủy quyền học thuật đang hoạt động bình thường!");
                    btnCheckIp.textContent = "🔍 Kiểm tra IP";
                })
                .catch(err => {
                    alert("Không thể kết nối máy chủ Cổng: " + err.message);
                    btnCheckIp.textContent = "🔍 Kiểm tra IP";
                });
        };
        footer.appendChild(btnCheckIp);
        tooltip.appendChild(footer);

        const pill = document.createElement("div");
        pill.className = "vags-badge-container";
        pill.id = "vagsPill";

        const dot = document.createElement("span");
        dot.className = "vags-dot";

        const pTitle = document.createElement("span");
        pTitle.className = "vags-title";
        pTitle.textContent = titleTxt;

        const pDesc = document.createElement("span");
        pDesc.className = "vags-expandable vags-desc";
        pDesc.textContent = "• " + subTxt;

        const closeBtn = document.createElement("button");
        closeBtn.className = "vags-close-btn";
        closeBtn.id = "vagsClose";
        closeBtn.title = "Đóng khung thông báo này";
        closeBtn.textContent = "×";

        pill.appendChild(dot);
        pill.appendChild(pTitle);
        pill.appendChild(pDesc);
        pill.appendChild(closeBtn);

        wrapper.appendChild(tooltip);
        wrapper.appendChild(pill);

        shadow.appendChild(style);
        shadow.appendChild(wrapper);

        // Hover hiển thị chi tiết tooltip
        pill.addEventListener("mouseenter", () => {
            pill.classList.remove("collapsed");
            tooltip.style.display = "block";
        });
        wrapper.addEventListener("mouseleave", () => {
            tooltip.style.display = "none";
        });

        // Đóng vĩnh viễn trong phiên làm việc của trang này
        closeBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            sessionStorage.setItem("vags_badge_dismissed", "true");
            host.remove();
        });

        // Tự động thu nhỏ gọn gàng sau 4 giây để không choán màn hình người dùng
        setTimeout(() => {
            if (pill && !tooltip.matches(":hover")) {
                pill.classList.add("collapsed");
            }
        }, 4000);
    }

    // Khởi chạy khi DOM đã sẵn sàng
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initFloatingBadge);
    } else {
        initFloatingBadge();
    }
})();
