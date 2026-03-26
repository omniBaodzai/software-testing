pm.test("Kiểm tra status code", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 400]);
});

let res = pm.response.json();

pm.test("Có field success", function () {
    pm.expect(res).to.have.property("success");
});

if (pm.response.code === 200) {
    pm.test("Đăng ký clinic thành công", function () {
        pm.expect(res.success).to.eql(true);
    });
} else {
    pm.test("Đăng ký thất bại", function () {
        pm.expect(res.success).to.eql(false);
    });
}