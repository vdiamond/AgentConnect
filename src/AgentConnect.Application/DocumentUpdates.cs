namespace AgentConnect;

/// <summary>
/// Re-reads a document and applies one mutation until the etag matches.
/// A stale snapshot is never written back over a newer token, role, or schema version.
/// </summary>
public static class DocumentUpdates
{
    /// <summary>
    /// Applies <paramref name="mutate"/> to the current document and replaces it.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="read">Reads the current document.</param>
    /// <param name="replace">Replaces it with <c>If-Match</c>.</param>
    /// <param name="mutate">The change to apply to the fresh document.</param>
    /// <param name="retries">How many times a stale write is tried again before <c>$: conflict</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The document that was stored.</returns>
    public static async Task<T> UpdateAsync<T>(
        Func<CancellationToken, Task<T?>> read,
        Func<T, CancellationToken, Task> replace,
        Action<T> mutate,
        int retries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(mutate);
        if (retries < 0)
        {
            retries = 0;
        }

        for (var attempt = 0; ; attempt++)
        {
            var current = await read(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                throw new DocumentMissingException();
            }

            mutate(current);
            try
            {
                await replace(current, cancellationToken).ConfigureAwait(false);
                return current;
            }
            catch (StorageConflictException) when (attempt < retries)
            {
            }
        }
    }

    /// <summary>
    /// Reads a document and treats a soft delete as gone. FR-51.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="read">Reads the current document.</param>
    /// <param name="deletedAt">Reads <c>deletedAt</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live document, or null when it is missing or deleted.</returns>
    public static async Task<T?> LiveAsync<T>(
        Func<CancellationToken, Task<T?>> read,
        Func<T, DateTimeOffset?> deletedAt,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(deletedAt);
        var document = await read(cancellationToken).ConfigureAwait(false);
        if (document is null || deletedAt(document) is not null)
        {
            return null;
        }

        return document;
    }
}

/// <summary>
/// Inserts a document and turns a unique-key conflict into the caller's 409 text.
/// </summary>
public static class Unique
{
    /// <summary>
    /// Creates a document. A concurrent insert of the same email or name returns <paramref name="conflictText"/>.
    /// </summary>
    /// <param name="create">The insert.</param>
    /// <param name="conflictText">The 409 text.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The conflict, or null when the insert succeeded.</returns>
    public static async Task<PlatformError?> CreateAsync(Func<CancellationToken, Task> create, string conflictText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);
        try
        {
            await create(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DuplicateValueException)
        {
            return PlatformError.Conflict(conflictText);
        }
    }
}
