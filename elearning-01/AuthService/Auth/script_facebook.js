pm.test("Status code is 200 or 401", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 401]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.environment.set("accessToken", res.accessToken);
    pm.environment.set("refreshToken", res.refreshToken);

    pm.test("Facebook login success", function () {
        pm.expect(res.success).to.eql(true);
    });
} else {
    pm.test("Facebook login failed", function () {
        pm.expect(res.success).to.eql(false);
    });
}