using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public sealed class ProtectedTokenStoreWindows : IProtectedTokenStore
{
    private readonly Func<string> _storeDirectory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WheelWizard.DropboxOAuth.v1");

    public ProtectedTokenStoreWindows(Func<string>? storeDirectory = null) =>
        _storeDirectory = storeDirectory ?? (() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WheelWizard", "Credentials"));

    public async Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct)
    {
        var path = GetPath(accountId);
        if (!File.Exists(path))
            return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            try
            {
                var plain = Unprotect(protectedBytes, accountId);
                try
                {
                    return JsonSerializer.Deserialize<OAuthTokens>(plain) ?? throw new InvalidDataException("Stored Dropbox credentials are invalid.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        catch (Exception e) when (e is CryptographicException or IOException or JsonException or UnauthorizedAccessException)
        {
            throw new SecureStorageUnavailableException("Windows protected credential storage could not read credentials.", e);
        }
    }

    public async Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var plain = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var protectedBytes = Protect(plain, accountId);
            var path = GetPath(accountId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedBytes, ct).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
            var check = await LoadAsync(accountId, ct).ConfigureAwait(false);
            if (check != tokens)
                throw new CryptographicException("Windows credential round trip did not match.");
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SecureStorageUnavailableException("Windows protected credential storage is unavailable.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Task DeleteAsync(string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Delete(GetPath(accountId));
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SecureStorageUnavailableException("Windows protected credential storage is unavailable.", e);
        }
    }

    private string GetPath(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
        return Path.Combine(_storeDirectory(), $"{name}.dpapi");
    }

    private static byte[] Protect(byte[] input, string accountId) => Transform(input, accountId, protect: true);
    private static byte[] Unprotect(byte[] input, string accountId) => Transform(input, accountId, protect: false);

    private static byte[] Transform(byte[] input, string accountId, bool protect)
    {
        var inputBlob = Blob.FromBytes(input);
        var accountEntropy = SHA256.HashData(Entropy.Concat(Encoding.UTF8.GetBytes(accountId)).ToArray());
        var entropyBlob = Blob.FromBytes(accountEntropy);
        DataBlob output = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0x1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!succeeded)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            inputBlob.Free();
            entropyBlob.Free();
            CryptographicOperations.ZeroMemory(accountEntropy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    private struct Blob
    {
        public int Length;
        public IntPtr Data;
        public static Blob FromBytes(byte[] value)
        {
            var ptr = Marshal.AllocHGlobal(value.Length);
            Marshal.Copy(value, 0, ptr, value.Length);
            return new Blob { Length = value.Length, Data = ptr };
        }
        public void Free()
        {
            if (Data != IntPtr.Zero)
            {
                var bytes = new byte[Length];
                Marshal.Copy(Data, bytes, 0, Length);
                CryptographicOperations.ZeroMemory(bytes);
                Marshal.Copy(bytes, 0, Data, Length);
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, ref Blob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
