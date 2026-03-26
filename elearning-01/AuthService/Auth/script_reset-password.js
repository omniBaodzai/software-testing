pm.test("Status code is 200 or 400", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 400]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.test("Reset password success", function () {
        pm.expect(res.success).to.eql(true);
    });
} else {
    pm.test("Reset password failed", function () {
        pm.expect(res.success).to.eql(false);
    });
}