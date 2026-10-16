using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexProfiles.Core;

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify the current Windows user.");
            security.SetOwner(user);
            foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void AtomicWrite(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".codex-profiles-" + Guid.NewGuid() + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (OperatingSystem.IsWindows())
                {
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(true, false);
                    var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify the current Windows user.");
                    security.SetOwner(user);
                    security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                    security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(temporary).SetAccessControl(security);
                }
                else File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(data);
                stream.Flush(true);
            }
            if (File.Exists(path) && new FileInfo(path).LinkTarget is not null)
                throw new IOException("Refusing to replace a symbolic link: " + path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void WriteText(string path, string value) => AtomicWrite(path, Encoding.UTF8.GetBytes(value));
    public static byte[] Protect(byte[] bytes) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
    public static byte[] Unprotect(byte[] bytes) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
}

public sealed class PrivateWorkspace : IDisposable
{
    public string Path { get; }
    public PrivateWorkspace(string root)
    {
        Path = System.IO.Path.Combine(root, "work", Guid.NewGuid().ToString());
        Storage.PrivateDirectory(Path);
    }
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
