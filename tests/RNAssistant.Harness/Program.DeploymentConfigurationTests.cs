using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RNAssistant.Core.Models;
using RNAssistant.Core.Services;
using RNAssistant.Core.Storage;

namespace RNAssistant.Harness
{
    internal static partial class Program
    {
        private static void ExternalBrowserSignedPolicy()
        {
            var root = Path.Combine(Path.GetTempPath(), "rna-policy-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var owner = AppDataPaths.CreateForRoot(Path.Combine(root, "owner"));
                var user = AppDataPaths.CreateForRoot(Path.Combine(root, "user"));
                const string password = "only-a-test-passphrase";
                byte[] privateKey;
                var trust = DeploymentConfigurationCrypto.CreateIssuer(password, out privateKey);
                var keyPath = Path.Combine(owner.Root, "signing-key.bin");
                File.WriteAllBytes(keyPath, privateKey);
                AssertTrue(!Encoding.UTF8.GetString(privateKey).Contains("Modulus"), "private parameters are encrypted, without a certificate container");
                AssertTrue(!JsonConvert.SerializeObject(trust).Contains(password), "public build metadata contains no plaintext password");
                using (var wrong = new DeploymentConfigurationStore(owner, trust))
                    RuntimeThrows<InvalidOperationException>(() => wrong.Unlock("incorrect"));
                using (var author = new DeploymentConfigurationStore(owner, trust))
                using (var recipient = new DeploymentConfigurationStore(user, trust))
                {
                    var address = new Uri("https://sub.example.com/path");
                    var outside = new Uri("https://outside.test/");
                    AssertTrue(!recipient.Allows(address, BrowserAccessKind.Manual), "missing configuration denies manual access");
                    RuntimeThrows<InvalidOperationException>(() => author.ConfigurationDirectory("not-authorized"));
                    var recipientEditor = recipient.Unlock(password);
                    AssertEqual(0, recipientEditor.Sites.Count, "password unlock does not require a key");
                    RuntimeThrows<FileNotFoundException>(() => recipient.Save(recipientEditor.SessionToken, new string[0], true, Path.Combine(user.Root, "missing.bin")));
                    var editor = author.Unlock(password);
                    AssertEqual(owner.Root, author.ConfigurationDirectory(editor.SessionToken), "folder access is developer-session guarded");
                    editor = author.Save(editor.SessionToken, new[] { "HTTPS://*.Example.COM:443/" }, false, keyPath);
                    var signed = File.ReadAllBytes(owner.DeploymentConfigFile);
                    AssertTrue(!Encoding.ASCII.GetString(signed).Contains("example.com"), "base64 obscures casual viewing");
                    File.WriteAllBytes(user.DeploymentConfigFile, signed);
                    AssertTrue(recipient.Allows(address, BrowserAccessKind.Agent) && recipient.Allows(address, BrowserAccessKind.Manual), "portable policy applies without the private key");
                    AssertTrue(!recipient.Allows(outside, BrowserAccessKind.Agent) && !recipient.Allows(outside, BrowserAccessKind.Manual), "manual broad access defaults off");
                    AssertEqual("{\"state\":\"Applied\"}", JsonConvert.SerializeObject(recipient.Status()), "public status exposes only the indicator");
                    using (var unconfigured = new DeploymentConfigurationStore(user))
                        AssertTrue(!unconfigured.Allows(address, BrowserAccessKind.Agent), "file cannot choose its own trust anchor");
                    RuntimeThrows<InvalidOperationException>(() => author.Save(editor.SessionToken, new[] { "*example.com" }, true, keyPath));
                    AssertTrue(signed.SequenceEqual(File.ReadAllBytes(owner.DeploymentConfigFile)), "invalid edits preserve the prior file");
                    editor = author.Save(editor.SessionToken, editor.Sites, true, keyPath);
                    signed = File.ReadAllBytes(owner.DeploymentConfigFile);
                    File.WriteAllBytes(user.DeploymentConfigFile, signed);
                    AssertTrue(recipient.Allows(outside, BrowserAccessKind.Manual) && !recipient.Allows(outside, BrowserAccessKind.Agent), "manual flag never authorizes agent reads/actions");
                    foreach (var invalid in new[] { "file:///C:/private", "https://u:p@outside.test/" })
                        AssertTrue(!recipient.Allows(new Uri(invalid), BrowserAccessKind.Manual), "manual flag cannot widen URL schemes or credentials");

                    var policy = DeploymentConfigurationCrypto.Verify(signed, trust);
                    foreach (var version in new[] { DeploymentConfigurationStore.RequiredPolicyVersion - 1, DeploymentConfigurationStore.RequiredPolicyVersion + 1 })
                    {
                        policy.PolicyVersion = version;
                        var versioned = DeploymentConfigurationCrypto.Sign(policy, privateKey, password, trust);
                        AssertEqual(version, DeploymentConfigurationCrypto.Verify(versioned, trust).PolicyVersion, "version test has a valid cryptographic signature");
                        File.WriteAllBytes(user.DeploymentConfigFile, versioned);
                        AssertTrue(!recipient.Allows(outside, BrowserAccessKind.Manual) && !recipient.Allows(address, BrowserAccessKind.Agent), "old/future policy versions reject all grants despite a valid signature");
                        AssertEqual(DeploymentConfigState.Rejected, recipient.Status().State, "version rejection is a small status, not an application error");
                    }
                    policy.PolicyVersion = DeploymentConfigurationStore.RequiredPolicyVersion;
                    policy.FormatVersion = 2;
                    File.WriteAllBytes(user.DeploymentConfigFile, DeploymentConfigurationCrypto.Sign(policy, privateKey, password, trust));
                    AssertEqual(DeploymentConfigState.Rejected, recipient.Status().State, "unknown schema is rejected as a whole");
                    policy.FormatVersion = 1;
                    RuntimeThrows<CryptographicException>(() => DeploymentConfigurationCrypto.Sign(policy, privateKey, "incorrect", trust));
                    var brokenKey = (byte[])privateKey.Clone(); brokenKey[60] ^= 1;
                    RuntimeThrows<CryptographicException>(() => DeploymentConfigurationCrypto.Sign(policy, brokenKey, password, trust));
                    byte[] anotherKey;
                    var otherTrust = DeploymentConfigurationCrypto.CreateIssuer(password, out anotherKey);
                    RuntimeThrows<CryptographicException>(() => DeploymentConfigurationCrypto.Sign(policy, anotherKey, password, trust));
                    File.WriteAllBytes(user.DeploymentConfigFile, DeploymentConfigurationCrypto.Sign(policy, anotherKey, password, otherTrust));
                    AssertEqual(DeploymentConfigState.Rejected, recipient.Status().State, "another issuer cannot replace the embedded public key");

                    var envelope = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(signed))));
                    var payload = Encoding.UTF8.GetString(Convert.FromBase64String((string)envelope["Payload"]));
                    envelope["Payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Replace("example.com", "attacker.test")));
                    File.WriteAllText(user.DeploymentConfigFile, Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.ToString())));
                    AssertEqual(DeploymentConfigState.Rejected, recipient.Status().State, "payload tampering invalidates the whole configuration");
                    File.WriteAllBytes(user.DeploymentConfigFile, signed);
                    AssertTrue(recipient.Allows(outside, BrowserAccessKind.Manual), "valid file replacement is applied");
                    File.Delete(user.DeploymentConfigFile);
                    AssertTrue(!recipient.Allows(outside, BrowserAccessKind.Manual), "deletion revokes manual permission without a last-good fallback");
                    File.WriteAllText(user.DeploymentConfigFile, new string('A', 131073));
                    AssertEqual(DeploymentConfigState.Rejected, recipient.Status().State, "bounded file input");
                    author.Lock();
                    RuntimeThrows<InvalidOperationException>(() => author.Save(editor.SessionToken, editor.Sites, true, keyPath));
                    RuntimeThrows<InvalidOperationException>(() => author.ConfigurationDirectory(editor.SessionToken));
                    editor = author.Unlock(password);
                    author.Save(editor.SessionToken, new string[0], false, keyPath);
                    AssertTrue(author.Status().State == DeploymentConfigState.Applied && !author.Allows(address, BrowserAccessKind.Manual), "signed empty configuration grants nothing");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
