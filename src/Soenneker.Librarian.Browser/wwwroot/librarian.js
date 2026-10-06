const databaseName = "Soenneker.Librarian.v1";
const storeName = "snapshots";
const prefix = "Soenneker.Librarian.v1:";

function address(key) {
    if (typeof key !== "string" || !key.trim()) throw new Error("A Librarian storage key is required.");
    return prefix + key;
}

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        let blocked = false;
        request.onupgradeneeded = () => request.result.createObjectStore(storeName);
        request.onerror = () => reject(request.error);
        request.onblocked = () => {
            blocked = true;
            reject(new Error("Librarian IndexedDB open is blocked by another connection."));
        };
        request.onsuccess = () => {
            if (blocked) { request.result.close(); return; }
            request.result.onversionchange = () => request.result.close();
            resolve(request.result);
        };
    });
}

async function indexedOperation(key, expected, replacement, write) {
    const database = await openDatabase();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction(storeName, write ? "readwrite" : "readonly");
            const store = transaction.objectStore(storeName);
            let result;
            const request = store.get(key);
            request.onsuccess = () => {
                const current = request.result ?? null;
                if (!write) { result = current; return; }
                result = current === expected;
                if (result) store.put(replacement, key);
            };
            transaction.oncomplete = () => resolve(result);
            transaction.onabort = () => reject(transaction.error ?? new Error("Librarian IndexedDB transaction aborted."));
            transaction.onerror = () => {}; // The abort event reports failure after rollback.
        });
    } finally { database.close(); }
}

export async function read(backend, key) {
    const storageKey = address(key);
    if (backend === "localStorage") return localStorage.getItem(storageKey);
    if (backend === "sessionStorage") return sessionStorage.getItem(storageKey);
    if (backend === "indexedDb") return await indexedOperation(storageKey, null, null, false);
    throw new Error("Unknown Librarian browser backend.");
}

export async function compareExchange(backend, key, expected, replacement) {
    const storageKey = address(key);
    if (typeof replacement !== "string" || (expected !== null && typeof expected !== "string"))
        throw new Error("Librarian snapshots must be strings.");
    if (backend === "indexedDb") return await indexedOperation(storageKey, expected, replacement, true);
    if (backend !== "localStorage" && backend !== "sessionStorage") throw new Error("Unknown Librarian browser backend.");
    if (!navigator.locks) throw new Error("Librarian Web Storage requires the Web Locks API in a secure context.");
    const storage = backend === "sessionStorage" ? sessionStorage : localStorage;
    const lockKey = backend === "sessionStorage" ? "session:" + storageKey : storageKey;
    return await navigator.locks.request(lockKey, () => {
        if (storage.getItem(storageKey) !== expected) return false;
        storage.setItem(storageKey, replacement);
        return true;
    });
}
