using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Runtime;
using RNAssistant.Runtime.Contracts;

namespace RNAssistant.Cli
{
    // Terminal presentation and explicit user input only. The application service
    // still owns continuation, validation, policy, effects and durable history.
    internal sealed class CliRunConsole
    {
        public void Progress(WorkspaceRunEvent update)
        {
            switch (update.Kind)
            {
                case "InputAccepted":
                    Console.WriteLine("Session: " + Text(update.Receipt.SessionId));
                    Console.WriteLine("Input accepted. Run: " + Text(update.Receipt.RunId));
                    break;
                case "Started": Console.WriteLine("Run started."); break;
                case "ModelStepStarted":
                    Console.WriteLine("Thinking (step " + update.Summary.IterationsUsed + ")..."); break;
                case "ToolStarted": Console.WriteLine("Tool: " + Text(update.ToolId)); break;
                case "ToolCompleted":
                    Console.WriteLine("Tool finished: " + Text(update.ToolId) + " — " + update.Activity?.Outcome);
                    if (!string.IsNullOrWhiteSpace(update.Activity?.Message))
                        Console.WriteLine("  " + Text(update.Activity.Message, 512));
                    if (update.Activity?.File != null) File(update.Activity.File);
                    if (update.Activity?.Verification != null) Verification(update.Activity.Verification);
                    break;
                case "compacting": Console.WriteLine("Compacting context..."); break;
                case "compacted": Console.WriteLine("Context checkpoint saved."); break;
            }
        }

        public void Result(WorkspaceRunResult result)
        {
            if (result.Duplicate)
            {
                Console.WriteLine("Input already accepted. Session: " + Text(result.SessionId) +
                    "; run: " + Text(result.Receipt.RunId) + ". Inspect the session for its outcome.");
                return;
            }
            var summary = result.Summary;
            Console.WriteLine("Session: " + Text(result.SessionId));
            Console.WriteLine("Run: " + Text(summary.RunId) + " — " + summary.Lifecycle +
                " (" + Text(summary.Reason) + "); health: " + summary.ExecutionHealth);
            if (!string.IsNullOrWhiteSpace(summary.AssistantMessage))
                Console.WriteLine("Assistant: " + Text(summary.AssistantMessage, 2000));
            Console.WriteLine("Steps: " + summary.IterationsUsed + "; tools: " + summary.ToolStepsUsed);
            var presentation = result.Presentation;
            Console.WriteLine("File effects recorded for this task:");
            if (presentation == null) Console.WriteLine("  Unavailable.");
            else
            {
                if (presentation.Files.Count == 0) Console.WriteLine("  No confirmed changes recorded.");
                foreach (var file in presentation.Files) File(file);
                if (presentation.FilesTruncated) Console.WriteLine("  Showing the latest 32 operations; inspect canonical history for earlier effects.");
            }
            Verification(presentation?.LatestVerification);
            var acceptance = result.Acceptance;
            Console.WriteLine("Acceptance: " + acceptance.State +
                (acceptance.Requested ? "; current complete reads: " + acceptance.AcceptedCompleteFileReads +
                    "; verified writes: " + acceptance.VerifiedFileChanges : " (no independent task checks requested)"));
            if (acceptance.RequireWebVerification)
                Console.WriteLine("Current files meet required browser checks: " + acceptance.VerifiedWebSnapshot);
            foreach (var file in acceptance.MissingFiles.Take(32)) Console.WriteLine("Missing: " + Text(file));
            if (!string.IsNullOrEmpty(acceptance.Error)) Console.WriteLine("Acceptance error: " + Text(acceptance.Error));
            if (summary.PendingConfirmation != null)
                Console.WriteLine("Approval required. Resume with rna resume --workspace <path> --session " + Text(result.SessionId) +
                    ", or use approve/deny with --pending " + Text(summary.PendingConfirmation.PendingId) + ".");
            else if (result.UserInput != null)
                Console.WriteLine("Answers required. Resume with rna resume --workspace <path> --session " + Text(result.SessionId) +
                    ", or use answer --answers-file <file>. inspect --jsonl returns the question IDs.");
            else if (summary.Reason == "model_needs_input")
                Console.WriteLine("New input required: rna run --workspace <path> --session " + Text(result.SessionId) + " --message <answer>.");
        }

        private static void File(WorkspaceFileActivity file)
        {
            var state = file.Effect == ResourceEffectOutcome.UnknownAfterDispatch ? "UNKNOWN possible effect" : "Verified change";
            Console.WriteLine("  " + state + ": " + Text(file.ToolId) + " " + Text(file.Path ?? "[target unavailable]", 1024) +
                (file.TargetPath == null ? "" : " -> " + Text(file.TargetPath, 1024)));
        }

        private static void Verification(WorkspaceVerificationSummary verification)
        {
            if (verification == null) { Console.WriteLine("Browser verification: not requested/run in this task."); return; }
            if (verification.Unavailable) { Console.WriteLine("Browser verification: evidence unavailable."); return; }
            Console.WriteLine("Browser snapshot verification: " + verification.State + "; " + Text(verification.EntryPath) +
                "; checks passed/failed/not-run: " + verification.PassedChecks + "/" + verification.FailedChecks + "/" + verification.NotRunChecks);
            Console.WriteLine("  Snapshot: " + Text(verification.SnapshotId) + "; record: " + Text(verification.VerificationId) +
                (verification.Historical ? " (historical)" : "") + ". This records the checked snapshot.");
        }

        public bool? Approval(string toolId, string path, string target, CancellationToken token)
        {
            if (path?.Length > 1024 || target?.Length > 1024)
            {
                Console.WriteLine("Target exceeds the terminal display bound. Inspect --jsonl and use explicit approve/deny; leaving approval pending.");
                return null;
            }
            Console.WriteLine("Approval: " + Text(toolId) + " " + Text(path, 1024) +
                (target == null ? "" : " -> " + Text(target, 1024)));
            while (true)
            {
                var value = ReadLine("Type yes to approve, no to deny, or Enter to leave pending: ", 16, token);
                if (value == null || value.Length == 0) return null;
                if (value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
                if (value.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
                Console.WriteLine("Enter yes, no, or leave it pending.");
            }
        }

        public UserQuestionAnswerCommand Answers(WorkspaceUserInput input, CancellationToken token)
        {
            var command = new UserQuestionAnswerCommand { RunId = input.RunId,
                QuestionSetId = input.QuestionSet.QuestionSetId,
                Answers = new System.Collections.Generic.List<UserQuestionAnswer>() };
            foreach (var question in input.QuestionSet.Questions)
            {
                Console.WriteLine(Text(question.Header) + ": " + Text(question.Prompt, 1200));
                for (var i = 0; i < question.Options.Count; i++)
                {
                    var option = question.Options[i];
                    Console.WriteLine("  " + (i + 1) + ". " + Text(option.Label) +
                        (option.Recommended ? " (recommended)" : "") + " — " + Text(option.Description));
                }
                while (true)
                {
                    var value = ReadLine((question.Selection == "multiple" ? "Choices (e.g. 1,2)" : "Choice number") +
                        (question.AllowFreeText ? " or text: <answer>" : "") + "; Esc/Ctrl+D to leave pending: ", 4006, token);
                    if (value == null) return null;
                    var answer = new UserQuestionAnswer { QuestionId = question.Id };
                    if (value.StartsWith("text:", StringComparison.OrdinalIgnoreCase)) answer.FreeText = value.Substring(5).Trim();
                    else
                    {
                        var valid = true;
                        foreach (var part in value.Split(','))
                        {
                            int index;
                            if (!int.TryParse(part.Trim(), out index) || index < 1 || index > question.Options.Count)
                            { valid = false; break; }
                            answer.OptionIds.Add(question.Options[index - 1].Id);
                        }
                        if (!valid) { Console.WriteLine("Choose a listed number or an allowed text answer."); continue; }
                    }
                    try
                    {
                        UserQuestionAnswers.Accept(new UserQuestionSet { QuestionSetId = input.QuestionSet.QuestionSetId,
                            Questions = new System.Collections.Generic.List<UserQuestion> { question } },
                            new UserQuestionAnswerCommand { QuestionSetId = command.QuestionSetId,
                                Answers = new System.Collections.Generic.List<UserQuestionAnswer> { answer } });
                    }
                    catch (ArgumentException) { Console.WriteLine("Invalid selection or empty/disallowed text. Try again."); continue; }
                    command.Answers.Add(answer);
                    break;
                }
            }
            return command;
        }

        private static string ReadLine(string prompt, int maximum, CancellationToken token)
        {
            // ReadKey switches the terminal into character mode. Polling
            // KeyAvailable first leaves some Unix terminals in line/echo mode.
            // A cancelled wait exits this CLI invocation; its reader can never
            // submit an answer or perform application work.
            return Task.Run(() => ReadTerminalLine(prompt, maximum, token), token)
                .WaitAsync(token).GetAwaiter().GetResult();
        }

        private static string ReadTerminalLine(string prompt, int maximum, CancellationToken token)
        {
            Console.Write(prompt);
            var line = new StringBuilder();
            var overflow = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
                    throw new OperationCanceledException();
                if (key.Key == ConsoleKey.Escape || key.KeyChar == '\u0004') { Console.WriteLine(); return null; }
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    if (!overflow) return line.ToString().Trim();
                    Console.WriteLine("Input exceeded " + maximum + " characters; enter a shorter answer.");
                    line.Clear(); overflow = false; Console.Write(prompt); continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (line.Length > 0) { line.Length--; Console.Write("\b \b"); }
                    continue;
                }
                if (key.KeyChar == '\0' || char.IsControl(key.KeyChar) ||
                    char.GetUnicodeCategory(key.KeyChar) == UnicodeCategory.Format) continue;
                if (line.Length < maximum) { line.Append(key.KeyChar); Console.Write(key.KeyChar); }
                else overflow = true;
            }
        }

        internal static string Text(string value, int maximum = 240)
        {
            if (value == null) return "";
            var safe = new string(value.Take(maximum).Select(c => char.IsControl(c) ||
                char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray());
            return value.Length > maximum ? safe + " [truncated]" : safe;
        }
    }
}
