pm.test("Create user", () => {
    pm.expect(pm.response.code).to.be.oneOf([200, 201, 409]);
});

let res = pm.response.json();

if (pm.response.code === 409) {
    pm.test("Email đã tồn tại", () => {
        pm.expect(res.success).to.eql(false);
    });
}

console.log("BODY SENT:", pm.request.body.raw);
console.log("RESPONSE:", pm.response.text());