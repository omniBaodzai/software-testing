pm.test("Status code is 200 or 400", function () {
    pm.expect(pm.response.code).to.be.oneOf([200, 400]);
});

let res = pm.response.json();

pm.test("Response has success", function () {
    pm.expect(res).to.have.property("success");
});

if (pm.response.code === 200) {
    pm.test("Verify success", function () {
        pm.expect(res.success).to.eql(true);
    });
} else {
    pm.test("Verify failed", function () {
        pm.expect(res.success).to.eql(false);
    });
}