// Build-time entry only. Browser output contains no module loader or Node API.
const parser = require('@microsoft/powerquery-parser');
module.exports = {
  DefaultSettings: parser.DefaultSettings,
  TaskUtils: parser.TaskUtils,
  Lexer: parser.Lexer,
  Parser: parser.Parser,
  CommonError: parser.CommonError,
  parserVersion: '5.0.0'
};
