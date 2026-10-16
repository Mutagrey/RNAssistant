(function (root) {
    "use strict";
    const fail = root.RNTabularSelectors.fail, freeze = Object.freeze;
    const limits = freeze({ fileBytes: 96 * 1048576, directoryBytes: 2 * 1048576, entries: 4096,
        partBytes: 256 * 1048576, expandedBytes: 512 * 1048576, inputChunkBytes: 8192 });
    const crcTable = new Uint32Array(256);
    for (let index = 0; index < 256; index++) {
        let value = index;
        for (let bit = 0; bit < 8; bit++) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
        crcTable[index] = value;
    }
    function name(bytes, utf8) {
        if (!utf8 && bytes.some(byte => byte > 127)) fail("XLSX_ZIP_UNSUPPORTED", "", "Non-UTF-8 ZIP names are unsupported.");
        let value;
        try { value = new TextDecoder("utf-8", { fatal: true }).decode(bytes); }
        catch (_) { fail("XLSX_ZIP_INVALID", "", "Invalid ZIP filename encoding."); }
        if (!value || value.length > 512 || /[\\\u0000-\u001f\u007f:%?#]/.test(value) || value[0] === "/" ||
            value.replace(/\/$/, "").split("/").some(part => !part || part === "." || part === ".."))
            fail("XLSX_ZIP_INVALID", "", "ZIP entry has an unsafe or unsupported part name.");
        return value;
    }

    // Selective ZIP32 reader. Only consumed parts are inflated/CRC-checked; no filesystem extraction.
    // Native deflate is required, never replaced by synchronous whole-archive decompression.
    async function open(source, hooks) {
        if (!root.RNTabularByteSource.isSource(source) || source.closed || source.byteLength > limits.fileBytes)
            fail("XLSX_INPUT_LIMIT", "source", "XLSX needs an open bounded byte source of at most 96 MiB.");
        const guard = () => { if (hooks.isCancelled()) fail("EXECUTION_CANCELLED", "source", "XLSX reading was cancelled."); };
        let sourceBytesRead = 0, expandedBytes = 0;
        const progress = (part, partBytes, totalPartBytes, force = false) => hooks.onProgress({ phase: "reading-xlsx",
            part, sourceBytesRead, sourceBytes: source.byteLength, expandedBytes, partBytes, totalPartBytes, force });
        async function read(offset, size) {
            guard();
            if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(size) || offset < 0 || size < 1 || offset > source.byteLength - size)
                fail("XLSX_ZIP_INVALID", "source", "Truncated ZIP structure.");
            // The bounded ZIP directory may exceed the transport chunk budget.
            const bytes = new Uint8Array(size);
            for (let position = 0; position < size;) {
                guard();
                const count = Math.min(size - position, root.RNTabularByteSource.maximumReadBytes);
                bytes.set(await source.readRange(offset + position, count), position); guard(); position += count;
            }
            sourceBytesRead += size; return bytes;
        }
        progress("directory", 0, null, true);
        const tailOffset = Math.max(0, source.byteLength - 65557), tail = await read(tailOffset, source.byteLength - tailOffset);
        const tv = new DataView(tail.buffer); let end = -1;
        for (let i = tail.length - 22; i >= 0; i--) if (tv.getUint32(i, true) === 0x06054b50 && i + 22 + tv.getUint16(i + 20, true) === tail.length) { end = i; break; }
        if (end < 0) fail("XLSX_ZIP_INVALID", "source", "ZIP end record is missing.");
        const count = tv.getUint16(end + 10, true), directorySize = tv.getUint32(end + 12, true), directoryOffset = tv.getUint32(end + 16, true);
        if (tv.getUint16(end + 4, true) || tv.getUint16(end + 6, true) || tv.getUint16(end + 8, true) !== count ||
            count === 65535 || directorySize === 0xffffffff || directoryOffset === 0xffffffff)
            fail("XLSX_ZIP_UNSUPPORTED", "source", "Split archives and ZIP64 are unsupported.");
        if (!count || count > limits.entries || directorySize > limits.directoryBytes)
            fail("XLSX_ZIP_LIMIT", "source", "ZIP directory exceeds its budget.");
        if (directoryOffset + directorySize !== tailOffset + end) fail("XLSX_ZIP_INVALID", "source", "ZIP directory boundary is inconsistent.");
        const directory = await read(directoryOffset, directorySize), dv = new DataView(directory.buffer);
        const entries = new Map(), folded = new Set(); let cursor = 0, declaredBytes = 0;
        for (let i = 0; i < count; i++) {
            if (cursor + 46 > directory.length || dv.getUint32(cursor, true) !== 0x02014b50)
                fail("XLSX_ZIP_INVALID", "source", "Invalid ZIP directory entry.");
            const flags = dv.getUint16(cursor + 8, true), method = dv.getUint16(cursor + 10, true);
            const compressed = dv.getUint32(cursor + 20, true), expanded = dv.getUint32(cursor + 24, true);
            const length = dv.getUint16(cursor + 28, true), extra = dv.getUint16(cursor + 30, true), comment = dv.getUint16(cursor + 32, true);
            const offset = dv.getUint32(cursor + 42, true), next = cursor + 46 + length + extra + comment;
            if (next > directory.length || !length) fail("XLSX_ZIP_INVALID", "source", "Truncated ZIP directory entry.");
            if ((flags & ~0x080e) || ![0, 8].includes(method) || dv.getUint16(cursor + 34, true) || offset === 0xffffffff ||
                compressed === 0xffffffff || expanded === 0xffffffff)
                fail("XLSX_ZIP_UNSUPPORTED", "source", "Only unencrypted stored/deflated ZIP32 entries are supported.");
            const path = name(directory.subarray(cursor + 46, cursor + 46 + length), Boolean(flags & 0x800));
            if (folded.has(path.toLowerCase())) fail("XLSX_ZIP_INVALID", path, "Duplicate or case-ambiguous ZIP part.");
            if (offset + 30 + compressed > directoryOffset || (method === 0 && compressed !== expanded))
                fail("XLSX_ZIP_INVALID", path, "ZIP part boundary is inconsistent.");
            declaredBytes += expanded;
            if (expanded > limits.partBytes || declaredBytes > limits.expandedBytes) fail("XLSX_ZIP_LIMIT", path, "Declared expanded ZIP data exceeds its budget.");
            entries.set(path, { path, offset, compressed, expanded, flags, method, crc: dv.getUint32(cursor + 16, true) });
            folded.add(path.toLowerCase()); cursor = next;
        }
        if (cursor !== directory.length) fail("XLSX_ZIP_INVALID", "source", "Unexpected ZIP directory suffix.");
        const ordered = Array.from(entries.values()).sort((a, b) => a.offset - b.offset);
        ordered.forEach((entry, index) => {
            entry.end = index + 1 < ordered.length ? ordered[index + 1].offset : directoryOffset;
            if (entry.offset + 30 + entry.compressed > entry.end) fail("XLSX_ZIP_INVALID", entry.path, "Overlapping ZIP parts.");
        });
        const consumed = new Set();
        async function consume(path, maximum, onChunk) {
            guard();
            const entry = entries.get(path);
            if (!entry || path.endsWith("/")) fail("XLSX_PART_MISSING", path, "Required XLSX part is absent.");
            if (consumed.has(path)) fail("XLSX_ZIP_INVALID", path, "A part may only be consumed once per reader.");
            consumed.add(path);
            if (entry.expanded > maximum) fail("XLSX_PART_LIMIT", path, "XLSX part exceeds its semantic budget.");
            const local = await read(entry.offset, 30), lv = new DataView(local.buffer);
            const length = lv.getUint16(26, true), start = entry.offset + 30 + length + lv.getUint16(28, true);
            if (lv.getUint32(0, true) !== 0x04034b50 || lv.getUint16(6, true) !== entry.flags || lv.getUint16(8, true) !== entry.method ||
                start + entry.compressed > entry.end || name(await read(entry.offset + 30, length), Boolean(entry.flags & 0x800)) !== path ||
                (!(entry.flags & 8) && (lv.getUint32(14, true) !== entry.crc || lv.getUint32(18, true) !== entry.compressed || lv.getUint32(22, true) !== entry.expanded)))
                fail("XLSX_ZIP_INVALID", path, "Local ZIP header differs from its directory.");
            if (entry.flags & 8) {
                const descriptorOffset = start + entry.compressed;
                const descriptor = await read(descriptorOffset, Math.min(16, entry.end - descriptorOffset)), view = new DataView(descriptor.buffer);
                const skip = descriptor.length >= 4 && view.getUint32(0, true) === 0x08074b50 ? 4 : 0;
                if (descriptor.length < skip + 12 || view.getUint32(skip, true) !== entry.crc ||
                    view.getUint32(skip + 4, true) !== entry.compressed || view.getUint32(skip + 8, true) !== entry.expanded)
                    fail("XLSX_ZIP_INVALID", path, "ZIP data descriptor differs from its directory.");
            }
            let position = 0, stopped = false, partBytes = 0, crc = 0xffffffff;
            let stream = new ReadableStream({ async pull(controller) {
                try {
                    if (position === entry.compressed) { controller.close(); return; }
                    const size = Math.min(limits.inputChunkBytes, entry.compressed - position), bytes = await read(start + position, size);
                    if (!stopped) { position += size; controller.enqueue(bytes); }
                } catch (error) { if (!stopped) controller.error(error); }
            }, cancel() { stopped = true; } }, { highWaterMark: 0 });
            if (entry.method === 8) {
                let decompressor;
                try { decompressor = new DecompressionStream("deflate-raw"); }
                catch (_) { fail("XLSX_CAPABILITY_UNAVAILABLE", path, "Streaming deflate-raw is required in this Worker."); }
                stream = stream.pipeThrough(decompressor);
            }
            const reader = stream.getReader(); let complete = false;
            progress(path, 0, entry.expanded, true);
            try {
                while (true) {
                    guard(); const item = await reader.read(); guard();
                    if (item.done) break;
                    partBytes += item.value.length; expandedBytes += item.value.length;
                    if (partBytes > maximum || partBytes > entry.expanded || expandedBytes > limits.expandedBytes)
                        fail("XLSX_PART_LIMIT", path, "Actual expanded XLSX data exceeds its budget or declared size.");
                    for (const byte of item.value) crc = crcTable[(crc ^ byte) & 255] ^ (crc >>> 8);
                    onChunk(item.value); progress(path, partBytes, entry.expanded);
                }
                if (partBytes !== entry.expanded || ((crc ^ 0xffffffff) >>> 0) !== entry.crc)
                    fail("XLSX_ZIP_INVALID", path, "XLSX part size or CRC does not match.");
                complete = true; progress(path, partBytes, entry.expanded, true);
            } catch (error) {
                if (error.code) throw error;
                fail("XLSX_ZIP_INVALID", path, "Invalid or truncated compressed XLSX data.");
            } finally {
                if (!complete) await reader.cancel().catch(() => {});
                reader.releaseLock();
            }
        }
        return freeze({ parts: freeze(Array.from(entries.keys())), consume,
            statistics: () => freeze({ sourceBytesRead, sourceBytes: source.byteLength, expandedBytes, consumedParts: consumed.size }) });
    }
    root.RNTabularXlsxZip = freeze({ open, limits });
}(globalThis));
