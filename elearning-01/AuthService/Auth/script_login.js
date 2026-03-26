try {

    console.log("Bắt đầu xử lý script LOGIN...");

    const response = pm.response.json();

    console.log("Response từ API:", response);

    if (!response) {
        throw new Error("Response rỗng hoặc không hợp lệ");
    }

    pm.test("Đăng nhập thành công", function () {
        pm.expect(response.success).to.eql(true);
    });

    pm.test("Có accessToken", function () {
        pm.expect(response.accessToken).to.exist;
    });

    pm.test("Có refreshToken", function () {
        pm.expect(response.refreshToken).to.exist;
    });

    
    pm.environment.set("accessToken", response.accessToken);
    pm.environment.set("refreshToken", response.refreshToken);
    pm.environment.set("expiresAt", response.expiresAt);

    console.log("Đã lưu accessToken");
    console.log("Đã lưu refreshToken");
    console.log("expiresAt:", response.expiresAt);

    
    function decodeJwt(token) {
        try {
            let base64Url = token.split('.')[1];

            let base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');

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


    const payload = decodeJwt(response.accessToken);

    if (!payload) {
        throw new Error("Không decode được JWT");
    }

    console.log("Payload JWT:", payload);

    
    const userId = payload.sub;

    const userEmail =
        payload.email ||
        payload["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"];

    const userRole = payload.user_type || payload.role;

    const tokenExpire = payload.exp;

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