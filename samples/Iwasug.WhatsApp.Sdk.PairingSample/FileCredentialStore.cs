using Iwasug.WhatsApp.Sdk.Accounts;
using Iwasug.WhatsApp.Sdk.Domain;
using Iwasug.WhatsApp.Sdk.Errors;
using Iwasug.WhatsApp.Sdk.Stores;

namespace Iwasug.WhatsApp.Sdk.Samples;

/// <summary>
/// Directory-backed <see cref="ICredentialStore"/> implementation — one file per account,
/// atomic write (write temp + move, never a torn read), deserialization failures throw
/// <see cref="Errors.SdkStoreException"/> rather than returning null (failure != missing,
/// D-19 contract rule in XML docs), Remove idempotent.
/// </summary>
/// <remarks>
/// <para>
/// Structural per-account namespacing (D-02): the instance IS the namespace. Obtain
/// instances from <see cref="ICredentialStoreFactory"/>; instances are long-lived per
/// account (D-24) and owned by the SDK (D-23).
/// </para>
/// <para>
/// Durability (D-06): the write completes only when an independent subsequent read
/// observes the value; the SDK awaits it before continuing. Failure ≠ missing (D-19):
/// a missing row returns <c>null</c>; failures throw. Durability class (D-05): must-not-lose.
/// Removing an absent row is idempotent and returns <c>false</c>.
/// </para>
/// </remarks>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _storePath;
    private readonly string _accountId;

    /// <summary>
    /// Creates a new <see cref="FileCredentialStore"/> for the specified account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="storePath">The directory path where credential files are stored.</param>
    public FileCredentialStore(AccountId accountId, string storePath)
    {
        _accountId = accountId.Value;
        _storePath = storePath;

        // Ensure the store directory exists
        System.IO.Directory.CreateDirectory(_storePath);
    }

    /// <summary>
    /// Returns the account's credential row, or <c>null</c> when none has been stored
    /// (missing ≠ failure, D-19).
    /// </summary>
    public async Task<CredentialRecord?> GetAsync(CancellationToken cancellationToken)
    {
        var filePath = GetFilePath();
        if (!System.IO.File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = await System.IO.File.ReadAllTextAsync(filePath, cancellationToken);
            return System.Text.Json.JsonSerializer.Deserialize<CredentialRecord>(json);
        }
        catch
        {
            // Deserialization failure =/= missing row (D-19): throw, don't return null
            throw new SdkStoreException($"Failed to deserialize credential row for account {_accountId}");
        }
    }

    /// <summary>
    /// Stores <paramref name="credentials"/> as the account's credential row, overwriting
    /// any existing row (last-write-wins). Completes only once an independent subsequent
    /// read observes the value (D-06 store-side durability contract).
    /// </summary>
    public async Task PutAsync(CredentialRecord credentials, CancellationToken cancellationToken)
    {
        var filePath = GetFilePath();
        var tempPath = filePath + ".tmp." + Guid.NewGuid();

        try
        {
            // Atomic write: write temp file first, then move to final location
            var serializable = new
            {
                credentials.CreatedAt,
                credentials.NoiseKeyPrivate,
                credentials.Device,
                credentials.PairedUser,
                credentials.RegistrationId
            };

            var json = System.Text.Json.JsonSerializer.Serialize(serializable);
            await System.IO.File.WriteAllTextAsync(tempPath, json, cancellationToken);
            System.IO.File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // Clean up temp file on failure
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Removes the account's credential row.
    /// </summary>
    /// <returns><c>true</c> when a row was removed; <c>false</c> when none existed
    /// (removing an absent row is idempotent).</returns>
    public Task<bool> RemoveAsync(CancellationToken cancellationToken)
    {
        var filePath = GetFilePath();
        if (!System.IO.File.Exists(filePath))
            return Task.FromResult(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.File.Delete(filePath);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// The account's credential file path: one JSON file per account inside the store
    /// directory (structural namespacing, D-02).
    /// </summary>
    private string GetFilePath() =>
        System.IO.Path.Combine(_storePath, $"{_accountId}.json");

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or
    /// resetting unmanaged resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // FileCredentialStore doesn't hold unmanaged resources beyond the file path;
        // no-op dispose. Clients should use RemoveAsync to delete credential data.
    }
}