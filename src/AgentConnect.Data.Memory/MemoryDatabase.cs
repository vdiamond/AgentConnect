using System.Text.Json;

namespace AgentConnect;

/// <summary>
/// The kind of storage fault to simulate on the next item write.
/// </summary>
public enum ItemWriteFault
{
    /// <summary>No fault.</summary>
    None,

    /// <summary>Cosmos DB throttling.</summary>
    Throttled,

    /// <summary>A request timeout, returned as 503.</summary>
    TimedOut,

    /// <summary>Cosmos DB is unavailable, returned as 503.</summary>
    Unavailable,

    /// <summary>An unexpected storage failure, returned as 500.</summary>
    Unexpected,

    /// <summary>The list partition is full.</summary>
    PartitionFull,

    /// <summary>The document is too large.</summary>
    TooLarge,
}

/// <summary>
/// An in-memory store with the same partition boundaries as section 5.
/// </summary>
public sealed class MemoryDatabase : ITenantRepository, IMemberRepository, IWorkspaceRepository, IListRepository, IItemRepository, IDeletionStore, IBatchStore
{
    /// <summary>Gets or sets a fault thrown by the next item write.</summary>
    public ItemWriteFault WriteFault { get; set; }

    /// <summary>Gets or sets a fault thrown by the next workspace read. Tests use it for the access gate.</summary>
    public ItemWriteFault ReadFault { get; set; }

    /// <summary>Gets or sets the Retry-After value used for throttled and unavailable writes.</summary>
    public TimeSpan ThrottleRetryAfter { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets how many of the next document replacements fail as a stale etag. FR-53.</summary>
    public int ReplaceConflictsRemaining { get; set; }

    /// <summary>Removes a list document so an in-flight update observes the delete. FR-51.</summary>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    public void ForgetList(string tenantId, string workspaceId, string listId)
    {
        lock (_gate)
        {
            _lists.Remove(Key(tenantId, workspaceId, listId));
        }
    }

    /// <summary>
    /// Runs after a list is read and before the clone is returned.
    /// Tests use it to change the stored schema version while a write still holds the version it read.
    /// </summary>
    public Action? AfterListRead { get; set; }

    /// <summary>Runs after a workspace is read and before the clone is returned.</summary>
    public Action? AfterWorkspaceRead { get; set; }

    /// <summary>Runs after a member is read and before the clone is returned.</summary>
    public Action? AfterMemberRead { get; set; }

    /// <summary>Runs after a tenant is read and before the clone is returned.</summary>
    public Action? AfterTenantRead { get; set; }

    /// <inheritdoc />
    public Task<TenantDocument?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_tenants.TryGetValue(id, out var tenant))
            {
                return Task.FromResult<TenantDocument?>(null);
            }

            var clone = Clone(tenant);
            AfterTenantRead?.Invoke();
            return Task.FromResult<TenantDocument?>(clone);
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Create(_tenants, tenant.Id, tenant);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplaceAsync(TenantDocument tenant, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Replace(_tenants, tenant.Id, tenant);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _tenants.Remove(id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MemberDocument?> GetAsync(string tenantId, string memberId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_members.TryGetValue(Key(tenantId, memberId), out var member))
            {
                return Task.FromResult<MemberDocument?>(null);
            }

            var clone = Clone(member);
            AfterMemberRead?.Invoke();
            return Task.FromResult<MemberDocument?>(clone);
        }
    }

    /// <inheritdoc />
    public Task<MemberDocument?> FindByEmailAsync(string tenantId, string email, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var member in _members.Values)
            {
                if (member.TenantId == tenantId && EmailRules.Same(member.Email, email))
                {
                    var clone = Clone(member);
                    AfterMemberRead?.Invoke();
                    return Task.FromResult<MemberDocument?>(clone);
                }
            }
        }

        return Task.FromResult<MemberDocument?>(null);
    }

    /// <inheritdoc />
    public Task CreateAsync(MemberDocument member, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            member.StampKey();
            if (_members.Values.Any(stored => stored.TenantId == member.TenantId && stored.Id != member.Id && stored.EmailLower == member.EmailLower))
            {
                throw new DuplicateValueException();
            }

            Create(_members, Key(member.TenantId, member.Id), member);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplaceAsync(MemberDocument member, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            member.StampKey();
            if (_members.Values.Any(stored => stored.TenantId == member.TenantId && stored.Id != member.Id && stored.EmailLower == member.EmailLower))
            {
                throw new DuplicateValueException();
            }

            Replace(_members, Key(member.TenantId, member.Id), member);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string memberId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _members.Remove(Key(tenantId, memberId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResultPage<MemberDocument>> ListAsync(string tenantId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ordered = _members.Values.Where(member => member.TenantId == tenantId)
                .OrderBy(member => member.Created)
                .ThenBy(member => member.Id, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(Page(ordered, pageSize, cursor, member => (member.Created.UtcTicks, member.Id), Clone));
        }
    }

    /// <inheritdoc />
    public Task<ResultPage<MemberDocument>> ListByWorkspaceAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ordered = _members.Values.Where(member => member.TenantId == tenantId && member.WorkspaceRoles.ContainsKey(workspaceId))
                .OrderBy(member => member.EmailLower, StringComparer.Ordinal)
                .ThenBy(member => member.Id, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(Page(ordered, pageSize, cursor, member => (0, member.EmailLower + "\n" + member.Id), Clone));
        }
    }

    /// <inheritdoc />
    Task<WorkspaceDocument?> IWorkspaceRepository.GetAsync(string tenantId, string workspaceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowReadFault();
            if (!_workspaces.TryGetValue(Key(tenantId, workspaceId), out var workspace))
            {
                return Task.FromResult<WorkspaceDocument?>(null);
            }

            var clone = Clone(workspace);
            AfterWorkspaceRead?.Invoke();
            return Task.FromResult<WorkspaceDocument?>(clone);
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(WorkspaceDocument workspace, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            workspace.StampKey();
            EnsureWorkspaceName(workspace);
            Create(_workspaces, Key(workspace.TenantId, workspace.Id), workspace);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplaceAsync(WorkspaceDocument workspace, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            workspace.StampKey();
            EnsureWorkspaceName(workspace);
            Replace(_workspaces, Key(workspace.TenantId, workspace.Id), workspace);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    Task IWorkspaceRepository.DeleteAsync(string tenantId, string workspaceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _workspaces.Remove(Key(tenantId, workspaceId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResultPage<WorkspaceDocument>> ListVisibleAsync(string tenantId, IReadOnlyCollection<string>? visibleIds, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ordered = _workspaces.Values.Where(workspace => workspace.TenantId == tenantId && workspace.DeletedAt is null && (visibleIds is null || visibleIds.Contains(workspace.Id)))
                .OrderBy(workspace => workspace.Updated)
                .ThenBy(workspace => workspace.Id, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(Page(ordered, pageSize, cursor, workspace => (workspace.Updated.UtcTicks, workspace.Id), Clone));
        }
    }

    /// <inheritdoc />
    public Task<bool> NameExistsAsync(string tenantId, string name, string? exceptId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var exists = _workspaces.Values.Any(workspace => workspace.TenantId == tenantId && workspace.DeletedAt is null && workspace.Id != exceptId && NameRules.SameName(workspace.Name, name));
            return Task.FromResult(exists);
        }
    }

    /// <inheritdoc />
    public Task<ListDocument?> GetAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_lists.TryGetValue(Key(tenantId, workspaceId, listId), out var list))
            {
                return Task.FromResult<ListDocument?>(null);
            }

            var clone = Clone(list);
            AfterListRead?.Invoke();
            return Task.FromResult<ListDocument?>(clone);
        }
    }

    /// <inheritdoc />
    public Task CreateAsync(ListDocument list, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            list.StampKey();
            EnsureListName(list);
            Create(_lists, Key(list.TenantId, list.WorkspaceId, list.Id), list);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplaceAsync(ListDocument list, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            list.StampKey();
            EnsureListName(list);
            Replace(_lists, Key(list.TenantId, list.WorkspaceId, list.Id), list);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string workspaceId, string listId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _lists.Remove(Key(tenantId, workspaceId, listId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResultPage<ListDocument>> ListAsync(string tenantId, string workspaceId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var ordered = _lists.Values.Where(list => list.TenantId == tenantId && list.WorkspaceId == workspaceId && list.DeletedAt is null)
                .OrderBy(list => list.Updated)
                .ThenBy(list => list.Id, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(Page(ordered, pageSize, cursor, list => (list.Updated.UtcTicks, list.Id), Clone));
        }
    }

    /// <inheritdoc />
    public Task<bool> NameExistsAsync(string tenantId, string workspaceId, string name, string? exceptId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var exists = _lists.Values.Any(list => list.TenantId == tenantId && list.WorkspaceId == workspaceId && list.DeletedAt is null && list.Id != exceptId && NameRules.SameName(list.Name, name));
            return Task.FromResult(exists);
        }
    }

    /// <inheritdoc />
    public Task<ItemDocument?> GetAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(Key(tenantId, workspaceId, listId, itemId), out var item) ? Clone(item) : null);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(ItemDocument item, CancellationToken cancellationToken)
    {
        if (!ItemIdRules.IsValid(item.Id))
        {
            throw new ArgumentException("itemId: invalid");
        }

        ThrowFault();
        lock (_gate)
        {
            var key = Key(item.TenantId, item.WorkspaceId, item.ListId, item.Id);
            if (_items.TryGetValue(key, out var stored))
            {
                item.Created = stored.Created;
            }

            item.ETag = Guid.NewGuid().ToString("N");
            _items[key] = Clone(item);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string tenantId, string workspaceId, string listId, string itemId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _items.Remove(Key(tenantId, workspaceId, listId, itemId));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResultPage<ItemDocument>> QueryAsync(CompiledItemQuery query, CancellationToken cancellationToken)
    {
        List<ItemDocument> items;
        lock (_gate)
        {
            items = _items.Values.Where(item => item.TenantId == query.TenantId && item.WorkspaceId == query.WorkspaceId && item.ListId == query.ListId)
                .Select(Clone)
                .ToList();
        }

        return Task.FromResult(ItemQueryEngine.Execute(items, query));
    }

    /// <summary>Inserts an item exactly as supplied, including <c>updated</c>. Used to load fixture F-1.</summary>
    /// <param name="item">The item.</param>
    public void InsertRaw(ItemDocument item)
    {
        lock (_gate)
        {
            _items[Key(item.TenantId, item.WorkspaceId, item.ListId, item.Id)] = Clone(item);
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteNextBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(DeleteBatch(batchSize));
        }
    }

    /// <inheritdoc />
    Task<ResultPage<MemberDocument>> IMemberRepository.QueryAsync(CompiledCatalogQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var rows = _members.Values.Where(member => member.TenantId == query.TenantId).ToList();
            if (query.Kind == CatalogKind.WorkspaceMember)
            {
                rows = rows.Where(member => member.WorkspaceRoles.ContainsKey(query.WorkspaceId ?? "")).ToList();
            }

            var page = CatalogEngine.Execute(rows, query, (member, path) => ReadMember(member, path, query.WorkspaceId));
            page.Items = page.Items.Select(Clone).ToArray();
            return Task.FromResult(page);
        }
    }

    /// <inheritdoc />
    Task<ResultPage<WorkspaceDocument>> IWorkspaceRepository.QueryAsync(CompiledCatalogQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (query.Ids is { Count: 0 })
            {
                return Task.FromResult(new ResultPage<WorkspaceDocument>());
            }

            var allowed = query.Ids is null ? null : query.Ids.ToHashSet(StringComparer.Ordinal);
            var rows = _workspaces.Values.Where(workspace => workspace.TenantId == query.TenantId && workspace.DeletedAt is null && (allowed is null || allowed.Contains(workspace.Id))).ToList();
            var page = CatalogEngine.Execute(rows, query, ReadWorkspace);
            page.Items = page.Items.Select(Clone).ToArray();
            return Task.FromResult(page);
        }
    }

    /// <inheritdoc />
    Task<ResultPage<ListDocument>> IListRepository.QueryAsync(CompiledCatalogQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var rows = _lists.Values.Where(list => list.TenantId == query.TenantId && list.WorkspaceId == query.WorkspaceId && list.DeletedAt is null).ToList();
            var page = CatalogEngine.Execute(rows, query, ReadList);
            page.Items = page.Items.Select(Clone).ToArray();
            return Task.FromResult(page);
        }
    }

    /// <inheritdoc />
    public Task CommitAsync(BatchCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            ThrowFault();
            var checksEtag = commit.Container is "member" or "workspace" or "list"
                && commit.Changes.Any(change => change.Action is "replace" or "delete");
            if (checksEtag && ReplaceConflictsRemaining > 0)
            {
                ReplaceConflictsRemaining--;
                throw new StorageConflictException();
            }

            switch (commit.Container)
            {
                case "member":
                {
                    CommitMembers(commit.Changes);
                    break;
                }
                case "workspace":
                {
                    CommitWorkspaces(commit.Changes);
                    break;
                }
                case "list":
                {
                    CommitLists(commit.Changes);
                    break;
                }
                default:
                {
                    CommitItems(commit.Changes);
                    break;
                }
            }
        }

        return Task.CompletedTask;
    }

    #region private

    private readonly object _gate = new();
    private readonly Dictionary<string, TenantDocument> _tenants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberDocument> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceDocument> _workspaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ListDocument> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ItemDocument> _items = new(StringComparer.Ordinal);

    private void ThrowReadFault()
    {
        var fault = ReadFault;
        ReadFault = ItemWriteFault.None;
        Throw(fault);
    }

    private void ThrowFault()
    {
        var fault = WriteFault;
        WriteFault = ItemWriteFault.None;
        Throw(fault);
    }

    private void Throw(ItemWriteFault fault)
    {
        switch (fault)
        {
            case ItemWriteFault.Throttled:
            {
                throw new StorageThrottleException(ThrottleRetryAfter);
            }
            case ItemWriteFault.TimedOut:
            case ItemWriteFault.Unavailable:
            {
                throw new StorageUnavailableException(ThrottleRetryAfter);
            }
            case ItemWriteFault.Unexpected:
            {
                throw new StorageFailureException(500, 0, "activity-9");
            }
            case ItemWriteFault.PartitionFull:
            {
                throw new StorageLimitException();
            }
            case ItemWriteFault.TooLarge:
            {
                throw new StorageTooLargeException();
            }
            default:
            {
                break;
            }
        }
    }

    private int DeleteBatch(int batchSize)
    {
        if (batchSize < 1)
        {
            return 0;
        }

        var removed = RemoveItems(batchSize);
        if (removed > 0)
        {
            return removed;
        }

        removed = RemoveEmptyLists(batchSize);
        if (removed > 0)
        {
            return removed;
        }

        removed = RemoveEmptyWorkspaces(batchSize);
        if (removed > 0)
        {
            return removed;
        }

        removed = RemoveMembers(batchSize);
        if (removed > 0)
        {
            return removed;
        }

        return RemoveTenants(batchSize);
    }

    private int RemoveItems(int batchSize)
    {
        var doomed = _lists.Values.Where(IsListDoomed).Select(list => Key(list.TenantId, list.WorkspaceId, list.Id)).ToHashSet(StringComparer.Ordinal);
        var keys = _items.Where(pair => doomed.Contains(Key(pair.Value.TenantId, pair.Value.WorkspaceId, pair.Value.ListId))).Select(pair => pair.Key).Take(batchSize).ToList();
        foreach (var key in keys)
        {
            _items.Remove(key);
        }

        return keys.Count;
    }

    private int RemoveEmptyLists(int batchSize)
    {
        var keys = _lists.Values.Where(IsListDoomed).Where(list => !_items.Values.Any(item => item.TenantId == list.TenantId && item.WorkspaceId == list.WorkspaceId && item.ListId == list.Id))
            .Take(batchSize)
            .Select(list => Key(list.TenantId, list.WorkspaceId, list.Id))
            .ToList();
        foreach (var key in keys)
        {
            _lists.Remove(key);
        }

        return keys.Count;
    }

    private int RemoveEmptyWorkspaces(int batchSize)
    {
        var workspaces = _workspaces.Values.Where(IsWorkspaceDoomed)
            .Where(workspace => !_lists.Values.Any(list => list.TenantId == workspace.TenantId && list.WorkspaceId == workspace.Id))
            .Take(batchSize)
            .ToList();
        foreach (var workspace in workspaces)
        {
            foreach (var member in _members.Values.Where(member => member.TenantId == workspace.TenantId))
            {
                member.WorkspaceRoles.Remove(workspace.Id);
            }

            _workspaces.Remove(Key(workspace.TenantId, workspace.Id));
        }

        return workspaces.Count;
    }

    private int RemoveMembers(int batchSize)
    {
        var deletedTenants = _tenants.Values.Where(tenant => tenant.DeletedAt is not null).Select(tenant => tenant.Id).ToHashSet(StringComparer.Ordinal);
        var keys = _members.Values.Where(member => deletedTenants.Contains(member.TenantId)).Take(batchSize).Select(member => Key(member.TenantId, member.Id)).ToList();
        foreach (var key in keys)
        {
            _members.Remove(key);
        }

        return keys.Count;
    }

    private int RemoveTenants(int batchSize)
    {
        var keys = _tenants.Values.Where(tenant => tenant.DeletedAt is not null)
            .Where(tenant => !_members.Values.Any(member => member.TenantId == tenant.Id) && !_workspaces.Values.Any(workspace => workspace.TenantId == tenant.Id))
            .Take(batchSize)
            .Select(tenant => tenant.Id)
            .ToList();
        foreach (var key in keys)
        {
            _tenants.Remove(key);
        }

        return keys.Count;
    }

    private bool IsWorkspaceDoomed(WorkspaceDocument workspace)
    {
        return workspace.DeletedAt is not null || (_tenants.TryGetValue(workspace.TenantId, out var tenant) && tenant.DeletedAt is not null);
    }

    private bool IsListDoomed(ListDocument list)
    {
        if (list.DeletedAt is not null)
        {
            return true;
        }

        if (_tenants.TryGetValue(list.TenantId, out var tenant) && tenant.DeletedAt is not null)
        {
            return true;
        }

        return _workspaces.TryGetValue(Key(list.TenantId, list.WorkspaceId), out var workspace) && workspace.DeletedAt is not null;
    }

    private static ResultPage<T> Page<T>(List<T> ordered, int pageSize, string? cursor, Func<T, (long Ticks, string Id)> key, Func<T, T> clone)
    {
        (long Ticks, string Id)? start = null;
        if (cursor is not null)
        {
            if (!TryReadCursor(cursor, out var ticks, out var id))
            {
                throw new InvalidContinuationException();
            }

            start = (ticks, id);
        }

        var page = new List<T>();
        (long Ticks, string Id)? last = null;
        var more = false;
        foreach (var item in ordered)
        {
            var itemKey = key(item);
            if (start is not null && Compare(itemKey, start.Value) <= 0)
            {
                continue;
            }

            if (page.Count == pageSize)
            {
                more = true;
                break;
            }

            page.Add(clone(item));
            last = itemKey;
        }

        return new ResultPage<T>
        {
            Items = page,
            Continuation = more && last is not null ? JsonSerializer.Serialize(new Cursor(last.Value.Ticks, last.Value.Id)) : null,
        };
    }

    private static int Compare((long Ticks, string Id) left, (long Ticks, string Id) right)
    {
        var ticks = left.Ticks.CompareTo(right.Ticks);
        if (ticks != 0)
        {
            return ticks;
        }

        return string.CompareOrdinal(left.Id, right.Id);
    }

    private static bool TryReadCursor(string cursor, out long ticks, out string id)
    {
        ticks = 0;
        id = "";
        try
        {
            var parsed = JsonSerializer.Deserialize<Cursor>(cursor);
            if (parsed is null || parsed.Id is null)
            {
                return false;
            }

            ticks = parsed.Ticks;
            id = parsed.Id;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void EnsureWorkspaceName(WorkspaceDocument workspace)
    {
        if (workspace.DeletedAt is not null)
        {
            return;
        }

        if (_workspaces.Values.Any(stored => stored.TenantId == workspace.TenantId && stored.Id != workspace.Id && stored.DeletedAt is null && stored.NameKey == workspace.NameKey))
        {
            throw new DuplicateValueException();
        }
    }

    private void EnsureListName(ListDocument list)
    {
        if (list.DeletedAt is not null)
        {
            return;
        }

        if (_lists.Values.Any(stored => stored.TenantId == list.TenantId && stored.WorkspaceId == list.WorkspaceId && stored.Id != list.Id && stored.DeletedAt is null && stored.NameKey == list.NameKey))
        {
            throw new DuplicateValueException();
        }
    }

    private static void Create<T>(Dictionary<string, T> store, string key, T incoming)
        where T : IConcurrencyDocument
    {
        incoming.ETag = Guid.NewGuid().ToString("N");
        store[key] = Clone(incoming);
    }

    private void Replace<T>(Dictionary<string, T> store, string key, T incoming)
        where T : IConcurrencyDocument
    {
        if (!store.TryGetValue(key, out var stored))
        {
            throw new DocumentMissingException();
        }

        if (ReplaceConflictsRemaining > 0)
        {
            ReplaceConflictsRemaining--;
            throw new StorageConflictException();
        }

        if (!string.Equals(stored.ETag, incoming.ETag, StringComparison.Ordinal))
        {
            throw new StorageConflictException();
        }

        incoming.ETag = Guid.NewGuid().ToString("N");
        store[key] = Clone(incoming);
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    private void CommitMembers(IReadOnlyList<BatchChange> changes)
    {
        foreach (var change in changes)
        {
            var member = change.Member!;
            var key = Key(member.TenantId, member.Id);
            if (change.Action == "delete")
            {
                if (!_members.ContainsKey(key))
                {
                    throw new DocumentMissingException();
                }

                _members.Remove(key);
                continue;
            }

            member.StampKey();
            if (_members.Values.Any(stored => stored.TenantId == member.TenantId && stored.Id != member.Id && stored.EmailLower == member.EmailLower))
            {
                throw new DuplicateValueException();
            }

            if (change.Action == "replace" && (!_members.TryGetValue(key, out var stored) || !string.Equals(stored.ETag, member.ETag, StringComparison.Ordinal)))
            {
                throw new StorageConflictException();
            }

            member.ETag = Guid.NewGuid().ToString("N");
            _members[key] = Clone(member);
        }
    }

    private void CommitWorkspaces(IReadOnlyList<BatchChange> changes)
    {
        foreach (var change in changes)
        {
            var workspace = change.Workspace!;
            var key = Key(workspace.TenantId, workspace.Id);
            if (change.Action == "delete")
            {
                if (!_workspaces.TryGetValue(key, out var stored) || !string.Equals(stored.ETag, workspace.ETag, StringComparison.Ordinal))
                {
                    throw new StorageConflictException();
                }

                workspace.StampKey();
                workspace.ETag = Guid.NewGuid().ToString("N");
                _workspaces[key] = Clone(workspace);
                continue;
            }

            workspace.StampKey();
            EnsureWorkspaceName(workspace);
            if (change.Action == "replace" && (!_workspaces.TryGetValue(key, out var current) || !string.Equals(current.ETag, workspace.ETag, StringComparison.Ordinal)))
            {
                throw new StorageConflictException();
            }

            workspace.ETag = Guid.NewGuid().ToString("N");
            _workspaces[key] = Clone(workspace);
        }
    }

    private void CommitLists(IReadOnlyList<BatchChange> changes)
    {
        foreach (var change in changes)
        {
            var list = change.List!;
            var key = Key(list.TenantId, list.WorkspaceId, list.Id);
            if (change.Action == "delete")
            {
                if (!_lists.TryGetValue(key, out var stored) || !string.Equals(stored.ETag, list.ETag, StringComparison.Ordinal))
                {
                    throw new StorageConflictException();
                }

                list.StampKey();
                list.ETag = Guid.NewGuid().ToString("N");
                _lists[key] = Clone(list);
                continue;
            }

            list.StampKey();
            EnsureListName(list);
            if (change.Action == "replace" && (!_lists.TryGetValue(key, out var current) || !string.Equals(current.ETag, list.ETag, StringComparison.Ordinal)))
            {
                throw new StorageConflictException();
            }

            list.ETag = Guid.NewGuid().ToString("N");
            _lists[key] = Clone(list);
        }
    }

    private void CommitItems(IReadOnlyList<BatchChange> changes)
    {
        foreach (var change in changes)
        {
            var item = change.Item!;
            var key = Key(item.TenantId, item.WorkspaceId, item.ListId, item.Id);
            if (change.Action == "delete")
            {
                _items.Remove(key);
                continue;
            }

            if (_items.TryGetValue(key, out var stored))
            {
                item.Created = stored.Created;
            }

            item.ETag = Guid.NewGuid().ToString("N");
            _items[key] = Clone(item);
        }
    }

    private static QueryValue? ReadWorkspace(WorkspaceDocument workspace, string path)
    {
        return path switch
        {
            "id" => CatalogEngine.String(workspace.Id),
            "name" => CatalogEngine.String(workspace.Name),
            "description" => workspace.Description is null ? null : CatalogEngine.String(workspace.Description),
            "created" => CatalogEngine.String(CatalogEngine.CanonicalTime(workspace.Created)),
            "updated" => CatalogEngine.String(CatalogEngine.CanonicalTime(workspace.Updated)),
            _ => null,
        };
    }

    private static QueryValue? ReadList(ListDocument list, string path)
    {
        return path switch
        {
            "id" => CatalogEngine.String(list.Id),
            "name" => CatalogEngine.String(list.Name),
            "description" => list.Description is null ? null : CatalogEngine.String(list.Description),
            "schemaVersion" => CatalogEngine.Number(list.SchemaVersion),
            "created" => CatalogEngine.String(CatalogEngine.CanonicalTime(list.Created)),
            "updated" => CatalogEngine.String(CatalogEngine.CanonicalTime(list.Updated)),
            _ => null,
        };
    }

    private static QueryValue? ReadMember(MemberDocument member, string path, string? workspaceId)
    {
        return path switch
        {
            "id" => CatalogEngine.String(member.Id),
            "email" => CatalogEngine.String(member.EmailLower),
            "isTenantOwner" => CatalogEngine.Boolean(member.IsTenantOwner),
            "invitationSent" => CatalogEngine.Boolean(member.InvitationSent),
            "created" => CatalogEngine.String(CatalogEngine.CanonicalTime(member.Created)),
            "role" => workspaceId is not null && member.WorkspaceRoles.TryGetValue(workspaceId, out var role) ? CatalogEngine.String(StoredRoles.ToApi(role)) : null,
            _ => null,
        };
    }

    private static string Key(params string[] parts)
    {
        return string.Join('\u001f', parts);
    }

    private sealed record Cursor(long Ticks, string Id);

    #endregion
}
