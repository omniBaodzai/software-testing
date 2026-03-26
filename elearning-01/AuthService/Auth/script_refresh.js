pm.test("Status code is 200 or 401", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 401]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.test("Refresh success", function () {
        pm.expect(res.success).to.eql(true);
        pm.expect(res).to.have.property("accessToken");
    });

    pm.environment.set("accessToken", res.accessToken);
} else {
    pm.test("Refresh failed", function () {
        pm.expect(res.success).to.eql(false);
    });
}