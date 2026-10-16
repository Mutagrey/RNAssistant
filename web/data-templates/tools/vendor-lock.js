"use strict";
// Development/export check. Runtime never loads this file or the provenance URLs.
const fs = require("node:fs"), path = require("node:path"), crypto = require("node:crypto");
const sha256 = bytes => crypto.createHash("sha256").update(bytes).digest("hex");

function listFiles(directory) {
    return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
        const absolute = path.join(directory, entry.name);
        if (entry.isDirectory()) return listFiles(absolute);
        if (!entry.isFile()) throw Error("Vendor members must be regular files: " + absolute);
        return [absolute];
    });
}

function verifyVendors(root, application) {
    root = fs.realpathSync(root);
    const lock = JSON.parse(fs.readFileSync(path.join(root, "vendor-lock.json"), "utf8"));
    if (lock.formatVersion !== 1 || lock.kind !== "data-templates-vendors" ||
        !Array.isArray(lock.packages) || lock.packages.length === 0)
        throw Error("Invalid Data Templates vendor lock.");
    const declared = new Set(), ids = new Set();
    for (const item of lock.packages) {
        if (typeof item.id !== "string" || !/^[a-z0-9-]+$/.test(item.id) || ids.has(item.id) ||
            typeof item.version !== "string" || !item.version || typeof item.license !== "string" ||
            !/^https:\/\//.test(item.npmTarball) || !/^sha512-/.test(item.npmIntegrity) ||
            !/^[a-f0-9]{40}$/.test(item.gitHead) || !Array.isArray(item.runtime) || !item.runtime.length ||
            !Array.isArray(item.licenses) || !item.licenses.length)
            throw Error("Invalid Data Templates vendor package.");
        ids.add(item.id);
        for (const entry of item.runtime.concat(item.licenses)) {
            const file = entry.path;
            if (typeof file !== "string" || !/^vendor\/[a-zA-Z0-9_.\/-]+$/.test(file) ||
                file.split("/").some(part => !part || part === "." || part === "..") ||
                declared.has(file) || !/^[a-f0-9]{64}$/.test(entry.sha256) ||
                !application.files.includes(file)) throw Error("Invalid vendor member: " + file);
            const absolute = path.join(root, file);
            const stat = fs.lstatSync(absolute);
            if (!stat.isFile() || fs.realpathSync(absolute) !== absolute)
                throw Error("Vendor members must be regular files: " + file);
            const bytes = fs.readFileSync(absolute);
            if (entry.bytes !== undefined && entry.bytes !== bytes.length || sha256(bytes) !== entry.sha256)
                throw Error("Vendor hash mismatch: " + file);
            declared.add(file);
        }
    }
    const actual = listFiles(path.join(root, "vendor")).map(file => path.relative(root, file).split(path.sep).join("/"));
    if (actual.length !== declared.size || actual.some(file => !declared.has(file)))
        throw Error("Vendor lock does not cover every local vendor file.");
    return lock;
}

module.exports = { verifyVendors };
