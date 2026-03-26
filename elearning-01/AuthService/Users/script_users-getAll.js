pm.test("Status code", () => {
    pm.expect(pm.response.code).to.be.oneOf([200,403]);
});

let res = pm.response.json();

if (pm.response.code === 200) {
    pm.test("Lấy danh sách users thành công", () => {
        pm.expect(res).to.be.an("array");
    });
}