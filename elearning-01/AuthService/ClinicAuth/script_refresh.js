pm.test("Status code", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 401]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.environment.set("accessToken", res.accessToken);

    pm.test("Refresh token thành công", function () {
        pm.expect(res.success).to.eql(true);
    });
}