using System.Security.AccessControl;
using System.Security.Principal;
using TapoCam.App.Services;

namespace TapoCam.App.Tests;

public sealed class RuntimeFileProtectionTests
{
    [Fact]
    public void ReplacingRuntimeFilePreservesExplicitCurrentUserOnlyAccess()
    {
        var folder = Directory.CreateTempSubdirectory("TapoCam-tests-");
        var path = Path.Combine(folder.FullName, "go2rtc.yaml");
        try
        {
            WindowsServices.WritePrivateText(path, "streams: {}");
            WindowsServices.WritePrivateText(path, "streams: []");

            Assert.Equal("streams: []", File.ReadAllText(path));
            Assert.Single(folder.GetFiles());

            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected);
            var rule = Assert.IsType<FileSystemAccessRule>(Assert.Single(
                security.GetAccessRules(includeExplicit: true, includeInherited: true,
                    typeof(SecurityIdentifier)).Cast<AuthorizationRule>()));
            Assert.False(rule.IsInherited);
            Assert.Equal(WindowsServices.CurrentUser(), rule.IdentityReference);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        }
        finally { folder.Delete(recursive: true); }
    }
}
