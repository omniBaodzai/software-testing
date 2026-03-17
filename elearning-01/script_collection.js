/**
 * =========================================================
 * - Tự login
 * - Tự refresh token
 * - Tự gắn Authorization
 * =========================================================
 */

console.log("Bắt đầu Pre-request Script...");

// ===== LẤY ENV =====
const baseUrl = pm.environment.get("baseUrl");
const accessToken = pm.environment.get("accessToken");
const refreshToken = pm.environment.get("refreshToken");
const expiresAt = pm.environment.get("expiresAt");

const email = pm.environment.get("email");
const password = pm.environment.get("password");


// ===== HÀM LOGIN =====
function doLogin(callback) {

    console.warn("Đang auto login...");

    pm.sendRequest({
        url: `${baseUrl}/api/Auth/login`,
        method: "POST",
        header: { "Content-Type": "application/json" },
        body: {
            mode: "raw",
            raw: JSON.stringify({ email, password })
        }
    }, function (err, res) {

        if (err) {
            console.error("Lỗi login:", err);
            return;
        }

        const json = res.json();

        if (json.success) {

            console.log("Login thành công");

            pm.environment.set("accessToken", json.accessToken);
            pm.environment.set("refreshToken", json.refreshToken);
            pm.environment.set("expiresAt", json.expiresAt);

            callback();

        } else {

            console.error("Login thất bại");
        }

    });
}


// ===== HÀM REFRESH =====
function doRefresh(callback) {

    console.warn("Đang refresh token...");

    pm.sendRequest({
        url: `${baseUrl}/api/Auth/refresh-token`,
        method: "POST",
        header: { "Content-Type": "application/json" },
        body: {
            mode: "raw",
            raw: JSON.stringify({ refreshToken })
        }
    }, function (err, res) {

        if (err) {
            console.error("Lỗi refresh:", err);
            return;
        }

        const json = res.json();

        if (json.success) {

            console.log("Refresh thành công");

            pm.environment.set("accessToken", json.accessToken);
            pm.environment.set("refreshToken", json.refreshToken);
            pm.environment.set("expiresAt", json.expiresAt);

            callback();

        } else {

            console.warn("Refresh thất bại → chuyển sang login");
            doLogin(callback);
        }

    });
}


// ===== GẮN TOKEN =====
function attachToken() {

    const token = pm.environment.get("accessToken");

    if (token) {

        pm.request.headers.upsert({
            key: "Authorization",
            value: `Bearer ${accessToken}`
        });

        console.log("Đã gắn token vào header");

    } else {

        console.warn("Không có token để gắn");
    }
}


// ===== LOGIC CHÍNH =====
function main() {

    if (!accessToken || !expiresAt) {

        console.warn("Chưa có token → login");
        return doLogin(attachToken);
    }

    const now = new Date();
    const exp = new Date(expiresAt);

    if (now >= exp) {

        console.warn("Token hết hạn");

        if (refreshToken) {
            return doRefresh(attachToken);
        } else {
            return doLogin(attachToken);
        }
    }

    console.log("Token còn hạn");

    attachToken();
}

main();

console.log("Kết thúc Pre-request Script");