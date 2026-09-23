using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WheelWizard.Features.CloudSync;

public sealed class ProtectedTokenStoreMac : IProtectedTokenStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    public async Task<OAuthTokens?> LoadAsync(string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var query = CreateDictionary(("class", "genp"), ("service", "WheelWizard.DropboxOAuth"), ("account", RequiredAccount(accountId)), ("returnData", true), ("matchLimit", "m_LimitOne"));
            try
            {
                var status = SecItemCopyMatching(query, out var result);
                if (status == ErrSecItemNotFound)
                    return null;
                if (status != ErrSecSuccess)
                    throw new CryptographicException($"Keychain returned status {status}.");
                try
                {
                    var length = (int)CFDataGetLength(result);
                    var bytes = new byte[length];
                    Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
                    try
                    {
                        return JsonSerializer.Deserialize<OAuthTokens>(bytes) ?? throw new InvalidDataException("Stored Dropbox credentials are invalid.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }
                finally { CFRelease(result); }
            }
            finally { CFRelease(query); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or CryptographicException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new SecureStorageUnavailableException("macOS Keychain is unavailable.", e);
        }
        finally { await Task.CompletedTask; }
    }

    public async Task SaveAsync(string accountId, OAuthTokens tokens, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens);
        try
        {
            var query = CreateDictionary(("class", "genp"), ("service", "WheelWizard.DropboxOAuth"), ("account", RequiredAccount(accountId)));
            var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            var attrs = CreateDictionary(("valueData", data));
            var addAttributes = CreateDictionary(("class", "genp"), ("service", "WheelWizard.DropboxOAuth"), ("account", RequiredAccount(accountId)), ("valueData", data));
            try
            {
                var status = SecItemAdd(addAttributes, IntPtr.Zero);
                if (status == ErrSecDuplicateItem)
                    status = SecItemUpdate(query, attrs);
                else if (status == ErrSecSuccess)
                {
                    // Add only the data attribute; query contains key attributes and never credential bytes.
                    CFRelease(attrs);
                    attrs = IntPtr.Zero;
                }
                if (status != ErrSecSuccess)
                    throw new CryptographicException($"Keychain returned status {status}.");
            }
            finally
            {
                if (attrs != IntPtr.Zero) CFRelease(attrs);
                CFRelease(addAttributes);
                CFRelease(data);
                CFRelease(query);
            }
            if (await LoadAsync(accountId, ct).ConfigureAwait(false) != tokens)
                throw new CryptographicException("Keychain credential round trip did not match.");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or CryptographicException or UnauthorizedAccessException)
        {
            throw new SecureStorageUnavailableException("macOS Keychain is unavailable.", e);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task DeleteAsync(string accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var query = CreateDictionary(("class", "genp"), ("service", "WheelWizard.DropboxOAuth"), ("account", RequiredAccount(accountId)));
            try
            {
                var status = SecItemDelete(query);
                if (status is not (ErrSecSuccess or ErrSecItemNotFound))
                    throw new CryptographicException($"Keychain returned status {status}.");
            }
            finally { CFRelease(query); }
            return Task.CompletedTask;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or CryptographicException)
        {
            throw new SecureStorageUnavailableException("macOS Keychain is unavailable.", e);
        }
    }

    private static string RequiredAccount(string accountId) => string.IsNullOrWhiteSpace(accountId) ? throw new ArgumentException("Dropbox account ID is required.", nameof(accountId)) : accountId;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RetainCallback(IntPtr allocator, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseCallback(IntPtr allocator, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DescriptionCallback(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool EqualCallback(IntPtr left, IntPtr right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint HashCallback(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct DictionaryCallbacks
    {
        public nint Version;
        public IntPtr Retain;
        public IntPtr Release;
        public IntPtr CopyDescription;
        public IntPtr Equal;
        public IntPtr Hash;
    }

    private static readonly RetainCallback Retain = (_, value) => CFRetain(value);
    private static readonly ReleaseCallback Release = (_, value) => CFRelease(value);
    private static readonly DescriptionCallback Describe = _ => IntPtr.Zero;
    private static readonly EqualCallback Equal = CFEqual;
    private static readonly HashCallback Hash = CFHash;

    private static IntPtr CreateDictionary(params (string Key, object Value)[] values)
    {
        var keys = new IntPtr[values.Length];
        var refs = new List<IntPtr>();
        var mappedValues = new IntPtr[values.Length];
        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                keys[i] = MakeString(KeyName(values[i].Key));
                mappedValues[i] = values[i].Value switch
                {
                    string text => MakeString(text),
                    bool boolean => MakeNumber(boolean ? 1 : 0),
                    IntPtr pointer => pointer,
                    _ => throw new ArgumentOutOfRangeException(nameof(values)),
                };
                refs.Add(keys[i]);
                if (values[i].Value is not IntPtr) refs.Add(mappedValues[i]);
            }
            var callbacks = new DictionaryCallbacks
            {
                Version = 0,
                Retain = Marshal.GetFunctionPointerForDelegate(Retain),
                Release = Marshal.GetFunctionPointerForDelegate(Release),
                CopyDescription = Marshal.GetFunctionPointerForDelegate(Describe),
                Equal = Marshal.GetFunctionPointerForDelegate(Equal),
                Hash = Marshal.GetFunctionPointerForDelegate(Hash),
            };
            var callbacksPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DictionaryCallbacks>());
            try
            {
                Marshal.StructureToPtr(callbacks, callbacksPtr, false);
                var dictionary = CFDictionaryCreate(IntPtr.Zero, keys, mappedValues, values.Length, callbacksPtr, callbacksPtr);
                if (dictionary == IntPtr.Zero) throw new CryptographicException("Keychain query allocation failed.");
                return dictionary;
            }
            finally { Marshal.FreeHGlobal(callbacksPtr); }
        }
        finally { foreach (var reference in refs) CFRelease(reference); }
    }

    private static string KeyName(string name) => name switch
    {
        "class" => "class", "service" => "svce", "account" => "acct", "valueData" => "v_Data",
        "returnData" => "r_Data", "matchLimit" => "m_Limit", _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static IntPtr MakeString(string value)
    {
        var result = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
        if (result == IntPtr.Zero) throw new CryptographicException("Keychain string allocation failed.");
        return result;
    }

    private static IntPtr MakeNumber(int value)
    {
        var ptr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(ptr, value);
        try
        {
            var number = CFNumberCreate(IntPtr.Zero, 3, ptr);
            if (number == IntPtr.Zero) throw new CryptographicException("Keychain number allocation failed.");
            return number;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFNumberCreate(IntPtr allocator, int numberType, IntPtr value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFRetain(IntPtr value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFEqual(IntPtr left, IntPtr right);
    [DllImport(CoreFoundation)] private static extern nuint CFHash(IntPtr value);
}
