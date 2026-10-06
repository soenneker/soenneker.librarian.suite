import { read, compareExchange } from "../../src/Soenneker.Librarian.Browser/wwwroot/librarian.js";

const results = [];
const check = (condition, name) => {
    if (!condition) throw new Error(name);
    results.push("PASS " + name);
};
const rejects = async (operation, name) => {
    let rejected = false;
    try { await operation(); } catch { rejected = true; }
    check(rejected, name);
};
const ready = frame => new Promise(resolve => {
    const probe = () => {
        if (frame.contentWindow.compareExchange) resolve(frame.contentWindow);
        else frame.contentWindow.addEventListener("provider-ready", () => resolve(frame.contentWindow), { once: true });
    };
    if (frame.contentDocument.readyState === "complete") probe();
    else frame.addEventListener("load", probe, { once: true });
});

try {
    const [first, second] = await Promise.all([ready(document.querySelector("#first")), ready(document.querySelector("#second"))]);
    for (const backend of ["localStorage", "indexedDb", "sessionStorage"]) {
        const key = crypto.randomUUID();
        const initial = JSON.stringify({ messages: [{ id: "one", value: "private 🚀" }] });
        check(await read(backend, key) === null, backend + " missing snapshot");
        check(await compareExchange(backend, key, null, initial), backend + " initial commit");
        check(await read(backend, key) === initial, backend + " Unicode roundtrip");
        check(!await compareExchange(backend, key, null, "stale"), backend + " stale writer rejected");
        check(await read(backend, key + "-other") === null, backend + " key isolation");
        const writes = await Promise.all([
            first.compareExchange(backend, key, initial, "winner-a"),
            second.compareExchange(backend, key, initial, "winner-b")
        ]);
        check(writes.filter(Boolean).length === 1, backend + " concurrent frames have one winner");
        const persisted = await read(backend, key);
        check(persisted === (writes[0] ? "winner-a" : "winner-b"), backend + " committed winner persists");
        if (backend !== "indexedDb") {
            const original = Storage.prototype.setItem;
            try {
                Storage.prototype.setItem = () => { throw new DOMException("Quota exceeded", "QuotaExceededError"); };
                await rejects(() => compareExchange(backend, key, persisted, "lost"), backend + " quota failure propagated");
            } finally { Storage.prototype.setItem = original; }
        } else {
            const original = IDBObjectStore.prototype.put;
            try {
                IDBObjectStore.prototype.put = function (...args) {
                    const request = original.apply(this, args);
                    this.transaction.abort();
                    return request;
                };
                await rejects(() => compareExchange(backend, key, persisted, "lost"), "indexedDb transaction abort propagated");
            } finally { IDBObjectStore.prototype.put = original; }
        }
        check(await read(backend, key) === persisted, backend + " failed write leaves previous snapshot intact");
    }
    window.testResult = { passed: true, checks: results.length };
} catch (error) {
    results.push("FAIL " + error.stack);
    window.testResult = { passed: false, error: String(error) };
}
document.querySelector("#results").textContent = results.join("\n");
