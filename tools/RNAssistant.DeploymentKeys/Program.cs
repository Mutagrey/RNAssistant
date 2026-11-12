using System;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || args.Length == 2 && args[1] != "--replace")
        { Console.WriteLine("RNAssistant.DeploymentKeys <repository-directory> [--replace]"); return 1; }
        try
        {
            if (Console.IsInputRedirected) throw new InvalidOperationException("Run interactively on your trusted computer. Passwords are never command arguments.");
            RNAssistant.DeploymentKeys.IssuerFiles.ValidateDestination(args[0], args.Length == 2);
            Console.WriteLine("This password opens developer mode and protects your signing key. Use at least 12 characters.");
            var password = ReadPassword("Password: ");
            if (password.Length < 12 || password != ReadPassword("Repeat: ")) throw new InvalidOperationException("Passwords must match and contain at least 12 characters.");
            var result = RNAssistant.DeploymentKeys.IssuerFiles.Generate(args[0], password, args.Length == 2);
            password = null;
            if (result.Replaced) Console.WriteLine("Previous issuer backed up under .local/deployment.");
            Console.WriteLine("Created and verified. Rebuild the application to embed config/deployment-trust.json.");
            Console.WriteLine("Keep .local/deployment/signing-key.bin private; it is ignored by Git and never embedded in the application.");
            Console.WriteLine("On Windows: Developer > password > key file path > Sign and save. Distribute only deployment-config.bin.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
    private static string ReadPassword(string prompt)
    {
        // Initialize interactive key input before the password prompt is displayed.
        _ = Console.KeyAvailable;
        Console.Write(prompt); var value = new StringBuilder();
        while (true)
        {
            var input = Console.ReadKey(true);
            if (input.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (input.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(input.KeyChar) && value.Length < 1024) value.Append(input.KeyChar);
        }
    }
}
