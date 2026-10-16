"use strict";
// Exported smoke fixture: proves that the pinned SheetJS build loads locally in a Worker.
importScripts("../vendor/sheetjs.full.min.js");
self.onmessage = event => {
    try {
        if (!(event.data instanceof ArrayBuffer)) throw Error("Expected workbook bytes.");
        const book = XLSX.read(new Uint8Array(event.data), { type: "array", dense: true, sheetRows: 16 });
        const sheet = book.Sheets[book.SheetNames[0]];
        self.postMessage({ version: XLSX.version, firstCode: sheet["!data"][11][0].v });
    } catch (error) { self.postMessage({ error: error.message }); }
};
