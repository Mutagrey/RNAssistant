"use strict";
// Development/export utility only. The delivered application has no Node dependency.
const fs = require("node:fs"), path = require("node:path"), crypto = require("node:crypto");
const { verifyVendors } = require("./vendor-lock");
const sourceRoot = path.resolve(__dirname, "..");
const digest = bytes => crypto.createHash("sha256").update(bytes).digest("hex");

function capture(root = sourceRoot) {
    root = fs.realpathSync(root);
    const manifestPath = path.join(root, "application.json");
    if (fs.lstatSync(manifestPath).isSymbolicLink()) throw Error("Linked manifest is not an application source.");
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    if (manifest.formatVersion !== 1 || manifest.kind !== "data-templates-application" || manifest.entry !== "index.html" ||
        !Array.isArray(manifest.files) || manifest.files.length > 200 ||
        new Set(manifest.files).size !== manifest.files.length ||
        ![manifest.entry, "application.json", "README.md"].every(file => manifest.files.includes(file)))
        throw Error("Invalid application manifest.");
    verifyVendors(root, manifest);
    let total = 0;
    const files = manifest.files.map(file => {
        if (typeof file !== "string" || !/^[a-zA-Z0-9_./-]+$/.test(file) ||
            file.split("/").some(part => !part || part === "." || part === "..") || file === "application.lock.json")
            throw Error("Invalid application member: " + file);
        const absolute = path.join(root, file);
        const stat = fs.lstatSync(absolute);
        if (fs.realpathSync(absolute) !== absolute || !stat.isFile())
            throw Error("Application members must be regular files: " + file);
        if (stat.size > 2 * 1024 * 1024 || total + stat.size > 8 * 1024 * 1024)
            throw Error("Application package budget exceeded.");
        const bytes = fs.readFileSync(absolute);
        total += bytes.length;
        // These are application-code budgets, never source-workbook limits.
        if (bytes.length > 2 * 1024 * 1024 || total > 8 * 1024 * 1024) throw Error("Application package budget exceeded.");
        return { path: file, bytes, sha256: digest(bytes) };
    });
    return { manifest, files, total };
}

function exportApplication(destination, root = sourceRoot) {
    const captured = capture(root);
    destination = path.resolve(destination);
    destination = path.join(fs.realpathSync(path.dirname(destination)), path.basename(destination));
    const actualRoot = fs.realpathSync(root), relative = path.relative(actualRoot, destination);
    if (!relative || !relative.startsWith(".." + path.sep) && relative !== ".." && !path.isAbsolute(relative))
        throw Error("Export to a new folder outside the application source.");
    // Never merge with or overwrite an existing folder. No recursive workspace scan.
    fs.mkdirSync(destination);
    for (const file of captured.files) {
        const target = path.join(destination, file.path);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.writeFileSync(target, file.bytes, { flag: "wx" });
    }
    const lock = { formatVersion: 1, kind: "data-templates-application-lock", entry: captured.manifest.entry,
        files: captured.files.map(file => ({ path: file.path, bytes: file.bytes.length, sha256: file.sha256 })) };
    fs.writeFileSync(path.join(destination, "application.lock.json"), JSON.stringify(lock, null, 2) + "\n", { flag: "wx" });
    return { directory: destination, files: captured.files.length, bytes: captured.total };
}

if (require.main === module) {
    try {
        if (process.argv.length !== 3) throw Error("Usage: node tools/export.js /absolute/new-output-folder");
        console.log(JSON.stringify(exportApplication(process.argv[2])));
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { capture, exportApplication };
