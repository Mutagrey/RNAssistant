(function (root) {
    "use strict";
    const geometry = root.RNTabularSelectors, fail = geometry.fail, freeze = Object.freeze;
    const spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const relations = "http://schemas.openxmlformats.org/package/2006/relationships";
    const officeRelations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    const contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    const limits = freeze({ metadataBytes: 2 * 1048576, worksheetBytes: 256 * 1048576, sharedStringBytes: 128 * 1048576,
        scannedCells: 2000000, sharedStrings: 2000000, retainedCharacters: 8 * 1048576, xmlDepth: 32, xmlAttributes: 64 });
    const attribute = (node, name, uri = "") => Object.values(node.attributes).find(item => item.local === name && item.uri === uri)?.value;
    const element = (node, local, uri = spreadsheet) => node && node.local === local && node.uri === uri;
    const unsupported = (path, message) => fail("XLSX_UNSUPPORTED", path, message);
    const invalid = (path, message) => fail("XLSX_INVALID", path, message);
    const integer = (value, path, maximum) => {
        if (typeof value !== "string" || !/^\d+$/.test(value) || !Number.isSafeInteger(Number(value)) || Number(value) > maximum)
            invalid(path, "Invalid bounded unsigned integer.");
        return Number(value);
    };
    // SpreadsheetML escapes are decoded once, after XML entity decoding. Rich text excludes phonetic runs.
    const unescape = value => value.replace(/_x([0-9a-f]{4})_/gi, (_, hex) => String.fromCharCode(parseInt(hex, 16)));
    function resolvePart(owner, target) {
        if (typeof target !== "string" || !target || /[\\\u0000-\u0020\u007f:%?#]/.test(target)) unsupported(owner, "Unsupported package relationship target.");
        const parts = target[0] === "/" ? [] : owner.split("/").slice(0, -1);
        for (const part of target.replace(/^\//, "").split("/")) {
            if (part === "..") { if (!parts.length) invalid(owner, "Relationship escapes the package."); parts.pop(); }
            else if (part === ".") continue;
            else if (!part) invalid(owner, "Empty relationship path component.");
            else parts.push(part);
        }
        return parts.join("/");
    }

    async function xml(zip, path, maximum, expectedRoot, namespace, handlers) {
        const parser = root.sax.parser(true, { xmlns: true, strictEntities: true, position: false });
        const decoder = new TextDecoder("utf-8", { fatal: true }), stack = []; let rootSeen = false, attributes;
        parser.onerror = () => fail("XLSX_XML_INVALID", path, "Malformed XML in XLSX part.");
        parser.ondoctype = parser.onsgmldeclaration = () => unsupported(path, "DTD/entity declarations are unsupported.");
        parser.onprocessinginstruction = item => {
            if (item.name !== "xml" || (/(?:^|\s)encoding\s*=\s*(['"])(.*?)\1/i.exec(item.body)?.[2] || "utf-8").toLowerCase() !== "utf-8")
                unsupported(path, "Only UTF-8 XML without processing instructions is supported.");
        };
        parser.onopentagstart = () => { attributes = new Set(); };
        parser.onattribute = item => {
            const key = item.uri + "|" + item.local;
            if (attributes.has(key) || ["__proto__", "constructor", "prototype", "hasOwnProperty"].includes(item.name)) invalid(path, "Duplicate or reserved XML attribute.");
            attributes.add(key);
            if (attributes.size > limits.xmlAttributes || item.value.length > 32767) fail("XLSX_XML_LIMIT", path, "XML attribute budget exceeded.");
        };
        parser.onopentag = node => {
            if (!stack.length) {
                if (rootSeen || !element(node, expectedRoot, namespace)) unsupported(path, "Unexpected XML root or namespace.");
                rootSeen = true;
            }
            if (stack.length >= limits.xmlDepth) fail("XLSX_XML_LIMIT", path, "XML nesting budget exceeded.");
            // AlternateContent needs format-specific selection; never silently discard its cells.
            if (node.local === "AlternateContent" && node.uri === "http://schemas.openxmlformats.org/markup-compatibility/2006")
                unsupported(path, "Markup compatibility alternatives are not supported by this reader profile.");
            stack.push(node); if (handlers.open) handlers.open(node, stack);
        };
        parser.ontext = parser.oncdata = text => { if (handlers.text) handlers.text(text, stack); };
        parser.onclosetag = () => { if (handlers.close) handlers.close(stack[stack.length - 1], stack); stack.pop(); };
        await zip.consume(path, maximum, bytes => {
            let text;
            try { text = decoder.decode(bytes, { stream: true }); }
            catch (_) { fail("XLSX_XML_INVALID", path, "XLSX XML is not valid UTF-8."); }
            parser.write(text);
        });
        let suffix;
        try { suffix = decoder.decode(); } catch (_) { fail("XLSX_XML_INVALID", path, "Incomplete UTF-8 XML sequence."); }
        parser.write(suffix).close();
        if (!rootSeen || stack.length) invalid(path, "Incomplete XML document.");
    }
    async function relationships(zip, path, owner) {
        const result = new Map();
        await xml(zip, path, limits.metadataBytes, "Relationships", relations, { open(node, stack) {
            if (stack.length === 1) return;
            if (stack.length !== 2 || !element(node, "Relationship", relations)) unsupported(path, "Unexpected relationship structure.");
            const id = attribute(node, "Id"), type = attribute(node, "Type"), target = attribute(node, "Target"), mode = attribute(node, "TargetMode") || "Internal";
            if (!id || !type || !target || result.has(id) || !["Internal", "External"].includes(mode)) invalid(path, "Invalid or duplicate relationship.");
            if (result.size >= 4096) fail("XLSX_XML_LIMIT", path, "Too many relationships.");
            result.set(id, freeze({ type, mode, target: mode === "Internal" ? resolvePart(owner, target) : target }));
        } });
        return result;
    }
    function oneRelationship(items, type, required) {
        const selected = Array.from(items.values()).filter(item => item.type === officeRelations + type);
        if (selected.length > 1 || (!selected.length && required)) invalid(type, "Expected one package relationship.");
        if (selected[0]?.mode === "External") unsupported(type, "External source relationships cannot be read.");
        return selected[0]?.target;
    }

    // One explicit matrix rectangle, raw stored values and source evidence. No display formatting/recalculation.
    async function open(blob, config, hooks) {
        root.RNTabularTemplate.fields(config, ["sheet", "bounds", "formulaPolicy"], "/source");
        if (typeof config.sheet !== "string" || !config.sheet || config.sheet.length > 128 || config.formulaPolicy !== "stored-cache")
            unsupported("/source", "An exact sheet and explicit stored-cache formula policy are required.");
        const box = geometry.rectangle(config.bounds), count = geometry.area(box);
        if (count > geometry.maxCells) fail("CAPTURE_LIMIT", "/source/bounds", "XLSX capture exceeds 100000 cells.");
        const zip = await root.RNTabularXlsxZip.open(blob, hooks);
        const types = new Map();
        await xml(zip, "[Content_Types].xml", limits.metadataBytes, "Types", contentTypes, { open(node, stack) {
            if (stack.length === 1) return;
            if (stack.length !== 2 || !["Default", "Override"].includes(node.local) || node.uri !== contentTypes) invalid("[Content_Types].xml", "Invalid content types.");
            const key = attribute(node, node.local === "Override" ? "PartName" : "Extension"), type = attribute(node, "ContentType");
            if (!key || !type || types.has(key)) invalid("[Content_Types].xml", "Duplicate or incomplete content type.");
            if (types.size >= 4096) fail("XLSX_XML_LIMIT", "[Content_Types].xml", "Too many content types.");
            types.set(key, type);
        } });
        const packageRels = await relationships(zip, "_rels/.rels", ""), workbook = oneRelationship(packageRels, "officeDocument", true);
        if (types.get("/" + workbook) !== "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")
            unsupported(workbook, "Only transitional XLSX workbooks are supported (not XLS/XLSB/XLSM/Strict OOXML).");
        const sheets = [], sheetNames = new Set(), sheetIds = new Set(); let dateSystem = "1900", propertiesSeen = false, sheetsSeen = false;
        await xml(zip, workbook, limits.metadataBytes, "workbook", spreadsheet, { open(node, stack) {
            if (element(node, "workbookPr") && stack.length === 2) {
                if (propertiesSeen) invalid(workbook, "Duplicate workbook properties."); propertiesSeen = true;
                const date1904 = attribute(node, "date1904");
                if (date1904 !== undefined && !["0", "1", "false", "true"].includes(date1904)) invalid(workbook, "Invalid workbook date system.");
                if (["1", "true"].includes(date1904)) dateSystem = "1904";
            }
            if (element(node, "sheets") && stack.length === 2) {
                if (sheetsSeen) invalid(workbook, "Duplicate sheets collection."); sheetsSeen = true;
            }
            if (!element(node, "sheet")) return;
            if (stack.length !== 3 || !element(stack[1], "sheets")) invalid(workbook, "Unexpected worksheet binding.");
            const name = attribute(node, "name"), id = attribute(node, "id", officeRelations.slice(0, -1));
            if (!name || !id || sheetNames.has(name.toLowerCase()) || sheetIds.has(id)) invalid(workbook, "Duplicate or incomplete worksheet binding.");
            if (sheets.length >= 256) fail("XLSX_XML_LIMIT", workbook, "Workbook exceeds 256 sheets.");
            sheetNames.add(name.toLowerCase()); sheetIds.add(id); sheets.push(freeze({ name, id }));
        } });
        const sheet = sheets.find(item => item.name === config.sheet);
        if (!sheet) fail("XLSX_SHEET_MISSING", "/source/sheet", "The exact worksheet is absent.");
        const slash = workbook.lastIndexOf("/"), relPath = workbook.slice(0, slash + 1) + "_rels/" + workbook.slice(slash + 1) + ".rels";
        const rels = await relationships(zip, relPath, workbook), selected = rels.get(sheet.id);
        if (!selected || selected.type !== officeRelations + "worksheet" || selected.mode !== "Internal") unsupported(workbook, "Selected sheet is not an internal worksheet.");
        if (types.get("/" + selected.target) !== "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml") unsupported(selected.target, "Unsupported worksheet content type.");
        const sharedPart = oneRelationship(rels, "sharedStrings", false), cells = new Map(), neededStrings = new Set();
        let scannedCells = 0, row = 0, column = 0, current = null, dataSeen = false, retainedCharacters = 0, formulaCells = 0, missingCaches = 0;
        function charge(value) {
            retainedCharacters += value;
            if (retainedCharacters > limits.retainedCharacters) fail("XLSX_CAPTURE_LIMIT", selected.target, "Retained cell strings/formulas exceed 8 Mi UTF-16 characters.");
        }
        await xml(zip, selected.target, limits.worksheetBytes, "worksheet", spreadsheet, {
            open(node, stack) {
                const parent = stack[stack.length - 2];
                if ((element(parent, "sheetData") && !element(node, "row")) ||
                    (element(parent, "row") && !element(node, "c"))) unsupported(selected.target, "Unsupported worksheet row/cell content.");
                if (element(node, "sheetData") && stack.length === 2) {
                    if (dataSeen) invalid(selected.target, "Duplicate worksheet data."); dataSeen = true;
                }
                if (element(node, "row")) {
                    if (stack.length !== 3 || !element(parent, "sheetData")) invalid(selected.target, "Unexpected row structure.");
                    const next = integer(attribute(node, "r"), selected.target, 1048576);
                    if (!next || next <= row) unsupported(selected.target, "Rows require explicit increasing coordinates.");
                    row = next; column = 0;
                }
                if (element(node, "c")) {
                    if (stack.length !== 4 || !element(parent, "row") || current) invalid(selected.target, "Unexpected cell structure.");
                    const address = attribute(node, "r");
                    if (!address || !/^[A-Z]{1,3}[1-9][0-9]*$/.test(address)) unsupported(selected.target, "Cells require explicit A1 coordinates.");
                    const coordinate = geometry.cell(address);
                    if (coordinate.row !== row || coordinate.column <= column) invalid(selected.target, "Cell coordinates are duplicated, unordered or outside their row.");
                    column = coordinate.column;
                    if (++scannedCells > limits.scannedCells) fail("XLSX_CELL_LIMIT", selected.target, "Worksheet exceeds 2000000 scanned cells.");
                    const capture = row >= box.firstRow && row <= box.lastRow && column >= box.firstColumn && column <= box.lastColumn;
                    current = { address, capture, type: attribute(node, "t") || "n", value: "", inline: "", hasValue: false, hasInline: false, formula: null };
                    if (capture && (attribute(node, "cm") !== undefined || attribute(node, "vm") !== undefined))
                        unsupported(address, "Cells with external value/cell metadata require a separate typed adapter.");
                }
                if (!current || !current.capture) return;
                if (!["n", "s", "b", "e", "str", "inlineStr"].includes(current.type)) unsupported(current.address, "Unsupported cell type: " + current.type);
                if (element(parent, "c") && !["f", "v", "is"].some(name => element(node, name)))
                    unsupported(current.address, "Unsupported cell value structure.");
                if ((element(parent, "v") || element(parent, "f"))) invalid(current.address, "Cell values/formulas cannot contain nested elements.");
                if (element(node, "t") && !element(parent, "is") && !element(parent, "r") && !element(parent, "rPh")) invalid(current.address, "Unexpected rich-text structure.");
                if (element(node, "f") || element(node, "v") || element(node, "is")) {
                    if (!element(parent, "c")) invalid(current.address, "Unexpected cell value structure.");
                    if (node.local === "f") {
                        if (current.formula) invalid(current.address, "Duplicate cell formula.");
                        current.formula = { text: "", type: attribute(node, "t") || "normal" };
                    } else if (node.local === "v") {
                        if (current.hasValue) invalid(current.address, "Duplicate cell value."); current.hasValue = true;
                    } else {
                        if (current.hasInline) invalid(current.address, "Duplicate inline string."); current.hasInline = true;
                    }
                }
            },
            text(value, stack) {
                if (!current?.capture) return;
                const node = stack[stack.length - 1];
                if (element(node, "v")) current.value += value;
                else if (element(node, "f")) current.formula.text += value;
                else if (element(node, "t") && stack.some(item => element(item, "is")) && !stack.some(item => element(item, "rPh"))) current.inline += value;
                if (current.value.length > 32767 || current.inline.length > 32767 || current.formula?.text.length > 32767)
                    fail("XLSX_CELL_LIMIT", current.address, "Cell text/formula exceeds 32767 characters.");
            },
            close(node) {
                if (!element(node, "c")) return;
                const item = current; current = null;
                if (!item.capture) return;
                if (item.formula && item.type === "inlineStr") invalid(item.address, "Inline strings cannot also contain formulas.");
                if (item.hasInline && (item.type !== "inlineStr" || item.hasValue || item.formula)) invalid(item.address, "Conflicting inline value.");
                let cell;
                if (item.formula) { formulaCells++; if (!item.hasValue || (item.value === "" && item.type !== "str")) missingCaches++; }
                if (item.formula && (!item.hasValue || (item.value === "" && item.type !== "str"))) cell = { kind: "unavailable", reason: "formula-cache-absent" };
                else if (item.type === "inlineStr") {
                    if (!item.hasInline || item.hasValue) invalid(item.address, "Missing inline string.");
                    cell = { kind: "text", value: unescape(item.inline) };
                } else if (!item.hasValue || (item.type === "n" && item.value === "")) cell = { kind: "blank" };
                else if (item.type === "n") cell = { kind: "numeric", lexeme: item.value };
                else if (item.type === "s") {
                    const index = integer(item.value, item.address, limits.sharedStrings - 1);
                    neededStrings.add(index); cell = { kind: "shared-reference", index };
                } else if (item.type === "b") {
                    if (!["0", "1"].includes(item.value)) invalid(item.address, "Invalid boolean cell."); cell = { kind: "boolean", value: item.value === "1" };
                } else if (item.type === "e") cell = { kind: "error", value: item.value };
                else if (item.type === "str") cell = { kind: "text", value: unescape(item.value) };
                else unsupported(item.address, "Unsupported cell type: " + item.type + ". ISO dates require a future typed date adapter.");
                if (item.formula) cell.formula = item.formula;
                charge((cell.value?.length || 0) + (cell.lexeme?.length || 0) + (cell.formula?.text.length || 0));
                cells.set(item.address, cell.kind === "shared-reference" ? cell : root.RNTabularSnapshot.copyCell(cell, item.address, 2));
            }
        });
        if (!dataSeen) invalid(selected.target, "Worksheet has no sheetData element.");
        if (neededStrings.size) {
            if (!sharedPart) fail("XLSX_PART_MISSING", selected.target, "Shared-string cells have no shared-string relationship.");
            if (types.get("/" + sharedPart) !== "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml") unsupported(sharedPart, "Unsupported shared-string content type.");
            const strings = new Map(); let index = -1, text = "", inString = false;
            await xml(zip, sharedPart, limits.sharedStringBytes, "sst", spreadsheet, {
                open(node, stack) {
                    const parent = stack[stack.length - 2];
                    if (stack.length === 2 && !element(node, "si") && !element(node, "extLst")) unsupported(sharedPart, "Unsupported shared-string entry.");
                    if (element(node, "t") && !element(parent, "si") && !element(parent, "r") && !element(parent, "rPh")) invalid(sharedPart, "Unexpected shared rich-text structure.");
                    if (!element(node, "si")) return;
                    if (stack.length !== 2 || inString) invalid(sharedPart, "Unexpected shared-string structure.");
                    inString = true; text = "";
                    if (++index >= limits.sharedStrings) fail("XLSX_STRING_LIMIT", sharedPart, "Shared-string count exceeds its budget.");
                },
                text(value, stack) {
                    if (inString && neededStrings.has(index) && element(stack[stack.length - 1], "t") && !stack.some(item => element(item, "rPh"))) {
                        text += value; if (text.length > 32767) fail("XLSX_STRING_LIMIT", sharedPart, "Shared string exceeds 32767 characters.");
                    }
                },
                close(node) {
                    if (!element(node, "si")) return;
                    if (neededStrings.has(index)) { const value = unescape(text); charge(value.length); strings.set(index, value); }
                    inString = false;
                }
            });
            for (const [address, cell] of cells) if (cell.kind === "shared-reference") {
                if (!strings.has(cell.index)) invalid(address, "Shared-string reference is outside the table.");
                cells.set(address, root.RNTabularSnapshot.copyCell({ kind: "text", value: strings.get(cell.index), ...(cell.formula ? { formula: cell.formula } : {}) }, address, 2));
            }
        }
        const blank = freeze({ kind: "blank" }), source = freeze({ kind: "xlsx-file", label: typeof blob.name === "string" ? blob.name.slice(0, 256) : "Selected XLSX" });
        const capabilities = freeze({ version: 1, shape: "matrix", cellVersion: 2, numeric: "exact-lexeme", formulaPolicy: "stored-cache",
            cacheFreshness: "unknown", formatting: "not-applied", dateSystem, coverage: "complete-rectangle" });
        return freeze({ source, sheet: config.sheet, count, capabilities,
            diagnostics: freeze({ ...zip.statistics(), scannedCells, storedCells: cells.size, retainedCharacters, formulaCells, missingCaches }),
            read(coordinate) {
                if (coordinate.sheet !== config.sheet) fail("CAPTURE_SHEET_MISMATCH", coordinate.address, "XLSX capture belongs to another sheet.");
                if (coordinate.row < box.firstRow || coordinate.row > box.lastRow || coordinate.column < box.firstColumn || coordinate.column > box.lastColumn ||
                    coordinate.address !== geometry.address(coordinate.row, coordinate.column)) fail("CAPTURE_INCOMPLETE", coordinate.address, "Cell is outside the fully read capture rectangle.");
                return cells.get(coordinate.address) || blank;
            }
        });
    }
    root.RNTabularXlsxReader = freeze({ open, limits });
}(globalThis));
