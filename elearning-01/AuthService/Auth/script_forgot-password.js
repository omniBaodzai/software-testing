pm.test("Status code is 200", function () {
    pm.response.to.have.status(200);
});

let res = pm.response.json();

pm.test("Always success", function () {
    pm.expect(res.success).to.eql(true);
});