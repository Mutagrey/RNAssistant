(function (root) {
    "use strict";

    const geometry = root.RNTabularSelectors;
    const fail = geometry.fail;
    const own = (object, key) => Object.prototype.hasOwnProperty.call(object, key);

    function object(value, keys, path) {
        if (!value || typeof value !== "object" || Array.isArray(value) ||
            Object.keys(value).some(key => !keys.includes(key)))
            fail("SCHEMA_INVALID", path, "Expected an object with only declared fields.");
    }

    function text(value, path, maximum = 128) {
        if (typeof value !== "string" || !value || value.length > maximum || /[\u0000-\u001f]/.test(value))
            fail("SCHEMA_INVALID", path, "Expected bounded nonempty text.");
    }

    function copyCell(value, path) {
        object(value, ["kind", "value", "reason"], path);
        const kind = value.kind;
        if (!["number", "text", "boolean", "blank", "error", "unavailable"].includes(kind))
            fail("SCHEMA_INVALID", path + ".kind", "Unsupported cell kind.");
        if (kind === "unavailable") {
            if (own(value, "value")) fail("SCHEMA_INVALID", path, "An unavailable cell has no value.");
            text(value.reason, path + ".reason", 256);
            return Object.freeze({ kind, reason: value.reason });
        }
        if (own(value, "reason")) fail("SCHEMA_INVALID", path, "Only unavailable cells have a reason.");
        if (kind === "blank") {
            if (own(value, "value")) fail("SCHEMA_INVALID", path, "A blank cell has no value.");
            return Object.freeze({ kind });
        }
        if ((kind === "number" && (typeof value.value !== "number" || !Number.isFinite(value.value))) ||
            (kind === "boolean" && typeof value.value !== "boolean") ||
            (["text", "error"].includes(kind) && (typeof value.value !== "string" || value.value.length > 32767)) ||
            (kind === "error" && !value.value))
            fail("SCHEMA_INVALID", path + ".value", "Cell value does not match its kind.");
        return Object.freeze({ kind, value: value.value });
    }

    // This normalized input is data, not resource authority or live-capture evidence.
    // Missing coverage is never synthesized as an empty cell.
    function open(input) {
        object(input, ["version", "source", "sheet", "ranges"], "snapshot");
        if (input.version !== 1) fail("VERSION_UNSUPPORTED", "snapshot.version", "Unsupported snapshot version.");
        object(input.source, ["kind", "label"], "snapshot.source");
        if (!["fixture", "retained-snapshot"].includes(input.source.kind))
            fail("SCHEMA_INVALID", "snapshot.source.kind", "M0 accepts fixtures and normalized retained snapshots only.");
        text(input.source.label, "snapshot.source.label", 256);
        text(input.sheet, "snapshot.sheet");
        if (!Array.isArray(input.ranges) || !input.ranges.length || input.ranges.length > 64)
            fail("CAPTURE_LIMIT", "snapshot.ranges", "Capture requires 1 to 64 bounded ranges.");
        const cells = new Map();
        let count = 0;
        input.ranges.forEach((range, index) => {
            const path = "snapshot.ranges[" + index + "]";
            object(range, ["address", "cells"], path);
            const box = geometry.rectangle(range.address);
            count += geometry.area(box);
            if (count > geometry.maxCells) fail("CAPTURE_LIMIT", path, "Capture exceeds 100000 cells.");
            if (!Array.isArray(range.cells) || range.cells.length !== box.lastRow - box.firstRow + 1)
                fail("CAPTURE_SHAPE_INVALID", path, "Capture row count differs from its physical range.");
            range.cells.forEach((row, rowIndex) => {
                if (!Array.isArray(row) || row.length !== box.lastColumn - box.firstColumn + 1)
                    fail("CAPTURE_SHAPE_INVALID", path, "Capture column count differs from its physical range.");
                row.forEach((value, columnIndex) => {
                    const address = geometry.address(box.firstRow + rowIndex, box.firstColumn + columnIndex);
                    if (cells.has(address)) fail("CAPTURE_OVERLAP", path, "Capture includes " + address + " more than once.");
                    cells.set(address, copyCell(value, path + ".cells[" + rowIndex + "][" + columnIndex + "]"));
                });
            });
        });
        const source = Object.freeze({ kind: input.source.kind, label: input.source.label }), sheet = input.sheet;
        return Object.freeze({ source, sheet, count,
            read: coordinate => {
                if (coordinate.sheet !== sheet)
                    fail("CAPTURE_SHEET_MISMATCH", "selection", "Capture belongs to another sheet.");
                if (!cells.has(coordinate.address))
                    fail("CAPTURE_INCOMPLETE", coordinate.address, "The selected cell was not captured.");
                return cells.get(coordinate.address);
            }
        });
    }

    root.RNTabularSnapshot = Object.freeze({ open, object });
}(globalThis));
