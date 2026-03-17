/**
 * =====================================================
 * SCRIPT LOGIN: LƯU TOKEN + DECODE (Giải mã) JWT 
 * =====================================================
 * Chức năng:
 * 1. Kiểm tra đăng nhập thành công
 * 2. Lưu accessToken, refreshToken
 * 3. Decode (giải mã) JWT để lấy thông tin user
 * 4. Lưu thông tin vào Environment
 * 5. Log chi tiết để debug
 * =====================================================
 */

try {

    console.log("Bắt đầu xử lý script LOGIN...");

    // =====================================================
    // 1. PARSE RESPONSE
    // =====================================================
    const response = pm.response.json();

    console.log("Response từ API:", response);

    if (!response) {
        throw new Error("Response rỗng hoặc không hợp lệ");
    }

    // =====================================================
    // 2. KIỂM TRA LOGIN
    // =====================================================
    pm.test("Đăng nhập thành công", function () {
        pm.expect(response.success).to.eql(true);
    });

    pm.test("Có accessToken", function () {
        pm.expect(response.accessToken).to.exist;
    });

    pm.test("Có refreshToken", function () {
        pm.expect(response.refreshToken).to.exist;
    });

    // =====================================================
    // 3. LƯU TOKEN
    // =====================================================
    pm.environment.set("accessToken", response.accessToken);
    pm.environment.set("refreshToken", response.refreshToken);
    pm.environment.set("expiresAt", response.expiresAt);

    console.log("Đã lưu accessToken");
    console.log("Đã lưu refreshToken");
    console.log("expiresAt:", response.expiresAt);

    // =====================================================
    // 4. HÀM DECODE JWT (XỬ LÝ BASE64URL CHUẨN)
    // =====================================================
    function decodeJwt(token) {
        try {
            let base64Url = token.split('.')[1];

            // Convert Base64URL -> Base64
            let base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');

            // Decode
            let jsonPayload = decodeURIComponent(
                atob(base64)
                    .split('')
                    .map(c => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
                    .join('')
            );

            return JSON.parse(jsonPayload);

        } catch (error) {
            console.error("Lỗi decode JWT:", error);
            return null;
        }
    }

    // =====================================================
    // 5. DECODE TOKEN
    // =====================================================
    const payload = decodeJwt(response.accessToken);

    if (!payload) {
        throw new Error("Không decode được JWT");
    }

    console.log("Payload JWT:", payload);

    // =====================================================
    // 6. LẤY THÔNG TIN TỪ PAYLOAD
    // =====================================================

    // userId (ưu tiên sub)
    const userId = payload.sub;

    // email (do bạn dùng .NET nên nó nằm ở claim dài)
    const userEmail =
        payload.email ||
        payload["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"];

    // role / userType
    const userRole = payload.user_type || payload.role;

    // thời gian hết hạn
    const tokenExpire = payload.exp;

    // =====================================================
    // 7. LƯU THÔNG TIN VÀO ENVIRONMENT
    // =====================================================
    if (userId) {
        pm.environment.set("tokenUserId", userId);
        console.log("userId:", userId);
    }

    if (userEmail) {
        pm.environment.set("tokenUserEmail", userEmail);
        console.log("email:", userEmail);
    }

    if (userRole) {
        pm.environment.set("tokenUserRole", userRole);
        console.log("role:", userRole);
    }

    if (tokenExpire) {
        pm.environment.set("tokenExpireUnix", tokenExpire);
        console.log("Token hết hạn (unix):", tokenExpire);
    }

    // =====================================================
    // 8. LƯU THÊM USER TỪ RESPONSE (OPTIONAL)
    // =====================================================
    if (response.user) {

        pm.environment.set("userId", response.user.id);
        pm.environment.set("userEmail", response.user.email);
        pm.environment.set("userFirstName", response.user.firstName);
        pm.environment.set("userLastName", response.user.lastName);
        pm.environment.set("userType", response.user.userType);

        console.log("Đã lưu thông tin user từ response");
    }

    console.log("HOÀN TẤT LOGIN SCRIPT");

} catch (error) {

    console.error("LỖI LOGIN SCRIPT:", error);

    pm.test("Script phải chạy thành công", function () {
        throw new Error(error.message);
    });
}