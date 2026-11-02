using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ServerHat;

/// The API token in %APPDATA%\ServerHat\api-token, readable only by the signed-in user:
/// the file's access list has that one user and doesn't inherit anything from its folder.
internal static class TokenFile
{
    public static string LoadOrCreate(string path)
    {
        try
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
            {
                Restrict(path);
                return existing;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        var token = string.Concat(bytes.Select(b => b.ToString("x2")));
        // Create it with the restricted access list rather than tightening it afterwards.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, OnlyMe()))
        {
            var data = Encoding.ASCII.GetBytes(token + "\n");
            stream.Write(data, 0, data.Length);
        }
        return token;
    }

    private static FileSecurity OnlyMe(bool setOwner = true)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        if (setOwner) security.SetOwner(me);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// Puts the access list back to "only me" if something widened it.
    private static void Restrict(string path)
    {
        try { File.SetAccessControl(path, OnlyMe(setOwner: false)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// True when only the current user (and no group) is allowed in, for the smoke test.
    public static bool IsPrivate(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = File.GetAccessControl(path);
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count > 0 && rules.All(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(me));
    }
}
