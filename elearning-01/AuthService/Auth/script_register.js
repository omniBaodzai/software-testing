pm.test("Status code is 200 or 400", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 400]);
});

let res = pm.response.json();

pm.test("Response has success field", function () {
    pm.expect(res).to.have.property("success");
});

if (pm.response.code === 200) {
    pm.test("Đăng ký thành công", function () {
        pm.expect(res.success).to.eql(true);
        pm.expect(res).to.have.property("accessToken");
    });

    // Lưu token
    pm.environment.set("accessToken", res.accessToken);
} else {
    pm.test("Đăng ký thất bại", function () {
        pm.expect(res.success).to.eql(false);
    });
}