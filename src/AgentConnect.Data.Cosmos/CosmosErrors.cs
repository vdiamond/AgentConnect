using System.Net;
using Microsoft.Azure.Cosmos;

namespace AgentConnect;

/// <summary>
/// Maps Cosmos DB failures onto the FR-42 storage errors.
/// </summary>
public static class CosmosErrors
{
    /// <summary>SubStatusCodes.PartitionKeyQuotaOverLimit. The logical partition is over 20 GB.</summary>
    public const int PartitionKeyQuotaOverLimit = 1014;

    /// <summary>A user-facing partition limit error observed from the service.</summary>
    public const int PartitionLimitExceededUserError = 3511;

    /// <summary>
    /// Maps a <see cref="CosmosException"/> thrown by an item write.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>A storage exception. The Cosmos exception text is not returned to callers.</returns>
    public static Exception MapWrite(CosmosException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.StatusCode == HttpStatusCode.Conflict)
        {
            return new DuplicateValueException();
        }

        if (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return Throttle(exception);
        }

        if (exception.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.ServiceUnavailable)
        {
            return new StorageUnavailableException(exception.RetryAfter ?? TimeSpan.FromSeconds(1));
        }

        if (exception.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            return new StorageTooLargeException();
        }

        if (exception.SubStatusCode is PartitionKeyQuotaOverLimit or PartitionLimitExceededUserError)
        {
            return new StorageLimitException();
        }

        return new StorageFailureException((int)exception.StatusCode, exception.SubStatusCode, exception.ActivityId);
    }

    #region private

    private static StorageThrottleException Throttle(CosmosException exception)
    {
        return new StorageThrottleException(exception.RetryAfter ?? TimeSpan.FromSeconds(1));
    }

    #endregion
}

/// <summary>
/// The composite index the Items container needs for the default order in FR-41.
/// This description is not applied by the application.
/// </summary>
public static class CosmosModel
{
    /// <summary>The Items composite index: <c>updated</c> ascending, then <c>id</c> ascending.</summary>
    public const string ItemCompositeIndex = """
        {
          "indexingMode": "consistent",
          "automatic": true,
          "includedPaths": [{ "path": "/*" }],
          "excludedPaths": [{ "path": "/\"_etag\"/?" }],
          "compositeIndexes": [
            [
              { "path": "/updated", "order": "ascending" },
              { "path": "/id", "order": "ascending" }
            ]
          ]
        }
        """;
}
