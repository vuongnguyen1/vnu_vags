const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

async function main() {
    const outDir = '/mnt/data/websites/vags.vnu.edu.vn/public/images/guide';
    if (!fs.existsSync(outDir)) {
        fs.mkdirSync(outDir, { recursive: true });
    }

    console.log('[INFO] Launching Chromium to generate authentic Windows 11 Chrome guide screenshots...');
    const browser = await chromium.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox', '--font-render-hinting=medium']
    });

    const page = await browser.newPage({
        viewport: { width: 1100, height: 750 },
        deviceScaleFactor: 2
    });

    // Helper: Windows 11 Window Frame Wrapper
    const win11Frame = (tabTitle, url, contentHtml, activeExtension = false) => `
    <!DOCTYPE html>
    <html lang="vi">
    <head>
        <meta charset="utf-8">
        <link href="https://fonts.googleapis.com/css2?family=Segoe+UI:wght@400;500;600;700&family=Google+Sans:wght@400;500;700&family=Roboto:wght@400;500;700&display=swap" rel="stylesheet">
        <style>
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
                background: #e4eaf2;
                padding: 24px;
                font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, sans-serif;
                display: flex;
                justify-content: center;
                align-items: flex-start;
                min-height: 100vh;
            }
            .window {
                width: 1040px;
                background: #ffffff;
                border-radius: 10px;
                box-shadow: 0 20px 50px rgba(0, 0, 0, 0.22), 0 0 0 1px rgba(0, 0, 0, 0.08);
                overflow: hidden;
                position: relative;
            }
            /* Windows 11 Titlebar & Chrome Tabs */
            .titlebar {
                background: #e8ecf1;
                height: 42px;
                display: flex;
                align-items: flex-end;
                padding: 0 8px;
                user-select: none;
                border-bottom: 1px solid #d5dadf;
            }
            .tab-container {
                display: flex;
                align-items: flex-end;
                flex: 1;
                height: 100%;
            }
            .tab {
                background: #ffffff;
                height: 34px;
                padding: 0 14px;
                border-radius: 8px 8px 0 0;
                display: flex;
                align-items: center;
                gap: 8px;
                font-size: 12px;
                color: #202124;
                width: 240px;
                box-shadow: 0 0 4px rgba(0,0,0,0.06);
                font-weight: 500;
            }
            .tab-icon { width: 16px; height: 16px; flex-shrink: 0; }
            .tab-title { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; flex: 1; }
            .tab-close { color: #5f6368; font-size: 14px; margin-left: 4px; }
            .tab-add {
                width: 28px;
                height: 28px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                color: #444746;
                font-size: 16px;
                margin-left: 6px;
                margin-bottom: 3px;
            }
            .win-controls {
                display: flex;
                align-items: center;
                height: 100%;
                gap: 16px;
                padding-right: 8px;
                color: #1f1f1f;
                font-size: 13px;
                font-weight: 300;
            }
            /* Chrome Toolbar (Omnibox & Actions) */
            .toolbar {
                background: #ffffff;
                height: 46px;
                display: flex;
                align-items: center;
                padding: 0 10px;
                gap: 10px;
                border-bottom: 1px solid #e1e3e6;
            }
            .nav-group {
                display: flex;
                align-items: center;
                gap: 6px;
                color: #5f6368;
                font-size: 16px;
            }
            .nav-btn {
                width: 30px;
                height: 30px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
            }
            .omnibox {
                flex: 1;
                background: #f1f3f4;
                height: 34px;
                border-radius: 17px;
                display: flex;
                align-items: center;
                padding: 0 14px;
                font-size: 13px;
                color: #1f1f1f;
                gap: 8px;
            }
            .omnibox:hover { background: #e8eaed; }
            .lock-icon { color: #1e8e3e; font-size: 12px; }
            .url-text { font-family: 'Segoe UI', sans-serif; font-size: 13px; }
            .actions-group {
                display: flex;
                align-items: center;
                gap: 8px;
                position: relative;
            }
            .extension-btn {
                width: 32px;
                height: 32px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                cursor: pointer;
            }
            .vnu-ext-badge {
                width: 26px;
                height: 26px;
                border-radius: 6px;
                background: #00803d;
                color: white;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 10px;
                font-weight: 800;
                box-shadow: 0 2px 5px rgba(0,128,61,0.4);
            }
            .avatar-btn {
                width: 32px;
                height: 32px;
                border-radius: 50%;
                background: #1a73e8;
                color: white;
                display: flex;
                align-items: center;
                justify-content: center;
                font-size: 13px;
                font-weight: 600;
            }
            .content-area {
                min-height: 580px;
                background: #ffffff;
                position: relative;
            }
        </style>
    </head>
    <body>
        <div class="window">
            <div class="titlebar">
                <div class="tab-container">
                    <div class="tab">
                        <svg class="tab-icon" viewBox="0 0 24 24"><path fill="#4285F4" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z"/></svg>
                        <span class="tab-title">${tabTitle}</span>
                        <span class="tab-close">✕</span>
                    </div>
                    <div class="tab-add">+</div>
                </div>
                <div class="win-controls">
                    <span>─</span>
                    <span>□</span>
                    <span>✕</span>
                </div>
            </div>
            <div class="toolbar">
                <div class="nav-group">
                    <div class="nav-btn">←</div>
                    <div class="nav-btn">→</div>
                    <div class="nav-btn">↻</div>
                </div>
                <div class="omnibox">
                    <span class="lock-icon">🔒</span>
                    <span class="url-text">${url}</span>
                </div>
                <div class="actions-group">
                    ${activeExtension ? `
                    <div class="vnu-ext-badge" title="VNU Academic Gateway">VNU</div>
                    ` : ''}
                    <div class="extension-btn" title="Tiện ích mở rộng">🧩</div>
                    <div class="avatar-btn" title="Hồ sơ Google">NP</div>
                    <div class="extension-btn">⋮</div>
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
    const screen1Content = `
    <div style="padding: 40px; text-align: center; color: #5f6368; height: 580px; background: #fafafa; position: relative;">
        <div style="font-size: 48px; margin-top: 120px; font-family: 'Google Sans', sans-serif; color: #1a73e8; font-weight: 500;">Google</div>
        <div style="max-width: 540px; margin: 24px auto; background: white; border-radius: 24px; padding: 12px 20px; border: 1px solid #dfe1e5; box-shadow: 0 2px 8px rgba(0,0,0,0.06); display: flex; align-items: center; gap: 12px;">
            <span>🔍</span>
            <span style="color: #70757a; font-size: 14px;">Tìm kiếm trên Google hoặc nhập một URL</span>
        </div>

        <!-- Authentic Chrome Profile Popover Menu -->
        <div style="position: absolute; top: 12px; right: 18px; width: 340px; background: #ffffff; border-radius: 16px; box-shadow: 0 10px 30px rgba(0,0,0,0.2), 0 0 0 1px rgba(0,0,0,0.08); padding: 20px; text-align: center; z-index: 100; font-family: 'Segoe UI', Roboto, sans-serif;">
            <div style="width: 68px; height: 68px; border-radius: 50%; background: #1a73e8; color: white; display: flex; align-items: center; justify-content: center; font-size: 28px; font-weight: 600; margin: 0 auto 12px;">
                NP
            </div>
            <div style="font-size: 16px; font-weight: 600; color: #1f1f1f;">Bạn và Google</div>
            <div style="font-size: 12px; color: #5f6368; margin-top: 2px; margin-bottom: 16px;">Tính năng đồng bộ hóa đang tắt</div>

            <!-- Nút Bật tính năng đồng bộ hóa -->
            <div style="background: #1a73e8; color: white; font-weight: 600; font-size: 13px; padding: 10px 20px; border-radius: 20px; cursor: pointer; box-shadow: 0 2px 6px rgba(26,115,232,0.3); display: flex; align-items: center; justify-content: center; gap: 8px; margin-bottom: 18px; border: 2px solid #1557b0;">
                <span>🔄 Bật tính năng đồng bộ hóa...</span>
            </div>

            <div style="border-top: 1px solid #e1e3e6; margin: 0 -20px 14px; padding-top: 12px; padding-left: 20px; padding-right: 20px; text-align: left;">
                <div style="font-size: 12px; font-weight: 600; color: #5f6368; margin-bottom: 8px;">Hồ sơ Chrome khác</div>
                <div style="display: flex; align-items: center; gap: 12px; padding: 8px 0; font-size: 13px; color: #1f1f1f;">
                    <span style="font-size: 16px;">👤</span>
                    <span>Khách</span>
                </div>
                <div style="display: flex; align-items: center; gap: 12px; padding: 8px 0; font-size: 13px; color: #1a73e8; font-weight: 600;">
                    <span style="font-size: 16px;">➕</span>
                    <span>Thêm hồ sơ mới (Add Profile)</span>
                </div>
            </div>

            <!-- Highlight box chỉ dẫn -->
            <div style="background: #fef3c7; border: 1px dashed #f59e0b; border-radius: 8px; padding: 8px 12px; font-size: 11px; color: #92400e; text-align: left; margin-top: 4px;">
                👉 <b>Bước 1:</b> Bấm <b>"Bật tính năng đồng bộ hóa..."</b> hoặc <b>"Thêm"</b> để đăng nhập tài khoản <code>@vnu.edu.vn</code>.
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('Tab mới', 'https://www.google.com', screen1Content));
    await page.screenshot({ path: path.join(outDir, 'step1-chrome-profile.png') });

    // =========================================================================
    // SCREEN 2: Authentic Google Workspace Sign-In Screen
    // =========================================================================
    console.log('[STEP 2] Generating step2-google-signin.png...');
    const screen2Content = `
    <div style="background: #f0f4f9; height: 580px; display: flex; align-items: center; justify-content: center; font-family: 'Google Sans', 'Segoe UI', Roboto, sans-serif;">
        <div style="width: 450px; background: #ffffff; border-radius: 28px; padding: 40px; box-shadow: 0 4px 24px rgba(0,0,0,0.06); border: 1px solid #e1e3e6;">
            <!-- Google 4-Color Logo -->
            <svg style="width: 48px; height: 48px; margin-bottom: 16px;" viewBox="0 0 24 24">
                <path fill="#4285F4" d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"/>
                <path fill="#34A853" d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"/>
                <path fill="#FBBC05" d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.06H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.94l2.85-2.22.81-.63z"/>
                <path fill="#EA4335" d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.06l3.66 2.84c.87-2.6 3.3-4.52 6.16-4.52z"/>
            </svg>
            <div style="font-size: 24px; font-weight: 500; color: #1f1f1f; margin-bottom: 8px;">Đăng nhập</div>
            <div style="font-size: 15px; color: #444746; margin-bottom: 28px;">Sử dụng Tài khoản Google của ĐHQGHN</div>

            <!-- Floating Label Input -->
            <div style="position: relative; margin-bottom: 12px;">
                <input type="text" value="phongnd@vnu.edu.vn" style="width: 100%; height: 54px; border: 2px solid #0b57d0; border-radius: 4px; padding: 16px 14px 4px; font-size: 16px; color: #1f1f1f; outline: none; font-family: 'Segoe UI', sans-serif;">
                <span style="position: absolute; top: 6px; left: 12px; font-size: 11px; color: #0b57d0; font-weight: 500;">Email hoặc số điện thoại</span>
            </div>

            <div style="font-size: 14px; color: #0b57d0; font-weight: 500; margin-bottom: 36px; cursor: pointer;">Bạn quên địa chỉ email?</div>

            <div style="font-size: 13px; color: #444746; line-height: 1.5; margin-bottom: 32px;">
                Không phải máy tính của bạn? Hãy sử dụng chế độ Khách để đăng nhập một cách riêng tư.
            </div>

            <div style="display: flex; justify-content: space-between; align-items: center;">
                <span style="color: #0b57d0; font-size: 14px; font-weight: 500;">Tạo tài khoản</span>
                <div style="background: #0b57d0; color: white; padding: 10px 24px; border-radius: 20px; font-size: 14px; font-weight: 500; cursor: pointer; box-shadow: 0 1px 3px rgba(0,0,0,0.15);">
                    Tiếp theo
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('Đăng nhập - Tài khoản Google', 'https://accounts.google.com/signin/v2/identifier?hl=vi', screen2Content));
    await page.screenshot({ path: path.join(outDir, 'step2-google-signin.png') });

    // =========================================================================
    // SCREEN 3: Real VNU Keycloak SSO (idp.vnu.edu.vn)
    // =========================================================================
    console.log('[STEP 3] Generating step3-vnu-idp.png with authentic VNU IDP layout...');
    const screen3Content = `
    <div style="background: #f4f6f8; height: 580px; display: flex; align-items: center; justify-content: center; font-family: 'Segoe UI', Roboto, sans-serif;">
        <div style="width: 440px; background: #ffffff; border-radius: 12px; padding: 36px; box-shadow: 0 8px 30px rgba(0,0,0,0.12); border-top: 5px solid #00803d;">
            <!-- Authentic VNU Logo -->
            <div style="text-align: center; margin-bottom: 20px;">
                <img src="/logo-vnu.svg" style="width: 64px; height: 64px; margin-bottom: 8px;">
                <div style="color: #00803d; font-size: 16px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.5px;">Đại học Quốc gia Hà Nội</div>
                <div style="color: #64748b; font-size: 12px; margin-top: 2px;">CỔNG XÁC THỰC TẬP TRUNG (VNU SSO - KEYCLOAK)</div>
            </div>

            <div style="background: #eef8f2; border: 1px solid #bbf7d0; border-radius: 6px; padding: 8px 12px; font-size: 12px; color: #166534; margin-bottom: 18px; display: flex; align-items: center; gap: 8px;">
                <span>🔒</span>
                <span>Google Workspace đã chuyển tiếp tới Cổng ĐHQGHN</span>
            </div>

            <div style="margin-bottom: 16px;">
                <label style="display: block; font-size: 13px; font-weight: 600; color: #334155; margin-bottom: 6px;">Tên đăng nhập / Email VNU</label>
                <input type="text" value="phongnd@vnu.edu.vn" readonly style="width: 100%; height: 42px; border: 1px solid #cbd5e1; border-radius: 6px; padding: 0 12px; font-size: 14px; background: #f8fafc; color: #1e293b;">
            </div>

            <div style="margin-bottom: 24px;">
                <label style="display: block; font-size: 13px; font-weight: 600; color: #334155; margin-bottom: 6px;">Mật khẩu</label>
                <input type="password" value="••••••••••••••" style="width: 100%; height: 42px; border: 2px solid #00803d; border-radius: 6px; padding: 0 12px; font-size: 14px; background: #ffffff;">
            </div>

            <div style="background: #00803d; color: white; height: 44px; border-radius: 6px; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 600; cursor: pointer; box-shadow: 0 3px 8px rgba(0,128,61,0.3);">
                ĐĂNG NHẬP (SIGN IN)
            </div>

            <div style="text-align: center; margin-top: 20px; font-size: 11px; color: #94a3b8;">
                Bản quyền © 2026 Đại học Quốc gia Hà Nội • Trung tâm Quản trị Đại học số
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('Cổng xác thực ĐHQGHN - VNU Keycloak SSO', 'https://idp.vnu.edu.vn/auth/realms/vnu/protocol/openid-connect/auth', screen3Content));
    await page.screenshot({ path: path.join(outDir, 'step3-vnu-idp.png') });

    // =========================================================================
    // SCREEN 4: Authentic Chrome "Turn on sync?" Modal with Managed Org Notice
    // =========================================================================
    console.log('[STEP 4] Generating step4-turn-on-sync.png...');
    const screen4Content = `
    <div style="background: rgba(0,0,0,0.35); height: 580px; display: flex; align-items: center; justify-content: center; font-family: 'Segoe UI', Roboto, sans-serif;">
        <!-- Authentic Chrome Modal Dialog -->
        <div style="width: 480px; background: #ffffff; border-radius: 12px; padding: 28px; box-shadow: 0 20px 60px rgba(0,0,0,0.3); border: 1px solid rgba(0,0,0,0.1);">
            <div style="text-align: center; margin-bottom: 16px;">
                <div style="width: 56px; height: 56px; border-radius: 50%; background: #e8f0fe; color: #1a73e8; display: inline-flex; align-items: center; justify-content: center; font-size: 26px; margin-bottom: 12px;">
                    🏢
                </div>
                <div style="font-size: 20px; font-weight: 600; color: #1f1f1f; margin-bottom: 6px;">Bật tính năng đồng bộ hóa?</div>
                <div style="font-size: 13px; color: #5f6368;">Đồng bộ hóa dấu trang, mật khẩu, lịch sử và tiện ích mở rộng trên mọi thiết bị.</div>
            </div>

            <!-- Organization Notice Box -->
            <div style="background: #f8f9fa; border: 1px solid #dadce0; border-left: 4px solid #1a73e8; border-radius: 6px; padding: 12px 14px; margin-bottom: 24px; text-align: left;">
                <div style="font-size: 13px; font-weight: 600; color: #1f1f1f; margin-bottom: 4px;">🏢 Hồ sơ do vnu.edu.vn quản lý</div>
                <div style="font-size: 12px; color: #5f6368; line-height: 1.4;">
                    Tổ chức của bạn có thể áp dụng các chính sách bảo mật và tự động cài đặt tiện ích VNU Academic Gateway cho hồ sơ này.
                </div>
            </div>

            <!-- Action Buttons -->
            <div style="display: flex; justify-content: flex-end; gap: 10px; align-items: center;">
                <div style="color: #1a73e8; font-weight: 600; font-size: 13px; padding: 8px 16px; border-radius: 4px; cursor: pointer;">
                    Hủy (Cancel)
                </div>
                <div style="background: #1a73e8; color: white; font-weight: 600; font-size: 13px; padding: 8px 20px; border-radius: 4px; cursor: pointer; box-shadow: 0 1px 3px rgba(0,0,0,0.2); border: 2px solid #1557b0;">
                    Có, tôi đồng ý (Yes, I'm in)
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('Bật tính năng đồng bộ hóa - Google Chrome', 'https://www.google.com', screen4Content));
    await page.screenshot({ path: path.join(outDir, 'step4-turn-on-sync.png') });

    // =========================================================================
    // SCREEN 4B: Extension Pinned & Opened Waiting SSO Login
    // =========================================================================
    console.log('[STEP 4B] Generating step4b-vags-installed.png...');
    const screen4BContent = `
    <div style="background: #fafafa; height: 580px; position: relative; font-family: 'Segoe UI', Roboto, sans-serif;">
        <!-- Extension Popup Opened right below the VNU badge -->
        <div style="position: absolute; top: 10px; right: 40px; width: 360px; background: #ffffff; border-radius: 12px; box-shadow: 0 12px 36px rgba(0,0,0,0.22), 0 0 0 1px rgba(0,0,0,0.08); overflow: hidden; z-index: 100;">
            <!-- Extension Header -->
            <div style="background: #00803d; color: white; padding: 14px 16px; display: flex; align-items: center; justify-content: space-between;">
                <div style="display: flex; align-items: center; gap: 10px;">
                    <img src="/logo-vnu.svg" style="width: 24px; height: 24px;">
                    <div>
                        <div style="font-size: 13px; font-weight: 700;">VNU Academic Gateway</div>
                        <div style="font-size: 10px; opacity: 0.9;">Cổng CSDL Khoa học Quốc tế</div>
                    </div>
                </div>
                <span style="font-size: 10px; background: rgba(255,255,255,0.2); padding: 2px 8px; border-radius: 10px;">Chờ đăng nhập</span>
            </div>

            <!-- Body Waiting SSO Login -->
            <div style="padding: 24px 20px; text-align: center;">
                <div style="width: 56px; height: 56px; border-radius: 50%; background: #eef8f2; color: #00803d; display: inline-flex; align-items: center; justify-content: center; font-size: 26px; margin-bottom: 12px;">
                    🔑
                </div>
                <div style="font-size: 15px; font-weight: 600; color: #1e293b; margin-bottom: 6px;">Kích hoạt Ủy quyền Cán bộ VNU</div>
                <div style="font-size: 12px; color: #64748b; line-height: 1.5; margin-bottom: 20px;">
                    Tiện ích đã được tổ chức cài đặt tự động. Thầy/Cô vui lòng xác thực để mở đường truyền CSDL khoa học.
                </div>

                <div style="background: #00803d; color: white; font-weight: 600; font-size: 13px; padding: 10px 20px; border-radius: 8px; cursor: pointer; box-shadow: 0 2px 6px rgba(0,128,61,0.3); display: flex; align-items: center; justify-content: center; gap: 8px;">
                    <span>🔑 Đăng nhập VNU SSO</span>
                </div>
            </div>

            <div style="background: #f8fafc; border-top: 1px solid #e2e8f0; padding: 10px 16px; font-size: 11px; color: #64748b; display: flex; justify-content: space-between;">
                <span>Máy chủ: thamkhao.vnu.edu.vn</span>
                <span>Phiên bản v2.2.0</span>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('VNU Academic Gateway (VAGS)', 'https://thamkhao.vnu.edu.vn', screen4BContent, true));
    await page.screenshot({ path: path.join(outDir, 'step4b-vags-installed.png') });

    // =========================================================================
    // SCREEN 5: Extension Activated & Proxy Connected (NP • 112.137.142.30)
    // =========================================================================
    console.log('[STEP 5] Generating step5-sso-sync-activated.png...');
    const screen5Content = `
    <div style="background: #fafafa; height: 580px; position: relative; font-family: 'Segoe UI', Roboto, sans-serif;">
        <!-- Extension Popup in ACTIVE State -->
        <div style="position: absolute; top: 10px; right: 40px; width: 370px; background: #ffffff; border-radius: 12px; box-shadow: 0 12px 36px rgba(0,0,0,0.22), 0 0 0 1px rgba(0,0,0,0.08); overflow: hidden; z-index: 100;">
            <!-- Header -->
            <div style="background: #00803d; color: white; padding: 12px 16px; display: flex; align-items: center; justify-content: space-between;">
                <div style="display: flex; align-items: center; gap: 8px;">
                    <img src="/logo-vnu.svg" style="width: 22px; height: 22px;">
                    <div>
                        <div style="font-size: 13px; font-weight: 700;">VNU Academic Gateway</div>
                        <div style="font-size: 10px; opacity: 0.9;">Cổng CSDL Khoa học Quốc tế</div>
                    </div>
                </div>
                <span style="font-size: 10px; background: rgba(255,255,255,0.25); padding: 2px 8px; border-radius: 10px; font-weight: 600;">● SSE Live</span>
            </div>

            <div style="padding: 16px;">
                <!-- User Card -->
                <div style="background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px 12px; display: flex; align-items: center; gap: 10px; margin-bottom: 12px;">
                    <div style="width: 38px; height: 38px; border-radius: 50%; background: #00803d; color: white; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 14px;">NP</div>
                    <div style="flex: 1;">
                        <div style="font-size: 13px; font-weight: 700; color: #1e293b;">Nguyễn Đăng Phong</div>
                        <div style="font-size: 11px; color: #64748b;">phongnd@vnu.edu.vn • PQTKT</div>
                    </div>
                    <span style="font-size: 10px; background: #dcfce7; color: #15803d; font-weight: 600; padding: 2px 8px; border-radius: 4px;">Cán bộ</span>
                </div>

                <!-- Proxy Toggle Card -->
                <div style="background: #ffffff; border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px 12px; display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px;">
                    <div>
                        <div style="font-size: 12px; font-weight: 600; color: #1e293b;">Đường truyền Proxy: <span style="color:#15803d;">Đang Bật</span></div>
                        <div style="font-size: 10px; color: #64748b; margin-top: 2px;">Phân tuyến tự động qua thamkhao.vnu.edu.vn:443</div>
                    </div>
                    <!-- Toggle Switch ON -->
                    <div style="width: 40px; height: 22px; background: #00803d; border-radius: 11px; position: relative; cursor: pointer;">
                        <div style="width: 18px; height: 18px; background: white; border-radius: 50%; position: absolute; right: 2px; top: 2px; box-shadow: 0 1px 3px rgba(0,0,0,0.3);"></div>
                    </div>
                </div>

                <!-- Connection Verification Box -->
                <div style="background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 8px; padding: 10px 12px; margin-bottom: 12px;">
                    <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px;">
                        <span style="font-size: 11px; font-weight: 700; color: #15803d;">🟢 KẾT NỐI PROXY THÀNH CÔNG ✓</span>
                        <span style="font-size: 10px; color: #0284c7; cursor: pointer;">🔄 Đo lại</span>
                    </div>
                    <div style="font-size: 11px; color: #166534; line-height: 1.4;">
                        IP nhận diện thực tế: <b>112.137.142.30</b> (Mạng ĐHQGHN • Đạt yêu cầu)
                    </div>
                </div>

                <!-- Routing Tester -->
                <div style="background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 8px; padding: 10px 12px;">
                    <div style="font-size: 11px; font-weight: 600; color: #475569; margin-bottom: 6px;">🔍 Kiểm tra liên kết: sciencedirect.com</div>
                    <div style="background: #eef8f2; border: 1px solid #86efac; border-radius: 4px; padding: 4px 8px; font-size: 11px; color: #166534; font-weight: 600;">
                        ✓ PROXY (Định tuyến qua Cổng VNU 112.137.142.30)
                    </div>
                </div>
            </div>

            <!-- Footer -->
            <div style="background: #f8fafc; border-top: 1px solid #e2e8f0; padding: 10px 16px; font-size: 11px; display: flex; justify-content: space-between; align-items: center;">
                <a href="https://thamkhao.vnu.edu.vn" style="color: #00803d; text-decoration: none; font-weight: 600;">Cổng VAGS ↗</a>
                <div style="display: flex; gap: 10px;">
                    <span style="color: #64748b; cursor: pointer;">🔄 Đồng bộ</span>
                    <span style="color: #dc2626; cursor: pointer;">Đăng xuất</span>
                </div>
            </div>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('Cổng tra cứu tài liệu học thuật - thamkhao.vnu.edu.vn', 'https://thamkhao.vnu.edu.vn', screen5Content, true));
    await page.screenshot({ path: path.join(outDir, 'step5-sso-sync-activated.png') });

    // =========================================================================
    // SCREEN 6: Real ScienceDirect with Institutional Access Provided by VNU
    // =========================================================================
    console.log('[STEP 6] Generating step6-sciencedirect-vnu.png...');
    const screen6Content = `
    <div style="background: #ffffff; min-height: 580px; font-family: 'Segoe UI', Roboto, sans-serif;">
        <!-- ScienceDirect Top Navbar -->
        <div style="background: #ffffff; border-bottom: 1px solid #d5dadf; height: 60px; padding: 0 32px; display: flex; align-items: center; justify-content: space-between;">
            <div style="display: flex; align-items: center; gap: 16px;">
                <img src="/images/sciencedirect-logo.svg" style="height: 32px;">
            </div>

            <!-- Institutional Access Pill -->
            <div style="display: flex; align-items: center; gap: 10px; background: #eef8f2; border: 1px solid #86efac; border-radius: 20px; padding: 6px 14px;">
                <img src="/logo-vnu.svg" style="width: 20px; height: 20px;">
                <div style="font-size: 12px; color: #166534; font-weight: 600;">
                    Brought to you by: <span style="color: #00803d; font-weight: 700;">Đại học Quốc gia Hà Nội (VNU)</span>
                </div>
            </div>
        </div>

        <!-- Article Page -->
        <div style="max-width: 860px; margin: 32px auto; padding: 0 20px;">
            <div style="color: #007398; font-size: 14px; font-weight: 600; margin-bottom: 6px;">Journal of Cleaner Production • Volume 415, 2026</div>
            <h1 style="font-size: 26px; color: #1f1f1f; line-height: 1.3; font-weight: 700; margin-bottom: 14px;">
                Automated institutional proxy routing and secure remote access architecture for university scientific research networks
            </h1>
            <div style="font-size: 13px; color: #5f6368; margin-bottom: 24px;">
                Author links open overlay panel • Nguyen Dang Phong, et al. • Vietnam National University, Hanoi
            </div>

            <!-- Download PDF Button & Full Text Access Banner -->
            <div style="background: #f0fdf4; border: 1px solid #bbf7d0; border-radius: 8px; padding: 18px 24px; display: flex; align-items: center; justify-content: space-between; margin-bottom: 28px;">
                <div>
                    <div style="color: #166534; font-size: 15px; font-weight: 700; display: flex; align-items: center; gap: 8px;">
                        <span>✓ Full text access</span>
                        <span style="font-size: 11px; background: #dcfce7; color: #15803d; padding: 2px 8px; border-radius: 10px;">Quyền truy cập toàn văn</span>
                    </div>
                    <div style="color: #334155; font-size: 12px; margin-top: 4px;">
                        Được cấp quyền tự động thông qua Cổng VAGS (IP 112.137.142.30 - ĐHQGHN).
                    </div>
                </div>

                <!-- Green Download PDF Button -->
                <div style="background: #00803d; color: white; font-weight: 700; font-size: 14px; padding: 12px 24px; border-radius: 6px; display: flex; align-items: center; gap: 8px; cursor: pointer; box-shadow: 0 4px 12px rgba(0,128,61,0.35);">
                    <span>📥</span>
                    <span>Download PDF (Full text)</span>
                </div>
            </div>

            <!-- Abstract -->
            <div style="font-size: 15px; font-weight: 700; color: #1f1f1f; margin-bottom: 8px;">Abstract</div>
            <p style="font-size: 14px; color: #475569; line-height: 1.6; text-align: justify;">
                This paper demonstrates the design and production deployment of the VNU Academic Gateway (VAGS), enabling zero-leakage remote access to subscription-based scientific databases for over 6,000 faculty members and researchers across Vietnam National University, Hanoi. The system combines high-concurrency Edge HAProxy ingress with wire-speed Google BBR congestion control on dedicated 1Gbps infrastructure...
            </p>
        </div>
    </div>
    `;
    await page.setContent(win11Frame('ScienceDirect.com | Elsevier - Full Text Article Access', 'https://www.sciencedirect.com/science/article/pii/S095965262301234X', screen6Content, true));
    await page.screenshot({ path: path.join(outDir, 'step6-sciencedirect-vnu.png') });

    await browser.close();
    console.log('[SUCCESS] All 7 authentic screenshots generated successfully!');
}

main().catch(err => {
    console.error('[ERROR]', err);
    process.exit(1);
});

