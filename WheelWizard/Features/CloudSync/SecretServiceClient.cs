using System.Runtime.CompilerServices;
using Tmds.DBus.Protocol;

[assembly: InternalsVisibleTo("WheelWizard.Test")]

namespace WheelWizard.Features.CloudSync;

/// <summary>
/// Thin seam over the org.freedesktop.Secret (Secret Service) D-Bus API so store semantics can be unit tested.
/// Implementations must wrap transport and protocol failures as <see cref="SecureStorageUnavailableException"/>.
/// </summary>
internal interface ISecretServiceClient
{
    /// <summary>Returns the value bytes of the first secret matching all attributes, or null when none is found.</summary>
    Task<byte[]?> SearchSecretAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>Creates or replaces the secret matching all attributes with the given value bytes.</summary>
    Task SaveSecretAsync(IReadOnlyDictionary<string, string> attributes, string label, byte[] value, CancellationToken ct);

    /// <summary>Deletes every secret matching all attributes.</summary>
    Task DeleteSecretsAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct);
}

/// <summary>
/// Secret Service client (org.freedesktop.secrets) over the session D-Bus using Tmds.DBus.Protocol.
/// One connection and OpenSession are cached per instance; register instances as singletons.
/// Every transport or protocol failure is wrapped as <see cref="SecureStorageUnavailableException"/> so callers fail closed.
/// </summary>
internal sealed class TmdsSecretServiceClient : ISecretServiceClient
{
    private const string SecretsBusName = "org.freedesktop.secrets";
    private const string ServiceObjectPath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    private const string SecretContentType = "application/octet-stream";
    private const string PlainSessionAlgorithm = "plain";
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(30);

    // The object path "/" means "no object" throughout the Secret Service API.
    private static readonly ObjectPath NoObjectPath = new("/");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private ObjectPath _sessionPath;

    public async Task<byte[]?> SearchSecretAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var connection = await GetConnectionAsync(ct).ConfigureAwait(false);
            var items = await SearchUnlockedItemsAsync(connection, attributes, unlockLocked: true, ct).ConfigureAwait(false);
            return items.Length == 0 ? null : await GetItemSecretAsync(connection, items[0], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not SecureStorageUnavailableException)
        {
            throw new SecureStorageUnavailableException("Linux Secret Service is unavailable.", e);
        }
    }

    public async Task SaveSecretAsync(IReadOnlyDictionary<string, string> attributes, string label, byte[] value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var connection = await GetConnectionAsync(ct).ConfigureAwait(false);
            var collection = await GetDefaultCollectionAsync(connection, ct).ConfigureAwait(false);
            if (collection == NoObjectPath)
                throw new SecureStorageUnavailableException(
                    "Linux Secret Service is unavailable: no default or login keyring collection exists."
                );
            await UnlockAsync(connection, [collection], ct).ConfigureAwait(false);
            await CreateItemAsync(connection, collection, attributes, label, value, replace: true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not SecureStorageUnavailableException)
        {
            throw new SecureStorageUnavailableException("Linux Secret Service is unavailable.", e);
        }
    }

    public async Task DeleteSecretsAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var connection = await GetConnectionAsync(ct).ConfigureAwait(false);
            var items = await SearchUnlockedItemsAsync(connection, attributes, unlockLocked: true, ct).ConfigureAwait(false);
            foreach (var item in items)
                await CallMethodAsync(connection, CreateMethodCall(connection, item, ItemInterface, "Delete", signature: ""), ct)
                    .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not SecureStorageUnavailableException)
        {
            throw new SecureStorageUnavailableException("Linux Secret Service is unavailable.", e);
        }
    }

    private async Task<DBusConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
            return _connection;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return _connection;
            var address =
                DBusAddress.Session
                ?? throw new SecureStorageUnavailableException(
                    "Linux Secret Service is unavailable: no D-Bus session bus address was found."
                );
            var connection = new DBusConnection(address);
            await connection.ConnectAsync().ConfigureAwait(false);
            var session = await OpenSessionAsync(connection, ct).ConfigureAwait(false);
            _sessionPath = session; // written before the connection is published
            _connection = connection;
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private delegate void MessageBodyWriter(ref MessageWriter writer);

    /// <summary>Builds a method call message synchronously; the MessageWriter struct never crosses an await.</summary>
    private static MessageBuffer CreateMethodCall(
        DBusConnection connection,
        ObjectPath path,
        string interfaceName,
        string member,
        string signature,
        MessageBodyWriter? writeBody = null
    )
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(SecretsBusName, path, interfaceName, member, signature);
        writeBody?.Invoke(ref writer);
        var message = writer.CreateMessage();
        writer.Dispose();
        return message;
    }

    private static async Task CallMethodAsync(DBusConnection connection, MessageBuffer message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }

    private static async Task<T> CallMethodAsync<T>(
        DBusConnection connection,
        MessageBuffer message,
        MessageValueReader<T> reader,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        return await connection.CallMethodAsync(message, reader, readerState: null).ConfigureAwait(false);
    }

    private static async Task<ObjectPath> OpenSessionAsync(DBusConnection connection, CancellationToken ct)
    {
        var message = CreateMethodCall(
            connection,
            ServiceObjectPath,
            ServiceInterface,
            "OpenSession",
            "sv",
            (ref MessageWriter writer) =>
            {
                writer.WriteString(PlainSessionAlgorithm);
                writer.WriteVariantString(string.Empty);
            }
        );
        return await CallMethodAsync(
                connection,
                message,
                static (reply, _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.AlignStruct();
                    _ = reader.ReadVariantValue(); // output is unused with the plain algorithm
                    return reader.ReadObjectPath();
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private static async Task<ObjectPath> ReadAliasAsync(DBusConnection connection, string alias, CancellationToken ct)
    {
        var message = CreateMethodCall(
            connection,
            ServiceObjectPath,
            ServiceInterface,
            "ReadAlias",
            "s",
            (ref MessageWriter writer) => writer.WriteString(alias)
        );
        return await CallMethodAsync(connection, message, static (reply, _) => reply.GetBodyReader().ReadObjectPath(), ct)
            .ConfigureAwait(false);
    }

    private static async Task<ObjectPath> GetDefaultCollectionAsync(DBusConnection connection, CancellationToken ct)
    {
        foreach (var alias in new[] { "default", "login" })
        {
            var collection = await ReadAliasAsync(connection, alias, ct).ConfigureAwait(false);
            if (collection != NoObjectPath)
                return collection;
        }
        return NoObjectPath;
    }

    private async Task UnlockAsync(DBusConnection connection, ObjectPath[] paths, CancellationToken ct)
    {
        var message = CreateMethodCall(connection, ServiceObjectPath, ServiceInterface, "Unlock", "ao", (ref MessageWriter writer) => writer.WriteArray(paths));
        var prompt = await CallMethodAsync(
                connection,
                message,
                static (reply, _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.AlignStruct();
                    _ = reader.ReadArrayOfObjectPath(); // unlocked paths, not needed here
                    return reader.ReadObjectPath();
                },
                ct
            )
            .ConfigureAwait(false);
        if (prompt != NoObjectPath)
            await PromptAndWaitAsync(connection, prompt, ct).ConfigureAwait(false);
    }

    private static async Task<(ObjectPath[] Unlocked, ObjectPath[] Locked)> SearchItemsAsync(
        DBusConnection connection,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken ct
    )
    {
        var message = CreateMethodCall(
            connection,
            ServiceObjectPath,
            ServiceInterface,
            "SearchItems",
            "a{ss}",
            (ref MessageWriter writer) =>
            {
                var dictionaryStart = writer.WriteDictionaryStart();
                foreach (var attribute in attributes)
                {
                    writer.WriteDictionaryEntryStart();
                    writer.WriteString(attribute.Key);
                    writer.WriteString(attribute.Value);
                }
                writer.WriteDictionaryEnd(dictionaryStart);
            }
        );
        return await CallMethodAsync(
                connection,
                message,
                static (reply, _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.AlignStruct();
                    var unlocked = reader.ReadArrayOfObjectPath();
                    var locked = reader.ReadArrayOfObjectPath();
                    return (unlocked, locked);
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task<ObjectPath[]> SearchUnlockedItemsAsync(
        DBusConnection connection,
        IReadOnlyDictionary<string, string> attributes,
        bool unlockLocked,
        CancellationToken ct
    )
    {
        var (unlocked, locked) = await SearchItemsAsync(connection, attributes, ct).ConfigureAwait(false);
        if (unlocked.Length > 0 || locked.Length == 0 || !unlockLocked)
            return unlocked;
        await UnlockAsync(connection, locked, ct).ConfigureAwait(false);
        (unlocked, _) = await SearchItemsAsync(connection, attributes, ct).ConfigureAwait(false);
        return unlocked;
    }

    private async Task<byte[]> GetItemSecretAsync(DBusConnection connection, ObjectPath item, CancellationToken ct)
    {
        var message = CreateMethodCall(connection, item, ItemInterface, "GetSecret", "o", (ref MessageWriter writer) => writer.WriteObjectPath(_sessionPath));
        return await CallMethodAsync(
                connection,
                message,
                static (reply, _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.AlignStruct();
                    _ = reader.ReadObjectPath(); // session the secret is tied to (plain: no encryption)
                    _ = reader.ReadArrayOfByte(); // parameters, empty with the plain algorithm
                    var value = reader.ReadArrayOfByte();
                    _ = reader.ReadString(); // content type
                    return value;
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task CreateItemAsync(
        DBusConnection connection,
        ObjectPath collection,
        IReadOnlyDictionary<string, string> attributes,
        string label,
        byte[] value,
        bool replace,
        CancellationToken ct
    )
    {
        var message = CreateMethodCall(
            connection,
            collection,
            CollectionInterface,
            "CreateItem",
            "a{sv}(oayays)b",
            (ref MessageWriter writer) =>
            {
                writer.WriteDictionary(
                    new KeyValuePair<string, VariantValue>[]
                    {
                        new("org.freedesktop.Secret.Item.Label", VariantValue.String(label)),
                        new("org.freedesktop.Secret.Item.Attributes", new Dict<string, string>(new Dictionary<string, string>(attributes))),
                    }
                );
                writer.WriteStructureStart();
                writer.WriteObjectPath(_sessionPath);
                writer.WriteArray(Array.Empty<byte>()); // parameters, empty with the plain algorithm
                writer.WriteArray(value);
                writer.WriteString(SecretContentType);
                writer.WriteBool(replace);
            }
        );
        var prompt = await CallMethodAsync(
                connection,
                message,
                static (reply, _) =>
                {
                    var reader = reply.GetBodyReader();
                    reader.AlignStruct();
                    _ = reader.ReadObjectPath(); // created item path, not needed here
                    return reader.ReadObjectPath();
                },
                ct
            )
            .ConfigureAwait(false);
        if (prompt != NoObjectPath)
            await PromptAndWaitAsync(connection, prompt, ct).ConfigureAwait(false);
    }

    private async Task PromptAndWaitAsync(DBusConnection connection, ObjectPath prompt, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = await connection
            .WatchSignalAsync(
                sender: SecretsBusName,
                path: prompt,
                @interface: PromptInterface,
                signal: "Completed",
                reader: static (message, _) => message.GetBodyReader().ReadBool(), // dismissed flag
                handler: notification =>
                {
                    if (notification.IsCompletion)
                        completed.TrySetException(
                            notification.Exception
                                ?? new DBusConnectionClosedException("The Secret Service signal watch ended before the prompt completed.")
                        );
                    else
                        completed.TrySetResult(notification.Value);
                },
                emitOnCapturedContext: false,
                flags: ObserverFlags.None,
                state: null
            )
            .ConfigureAwait(false);

        await CallMethodAsync(
                connection,
                CreateMethodCall(connection, prompt, PromptInterface, "Prompt", "s", (ref MessageWriter writer) => writer.WriteString(string.Empty)),
                ct
            )
            .ConfigureAwait(false);

        var dismissed = await completed.Task.WaitAsync(PromptTimeout, ct).ConfigureAwait(false);
        if (dismissed)
            throw new SecureStorageUnavailableException("The Linux Secret Service unlock prompt was dismissed.");
    }
}
