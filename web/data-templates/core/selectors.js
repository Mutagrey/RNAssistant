(function (root) {
    "use strict";

    const MAX_CELLS = 100000;
    const MAX_WORK = 200000;

    function fail(code, path, message) {
        const error = new Error(message);
        error.code = code;
        error.path = path;
        throw error;
    }

    function columnNumber(text) {
        let value = 0;
        for (const letter of text.toUpperCase()) value = value * 26 + letter.charCodeAt(0) - 64;
        if (value < 1 || value > 16384) fail("SELECTOR_INVALID", "selection", "Column exceeds Excel bounds.");
        return value;
    }

    function address(row, column) {
        let letters = "";
        for (let value = column; value > 0; value = Math.floor((value - 1) / 26))
            letters = String.fromCharCode(65 + (value - 1) % 26) + letters;
        return letters + row;
    }

    function cell(text) {
        const match = /^\$?([A-Z]{1,3})\$?([1-9][0-9]{0,6})$/i.exec(text);
        if (!match || Number(match[2]) > 1048576)
            fail("SELECTOR_INVALID", "selection", "An explicit A1 cell within Excel bounds is required.");
        return { row: Number(match[2]), column: columnNumber(match[1]) };
    }

    function rectangle(text) {
        if (typeof text !== "string" || text.length > 64)
            fail("SELECTOR_INVALID", "bounds", "A bounded A1 rectangle is required.");
        const parts = text.split(":");
        if (parts.length > 2) fail("SELECTOR_INVALID", "bounds", "Invalid rectangle.");
        const first = cell(parts[0]), last = cell(parts.length === 2 ? parts[1] : parts[0]);
        if (last.row < first.row || last.column < first.column)
            fail("SELECTOR_INVALID", "bounds", "Reversed rectangles are not supported.");
        return Object.freeze({ firstRow: first.row, lastRow: last.row,
            firstColumn: first.column, lastColumn: last.column });
    }

    function area(box) {
        return (box.lastRow - box.firstRow + 1) * (box.lastColumn - box.firstColumn + 1);
    }

    // All coordinates are resolved inside a single explicit sheet and bounded area.
    // Difference binds tighter than union. No clipping of explicit out-of-bounds cells.
    function select(text, boundsText, sheet, maxCells = MAX_CELLS) {
        if (typeof text !== "string" || !text.trim() || text.length > 2048 ||
            typeof sheet !== "string" || !sheet || sheet.length > 128)
            fail("SELECTOR_INVALID", "selection", "A bounded selector and sheet are required.");
        if (!Number.isInteger(maxCells) || maxCells < 1 || maxCells > MAX_CELLS)
            fail("SELECTOR_LIMIT", "maxCells", "Invalid selection limit.");
        const bounds = rectangle(boundsText), width = bounds.lastColumn - bounds.firstColumn + 1;
        if (area(bounds) > MAX_CELLS) fail("SELECTOR_LIMIT", "bounds", "Bounds exceed 100000 cells.");
        let position = 0, atoms = 0, work = 0;
        const skip = () => { while (/\s/.test(text[position] || "") && position < text.length) position++; };

        function atomBox(token) {
            let qualifier = null, range = token;
            if (token.startsWith("'")) {
                const quoted = /^'((?:[^']|'')+)'!([^!]+)$/.exec(token);
                if (!quoted) fail("SELECTOR_INVALID", "selection", "Invalid quoted sheet selector.");
                qualifier = quoted[1].replace(/''/g, "'"); range = quoted[2];
            } else if (token.includes("!")) {
                const parts = token.split("!");
                if (parts.length !== 2) fail("SELECTOR_INVALID", "selection", "Invalid sheet selector.");
                qualifier = parts[0]; range = parts[1];
            }
            if (qualifier !== null && qualifier !== sheet)
                fail("SELECTOR_SHEET_MISMATCH", "selection", "Selection belongs to another sheet.");
            let box;
            if (/^\$?[A-Z]{1,3}(?::\$?[A-Z]{1,3})?$/i.test(range)) {
                const columns = range.replace(/\$/g, "").split(":").map(columnNumber);
                box = { ...bounds, firstColumn: columns[0], lastColumn: columns[columns.length - 1] };
            } else if (/^[1-9][0-9]{0,6}(?::[1-9][0-9]{0,6})?$/.test(range)) {
                const rows = range.split(":").map(Number);
                box = { ...bounds, firstRow: rows[0], lastRow: rows[rows.length - 1] };
            } else if (/^\$?[A-Z]{1,3}\$?[1-9][0-9]{0,6}:\$?[A-Z]{1,3}\*$/i.test(range)) {
                const parts = range.split(":");
                const first = cell(parts[0]);
                box = { firstRow: first.row, lastRow: bounds.lastRow, firstColumn: first.column,
                    lastColumn: columnNumber(parts[1].replace(/[$*]/g, "")) };
            } else box = rectangle(range);
            if (box.firstRow > box.lastRow || box.firstColumn > box.lastColumn ||
                box.firstRow < bounds.firstRow || box.lastRow > bounds.lastRow ||
                box.firstColumn < bounds.firstColumn || box.lastColumn > bounds.lastColumn)
                fail("SELECTOR_OUTSIDE_BOUNDS", "selection", "Selection exceeds the declared bounds.");
            return box;
        }

        function primary(depth) {
            skip();
            if (depth > 16) fail("SELECTOR_LIMIT", "selection", "Selector nesting exceeds 16.");
            if (text[position] === "(") {
                position++;
                const result = union(depth + 1);
                skip();
                if (text[position++] !== ")") fail("SELECTOR_INVALID", "selection", "Missing closing parenthesis.");
                return result;
            }
            const start = position;
            let quoted = false;
            while (position < text.length) {
                const character = text[position];
                if (character === "'") {
                    if (quoted && text[position + 1] === "'") { position += 2; continue; }
                    quoted = !quoted;
                }
                if (!quoted && /[|\\()]/.test(character)) break;
                position++;
            }
            const token = text.slice(start, position).trim();
            if (!token || quoted) fail("SELECTOR_INVALID", "selection", "Missing or unterminated selector.");
            if (++atoms > 64) fail("SELECTOR_LIMIT", "selection", "Selector has more than 64 terms.");
            const box = atomBox(token);
            work += area(box);
            if (work > MAX_WORK) fail("SELECTOR_LIMIT", "selection", "Selection scan budget exceeded.");
            const result = new Set();
            for (let row = box.firstRow; row <= box.lastRow; row++)
                for (let column = box.firstColumn; column <= box.lastColumn; column++)
                    result.add((row - bounds.firstRow) * width + column - bounds.firstColumn);
            return result;
        }

        function difference(depth) {
            const result = primary(depth);
            skip();
            while (text[position] === "\\") {
                position++;
                for (const coordinate of primary(depth)) result.delete(coordinate);
                skip();
            }
            return result;
        }

        function union(depth) {
            const result = difference(depth);
            skip();
            while (text[position] === "|") {
                position++;
                for (const coordinate of difference(depth)) result.add(coordinate);
                skip();
            }
            return result;
        }

        const selected = union(0);
        skip();
        if (position !== text.length) fail("SELECTOR_INVALID", "selection", "Unexpected selector suffix.");
        if (selected.size > maxCells) fail("SELECTOR_LIMIT", "selection", "Selected cell limit exceeded.");
        return Object.freeze(Array.from(selected).sort((a, b) => a - b).map(index => {
            const row = bounds.firstRow + Math.floor(index / width), column = bounds.firstColumn + index % width;
            return Object.freeze({ sheet, row, column, address: address(row, column) });
        }));
    }

    root.RNTabularSelectors = Object.freeze({ select, cell, rectangle, area, address, fail, maxCells: MAX_CELLS });
}(globalThis));
