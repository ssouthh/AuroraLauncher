using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AuroraLauncher.Core;

/// <summary>
/// Защита учётных данных: шифрование через Windows DPAPI (область CurrentUser —
/// расшифровать данные может только текущая учётная запись Windows) и ограничение
/// NTFS-прав на файл аккаунтов.
/// </summary>
public static class Secure
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AuroraLauncher.Accounts.v1");

    public static byte[] Protect(byte[] data)
        => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Protect(byte[] data, string purpose)
        => ProtectedData.Protect(data, Encoding.UTF8.GetBytes("AuroraLauncher." + purpose), DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data)
        => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data, string purpose)
        => ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes("AuroraLauncher." + purpose), DataProtectionScope.CurrentUser);

    /// <summary>Оставляет доступ к файлу только текущему пользователю Windows.</summary>
    public static void RestrictToCurrentUser(string file)
    {
        try
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(true, false); // отключить наследование прав
            var rule = new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow);
            sec.AddAccessRule(rule);
            new FileInfo(file).SetAccessControl(sec);
        }
        catch { /* не критично */ }
    }
}
