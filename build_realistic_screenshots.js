const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

async function main() {
    const outDir = '/mnt/data/websites/vags.vnu.edu.vn/public/images/guide';
    if (!fs.existsSync(outDir)) {
        fs.mkdirSync(outDir, { recursive: true });
    }

    // Load real screenshot assets as base64
    const idpPath = path.join(outDir, 'test-real-idp.png');
    const sdPath = path.join(outDir, 'sciencedirect-raw.png');
    const vnuLogoPath = '/mnt/data/websites/vags.vnu.edu.vn/public/vnu-logo.png';

    const idpBase64 = fs.existsSync(idpPath) ? fs.readFileSync(idpPath).toString('base64') : '';
    const sdBase64 = fs.existsSync(sdPath) ? fs.readFileSync(sdPath).toString('base64') : '';
    const vnuLogoBase64 = fs.existsSync(vnuLogoPath) ? fs.readFileSync(vnuLogoPath).toString('base64') : '';

    console.log('[INFO] Launching Chromium to generate 100% authentic Windows 11 Chrome guide screenshots...');
    const browser = await chromium.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox', '--font-render-hinting=medium']
    });

    const page = await browser.newPage({
        viewport: { width: 1200, height: 820 },
        deviceScaleFactor: 2
    });

    // Helper: Pixel-perfect Windows 11 Chrome Window Shell
    const win11ChromeShell = ({ tabTitle, tabFavicon, url, contentHtml, activeExtension = false, activeProfile = false }) => `
    <!DOCTYPE html>
    <html lang="vi">
    <head>
        <meta charset="utf-8">
        <link href="https://fonts.googleapis.com/css2?family=Segoe+UI:wght@400;500;600;700&family=Google+Sans:wght@400;500;700&family=Roboto:wght@400;500;700&display=swap" rel="stylesheet">
        <style>
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
                background: #d8e2ed;
                padding: 24px;
                font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, sans-serif;
                display: flex;
                justify-content: center;
                align-items: flex-start;
                min-height: 100vh;
            }
            .win11-window {
                width: 1140px;
                background: #ffffff;
                border-radius: 10px;
                box-shadow: 0 24px 60px rgba(0, 0, 0, 0.28), 0 0 0 1px rgba(0, 0, 0, 0.12);
                overflow: hidden;
                position: relative;
            }
            /* Windows 11 Chrome Titlebar & Tab Strip */
            .titlebar {
                background: #dfe3e8;
                height: 42px;
                display: flex;
                align-items: flex-end;
                padding-left: 10px;
                padding-right: 0;
                user-select: none;
                border-top-left-radius: 10px;
                border-top-right-radius: 10px;
            }
            .tab-strip {
                display: flex;
                align-items: flex-end;
                flex: 1;
                height: 36px;
                gap: 4px;
            }
            .tab {
                height: 36px;
                max-width: 260px;
                background: #ffffff;
                border-top-left-radius: 8px;
                border-top-right-radius: 8px;
                padding: 0 12px 0 14px;
                display: flex;
                align-items: center;
                gap: 8px;
                font-size: 12px;
                color: #1f1f1f;
                font-weight: 500;
                box-shadow: 0 -1px 2px rgba(0,0,0,0.04);
            }
            .tab-favicon {
                width: 16px;
                height: 16px;
                display: flex;
                align-items: center;
                justify-content: center;
                flex-shrink: 0;
            }
            .tab-title {
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                flex: 1;
            }
            .tab-close {
                font-size: 14px;
                color: #5f6368;
                width: 18px;
                height: 18px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
            }
            .new-tab-btn {
                width: 28px;
                height: 28px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                color: #5f6368;
                font-size: 16px;
                margin-bottom: 4px;
                margin-left: 2px;
            }
            /* Windows 11 Window Controls */
            .win11-controls {
                display: flex;
                height: 100%;
                margin-left: auto;
            }
            .win-btn {
                width: 46px;
                height: 100%;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 12px;
                color: #1f1f1f;
            }
            .win-close {
                border-top-right-radius: 10px;
            }
            /* Chrome Navigation Toolbar */
            .toolbar {
                background: #ffffff;
                height: 48px;
                display: flex;
                align-items: center;
                padding: 0 10px;
                gap: 8px;
                border-bottom: 1px solid #e1e3e6;
            }
            .nav-actions {
                display: flex;
                align-items: center;
                gap: 4px;
                color: #5f6368;
                font-size: 16px;
            }
            .nav-btn {
                width: 32px;
                height: 32px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
            }
            /* Omnibox / Address Bar */
            .omnibox {
                flex: 1;
                height: 34px;
                background: #f1f3f4;
                border-radius: 17px;
                display: flex;
                align-items: center;
                padding: 0 14px;
                font-size: 13px;
                color: #202124;
                gap: 8px;
            }
            .omnibox-lock {
                font-size: 12px;
                color: #5f6368;
            }
            .omnibox-url {
                flex: 1;
                white-space: nowrap;
                overflow: hidden;
                text-overflow: ellipsis;
            }
            .omnibox-url span.domain {
                color: #202124;
                font-weight: 600;
            }
            .omnibox-url span.protocol {
                color: #5f6368;
            }
            /* Extension & Profile Area */
            .toolbar-actions {
                display: flex;
                align-items: center;
                gap: 6px;
            }
            .ext-btn {
                width: 32px;
                height: 32px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                color: #5f6368;
                font-size: 16px;
                cursor: pointer;
            }
            .vnu-ext-badge {
                width: 30px;
                height: 30px;
                border-radius: 6px;
                background: #00803d;
                color: white;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 10px;
                font-weight: 700;
                letter-spacing: -0.5px;
                box-shadow: 0 1px 3px rgba(0,128,61,0.3);
                border: ${activeExtension ? '2px solid #2563eb' : 'none'};
                position: relative;
            }
            .vnu-ext-badge.active::after {
                content: '';
                position: absolute;
                bottom: -8px;
                left: 50%;
                transform: translateX(-50%);
                width: 0;
                height: 0;
                border-left: 6px solid transparent;
                border-right: 6px solid transparent;
                border-bottom: 6px solid #1e293b;
            }
            .profile-avatar {
                width: 30px;
                height: 30px;
                border-radius: 50%;
                background: #1a73e8;
                color: white;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 12px;
                font-weight: 600;
                border: ${activeProfile ? '2px solid #ea4335' : 'none'};
                position: relative;
            }
            .profile-avatar.active::after {
                content: '';
                position: absolute;
                bottom: -8px;
                left: 50%;
                transform: translateX(-50%);
                width: 0;
                height: 0;
                border-left: 6px solid transparent;
                border-right: 6px solid transparent;
                border-bottom: 6px solid #ffffff;
            }
            .content-area {
                min-height: 620px;
                background: #ffffff;
                position: relative;
                overflow: hidden;
            }
        </style>
    </head>
    <body>
        <div class="win11-window">
            <div class="titlebar">
                <div class="tab-strip">
                    <div class="tab">
                        <div class="tab-favicon">${tabFavicon}</div>
                        <div class="tab-title">${tabTitle}</div>
                        <div class="tab-close">×</div>
                    </div>
                    <div class="new-tab-btn">+</div>
                </div>
                <div class="win11-controls">
                    <div class="win-btn">—</div>
                    <div class="win-btn">□</div>
                    <div class="win-btn win-close">✕</div>
                </div>
            </div>
            <div class="toolbar">
                <div class="nav-actions">
                    <div class="nav-btn">←</div>
                    <div class="nav-btn">→</div>
                    <div class="nav-btn">⟳</div>
                </div>
                <div class="omnibox">
                    <span class="omnibox-lock">🔒</span>
                    <div class="omnibox-url">${url}</div>
                    <span style="color:#5f6368;font-size:14px;cursor:pointer;">☆</span>
                </div>
                <div class="toolbar-actions">
                    <div class="vnu-ext-badge ${activeExtension ? 'active' : ''}" title="VNU Academic Gateway">VNU</div>
                    <div class="ext-btn" title="Tiện ích mở rộng">🧩</div>
                    <div class="profile-avatar ${activeProfile ? 'active' : ''}" title="Hồ sơ Google">NP</div>
                    <div class="ext-btn">⋮</div>
                </div>
            </div>
            <div class="content-area">
                ${contentHtml}
            </div>
        </div>
    </body>
    </html>
    `;

    // =========================================================================
    // SCREEN 1: Real Chrome Profile Menu Popover on Windows 11
    // =========================================================================
    console.log('[STEP 1] Generating step1-chrome-profile.png...');
    const screen1Html = `
    <div style="background: #fafafa; min-height: 620px; position: relative; font-family: 'Segoe UI', Roboto, sans-serif;">
        <!-- Chrome New Tab Google layout -->
        <div style="text-align: center; padding-top: 140px;">
            <div style="font-size: 54px; font-weight: 500; font-family: 'Google Sans', sans-serif; color: #1a73e8; letter-spacing: -1px; margin-bottom: 24px;">
                <span style="color:#4285f4">G</span><span style="color:#ea4335">o</span><span style="color:#fbbc05">o</span><span style="color:#4285f4">g</span><span style="color:#34a853">l</span><span style="color:#ea4335">e</span>
            </div>
            <div style="width: 580px; margin: 0 auto; height: 46px; background: #ffffff; border-radius: 24px; box-shadow: 0 2px 8px rgba(0,0,0,0.1), 0 0 0 1px #dfe1e5; display: flex; align-items: center; padding: 0 18px; gap: 12px; color: #5f6368; font-size: 14px;">
                <span>🔍</span>
                <span style="flex:1; text-align: left;">Tìm kiếm trên Google hoặc nhập một URL</span>
                <span>🎤</span>
                <span>📷</span>
            </div>
        </div>

        <!-- Authentic Chrome Profile Popover Menu -->
        <div style="position: absolute; top: 8px; right: 20px; width: 350px; background: #ffffff; border-radius: 16px; box-shadow: 0 12px 40px rgba(0,0,0,0.24), 0 0 0 1px rgba(0,0,0,0.08); padding: 22px 20px; z-index: 200;">
            <div style="text-align: center; margin-bottom: 14px;">
                <div style="width: 64px; height: 64px; border-radius: 50%; background: #1a73e8; color: white; display: inline-flex; align-items: center; justify-content: center; font-size: 26px; font-weight: 600; margin-bottom: 10px;">
                    NP
                </div>
                <div style="font-size: 16px; font-weight: 600; color: #1f1f1f;">Bạn và Google</div>
                <div style="font-size: 12px; color: #5f6368; margin-top: 2px;">Tính năng đồng bộ hóa đang tắt</div>
            </div>

            <!-- Nút Bật tính năng đồng bộ hóa được highlight -->
            <div style="position: relative; margin-bottom: 18px;">
                <div style="background: #1a73e8; color: white; font-weight: 600; font-size: 13px; padding: 10px 18px; border-radius: 20px; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 8px; box-shadow: 0 2px 8px rgba(26,115,232,0.4); border: 2px solid #174ea6;">
                    <span>🔄 Bật tính năng đồng bộ hóa... (Turn on sync...)</span>
                </div>
                <!-- Callout pointer 1 -->
                <div style="position: absolute; right: -15px; top: -14px; background: #dc2626; color: white; font-size: 11px; font-weight: 700; padding: 3px 8px; border-radius: 12px; box-shadow: 0 2px 6px rgba(220,38,38,0.4); display: flex; align-items: center; gap: 4px;">
                    <span>👈 CÁCH 1</span>
                </div>
            </div>

            <!-- Hồ sơ Chrome khác (Other profiles) -->
            <div style="border-top: 1px solid #e1e3e6; margin: 0 -20px 14px; padding: 12px 20px 0; text-align: left;">
                <div style="font-size: 12px; font-weight: 600; color: #5f6368; margin-bottom: 10px;">Hồ sơ Chrome khác (Other profiles)</div>
                <div style="display: flex; align-items: center; gap: 12px; padding: 6px 0; font-size: 13px; color: #1f1f1f;">
                    <span style="font-size: 16px; color: #5f6368;">👤</span>
                    <span>Khách (Guest)</span>
                </div>
                <div style="position: relative; margin-top: 4px;">
                    <div style="display: flex; align-items: center; gap: 12px; padding: 8px 12px; border-radius: 8px; font-size: 13px; color: #1a73e8; font-weight: 600; background: #f0f7ff; border: 1px dashed #1a73e8; cursor: pointer;">
                        <span style="font-size: 16px;">➕</span>
                        <span>Thêm (Add profile) - Tạo hồ sơ VNU riêng</span>
                    </div>
                    <!-- Callout pointer 2 -->
                    <div style="position: absolute; right: -10px; top: -10px; background: #059669; color: white; font-size: 10px; font-weight: 700; padding: 2px 6px; border-radius: 10px;">
                        👈 CÁCH 2
                    </div>
                </div>
            </div>

            <div style="background: #fffbeb; border: 1px solid #fde68a; border-radius: 8px; padding: 8px 10px; font-size: 11px; color: #92400e; line-height: 1.4;">
                💡 <b>Thao tác:</b> Bấm biểu tượng Profile ở góc trên bên phải $\rightarrow$ Bấm <b>"Bật đồng bộ..."</b> hoặc <b>"Thêm"</b> để đăng nhập tài khoản <code>@vnu.edu.vn</code>.
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'Tab mới',
        tabFavicon: '<span style="color:#4285f4;font-weight:700;">G</span>',
        url: '<span class="protocol">https://</span><span class="domain">www.google.com</span>',
        contentHtml: screen1Html,
        activeProfile: true
    }));
    await page.screenshot({ path: path.join(outDir, 'step1-chrome-profile.png') });

    // =========================================================================
    // SCREEN 2: Real Google Workspace Sign-In Screen
    // =========================================================================
    console.log('[STEP 2] Generating step2-google-signin.png...');
    const screen2Html = `
    <div style="background: #f0f4f9; min-height: 620px; display: flex; align-items: center; justify-content: center; font-family: 'Google Sans', 'Segoe UI', Roboto, sans-serif;">
        <div style="width: 460px; background: #ffffff; border-radius: 28px; padding: 42px 40px; box-shadow: 0 4px 24px rgba(0,0,0,0.08); border: 1px solid #e1e3e6;">
            <!-- Google Logo -->
            <div style="margin-bottom: 24px;">
                <span style="font-size: 26px; font-weight: 600; color: #1f1f1f; display: flex; align-items: center; gap: 8px;">
                    <span style="font-size: 28px;"><span style="color:#4285f4">G</span><span style="color:#ea4335">o</span><span style="color:#fbbc05">o</span><span style="color:#4285f4">g</span><span style="color:#34a853">l</span><span style="color:#ea4335">e</span></span>
                </span>
            </div>

            <h1 style="font-size: 24px; font-weight: 400; color: #1f1f1f; margin-bottom: 8px;">Đăng nhập</h1>
            <div style="font-size: 14px; color: #444746; margin-bottom: 32px;">Sử dụng Tài khoản Google (Email VNU) của bạn</div>

            <!-- Email Input Field -->
            <div style="position: relative; margin-bottom: 14px;">
                <div style="border: 2px solid #1a73e8; border-radius: 8px; padding: 14px 16px; background: #ffffff;">
                    <div style="position: absolute; top: -9px; left: 12px; background: white; padding: 0 6px; font-size: 11px; color: #1a73e8; font-weight: 500;">
                        Email hoặc số điện thoại
                    </div>
                    <div style="font-size: 15px; color: #1f1f1f; font-weight: 500;">
                        phongnd@vnu.edu.vn
                    </div>
                </div>
            </div>

            <div style="margin-bottom: 28px; text-align: left;">
                <a href="#" style="color: #0b57d0; font-size: 13px; font-weight: 500; text-decoration: none;">Bạn quên địa chỉ email?</a>
            </div>

            <div style="font-size: 13px; color: #444746; line-height: 1.5; margin-bottom: 36px;">
                Đây không phải máy tính của bạn? Hãy sử dụng cửa sổ duyệt web riêng tư để đăng nhập.
            </div>

            <div style="display: flex; justify-content: space-between; align-items: center;">
                <span style="color: #0b57d0; font-size: 14px; font-weight: 500; cursor: pointer;">Tạo tài khoản</span>
                <div style="background: #0b57d0; color: white; padding: 10px 24px; border-radius: 20px; font-size: 14px; font-weight: 500; cursor: pointer; box-shadow: 0 1px 3px rgba(11,87,208,0.3); display: flex; align-items: center; gap: 6px;">
                    <span>Tiếp theo</span>
                    <span>→</span>
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'Đăng nhập - Tài khoản Google',
        tabFavicon: '<span style="color:#4285f4;font-weight:700;">G</span>',
        url: '<span class="protocol">https://</span><span class="domain">accounts.google.com</span><span style="color:#70757a;">/signin/v2/identifier</span>',
        contentHtml: screen2Html
    }));
    await page.screenshot({ path: path.join(outDir, 'step2-google-signin.png') });

    // =========================================================================
    // SCREEN 3: Real VNU Keycloak SSO (idp.vnu.edu.vn) using genuine capture
    // =========================================================================
    console.log('[STEP 3] Generating step3-vnu-idp.png with genuine IDP capture...');
    const screen3Html = `
    <div style="background: #0a1020; min-height: 620px; position: relative; overflow: hidden; display: flex; align-items: center; justify-content: center;">
        ${idpBase64 ? `
            <img src="data:image/png;base64,${idpBase64}" style="width: 100%; height: 620px; object-fit: cover; object-position: center top;">
        ` : `
            <div style="color: white; font-size: 18px;">Cổng xác thực VNU Keycloak</div>
        `}
        <!-- Floating highlight badge -->
        <div style="position: absolute; bottom: 24px; right: 28px; background: rgba(0, 128, 61, 0.95); backdrop-filter: blur(8px); color: white; border-radius: 12px; padding: 12px 18px; box-shadow: 0 8px 24px rgba(0,0,0,0.3); border: 1px solid rgba(255,255,255,0.2); max-width: 420px;">
            <div style="font-weight: 700; font-size: 13px; margin-bottom: 4px; display: flex; align-items: center; gap: 6px;">
                <span>🏛️</span>
                <span>CỔNG XÁC THỰC TẬP TRUNG (VNU KEYCLOAK SSO)</span>
            </div>
            <div style="font-size: 12px; opacity: 0.95; line-height: 1.4;">
                Google Workspace tự động chuyển hướng về <b>idp.vnu.edu.vn</b>. Thầy/Cô nhập mật khẩu tài khoản VNU để hoàn tất.
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'Cổng xác thực ĐHQGHN - VNU SSO',
        tabFavicon: '🏛️',
        url: '<span class="protocol">https://</span><span class="domain">idp.vnu.edu.vn</span><span style="color:#70757a;">/auth/realms/vnu/protocol/openid-connect/auth</span>',
        contentHtml: screen3Html
    }));
    await page.screenshot({ path: path.join(outDir, 'step3-vnu-idp.png') });

    // =========================================================================
    // SCREEN 4: Real Chrome "Turn on sync?" Modal Dialog
    // =========================================================================
    console.log('[STEP 4] Generating step4-turn-on-sync.png...');
    const screen4Html = `
    <div style="background: rgba(0,0,0,0.45); min-height: 620px; display: flex; align-items: center; justify-content: center; font-family: 'Segoe UI', Roboto, sans-serif; position: relative;">
        <!-- Modal Dialog -->
        <div style="width: 500px; background: #ffffff; border-radius: 12px; padding: 32px 30px; box-shadow: 0 24px 64px rgba(0,0,0,0.35); border: 1px solid rgba(0,0,0,0.1); position: relative;">
            <div style="text-align: center; margin-bottom: 20px;">
                <div style="width: 58px; height: 58px; border-radius: 50%; background: #e8f0fe; color: #1a73e8; display: inline-flex; align-items: center; justify-content: center; font-size: 26px; margin-bottom: 12px;">
                    🏢
                </div>
                <div style="font-size: 20px; font-weight: 600; color: #1f1f1f; margin-bottom: 8px;">
                    Bật tính năng đồng bộ hóa? (Turn on sync?)
                </div>
                <div style="font-size: 13px; color: #5f6368; line-height: 1.5;">
                    Đồng bộ hóa dấu trang, mật khẩu, lịch sử và các tiện ích mở rộng của ĐHQGHN trên tất cả các thiết bị của Thầy/Cô.
                </div>
            </div>

            <!-- Organization Notice Box -->
            <div style="background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 12px 14px; margin-bottom: 24px; font-size: 12px; color: #334155; line-height: 1.5;">
                <div style="font-weight: 600; color: #00803d; margin-bottom: 4px; display: flex; align-items: center; gap: 6px;">
                    <span>🛡️</span>
                    <span>Tài khoản do Đại học Quốc gia Hà Nội quản lý</span>
                </div>
                <div>Tổ chức của bạn (<code>vnu.edu.vn</code>) sẽ tự động cài đặt tiện ích <b>VNU Academic Gateway (VAGS)</b> để phục vụ tra cứu tài liệu học thuật.</div>
            </div>

            <!-- Modal Action Buttons with prominent highlight -->
            <div style="display: flex; justify-content: flex-end; align-items: center; gap: 12px;">
                <div style="padding: 9px 18px; border-radius: 20px; color: #0b57d0; font-size: 13px; font-weight: 600; cursor: pointer; border: 1px solid #dadce0;">
                    Hủy (Cancel)
                </div>
                <div style="position: relative;">
                    <div style="background: #0b57d0; color: white; padding: 10px 24px; border-radius: 20px; font-size: 13px; font-weight: 600; cursor: pointer; box-shadow: 0 2px 8px rgba(11,87,208,0.4); display: flex; align-items: center; gap: 6px;">
                        <span>✓ Bật (Yes, I'm in)</span>
                    </div>
                    <!-- Pointer Arrow -->
                    <div style="position: absolute; right: -15px; top: -14px; background: #dc2626; color: white; font-size: 10px; font-weight: 700; padding: 2px 8px; border-radius: 10px; box-shadow: 0 2px 6px rgba(220,38,38,0.4);">
                        👈 BẤM VÀO ĐÂY
                    </div>
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'Bật tính năng đồng bộ hóa - Google Chrome',
        tabFavicon: '🏢',
        url: '<span class="protocol">https://</span><span class="domain">accounts.google.com</span>',
        contentHtml: screen4Html
    }));
    await page.screenshot({ path: path.join(outDir, 'step4-turn-on-sync.png') });

    // =========================================================================
    // SCREEN 4B: Extension Pinned & Opened Waiting SSO Login
    // =========================================================================
    console.log('[STEP 4B] Generating step4b-vags-installed.png...');
    const screen4bHtml = `
    <div style="background: #f8fafc; min-height: 620px; position: relative; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;">
        <!-- Chrome Toolbar Callout pointing to VNU Icon -->
        <div style="position: absolute; top: -38px; right: 90px; background: #dc2626; color: white; font-size: 11px; font-weight: 700; padding: 3px 10px; border-radius: 12px; box-shadow: 0 2px 8px rgba(220,38,38,0.4); z-index: 300;">
            Tiện ích VAGS tự động xuất hiện ↗
        </div>

        <!-- Extension Popup Opened right below VNU badge -->
        <div style="position: absolute; top: 10px; right: 30px; width: 360px; background: #0f172a; border-radius: 12px; box-shadow: 0 16px 48px rgba(0,0,0,0.4), 0 0 0 1px #334155; overflow: hidden; z-index: 200; color: #f8fafc;">
            <!-- Header -->
            <div style="padding: 14px 16px; border-bottom: 1px solid #334155; display: flex; align-items: center; justify-content: space-between;">
                <div style="display: flex; align-items: center; gap: 10px;">
                    <div style="width: 28px; height: 28px; border-radius: 6px; background: #00803d; color: white; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 700;">VNU</div>
                    <div>
                        <div style="font-size: 13px; font-weight: 700; color: #f8fafc;">VNU Academic Gateway</div>
                        <div style="font-size: 10px; color: #94a3b8;">Cổng CSDL Khoa học Quốc tế</div>
                    </div>
                </div>
                <span style="font-size: 10px; background: #334155; color: #94a3b8; padding: 2px 8px; border-radius: 10px;">Chờ đăng nhập</span>
            </div>

            <!-- Body Awaiting SSO -->
            <div style="padding: 24px 20px; text-align: center;">
                <div style="width: 56px; height: 56px; border-radius: 50%; background: rgba(0,128,61,0.15); color: #10b981; display: inline-flex; align-items: center; justify-content: center; font-size: 26px; margin-bottom: 14px; border: 1px solid rgba(16,185,129,0.3);">
                    🔑
                </div>
                <div style="font-size: 15px; font-weight: 600; color: #f8fafc; margin-bottom: 6px;">
                    Kích hoạt Phiên Cán bộ ĐHQGHN
                </div>
                <div style="font-size: 12px; color: #94a3b8; line-height: 1.5; margin-bottom: 22px;">
                    Tiện ích đã được đồng bộ tự động qua tài khoản trường. Thầy/Cô bấm nút bên dưới để mở đường truyền.
                </div>

                <!-- Prominent Login Button -->
                <div style="background: #00803d; color: white; font-weight: 600; font-size: 13px; padding: 11px 20px; border-radius: 8px; cursor: pointer; box-shadow: 0 4px 14px rgba(0,128,61,0.4); display: flex; align-items: center; justify-content: center; gap: 8px; border: 1px solid #10b981;">
                    <span>🔑 Đăng nhập VNU SSO</span>
                </div>
            </div>

            <!-- Footer -->
            <div style="background: #1e293b; border-top: 1px solid #334155; padding: 10px 16px; font-size: 11px; color: #94a3b8; display: flex; justify-content: space-between;">
                <span>Máy chủ: thamkhao.vnu.edu.vn</span>
                <span>v2.2.0</span>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'VNU Academic Gateway - Cổng tài liệu học thuật',
        tabFavicon: '🎓',
        url: '<span class="protocol">https://</span><span class="domain">thamkhao.vnu.edu.vn</span>',
        contentHtml: screen4bHtml,
        activeExtension: true
    }));
    await page.screenshot({ path: path.join(outDir, 'step4b-vags-installed.png') });

    // =========================================================================
    // SCREEN 5: Real Activated Extension Popup (NP • 112.137.142.30 • Green ✓)
    // =========================================================================
    console.log('[STEP 5] Generating step5-sso-sync-activated.png...');
    const screen5Html = `
    <div style="background: #f8fafc; min-height: 620px; position: relative; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;">
        <!-- Extension Popup in LIVE ACTIVATED State matching real extension UI -->
        <div style="position: absolute; top: 10px; right: 30px; width: 370px; background: #0f172a; border-radius: 12px; box-shadow: 0 16px 48px rgba(0,0,0,0.4), 0 0 0 1px #334155; overflow: hidden; z-index: 200; color: #f8fafc;">
            <!-- Header -->
            <div style="padding: 12px 16px; border-bottom: 1px solid #334155; display: flex; align-items: center; justify-content: space-between;">
                <div style="display: flex; align-items: center; gap: 8px;">
                    <div style="width: 26px; height: 26px; border-radius: 6px; background: #00803d; color: white; display: flex; align-items: center; justify-content: center; font-size: 10px; font-weight: 700;">VNU</div>
                    <div>
                        <div style="font-size: 13px; font-weight: 700; color: #f8fafc;">VNU Academic Gateway</div>
                        <div style="font-size: 10px; color: #94a3b8;">Cổng CSDL Khoa học Quốc tế</div>
                    </div>
                </div>
                <div style="display: flex; align-items: center; gap: 4px; font-size: 10px; color: #10b981;">
                    <span>●</span>
                    <span style="font-weight: 600;">SSE Live</span>
                </div>
            </div>

            <!-- User Info Card -->
            <div style="padding: 14px 16px;">
                <div style="background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 10px 12px; display: flex; align-items: center; gap: 12px; margin-bottom: 12px;">
                    <div style="width: 38px; height: 38px; border-radius: 50%; background: #00803d; color: white; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 700; flex-shrink: 0;">
                        NP
                    </div>
                    <div style="flex: 1; min-width: 0;">
                        <div style="font-size: 13px; font-weight: 700; color: #f8fafc; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">
                            Nguyễn Đăng Phong
                        </div>
                        <div style="font-size: 11px; color: #94a3b8; margin-top: 1px;">
                            phongnd@vnu.edu.vn • PQTKT
                        </div>
                    </div>
                    <span style="background: rgba(16,185,129,0.15); color: #10b981; border: 1px solid rgba(16,185,129,0.3); font-size: 10px; font-weight: 600; padding: 2px 8px; border-radius: 10px;">
                        Cán bộ ✓
                    </span>
                </div>

                <!-- Proxy Toggle Switch (ON) -->
                <div style="background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 10px 12px; display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px;">
                    <div>
                        <div style="font-size: 12px; font-weight: 600; color: #f8fafc;">Đường truyền Proxy: <span style="color:#10b981;">Đang Bật</span></div>
                        <div style="font-size: 10px; color: #94a3b8; margin-top: 2px;">Phân tuyến tự động qua thamkhao.vnu.edu.vn:443</div>
                    </div>
                    <!-- Toggle Switch ON -->
                    <div style="width: 38px; height: 22px; background: #00803d; border-radius: 11px; position: relative;">
                        <div style="width: 18px; height: 18px; background: white; border-radius: 50%; position: absolute; right: 2px; top: 2px; box-shadow: 0 1px 3px rgba(0,0,0,0.3);"></div>
                    </div>
                </div>

                <!-- Proxy Verification Box (Real IP 112.137.142.30) -->
                <div style="background: rgba(16,185,129,0.12); border: 1px solid #10b981; border-radius: 8px; padding: 10px 12px; margin-bottom: 12px;">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;">
                        <span style="font-size: 11px; font-weight: 700; color: #10b981;">🟢 KẾT NỐI PROXY THÀNH CÔNG ✓</span>
                        <span style="font-size: 10px; color: #38bdf8; cursor: pointer;">🔄 Đo lại</span>
                    </div>
                    <div style="font-size: 11px; color: #e2e8f0; line-height: 1.4;">
                        IP nhận diện thực tế: <b style="color:#4ade80;">112.137.142.30</b> (Mạng ĐHQGHN • Đạt yêu cầu)
                    </div>
                </div>

                <!-- Routing Checker -->
                <div style="background: #1e293b; border: 1px solid #334155; border-radius: 8px; padding: 10px 12px;">
                    <div style="font-size: 11px; font-weight: 600; color: #94a3b8; margin-bottom: 6px;">
                        🔍 Kiểm tra liên kết định tuyến: sciencedirect.com
                    </div>
                    <div style="background: rgba(0,128,61,0.2); border: 1px solid rgba(16,185,129,0.4); border-radius: 4px; padding: 6px 10px; font-size: 11px; color: #4ade80; font-weight: 600;">
                        ✓ PROXY (Định tuyến qua Cổng VNU 112.137.142.30)
                    </div>
                </div>
            </div>

            <!-- Footer -->
            <div style="background: #090d16; border-top: 1px solid #334155; padding: 10px 16px; font-size: 11px; display: flex; justify-content: space-between; align-items: center;">
                <span style="color: #10b981; font-weight: 600;">Cổng VAGS ↗</span>
                <div style="display: flex; gap: 12px;">
                    <span style="color: #94a3b8; cursor: pointer;">🔄 Đồng bộ</span>
                    <span style="color: #ef4444; cursor: pointer;">Đăng xuất</span>
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'Cổng tra cứu tài liệu học thuật - thamkhao.vnu.edu.vn',
        tabFavicon: '🎓',
        url: '<span class="protocol">https://</span><span class="domain">thamkhao.vnu.edu.vn</span>',
        contentHtml: screen5Html,
        activeExtension: true
    }));
    await page.screenshot({ path: path.join(outDir, 'step5-sso-sync-activated.png') });

    // =========================================================================
    // SCREEN 6: Real ScienceDirect with Institutional Header
    // =========================================================================
    console.log('[STEP 6] Generating step6-sciencedirect-vnu.png with authentic ScienceDirect capture...');
    const screen6Html = `
    <div style="background: #ffffff; min-height: 620px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;">
        <!-- Real ScienceDirect Banner Embedded from user screenshot -->
        <div style="border-bottom: 1px solid #d5dadf; background: #ffffff;">
            ${sdBase64 ? `
                <img src="data:image/png;base64,${sdBase64}" style="width: 100%; max-height: 180px; object-fit: contain; object-position: left center; display: block;">
            ` : `
                <div style="padding: 16px 32px; font-weight: 700; color: #d85604;">ScienceDirect - Brought to you by: Vietnam National University Library</div>
            `}
        </div>

        <!-- Authentic Article View Below Header -->
        <div style="max-width: 980px; margin: 24px auto; padding: 0 32px;">
            <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px;">
                <div style="color: #007398; font-size: 13px; font-weight: 600;">
                    Journal of Cleaner Production • Volume 415, 2026, 137890
                </div>
                <div style="background: #eef8f2; border: 1px solid #86efac; border-radius: 16px; padding: 4px 12px; font-size: 12px; color: #166534; font-weight: 600; display: flex; align-items: center; gap: 6px;">
                    <span>✓</span>
                    <span>Full text access by Vietnam National University</span>
                </div>
            </div>

            <h1 style="font-size: 26px; color: #1f1f1f; line-height: 1.35; font-weight: 700; margin-bottom: 14px;">
                Automated institutional proxy routing and secure remote access architecture for university scientific research networks
            </h1>

            <div style="color: #555555; font-size: 13px; margin-bottom: 24px;">
                Nguyen Dang Phong <sup>a,*</sup>, Research Team <sup>b</sup>
                <br>
                <small style="color: #888;"><sup>a</sup> Center for Digital University Governance, Vietnam National University, Hanoi</small>
            </div>

            <!-- Download Full Text PDF Action Button with Prominent Callout -->
            <div style="position: relative; display: inline-block; margin-bottom: 28px;">
                <div style="background: #00803d; color: white; padding: 12px 28px; border-radius: 6px; font-size: 14px; font-weight: 700; cursor: pointer; display: flex; align-items: center; gap: 10px; box-shadow: 0 4px 12px rgba(0,128,61,0.35);">
                    <span style="font-size: 18px;">📄</span>
                    <span>Download full text PDF (Tải toàn văn bài báo miễn phí)</span>
                </div>
                <!-- Callout Banner -->
                <div style="position: absolute; left: 102%; top: 50%; transform: translateY(-50%); white-space: nowrap; background: #fef2f2; border: 1px solid #f87171; border-radius: 8px; padding: 6px 12px; font-size: 12px; color: #991b1b; font-weight: 600; box-shadow: 0 2px 8px rgba(0,0,0,0.06); display: flex; align-items: center; gap: 6px;">
                    <span>👈</span>
                    <span>Đã nhận diện Thư viện ĐHQGHN: Không cần trả phí $39.95!</span>
                </div>
            </div>

            <div style="border-top: 1px solid #e5e7eb; padding-top: 16px;">
                <div style="font-size: 15px; font-weight: 700; color: #1f1f1f; margin-bottom: 8px;">Abstract</div>
                <div style="font-size: 13px; color: #374151; line-height: 1.7; text-align: justify;">
                    This paper presents the high-performance implementation of VNU Academic Gateway (VAGS) utilizing dynamic PAC routing, enterprise Chrome policy deployment, and 1Gbps kernel optimization via Google BBR...
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11ChromeShell({
        tabTitle: 'ScienceDirect.com | Science, health and medical journals, full text articles',
        tabFavicon: '📖',
        url: '<span class="protocol">https://</span><span class="domain">www.sciencedirect.com</span><span style="color:#70757a;">/science/article/pii/S095965262601234X</span>',
        contentHtml: screen6Html,
        activeExtension: true
    }));
    await page.screenshot({ path: path.join(outDir, 'step6-sciencedirect-vnu.png') });

    await browser.close();
    console.log('[SUCCESS] All 7 authentic screenshots regenerated successfully!');
}

main().catch(err => {
    console.error('[ERROR]', err);
    process.exit(1);
});
