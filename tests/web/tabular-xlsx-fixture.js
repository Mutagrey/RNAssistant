"use strict";
// Test-only ZIP producer/fixture mutations. Uses Node zlib, never the production ZIP reader.
const fs = require("node:fs"), path = require("node:path"), zlib = require("node:zlib");
const source = path.resolve(__dirname, "../../web/data-templates/core/examples/matrix-source.xlsx");
function parts() {
    const input = fs.readFileSync(source), result = new Map(); let position = 0;
    while (input.readUInt32LE(position) === 0x04034b50) {
        const nameLength = input.readUInt16LE(position + 26), extraLength = input.readUInt16LE(position + 28);
        const name = input.subarray(position + 30, position + 30 + nameLength).toString("utf8");
        const start = position + 30 + nameLength + extraLength, end = start + input.readUInt32LE(position + 18);
        result.set(name, zlib.inflateRawSync(input.subarray(start, end)).toString("utf8")); position = end;
    }
    return result;
}
function zip(parts, options = {}) {
    const local = [], central = []; let offset = 0, index = 0;
    for (const [name, value] of parts) {
        const text = Buffer.isBuffer(value) ? value : Buffer.from(value), filename = Buffer.from(name), method = options.stored ? 0 : 8;
        const bytes = method ? zlib.deflateRawSync(text) : text, crc = zlib.crc32(text);
        const header = Buffer.alloc(30), entry = Buffer.alloc(46), descriptor = options.descriptor ? Buffer.alloc(16) : Buffer.alloc(0);
        header.writeUInt32LE(0x04034b50, 0); header.writeUInt16LE(20, 4); header.writeUInt16LE(0x800 | (options.descriptor ? 8 : 0), 6);
        header.writeUInt16LE(method, 8); header.writeUInt16LE(filename.length, 26);
        if (options.descriptor) { descriptor.writeUInt32LE(0x08074b50); descriptor.writeUInt32LE(crc, 4); descriptor.writeUInt32LE(bytes.length, 8); descriptor.writeUInt32LE(text.length, 12); }
        else { header.writeUInt32LE(crc, 14); header.writeUInt32LE(bytes.length, 18); header.writeUInt32LE(text.length, 22); }
        entry.writeUInt32LE(0x02014b50, 0); entry.writeUInt16LE(20, 4); entry.writeUInt16LE(20, 6);
        header.copy(entry, 8, 6, 26); entry.writeUInt32LE(crc, 16); entry.writeUInt32LE(bytes.length, 20); entry.writeUInt32LE(text.length, 24);
        entry.writeUInt16LE(filename.length, 28); entry.writeUInt32LE(offset, 42);
        if (options.mutate) options.mutate({ name, header, entry, bytes, descriptor, index });
        local.push(header, filename, bytes, descriptor); central.push(entry, filename);
        offset += header.length + filename.length + bytes.length + descriptor.length; index++;
    }
    const directory = Buffer.concat(central), end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50); end.writeUInt16LE(index, 8); end.writeUInt16LE(index, 10); end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
    return Buffer.concat([...local, directory, end]);
}
module.exports = { parts, zip, source };
