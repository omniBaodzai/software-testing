pm.test("Kiểm tra status code", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 401]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.test("Lấy thông tin user thành công", function () {
        pm.expect(res.success).to.eql(true);
        pm.expect(res).to.have.property("data");
    });

    pm.test("Kiểm tra dữ liệu user", function () {
        pm.expect(res.data).to.have.property("id");
        pm.expect(res.data).to.have.property("email");
        pm.expect(res.data).to.have.property("username");
    });

} else {
    pm.test("Không có quyền truy cập (chưa login hoặc token sai)", function () {
        pm.expect(res.success).to.eql(false);
    });
}