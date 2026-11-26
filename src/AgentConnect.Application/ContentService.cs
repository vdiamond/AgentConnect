using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// Lists, schemas, and items.
/// </summary>
public sealed class ContentService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContentService"/> class.
    /// </summary>
    /// <param name="workspaces">The workspaces.</param>
    /// <param name="lists">The lists.</param>
    /// <param name="items">The items.</param>
    /// <param name="schemas">The schema gateway.</param>
    /// <param name="audit">The audit recorder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="options">The platform options.</param>
    public ContentService(
        IWorkspaceRepository workspaces,
        IListRepository lists,
        IItemRepository items,
        SchemaGateway schemas,
        IAuditRecorder audit,
        IClock clock,
        AgentConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _workspaces = workspaces;
        _lists = lists;
        _items = items;
        _schemas = schemas;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    /// <summary>Creates a list. FR-22.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="name">The raw name.</param>
    /// <param name="description">The raw description.</param>
    /// <param name="descriptionSpecified">Whether a description was sent.</param>
    /// <param name="schemaJson">The schema JSON.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The list.</returns>
    public async Task<OperationResult<ListView>> CreateListAsync(Caller caller, string workspaceId, string? name, string? description, bool descriptionSpecified, string? schemaJson, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, null, PlatformAction.CreateList, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ListView>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ListView>.Fail(blocked);
        }

        if (name is null)
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest("name: required"));
        }

        if (!NameRules.TryNormalizeName(name, out var listName, out var nameError))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest(nameError));
        }

        string? listDescription = null;
        if (descriptionSpecified && !NameRules.TryNormalizeDescription(description, out listDescription, out var descriptionError))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest(descriptionError));
        }

        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest("schema: required"));
        }

        if (!_schemas.TryValidateSchema(schemaJson, out var schemaError))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest(schemaError));
        }

        if (await _lists.NameExistsAsync(caller.Tenant.Id, workspaceId, listName, null, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ListView>.Fail(PlatformError.Conflict("name: already exists"));
        }

        var now = _clock.UtcNow;
        var list = new ListDocument
        {
            Id = PlatformSupport.NewId(),
            TenantId = caller.Tenant.Id,
            WorkspaceId = workspaceId,
            Name = listName,
            Description = listDescription,
            Schema = schemaJson,
            SchemaVersion = 1,
            Created = now,
            Updated = now,
        };
        var duplicate = await Unique.CreateAsync(ct => _lists.CreateAsync(list, ct), "name: already exists", cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return OperationResult<ListView>.Fail(duplicate);
        }
        Record(caller.Member.Id, AuditActions.ListCreated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("listId", list.Id));
        return OperationResult<ListView>.Ok(ToView(list));
    }

    /// <summary>Updates a list name or description. FR-40.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="name">The name field.</param>
    /// <param name="description">The description field.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The list.</returns>
    public async Task<OperationResult<ListView>> UpdateListAsync(Caller caller, string workspaceId, string? listId, OptionalText name, OptionalText description, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.UpdateList, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ListView>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ListView>.Fail(blocked);
        }

        if (listId is null)
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest("listId: required"));
        }

        var list = gate.Value!;
        var nextName = list.Name;
        if (name.Specified && !NameRules.TryNormalizeName(name.Value, out nextName, out var nameError))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest(nameError));
        }

        var nextDescription = list.Description;
        if (description.Specified && !NameRules.TryNormalizeDescription(description.Value, out nextDescription, out var descriptionError))
        {
            return OperationResult<ListView>.Fail(PlatformError.BadRequest(descriptionError));
        }

        if (!NameRules.SameName(nextName, list.Name)
            && await _lists.NameExistsAsync(caller.Tenant.Id, workspaceId, nextName, list.Id, cancellationToken).ConfigureAwait(false))
        {
            return OperationResult<ListView>.Fail(PlatformError.Conflict("name: already exists"));
        }

        if (nextName != list.Name || nextDescription != list.Description)
        {
            try
            {
                list = await DocumentUpdates.UpdateAsync(
                    ct => DocumentUpdates.LiveAsync(token => _lists.GetAsync(caller.Tenant.Id, workspaceId, listId, token), list => list.DeletedAt, ct),
                    (current, ct) => _lists.ReplaceAsync(current, ct),
                    current =>
                    {
                        current.Name = nextName;
                        current.Description = nextDescription;
                        current.Updated = _clock.UtcNow;
                    },
                    _options.ConcurrencyRetries,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DuplicateValueException)
            {
                return OperationResult<ListView>.Fail(PlatformError.Conflict("name: already exists"));
            }
            Record(caller.Member.Id, AuditActions.ListUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("listId", list.Id));
        }

        return OperationResult<ListView>.Ok(ToView(list));
    }

    /// <summary>Marks a list deleted. FR-23.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> DeleteListAsync(Caller caller, string workspaceId, string? listId, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.DeleteList, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<DeletedResult>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<DeletedResult>.Fail(blocked);
        }

        if (listId is null)
        {
            return OperationResult<DeletedResult>.Fail(PlatformError.BadRequest("listId: required"));
        }

        await DocumentUpdates.UpdateAsync(
            ct => _lists.GetAsync(caller.Tenant.Id, workspaceId, listId, ct),
            (current, ct) => _lists.ReplaceAsync(current, ct),
            current =>
            {
                current.DeletedAt = _clock.UtcNow;
                current.Updated = current.DeletedAt.Value;
            },
            _options.ConcurrencyRetries,
            cancellationToken).ConfigureAwait(false);
        Record(caller.Member.Id, AuditActions.ListDeleted, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("listId", listId));
        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>Lists lists. FR-24.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="pageSize">The requested page size, when specified.</param>
    /// <param name="pageSizeSpecified">Whether a page size was sent.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <param name="where">The where value, or undefined.</param>
    /// <param name="orderBy">The orderBy value, or undefined.</param>
    /// <param name="unknown">Unknown query keys.</param>
    /// <returns>The page.</returns>
    public async Task<OperationResult<ListPageResult>> ListListsAsync(Caller caller, string workspaceId, int? pageSize, bool pageSizeSpecified, string? continuation, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null, JsonElement where = default, JsonElement orderBy = default, IReadOnlyList<string>? unknown = null)
    {
        var gate = await GateAsync(caller, workspaceId, null, PlatformAction.ListLists, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ListPageResult>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ListPageResult>.Fail(blocked);
        }

        var parsed = CatalogQueryParser.Parse(CatalogKind.List, where, orderBy, unknown);
        if (parsed.Error is not null)
        {
            return OperationResult<ListPageResult>.Fail(parsed.Error);
        }

        var fingerprint = "lists|" + caller.Tenant.Id + "|" + workspaceId + parsed.Value!.Canonical;
        var window = PlatformSupport.OpenPage(caller.Tenant, pageSize, pageSizeSpecified, continuation, fingerprint);
        if (window.Error is not null)
        {
            return OperationResult<ListPageResult>.Fail(window.Error);
        }

        var query = new CompiledCatalogQuery
        {
            Kind = CatalogKind.List,
            TenantId = caller.Tenant.Id,
            WorkspaceId = workspaceId,
            Where = parsed.Value.Where,
            OrderBy = parsed.Value.OrderBy,
            DefaultSort = parsed.Value.DefaultSort,
            PageSize = window.Value!.PageSize,
            StoreCursor = window.Value.Cursor,
        };
        ResultPage<ListDocument> page;
        try
        {
            page = await _lists.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidContinuationException)
        {
            return OperationResult<ListPageResult>.Fail(PlatformError.BadRequest("continuation: invalid token"));
        }

        var views = page.Items.Select(ToView).ToArray();
        return OperationResult<ListPageResult>.Ok(new ListPageResult(
            views,
            PlatformSupport.Seal(fingerprint, window.Value.PageSize, page.Continuation, window.Value.RowStart + views.Length),
            window.Value.RowStart));
    }

    /// <summary>Reads a schema. FR-25.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The schema.</returns>
    public async Task<OperationResult<SchemaView>> GetSchemaAsync(Caller caller, string workspaceId, string? listId, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.GetSchema, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<SchemaView>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<SchemaView>.Fail(blocked);
        }

        using var document = JsonDocument.Parse(gate.Value!.Schema);
        return OperationResult<SchemaView>.Ok(new SchemaView(document.RootElement.Clone(), gate.Value.SchemaVersion));
    }

    /// <summary>Replaces a schema and increments the version. FR-26.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="schemaJson">The schema JSON.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The new version.</returns>
    public async Task<OperationResult<SchemaView>> UpdateSchemaAsync(Caller caller, string workspaceId, string? listId, string? schemaJson, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.UpdateSchema, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<SchemaView>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<SchemaView>.Fail(blocked);
        }

        if (listId is null)
        {
            return OperationResult<SchemaView>.Fail(PlatformError.BadRequest("listId: required"));
        }

        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return OperationResult<SchemaView>.Fail(PlatformError.BadRequest("schema: required"));
        }

        if (!_schemas.TryValidateSchema(schemaJson, out var schemaError))
        {
            return OperationResult<SchemaView>.Fail(PlatformError.BadRequest(schemaError));
        }

        var list = await DocumentUpdates.UpdateAsync(
            ct => DocumentUpdates.LiveAsync(token => _lists.GetAsync(caller.Tenant.Id, workspaceId, listId, token), list => list.DeletedAt, ct),
            (current, ct) => _lists.ReplaceAsync(current, ct),
            current =>
            {
                current.Schema = schemaJson;
                current.SchemaVersion += 1;
                current.Updated = _clock.UtcNow;
            },
            _options.ConcurrencyRetries,
            cancellationToken).ConfigureAwait(false);
        Record(caller.Member.Id, AuditActions.SchemaUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspaceId), ("listId", listId));
        using var document = JsonDocument.Parse(schemaJson);
        return OperationResult<SchemaView>.Ok(new SchemaView(document.RootElement.Clone(), list.SchemaVersion));
    }

    /// <summary>Queries items. FR-29.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="shape">The shape, or null when omitted.</param>
    /// <param name="schemaVersion">The schemaVersion filter, or undefined.</param>
    /// <param name="where">The where value, or undefined.</param>
    /// <param name="orderBy">The orderBy value, or undefined.</param>
    /// <param name="unknownPaths">Unknown keys.</param>
    /// <param name="pageSize">The requested page size, when specified.</param>
    /// <param name="pageSizeSpecified">Whether a page size was sent.</param>
    /// <param name="continuation">The continuation token, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The page.</returns>
    public async Task<OperationResult<ItemPageResult>> QueryItemsAsync(
        Caller caller,
        string workspaceId,
        string? listId,
        string? shape,
        JsonElement schemaVersion,
        JsonElement where,
        JsonElement orderBy,
        IReadOnlyList<string> unknownPaths,
        int? pageSize,
        bool pageSizeSpecified,
        string? continuation,
        CancellationToken cancellationToken,
        Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.QueryItems, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ItemPageResult>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ItemPageResult>.Fail(blocked);
        }

        if (listId is null)
        {
            return OperationResult<ItemPageResult>.Fail(PlatformError.BadRequest("listId: required"));
        }

        var parsed = ItemQueryParser.Parse(SchemaIndex.Load(gate.Value!.Schema), gate.Value.SchemaVersion, caller.Tenant.Id, workspaceId, listId, shape, schemaVersion, where, orderBy, unknownPaths, 1, null);
        if (parsed.Error is not null)
        {
            return OperationResult<ItemPageResult>.Fail(parsed.Error);
        }

        if (pageSizeSpecified && (pageSize is null || pageSize < 1))
        {
            return OperationResult<ItemPageResult>.Fail(PlatformError.BadRequest("pageSize: must be a whole number of at least 1"));
        }

        var window = PlatformSupport.OpenPage(caller.Tenant, pageSize, pageSizeSpecified, continuation, parsed.Value!.Fingerprint);
        if (window.Error is not null)
        {
            return OperationResult<ItemPageResult>.Fail(window.Error);
        }

        var query = parsed.Value with
        {
            PageSize = window.Value!.PageSize,
            StoreCursor = window.Value.Cursor,
        };
        ResultPage<ItemDocument> page;
        try
        {
            page = await _items.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidContinuationException)
        {
            return OperationResult<ItemPageResult>.Fail(PlatformError.BadRequest("continuation: invalid token"));
        }
        catch (StorageThrottleException exception)
        {
            return OperationResult<ItemPageResult>.Fail(PlatformError.TooManyRequests(exception.RetryAfter));
        }

        var includeData = query.Shape == "full";
        var views = page.Items.Select(item => new ItemView(item.Id, item.SchemaVersion, includeData ? ParseElement(item.Data) : null)).ToArray();
        return OperationResult<ItemPageResult>.Ok(new ItemPageResult(
            views,
            PlatformSupport.Seal(query.Fingerprint, query.PageSize, page.Continuation, window.Value.RowStart + views.Length),
            window.Value.RowStart));
    }

    /// <summary>Reads one item. FR-28.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The item.</returns>
    public async Task<OperationResult<ItemView>> GetItemAsync(Caller caller, string workspaceId, string? listId, string? itemId, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.GetItem, true, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ItemView>.Fail(gate.Error);
        }

        if (listId is null)
        {
            var deniedList = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.GetItem, null, null, null);
            if (deniedList is not null)
            {
                return OperationResult<ItemView>.Fail(deniedList);
            }

            if (PlatformSupport.AfterGate(afterGate) is { } missingList)
            {
                return OperationResult<ItemView>.Fail(missingList);
            }

            return OperationResult<ItemView>.Fail(PlatformError.BadRequest("listId: required"));
        }

        if (itemId is null)
        {
            var deniedItem = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.GetItem, null, null, null);
            if (deniedItem is not null)
            {
                return OperationResult<ItemView>.Fail(deniedItem);
            }

            if (PlatformSupport.AfterGate(afterGate) is { } missingItem)
            {
                return OperationResult<ItemView>.Fail(missingItem);
            }

            return OperationResult<ItemView>.Fail(PlatformError.BadRequest("itemId: required"));
        }

        var idOk = ItemIdRules.IsValid(itemId);
        var item = idOk
            ? await _items.GetAsync(caller.Tenant.Id, workspaceId, listId, itemId, cancellationToken).ConfigureAwait(false)
            : null;
        if (item is null)
        {
            var deniedEarly = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.GetItem, null, null, null);
            if (!idOk && deniedEarly is not null)
            {
                return OperationResult<ItemView>.Fail(deniedEarly);
            }

            if (!idOk)
            {
                return OperationResult<ItemView>.Fail(PlatformError.BadRequest("itemId: invalid"));
            }

            return OperationResult<ItemView>.Fail(PlatformError.NotFound);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.GetItem, null, null, null);
        if (denied is not null)
        {
            return OperationResult<ItemView>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ItemView>.Fail(blocked);
        }

        return OperationResult<ItemView>.Ok(new ItemView(item.Id, item.SchemaVersion, ParseElement(item.Data)));
    }

    /// <summary>Creates or replaces an item. The schema version is the one used for validation. FR-27.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id, or null to generate one.</param>
    /// <param name="dataJson">The item JSON.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The id and stored version.</returns>
    public async Task<OperationResult<ItemWriteResult>> UpsertItemAsync(Caller caller, string workspaceId, string? listId, string? itemId, string? dataJson, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.UpsertItem, false, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<ItemWriteResult>.Fail(gate.Error);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<ItemWriteResult>.Fail(blocked);
        }

        if (listId is null)
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.BadRequest("listId: required"));
        }

        var id = itemId ?? PlatformSupport.NewId();
        if (!ItemIdRules.IsValid(id))
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.BadRequest("itemId: invalid"));
        }

        var list = gate.Value!;
        var version = list.SchemaVersion;
        var schema = list.Schema;
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.BadRequest("$: no JSON provided"));
        }

        if (!_schemas.TryValidateData(schema, dataJson, out var dataError))
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.BadRequest(dataError));
        }

        if (!_schemas.TryNormalizeData(schema, dataJson, out var normalized, out var normalizeError))
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.BadRequest(normalizeError));
        }

        var existing = await _items.GetAsync(caller.Tenant.Id, workspaceId, listId, id, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var item = new ItemDocument
        {
            Id = id,
            TenantId = caller.Tenant.Id,
            WorkspaceId = workspaceId,
            ListId = listId,
            Data = ItemDocument.ParseData(normalized),
            SchemaVersion = version,
            Created = existing?.Created ?? now,
            Updated = now,
        };
        if (DocumentLimits.ItemTooLarge(item))
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.ItemTooLarge);
        }

        try
        {
            await _items.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageTooLargeException)
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.ItemTooLarge);
        }
        catch (StorageThrottleException exception)
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.TooManyRequests(exception.RetryAfter));
        }
        catch (StorageLimitException)
        {
            return OperationResult<ItemWriteResult>.Fail(PlatformError.ListStorageLimit);
        }

        return OperationResult<ItemWriteResult>.Ok(new ItemWriteResult(id, version));
    }

    /// <summary>Deletes an item. FR-30.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="afterGate">A check that runs after access is allowed.</param>
    /// <returns>The confirmation.</returns>
    public async Task<OperationResult<DeletedResult>> DeleteItemAsync(Caller caller, string workspaceId, string? listId, string? itemId, CancellationToken cancellationToken, Func<PlatformError?>? afterGate = null)
    {
        var gate = await GateAsync(caller, workspaceId, listId, PlatformAction.DeleteItem, true, cancellationToken).ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return OperationResult<DeletedResult>.Fail(gate.Error);
        }

        if (listId is null)
        {
            var deniedList = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.DeleteItem, null, null, null);
            if (deniedList is not null)
            {
                return OperationResult<DeletedResult>.Fail(deniedList);
            }

            if (PlatformSupport.AfterGate(afterGate) is { } missingList)
            {
                return OperationResult<DeletedResult>.Fail(missingList);
            }

            return OperationResult<DeletedResult>.Fail(PlatformError.BadRequest("listId: required"));
        }

        if (itemId is null)
        {
            var deniedItem = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.DeleteItem, null, null, null);
            if (deniedItem is not null)
            {
                return OperationResult<DeletedResult>.Fail(deniedItem);
            }

            if (PlatformSupport.AfterGate(afterGate) is { } missingItem)
            {
                return OperationResult<DeletedResult>.Fail(missingItem);
            }

            return OperationResult<DeletedResult>.Fail(PlatformError.BadRequest("itemId: required"));
        }

        var idOk = ItemIdRules.IsValid(itemId);
        var item = idOk
            ? await _items.GetAsync(caller.Tenant.Id, workspaceId, listId, itemId, cancellationToken).ConfigureAwait(false)
            : null;
        if (item is null)
        {
            var deniedEarly = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.DeleteItem, null, null, null);
            if (!idOk && deniedEarly is not null)
            {
                return OperationResult<DeletedResult>.Fail(deniedEarly);
            }

            if (!idOk)
            {
                return OperationResult<DeletedResult>.Fail(PlatformError.BadRequest("itemId: invalid"));
            }

            return OperationResult<DeletedResult>.Fail(PlatformError.NotFound);
        }

        var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, PlatformAction.DeleteItem, null, null, null);
        if (denied is not null)
        {
            return OperationResult<DeletedResult>.Fail(denied);
        }

        if (PlatformSupport.AfterGate(afterGate) is { } blocked)
        {
            return OperationResult<DeletedResult>.Fail(blocked);
        }

        try
        {
            await _items.DeleteAsync(caller.Tenant.Id, workspaceId, listId, itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageThrottleException exception)
        {
            return OperationResult<DeletedResult>.Fail(PlatformError.TooManyRequests(exception.RetryAfter));
        }

        return OperationResult<DeletedResult>.Ok(new DeletedResult(true));
    }

    /// <summary>Checks list or item access before the body is read. FR-42.</summary>
    /// <param name="caller">The caller.</param>
    /// <param name="workspaceId">The workspace id.</param>
    /// <param name="listId">The list id, or null when the action is on the workspace.</param>
    /// <param name="action">The action.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 404 or 403, or null when the body may be read.</returns>
    public async Task<PlatformError?> AccessAsync(Caller caller, string workspaceId, string? listId, PlatformAction action, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(caller, workspaceId, listId, action, false, cancellationToken).ConfigureAwait(false);
        return gate.Error;
    }

    #region private

    private readonly IWorkspaceRepository _workspaces;
    private readonly IListRepository _lists;
    private readonly IItemRepository _items;
    private readonly SchemaGateway _schemas;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly AgentConnectOptions _options;

    private async Task<OperationResult<ListDocument>> GateAsync(Caller caller, string workspaceId, string? listId, PlatformAction action, bool authorizeAfterItem, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || (listId is not null && string.IsNullOrWhiteSpace(listId)))
        {
            return OperationResult<ListDocument>.Fail(PlatformError.NotFound);
        }

        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null || !caller.CanSee(workspaceId))
        {
            return OperationResult<ListDocument>.Fail(PlatformError.NotFound);
        }

        ListDocument? list = null;
        if (listId is not null)
        {
            list = await _lists.GetAsync(caller.Tenant.Id, workspaceId, listId, cancellationToken).ConfigureAwait(false);
            if (list is null || list.DeletedAt is not null)
            {
                return OperationResult<ListDocument>.Fail(PlatformError.NotFound);
            }
        }

        if (!authorizeAfterItem)
        {
            var denied = Permissions.AuthorizeWorkspace(caller, workspaceId, action, null, null, null);
            if (denied is not null)
            {
                return OperationResult<ListDocument>.Fail(denied);
            }
        }

        return OperationResult<ListDocument>.Ok(list ?? new ListDocument());
    }

    private void Record(string actor, string action, params (string Key, string Value)[] targets)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in targets)
        {
            map[key] = value;
        }

        _audit.Record(new AuditEntry(actor, action, _clock.UtcNow, map));
    }

    private static ListView ToView(ListDocument list)
    {
        return new ListView(list.Id, list.Name, list.SchemaVersion, list.Description);
    }

    private static JsonElement ParseElement(JsonElement data)
    {
        return data.ValueKind == JsonValueKind.Undefined ? data : data.Clone();
    }

    #endregion
}

/// <summary>
/// Runs background deletion until nothing marked deleted remains. FR-51.
/// </summary>
public sealed class DeletionService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeletionService"/> class.
    /// </summary>
    /// <param name="store">The deletion store.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger.</param>
    public DeletionService(IDeletionStore store, AgentConnectOptions options, ILogger<DeletionService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Removes every marked document and its children.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunUntilQuietAsync(CancellationToken cancellationToken)
    {
        var batch = _options.DeletionBatchSize < 1 ? 100 : _options.DeletionBatchSize;
        try
        {
            while (await _store.DeleteNextBatchAsync(batch, cancellationToken).ConfigureAwait(false) > 0)
            {
                _logger.LogInformation("Deletion batch completed.");
            }
        }
        catch (StorageFailureException exception)
        {
            PlatformSupport.LogCosmosFault(_logger, exception);
            throw;
        }
    }

    /// <summary>
    /// Removes one batch and returns how many documents were removed.
    /// </summary>
    /// <param name="batchSize">The maximum number of documents.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number removed.</returns>
    public Task<int> DeleteNextBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        return _store.DeleteNextBatchAsync(batchSize, cancellationToken);
    }

    #region private

    private readonly IDeletionStore _store;
    private readonly AgentConnectOptions _options;
    private readonly ILogger<DeletionService> _logger;

    #endregion
}
