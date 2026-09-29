using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tog.Core.Secrets;

/// <summary>
/// Where secret values are kept: the operating system's own store, never a file
/// of the app's. Names and grants are in <see cref="SecretCatalog"/>; only the
/// value is here, under the service <see cref="Service"/> and the secret's name.
/// </summary>
public interface ISecretVault
{
    /// <summary>False when this machine has no store the app can use, such as Linux without <c>secret-tool</c>.</summary>
    bool Available { get; }

    /// <summary>What the store is called on this platform, for the UI.</summary>
    string Description { get; }

    /// <summary>The value, or null when there is none.</summary>
    string? Read(string name);

    /// <summary>Adds the value, or replaces it.</summary>
    void Write(string name, string value);

    /// <summary>Removes the value. Nothing happens when there is none.</summary>
    void Delete(string name);
}

/// <summary>Picks the vault for the platform the app is running on.</summary>
public static class SecretVaults
{
    /// <summary>The service name every value is filed under, so the user can find them in Keychain Access.</summary>
    public const string Service = "tog";

    public static ISecretVault ForThisMachine() => Guarded(
        OperatingSystem.IsMacOS() ? new MacKeychain()
        : OperatingSystem.IsWindows() ? new WindowsCredentials()
        : new LibSecret());

    /// <summary>
    /// <paramref name="vault"/>, refusing every read, write and delete made
    /// while answering an agent (<see cref="SecretBroker.ForAgent"/>). The
    /// broker checks too; this is so code that reaches the store some other
    /// way than the broker, an app tool that took the vault from the
    /// container say, is stopped as well.
    /// </summary>
    public static ISecretVault Guarded(ISecretVault vault) => new GuardedVault(vault);

    private sealed class GuardedVault(ISecretVault inner) : ISecretVault
    {
        public bool Available => inner.Available;

        public string Description => inner.Description;

        public string? Read(string name)
        {
            SecretBroker.RefuseAgents();
            return inner.Read(name);
        }

        public void Write(string name, string value)
        {
            SecretBroker.RefuseAgents();
            inner.Write(name, value);
        }

        public void Delete(string name)
        {
            SecretBroker.RefuseAgents();
            inner.Delete(name);
        }
    }
}

/// <summary>A failure of the platform's store, with what it said.</summary>
public sealed class SecretVaultException(string message) : Exception(message);

/// <summary>
/// The login keychain, through Security.framework. Not the <c>security</c>
/// command: it takes the value on its command line, which every process on the
/// machine can list while it runs.
/// </summary>
/// <remarks>
/// The item is created by this process, so macOS trusts the app's own binary to
/// read it back without asking, and asks you before any other program, the
/// <c>security</c> command an agent might run included. That is the one
/// platform where the store itself keeps other programs of yours out.
/// </remarks>
internal sealed class MacKeychain : ISecretVault
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;

    public bool Available => true;

    public string Description => "the macOS login keychain";

    public string? Read(string name)
    {
        var service = Encoding.UTF8.GetBytes(SecretVaults.Service);
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(
            0, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ItemNotFound)
        {
            return null;
        }

        Check(status, "read");
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(0, data);
            Release(item);
        }
    }

    public void Write(string name, string value)
    {
        var service = Encoding.UTF8.GetBytes(SecretVaults.Service);
        var account = Encoding.UTF8.GetBytes(name);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var status = SecKeychainFindGenericPassword(
                0, (uint)service.Length, service, (uint)account.Length, account, out _, out _, out var item);
            if (status == 0)
            {
                try
                {
                    Check(SecKeychainItemModifyAttributesAndData(item, 0, (uint)bytes.Length, bytes), "replace");
                }
                finally
                {
                    Release(item);
                }

                return;
            }

            if (status != ItemNotFound)
            {
                Check(status, "look up");
            }

            Check(SecKeychainAddGenericPassword(
                0, (uint)service.Length, service, (uint)account.Length, account, (uint)bytes.Length, bytes, out var added), "add");
            Release(added);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    public void Delete(string name)
    {
        var service = Encoding.UTF8.GetBytes(SecretVaults.Service);
        var account = Encoding.UTF8.GetBytes(name);
        var status = SecKeychainFindGenericPassword(
            0, (uint)service.Length, service, (uint)account.Length, account, out _, out _, out var item);
        if (status == ItemNotFound)
        {
            return;
        }

        Check(status, "look up");
        try
        {
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            Release(item);
        }
    }

    private static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new SecretVaultException($"The keychain could not {what} the secret (OSStatus {status}).");
        }
    }

    private static void Release(nint item)
    {
        if (item != 0)
        {
            CFRelease(item);
        }
    }

    // The SecKeychain* calls are marked deprecated in favour of SecItem*, which
    // takes CFDictionaries built from constants looked up at runtime. These
    // take plain bytes, still work on every macOS the app runs on, and file the
    // item in the same login keychain.
    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        nint keychainOrArray, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out nint passwordData, out nint itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] passwordData, out nint itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(nint itemRef, nint attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(nint itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(nint attrList, nint data);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}

/// <summary>
/// Windows Credential Manager, as generic credentials named
/// <c>tog:&lt;name&gt;</c>. Any program running as you can read
/// them; Windows does not tell programs apart here.
/// </summary>
internal sealed class WindowsCredentials : ISecretVault
{
    private const int GenericType = 1;
    private const int PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public bool Available => true;

    public string Description => "Windows Credential Manager";

    private static string Target(string name) => $"{SecretVaults.Service}:{name}";

    public string? Read(string name)
    {
        if (!CredRead(Target(name), GenericType, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NotFound ? null : throw new SecretVaultException($"Credential Manager could not read the secret (error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Write(string name, string value)
    {
        var blob = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = Target(name),
                CredentialBlobSize = (uint)(value.Length * 2),
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = name,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new SecretVaultException($"Credential Manager could not save the secret (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public void Delete(string name)
    {
        if (!CredDelete(Target(name), GenericType, 0) && Marshal.GetLastPInvokeError() is var error and not NotFound)
        {
            throw new SecretVaultException($"Credential Manager could not delete the secret (error {error}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}

/// <summary>
/// The Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c>,
/// which reads the value on its standard input, so it is never on a command
/// line. Any program running as you can read an unlocked collection.
/// </summary>
internal sealed class LibSecret : ISecretVault
{
    private readonly Lazy<bool> _available = new(() => Run(["--version"], null, out _, out _) is 0);

    public bool Available => _available.Value;

    public string Description => "the Secret Service (libsecret)";

    public string? Read(string name)
    {
        var code = Run(["lookup", "service", SecretVaults.Service, "account", name], null, out var output, out var error);

        // secret-tool exits 1 with nothing written for a value that is not there.
        return code switch
        {
            0 => output,
            1 when error.Length == 0 => null,
            _ => throw new SecretVaultException($"secret-tool could not read the secret: {error.Trim()}"),
        };
    }

    public void Write(string name, string value)
    {
        if (Run(["store", $"--label=Tog: {name}", "service", SecretVaults.Service, "account", name], value, out _, out var error) != 0)
        {
            throw new SecretVaultException($"secret-tool could not save the secret: {error.Trim()}");
        }
    }

    public void Delete(string name)
    {
        var code = Run(["clear", "service", SecretVaults.Service, "account", name], null, out _, out var error);
        if (code != 0 && error.Length > 0)
        {
            throw new SecretVaultException($"secret-tool could not delete the secret: {error.Trim()}");
        }
    }

    private static int? Run(IReadOnlyList<string> args, string? input, out string output, out string error)
    {
        output = "";
        error = "";
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEnd();
            error = stderr.GetAwaiter().GetResult();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A vault in memory, for tests and for a machine with no store.</summary>
public sealed class MemoryVault : ISecretVault
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool Available => true;

    public string Description => "memory";

    public string? Read(string name)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public void Write(string name, string value)
    {
        lock (_gate)
        {
            _values[name] = value;
        }
    }

    public void Delete(string name)
    {
        lock (_gate)
        {
            _values.Remove(name);
        }
    }
}
