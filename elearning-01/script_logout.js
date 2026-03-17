/**
 * =====================================================
 * SCRIPT LOGOUT: XOÁ TOKEN KHỎI ENVIRONMENT
 * =====================================================
 */

console.log("Đang logout...");

// Xoá token
pm.environment.unset("accessToken");
pm.environment.unset("refreshToken");
pm.environment.unset("expiresAt");

pm.environment.unset("tokenUserId");
pm.environment.unset("tokenUserRole");

console.log("Đã xoá toàn bộ token khỏi Environment");

pm.test("Logout thành công", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 204]);
});

console.log("Hoàn tất logout");