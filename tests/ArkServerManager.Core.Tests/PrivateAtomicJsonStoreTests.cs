using ArkServerManager.Core;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ArkServerManager.Core.Tests;

public sealed class PrivateAtomicJsonStoreTests
{
    [Fact]
    public async Task CredentialDocumentReplacementRetainsRestrictedPermissionsAndReadableContents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ark-private-json-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "credentials.json");
        try
        {
            var store = new AtomicJsonStore<Document>(path, privateFile: true);
            foreach (var value in new[] { "first", "replacement" })
            {
                await store.SaveAsync(new(value)); Assert.Equal(value, (await store.LoadAsync())!.Value);
                if (OperatingSystem.IsWindows())
                {
                    var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                    Assert.True(security.AreAccessRulesProtected);
                    using var identity = WindowsIdentity.GetCurrent();
                    var allowed = new[] { identity.User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value };
                    foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                        Assert.Contains(rule.IdentityReference.Value, allowed);
                }
                else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    public sealed record Document(string Value);
}
