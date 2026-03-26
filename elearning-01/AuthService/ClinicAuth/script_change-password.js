pm.test("Status code", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 400, 401]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.test("Đổi mật khẩu thành công", function () {
        pm.expect(res.success).to.eql(true);
    });
} else {
    pm.test("Đổi mật khẩu thất bại", function () {
        pm.expect(res.success).to.eql(false);
    });
}