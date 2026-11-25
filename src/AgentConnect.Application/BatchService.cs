using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgentConnect;

/// <summary>
/// All-or-nothing batches of one entity type. FR-56. Nothing is written until every operation has been checked.
/// </summary>
public sealed class BatchService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BatchService"/> class.
    /// </summary>
    /// <param name="members">The members.</param>
    /// <param name="workspaces">The workspaces.</param>
    /// <param name="lists">The lists.</param>
    /// <param name="items">The items.</param>
    /// <param name="store">The atomic batch store.</param>
    /// <param name="tokens">The token service.</param>
    /// <param name="schemas">The schema gateway.</param>
    /// <param name="audit">The audit recorder.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="options">The options.</param>
    /// <param name="logger">The logger. It must not receive a token.</param>
    public BatchService(
        IMemberRepository members,
        IWorkspaceRepository workspaces,
        IListRepository lists,
        IItemRepository items,
        IBatchStore store,
        TokenService tokens,
        SchemaGateway schemas,
        IAuditRecorder audit,
        IClock clock,
        AgentConnectOptions options,
        ILogger<BatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _members = members;
        _workspaces = workspaces;
        _lists = lists;
        _items = items;
        _store = store;
        _tokens = tokens;
        _schemas = schemas;
        _audit = audit;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs one batch. A request-shape failure is a single 400. Checked operations return one result each.
    /// </summary>
    /// <param name="caller">The caller.</param>
    /// <param name="operations">The operations array, or undefined.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results, or the request error.</returns>
    public async Task<OperationResult<BatchResult>> RunAsync(Caller caller, JsonElement operations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var parsed = Parse(operations);
        if (parsed.Error is not null)
        {
            return OperationResult<BatchResult>.Fail(parsed.Error);
        }

        var ops = parsed.Value!;
        if (ParentIdError(ops[0]) is { } idError)
        {
            return OperationResult<BatchResult>.Fail(idError);
        }

        var errors = new PlatformError?[ops.Count];
        var prepared = await PrepareAsync(caller, ops, errors, cancellationToken).ConfigureAwait(false);
        if (errors.Any(error => error is not null))
        {
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }

        if (BatchBytes(prepared) > DocumentLimits.MaxItemBytes)
        {
            errors[0] = PlatformError.BatchTooLarge;
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var stale = await ApplyOnFreshAsync(caller, prepared, cancellationToken).ConfigureAwait(false);
                    if (stale is int failed)
                    {
                        errors[failed] = PlatformError.NotFound;
                        return OperationResult<BatchResult>.Ok(Results(errors, prepared));
                    }

                    if (prepared.Changes.Count > 0)
                    {
                        await _store.CommitAsync(new BatchCommit { Container = ops[0].Type, Changes = prepared.Changes }, cancellationToken).ConfigureAwait(false);
                    }

                    WriteAudit(caller, prepared.Audits);
                    return OperationResult<BatchResult>.Ok(Results(errors, prepared));
                }
                catch (StorageConflictException) when (attempt < _options.ConcurrencyRetries)
                {
                }
            }
        }
        catch (StorageConflictException)
        {
            var index = prepared.ConflictIndex;
            errors[index] = PlatformError.Conflict("$: conflict");
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (StorageThrottleException exception)
        {
            errors[0] = PlatformError.TooManyRequests(exception.RetryAfter);
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (StorageUnavailableException exception)
        {
            errors[0] = PlatformError.ServiceUnavailable(exception.RetryAfter);
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (StorageLimitException)
        {
            errors[0] = PlatformError.ListStorageLimit;
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (StorageTooLargeException)
        {
            errors[0] = PlatformError.BatchTooLarge;
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (DuplicateValueException)
        {
            errors[prepared.ConflictIndex] = PlatformError.Conflict("name: already exists");
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (DocumentMissingException)
        {
            errors[prepared.ConflictIndex] = PlatformError.NotFound;
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
        catch (StorageFailureException exception)
        {
            PlatformSupport.LogCosmosFault(_logger, exception);
            errors[0] = PlatformError.InternalError;
            return OperationResult<BatchResult>.Ok(Results(errors, prepared));
        }
    }

    #region private

    private const int MaxOperations = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMemberRepository _members;
    private readonly IWorkspaceRepository _workspaces;
    private readonly IListRepository _lists;
    private readonly IItemRepository _items;
    private readonly IBatchStore _store;
    private readonly TokenService _tokens;
    private readonly SchemaGateway _schemas;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly AgentConnectOptions _options;
    private readonly ILogger _logger;

    private static OperationResult<List<Op>> Parse(JsonElement operations)
    {
        if (operations.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return OperationResult<List<Op>>.Fail(PlatformError.BadRequest("operations: required"));
        }

        if (operations.ValueKind != JsonValueKind.Array)
        {
            return OperationResult<List<Op>>.Fail(PlatformError.BadRequest("operations: expected array, found " + JsonKinds.Of(operations)));
        }

        var count = operations.GetArrayLength();
        if (count < 1 || count > MaxOperations)
        {
            return OperationResult<List<Op>>.Fail(PlatformError.BadRequest("operations: must have 1 to 100 entries"));
        }

        var ops = new List<Op>(count);
        var index = 0;
        foreach (var element in operations.EnumerateArray())
        {
            var parsed = ParseOne(element, index, ops.Count == 0 ? null : ops[0]);
            if (parsed.Error is not null)
            {
                return OperationResult<List<Op>>.Fail(parsed.Error);
            }

            ops.Add(parsed.Value!);
            index++;
        }

        var duplicate = Duplicate(ops);
        if (duplicate is not null)
        {
            return OperationResult<List<Op>>.Fail(duplicate);
        }

        return OperationResult<List<Op>>.Ok(ops);
    }

    private static OperationResult<Op> ParseOne(JsonElement element, int index, Op? first)
    {
        var prefix = "operations[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ": expected Object, found " + JsonKinds.Of(element)));
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value;
        }

        if (!map.TryGetValue("op", out var opElement) || opElement.ValueKind == JsonValueKind.Null)
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ".op: required"));
        }

        if (opElement.ValueKind != JsonValueKind.String || opElement.GetString() is not ("upsert" or "delete" or "get"))
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ".op: must be upsert, delete or get"));
        }

        if (!map.TryGetValue("type", out var typeElement) || typeElement.ValueKind == JsonValueKind.Null)
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ".type: required"));
        }

        if (typeElement.ValueKind != JsonValueKind.String || typeElement.GetString() is not ("member" or "workspace" or "list" or "item"))
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ".type: must be member, workspace, list or item"));
        }

        var op = new Op
        {
            Index = index,
            Action = opElement.GetString()!,
            Type = typeElement.GetString()!,
        };
        if (first is not null && op.Type != first.Type)
        {
            return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + ".type: cannot mix types in one batch"));
        }

        var allowed = Allowed(op);
        foreach (var key in map.Keys)
        {
            if (!allowed.Contains(key))
            {
                return OperationResult<Op>.Fail(PlatformError.BadRequest(prefix + "." + key + ": unknown field"));
            }
        }

        op.Email = OptionalString(map, "email", out var emailError);
        op.WorkspaceId = OptionalString(map, "workspaceId", out var workspaceError);
        op.ListId = OptionalString(map, "listId", out var listError);
        op.ItemId = OptionalString(map, "itemId", out var itemError);
        op.Name = OptionalString(map, "name", out var nameError);
        op.NameSpecified = map.ContainsKey("name") && map["name"].ValueKind != JsonValueKind.Null;
        op.Description = ReadDescription(map);
        op.SchemaJson = RawObject(map, "schema", out var schemaError);
        op.DataJson = RawObject(map, "data", out var dataError);
        op.HasFlag = map.TryGetValue("isTenantOwner", out var flag) && flag.ValueKind != JsonValueKind.Null;
        if (op.HasFlag)
        {
            if (flag.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                op.ValueError = "isTenantOwner: expected Boolean, found " + JsonKinds.Of(flag);
            }
            else
            {
                op.Flag = flag.GetBoolean();
            }
        }

        op.ValueError ??= emailError ?? workspaceError ?? listError ?? itemError ?? nameError ?? schemaError ?? dataError ?? DescriptionError(map);
        if (SameParent(first, op, prefix) is { } parentError)
        {
            return OperationResult<Op>.Fail(parentError);
        }

        if (Missing(op, prefix) is { } missing)
        {
            return OperationResult<Op>.Fail(missing);
        }

        return OperationResult<Op>.Ok(op);
    }

    private static HashSet<string> Allowed(Op op)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { "op", "type" };
        switch (op.Type)
        {
            case "member":
            {
                keys.Add("email");
                if (op.Action == "upsert")
                {
                    keys.Add("isTenantOwner");
                }

                break;
            }
            case "workspace":
            {
                keys.Add("workspaceId");
                if (op.Action == "upsert")
                {
                    keys.Add("name");
                    keys.Add("description");
                }

                break;
            }
            case "list":
            {
                keys.Add("workspaceId");
                keys.Add("listId");
                if (op.Action == "upsert")
                {
                    keys.Add("name");
                    keys.Add("description");
                    keys.Add("schema");
                }

                break;
            }
            default:
            {
                keys.Add("workspaceId");
                keys.Add("listId");
                keys.Add("itemId");
                if (op.Action == "upsert")
                {
                    keys.Add("data");
                }

                break;
            }
        }

        return keys;
    }

    private static PlatformError? SameParent(Op? first, Op op, string prefix)
    {
        if (first is null || op.Type is not ("list" or "item"))
        {
            return null;
        }

        if (op.WorkspaceId is not null && first.WorkspaceId is not null && !string.Equals(op.WorkspaceId, first.WorkspaceId, StringComparison.Ordinal))
        {
            return PlatformError.BadRequest(prefix + ".workspaceId: must target one workspaceId per batch");
        }

        if (op.Type == "item" && op.ListId is not null && first.ListId is not null && !string.Equals(op.ListId, first.ListId, StringComparison.Ordinal))
        {
            return PlatformError.BadRequest(prefix + ".listId: must target one listId per batch");
        }

        return null;
    }

    private static PlatformError? Missing(Op op, string prefix)
    {
        if (op.Type == "member" && op.Email is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".email: required");
        }

        if (op.Type == "workspace" && op.Action != "upsert" && op.WorkspaceId is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".workspaceId: required");
        }

        if (op.Type == "workspace" && op.Action == "upsert" && op.WorkspaceId is not null && op.ValueError is null)
        {
            return null;
        }

        if (op.Type == "workspace" && op.Action == "upsert" && op.WorkspaceId is null && !op.NameSpecified && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".name: required");
        }

        if (op.Type is "list" or "item" && op.WorkspaceId is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".workspaceId: required");
        }

        if (op.Type == "list" && op.Action != "upsert" && op.ListId is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".listId: required");
        }

        if (op.Type == "list" && op.Action == "upsert" && op.ListId is null && op.ValueError is null)
        {
            if (!op.NameSpecified)
            {
                return PlatformError.BadRequest(prefix + ".name: required");
            }

            if (op.SchemaJson is null)
            {
                return PlatformError.BadRequest(prefix + ".schema: required");
            }
        }

        if (op.Type == "item" && op.ListId is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".listId: required");
        }

        if (op.Type == "item" && op.Action != "upsert" && op.ItemId is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".itemId: required");
        }

        if (op.Type == "item" && op.Action == "upsert" && op.DataJson is null && op.ValueError is null)
        {
            return PlatformError.BadRequest(prefix + ".data: required");
        }

        return null;
    }

    private static PlatformError? Duplicate(List<Op> ops)
    {
        var comparer = ops[0].Type == "member" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var gets = new HashSet<string>(comparer);
        var writes = new HashSet<string>(comparer);
        foreach (var op in ops)
        {
            var key = op.Type switch
            {
                "member" => op.Email?.Trim(),
                "workspace" => op.WorkspaceId,
                "list" => op.ListId,
                _ => op.ItemId,
            };
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            // Two gets of one id are one read (T-93). A write combined with any other operation on that id is a duplicate target (T-95).
            var duplicate = op.Action == "get"
                ? writes.Contains(key)
                : !writes.Add(key) || gets.Contains(key);
            if (op.Action == "get")
            {
                gets.Add(key);
            }

            if (duplicate)
            {
                return PlatformError.BadRequest("operations[" + op.Index.ToString(CultureInfo.InvariantCulture) + "]: duplicate target");
            }
        }

        return null;
    }

    private async Task<Prepared> PrepareAsync(Caller caller, List<Op> ops, PlatformError?[] errors, CancellationToken cancellationToken)
    {
        var prepared = new Prepared();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WorkspaceDocument? workspace = null;
        ListDocument? list = null;
        if (ops[0].Type is "list" or "item")
        {
            workspace = await LiveWorkspaceAsync(caller, ops[0].WorkspaceId!, cancellationToken).ConfigureAwait(false);
            if (workspace is null)
            {
                foreach (var op in ops)
                {
                    errors[op.Index] = PlatformError.NotFound;
                }

                return prepared;
            }
        }

        if (ops[0].Type == "item")
        {
            list = await _lists.GetAsync(caller.Tenant.Id, ops[0].WorkspaceId!, ops[0].ListId!, cancellationToken).ConfigureAwait(false);
            if (list is null || list.DeletedAt is not null)
            {
                foreach (var op in ops)
                {
                    errors[op.Index] = PlatformError.NotFound;
                }

                return prepared;
            }
        }

        foreach (var op in ops)
        {
            var error = await CheckAsync(caller, op, workspace, list, names, prepared, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                errors[op.Index] = error;
            }
        }

        return prepared;
    }

    private async Task<PlatformError?> CheckAsync(Caller caller, Op op, WorkspaceDocument? workspace, ListDocument? list, HashSet<string> names, Prepared prepared, CancellationToken cancellationToken)
    {
        if (op.Type == "member" && !caller.IsTenantOwner)
        {
            return PlatformError.NotAllowed;
        }

        if (op.Type == "workspace" && op.Action is "delete" or "upsert" && op.WorkspaceId is null && !caller.IsTenantOwner)
        {
            return PlatformError.NotAllowed;
        }

        if (op.Type == "workspace" && op.Action == "delete" && !caller.IsTenantOwner)
        {
            return PlatformError.NotAllowed;
        }

        if (op.ValueError is not null && op.Type == "member" && caller.IsTenantOwner)
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        return op.Type switch
        {
            "member" => await CheckMemberAsync(caller, op, prepared, cancellationToken).ConfigureAwait(false),
            "workspace" => await CheckWorkspaceAsync(caller, op, names, prepared, cancellationToken).ConfigureAwait(false),
            "list" => await CheckListAsync(caller, op, workspace!, names, prepared, cancellationToken).ConfigureAwait(false),
            _ => await CheckItemAsync(caller, op, workspace!, list!, prepared, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<PlatformError?> CheckMemberAsync(Caller caller, Op op, Prepared prepared, CancellationToken cancellationToken)
    {
        if (!EmailRules.TryNormalize(op.Email, out var email, out var emailError))
        {
            return PlatformError.BadRequest(emailError);
        }

        if (EmailRules.Same(email, caller.Member.Email) && (op.Action == "delete" || (op.Action == "upsert" && op.HasFlag)))
        {
            return PlatformError.NotAllowedOnYourself;
        }

        var existing = await _members.FindByEmailAsync(caller.Tenant.Id, email, cancellationToken).ConfigureAwait(false);
        if (op.Action == "get")
        {
            if (existing is null)
            {
                return PlatformError.NotFound;
            }

            prepared.Bodies[op.Index] = Body(new MemberResult(existing.Id, existing.Email, existing.IsTenantOwner, existing.InvitationSent));
            return null;
        }

        if (op.Action == "delete")
        {
            if (existing is null)
            {
                return PlatformError.NotFound;
            }

            prepared.Changes.Add(new BatchChange { Action = "delete", Member = existing });
            prepared.Intents.Add(new Intent { Index = op.Index, Target = "member", Action = "delete", MemberId = existing.Id });
            prepared.Audits.Add(Audit(caller, AuditActions.MemberDeleted, ("tenantId", caller.Tenant.Id), ("memberId", existing.Id)));
            prepared.Bodies[op.Index] = Body(new DeletedResult(true));
            prepared.NoteWrite(op.Index);
            return null;
        }

        if (existing is null)
        {
            var (member, token) = NewMember(caller.Tenant.Id, email, op.HasFlag && op.Flag);
            prepared.Changes.Add(new BatchChange { Action = "create", Member = member });
            prepared.Audits.Add(Audit(caller, AuditActions.MemberAdded, ("tenantId", caller.Tenant.Id), ("memberId", member.Id)));
            prepared.Bodies[op.Index] = Body(new MemberResult(member.Id, member.Email, member.IsTenantOwner, false, token));
            return null;
        }

        if (op.HasFlag && existing.IsTenantOwner != op.Flag)
        {
            existing.IsTenantOwner = op.Flag;
            prepared.Changes.Add(new BatchChange { Action = "replace", Member = existing });
            prepared.Intents.Add(new Intent { Index = op.Index, Target = "member", Action = "replace", MemberId = existing.Id, Flag = op.Flag });
            prepared.Audits.Add(Audit(caller, AuditActions.TenantOwnerChanged, ("tenantId", caller.Tenant.Id), ("memberId", existing.Id)));
            prepared.NoteWrite(op.Index);
        }

        prepared.Bodies[op.Index] = Body(new MemberResult(existing.Id, existing.Email, op.HasFlag ? op.Flag : existing.IsTenantOwner, existing.InvitationSent));
        return null;
    }

    private async Task<PlatformError?> CheckWorkspaceAsync(Caller caller, Op op, HashSet<string> names, Prepared prepared, CancellationToken cancellationToken)
    {
        if (op.ValueError is not null)
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        if (op.Action == "upsert" && op.WorkspaceId is null)
        {
            if (!NameRules.TryNormalizeName(op.Name, out var name, out var createNameError))
            {
                return PlatformError.BadRequest(createNameError);
            }

            string? description = null;
            if (op.Description.Specified && !NameRules.TryNormalizeDescription(op.Description.Value, out description, out var createDescriptionError))
            {
                return PlatformError.BadRequest(createDescriptionError);
            }

            if (!names.Add(name) || await _workspaces.NameExistsAsync(caller.Tenant.Id, name, null, cancellationToken).ConfigureAwait(false))
            {
                return PlatformError.Conflict("name: already exists");
            }

            var now = _clock.UtcNow;
            var workspace = new WorkspaceDocument
            {
                Id = PlatformSupport.NewId(),
                TenantId = caller.Tenant.Id,
                Name = name,
                Description = description,
                Created = now,
                Updated = now,
            };
            prepared.Changes.Add(new BatchChange { Action = "create", Workspace = workspace });
            prepared.Audits.Add(Audit(caller, AuditActions.WorkspaceCreated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id)));
            prepared.Bodies[op.Index] = Body(new WorkspaceCreatedResult(workspace.Id, workspace.Name, workspace.Description, []));
            return null;
        }

        var stored = await _workspaces.GetAsync(caller.Tenant.Id, op.WorkspaceId!, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.DeletedAt is not null || !caller.CanSee(op.WorkspaceId!))
        {
            return PlatformError.NotFound;
        }

        if (op.Action == "get")
        {
            prepared.Bodies[op.Index] = Body(new WorkspaceView(stored.Id, stored.Name, stored.Description, caller.RoleOn(stored.Id) is string role ? StoredRoles.ToApi(role) : null));
            return null;
        }

        if (op.Action == "delete")
        {
            stored.DeletedAt = _clock.UtcNow;
            stored.Updated = stored.DeletedAt.Value;
            prepared.Changes.Add(new BatchChange { Action = "delete", Workspace = stored });
            prepared.Intents.Add(new Intent { Index = op.Index, Target = "workspace", Action = "delete", WorkspaceId = stored.Id });
            prepared.Audits.Add(Audit(caller, AuditActions.WorkspaceDeleted, ("tenantId", caller.Tenant.Id), ("workspaceId", stored.Id)));
            prepared.Bodies[op.Index] = Body(new DeletedResult(true));
            prepared.NoteWrite(op.Index);
            return null;
        }

        if (Permissions.AuthorizeWorkspace(caller, stored.Id, PlatformAction.UpdateWorkspace, null, null, null) is { } denied)
        {
            return denied;
        }

        var nextName = stored.Name;
        if (op.NameSpecified && !NameRules.TryNormalizeName(op.Name, out nextName, out var nameError))
        {
            return PlatformError.BadRequest(nameError);
        }

        var nextDescription = stored.Description;
        if (op.Description.Specified && !NameRules.TryNormalizeDescription(op.Description.Value, out nextDescription, out var descriptionError))
        {
            return PlatformError.BadRequest(descriptionError);
        }

        if (!string.Equals(nextName, stored.Name, StringComparison.Ordinal) && (!names.Add(nextName) || await _workspaces.NameExistsAsync(caller.Tenant.Id, nextName, stored.Id, cancellationToken).ConfigureAwait(false)))
        {
            return PlatformError.Conflict("name: already exists");
        }

        if (nextName != stored.Name || nextDescription != stored.Description)
        {
            stored.Name = nextName;
            stored.Description = nextDescription;
            stored.Updated = PlatformSupport.After(_clock.UtcNow, stored.Updated);
            prepared.Changes.Add(new BatchChange { Action = "replace", Workspace = stored });
            prepared.Intents.Add(new Intent
            {
                Index = op.Index,
                Target = "workspace",
                Action = "replace",
                WorkspaceId = stored.Id,
                SetName = op.NameSpecified,
                Name = nextName,
                SetDescription = op.Description.Specified,
                Description = nextDescription,
            });
            prepared.Audits.Add(Audit(caller, AuditActions.WorkspaceUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", stored.Id)));
            prepared.NoteWrite(op.Index);
        }

        prepared.Bodies[op.Index] = Body(new WorkspaceUpdatedResult(stored.Id, nextName, nextDescription));
        return null;
    }

    private async Task<PlatformError?> CheckListAsync(Caller caller, Op op, WorkspaceDocument workspace, HashSet<string> names, Prepared prepared, CancellationToken cancellationToken)
    {
        if (op.ValueError is not null)
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        if (op.Action == "upsert" && op.ListId is null)
        {
            if (Permissions.AuthorizeWorkspace(caller, workspace.Id, PlatformAction.CreateList, null, null, null) is { } denied)
            {
                return denied;
            }

            if (!NameRules.TryNormalizeName(op.Name, out var name, out var createNameError))
            {
                return PlatformError.BadRequest(createNameError);
            }

            string? description = null;
            if (op.Description.Specified && !NameRules.TryNormalizeDescription(op.Description.Value, out description, out var createDescriptionError))
            {
                return PlatformError.BadRequest(createDescriptionError);
            }

            if (!_schemas.TryValidateSchema(op.SchemaJson!, out var schemaError))
            {
                return PlatformError.BadRequest(schemaError);
            }

            if (!names.Add(name) || await _lists.NameExistsAsync(caller.Tenant.Id, workspace.Id, name, null, cancellationToken).ConfigureAwait(false))
            {
                return PlatformError.Conflict("name: already exists");
            }

            var now = _clock.UtcNow;
            var created = new ListDocument
            {
                Id = PlatformSupport.NewId(),
                TenantId = caller.Tenant.Id,
                WorkspaceId = workspace.Id,
                Name = name,
                Description = description,
                Schema = op.SchemaJson!,
                SchemaVersion = 1,
                Created = now,
                Updated = now,
            };
            prepared.Changes.Add(new BatchChange { Action = "create", List = created });
            prepared.Audits.Add(Audit(caller, AuditActions.ListCreated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id), ("listId", created.Id)));
            prepared.Bodies[op.Index] = Body(new ListView(created.Id, created.Name, 1, created.Description));
            return null;
        }

        var stored = await _lists.GetAsync(caller.Tenant.Id, workspace.Id, op.ListId!, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.DeletedAt is not null)
        {
            return PlatformError.NotFound;
        }

        if (op.Action == "get")
        {
            if (Permissions.AuthorizeWorkspace(caller, workspace.Id, PlatformAction.ListLists, null, null, null) is { } denied)
            {
                return denied;
            }

            prepared.Bodies[op.Index] = Body(new ListDetail(stored.Id, stored.Name, stored.SchemaVersion, stored.Description, Parse(stored.Schema)));
            return null;
        }

        if (op.Action == "delete")
        {
            if (Permissions.AuthorizeWorkspace(caller, workspace.Id, PlatformAction.DeleteList, null, null, null) is { } denied)
            {
                return denied;
            }

            stored.DeletedAt = _clock.UtcNow;
            stored.Updated = stored.DeletedAt.Value;
            prepared.Changes.Add(new BatchChange { Action = "delete", List = stored });
            prepared.Intents.Add(new Intent { Index = op.Index, Target = "list", Action = "delete", WorkspaceId = workspace.Id, ListId = stored.Id });
            prepared.Audits.Add(Audit(caller, AuditActions.ListDeleted, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id), ("listId", stored.Id)));
            prepared.Bodies[op.Index] = Body(new DeletedResult(true));
            prepared.NoteWrite(op.Index);
            return null;
        }

        if (Permissions.AuthorizeWorkspace(caller, workspace.Id, PlatformAction.UpdateList, null, null, null) is { } updateDenied)
        {
            return updateDenied;
        }

        var changed = false;
        if (op.NameSpecified)
        {
            if (!NameRules.TryNormalizeName(op.Name, out var name, out var nameError))
            {
                return PlatformError.BadRequest(nameError);
            }

            if (!string.Equals(name, stored.Name, StringComparison.Ordinal) && (!names.Add(name) || await _lists.NameExistsAsync(caller.Tenant.Id, workspace.Id, name, stored.Id, cancellationToken).ConfigureAwait(false)))
            {
                return PlatformError.Conflict("name: already exists");
            }

            if (name != stored.Name)
            {
                stored.Name = name;
                changed = true;
            }
        }

        if (op.Description.Specified)
        {
            if (!NameRules.TryNormalizeDescription(op.Description.Value, out var description, out var descriptionError))
            {
                return PlatformError.BadRequest(descriptionError);
            }

            if (description != stored.Description)
            {
                stored.Description = description;
                changed = true;
            }
        }

        if (op.SchemaJson is not null)
        {
            if (Permissions.AuthorizeWorkspace(caller, workspace.Id, PlatformAction.UpdateSchema, null, null, null) is { } schemaDenied)
            {
                return schemaDenied;
            }

            if (!_schemas.TryValidateSchema(op.SchemaJson, out var schemaError))
            {
                return PlatformError.BadRequest(schemaError);
            }

            stored.Schema = op.SchemaJson;
            stored.SchemaVersion++;
            changed = true;
            prepared.Audits.Add(Audit(caller, AuditActions.SchemaUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id), ("listId", stored.Id)));
        }

        if (changed)
        {
            stored.Updated = PlatformSupport.After(_clock.UtcNow, stored.Updated);
            prepared.Changes.Add(new BatchChange { Action = "replace", List = stored });
            prepared.Intents.Add(new Intent
            {
                Index = op.Index,
                Target = "list",
                Action = "replace",
                WorkspaceId = workspace.Id,
                ListId = stored.Id,
                SetName = op.NameSpecified,
                Name = stored.Name,
                SetDescription = op.Description.Specified,
                Description = stored.Description,
                SetSchema = op.SchemaJson is not null,
                Schema = op.SchemaJson,
            });
            if (op.NameSpecified || op.Description.Specified)
            {
                prepared.Audits.Add(Audit(caller, AuditActions.ListUpdated, ("tenantId", caller.Tenant.Id), ("workspaceId", workspace.Id), ("listId", stored.Id)));
            }

            prepared.NoteWrite(op.Index);
        }

        prepared.Bodies[op.Index] = op.SchemaJson is not null && !op.NameSpecified && !op.Description.Specified
            ? Body(new SchemaView(Parse(stored.Schema), stored.SchemaVersion))
            : Body(new ListView(stored.Id, stored.Name, stored.SchemaVersion, stored.Description));
        return null;
    }

    private async Task<PlatformError?> CheckItemAsync(Caller caller, Op op, WorkspaceDocument workspace, ListDocument list, Prepared prepared, CancellationToken cancellationToken)
    {
        if (op.ValueError is not null)
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        var action = op.Action switch
        {
            "get" => PlatformAction.GetItem,
            "delete" => PlatformAction.DeleteItem,
            _ => PlatformAction.UpsertItem,
        };
        if (Permissions.AuthorizeWorkspace(caller, workspace.Id, action, null, null, null) is { } denied)
        {
            return denied;
        }

        if (op.Action != "upsert")
        {
            if (!ItemIdRules.IsValid(op.ItemId))
            {
                return PlatformError.BadRequest("itemId: invalid");
            }

            var existing = await _items.GetAsync(caller.Tenant.Id, workspace.Id, list.Id, op.ItemId!, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return PlatformError.NotFound;
            }

            if (op.Action == "delete")
            {
                prepared.Changes.Add(new BatchChange { Action = "delete", Item = existing });
                prepared.Bodies[op.Index] = Body(new DeletedResult(true));
                return null;
            }

            prepared.Bodies[op.Index] = Body(new ItemView(existing.Id, existing.SchemaVersion, Parse(existing.Data.GetRawText())));
            return null;
        }

        var id = op.ItemId ?? PlatformSupport.NewId();
        if (!ItemIdRules.IsValid(id))
        {
            return PlatformError.BadRequest("itemId: invalid");
        }

        if (!_schemas.TryValidateData(list.Schema, op.DataJson!, out var dataError))
        {
            return PlatformError.BadRequest(dataError);
        }

        if (!_schemas.TryNormalizeData(list.Schema, op.DataJson!, out var normalized, out var normalizeError))
        {
            return PlatformError.BadRequest(normalizeError);
        }

        var current = await _items.GetAsync(caller.Tenant.Id, workspace.Id, list.Id, id, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var item = new ItemDocument
        {
            Id = id,
            TenantId = caller.Tenant.Id,
            WorkspaceId = workspace.Id,
            ListId = list.Id,
            Data = ItemDocument.ParseData(normalized),
            SchemaVersion = list.SchemaVersion,
            Created = current?.Created ?? now,
            Updated = now,
        };
        if (DocumentLimits.ItemTooLarge(item))
        {
            return PlatformError.ItemTooLarge;
        }

        prepared.Changes.Add(new BatchChange { Action = "upsert", Item = item });
        prepared.ItemBytes += DocumentLimits.ItemBytes(item);
        prepared.Bodies[op.Index] = Body(new ItemWriteResult(id, list.SchemaVersion));
        return null;
    }

    /// <summary>
    /// Rebuilds every replace and delete from a new read and applies only that operation's fields.
    /// A concurrent token, role, schema, or delete stays on the fresh document. FR-53.
    /// </summary>
    /// <returns>The operation index when its target is gone, or null when the changes are ready to commit.</returns>
    private async Task<int?> ApplyOnFreshAsync(Caller caller, Prepared prepared, CancellationToken cancellationToken)
    {
        if (prepared.Intents.Count == 0)
        {
            return null;
        }

        var creates = prepared.Changes.Where(change => change.Action == "create").ToList();
        var writes = new List<BatchChange>();
        foreach (var intent in prepared.Intents)
        {
            var missing = await ApplyIntentAsync(caller, intent, writes, prepared, cancellationToken).ConfigureAwait(false);
            if (missing is int index)
            {
                return index;
            }
        }

        prepared.Changes.Clear();
        prepared.Changes.AddRange(creates);
        prepared.Changes.AddRange(writes);
        return null;
    }

    private async Task<int?> ApplyIntentAsync(Caller caller, Intent intent, List<BatchChange> writes, Prepared prepared, CancellationToken cancellationToken)
    {
        switch (intent.Target)
        {
            case "member":
            {
                var member = await _members.GetAsync(caller.Tenant.Id, intent.MemberId!, cancellationToken).ConfigureAwait(false);
                if (member is null)
                {
                    return intent.Index;
                }

                if (intent.Action == "replace")
                {
                    member.IsTenantOwner = intent.Flag;
                    prepared.Bodies[intent.Index] = Body(new MemberResult(member.Id, member.Email, member.IsTenantOwner, member.InvitationSent));
                }

                writes.Add(new BatchChange { Action = intent.Action, Member = member });
                return null;
            }
            case "workspace":
            {
                var workspace = await _workspaces.GetAsync(caller.Tenant.Id, intent.WorkspaceId!, cancellationToken).ConfigureAwait(false);
                if (workspace is null || workspace.DeletedAt is not null)
                {
                    return intent.Index;
                }

                if (intent.Action == "delete")
                {
                    workspace.DeletedAt = _clock.UtcNow;
                    workspace.Updated = workspace.DeletedAt.Value;
                }
                else
                {
                    if (intent.SetName)
                    {
                        workspace.Name = intent.Name ?? workspace.Name;
                    }

                    if (intent.SetDescription)
                    {
                        workspace.Description = intent.Description;
                    }

                    workspace.Updated = PlatformSupport.After(_clock.UtcNow, workspace.Updated);
                    prepared.Bodies[intent.Index] = Body(new WorkspaceUpdatedResult(workspace.Id, workspace.Name, workspace.Description));
                }

                writes.Add(new BatchChange { Action = intent.Action, Workspace = workspace });
                return null;
            }
            default:
            {
                var list = await _lists.GetAsync(caller.Tenant.Id, intent.WorkspaceId!, intent.ListId!, cancellationToken).ConfigureAwait(false);
                if (list is null || list.DeletedAt is not null)
                {
                    return intent.Index;
                }

                if (intent.Action == "delete")
                {
                    list.DeletedAt = _clock.UtcNow;
                    list.Updated = list.DeletedAt.Value;
                }
                else
                {
                    if (intent.SetName)
                    {
                        list.Name = intent.Name ?? list.Name;
                    }

                    if (intent.SetDescription)
                    {
                        list.Description = intent.Description;
                    }

                    if (intent.SetSchema)
                    {
                        list.Schema = intent.Schema ?? list.Schema;
                        list.SchemaVersion++;
                    }

                    list.Updated = PlatformSupport.After(_clock.UtcNow, list.Updated);
                    prepared.Bodies[intent.Index] = intent.SetSchema && !intent.SetName && !intent.SetDescription
                        ? Body(new SchemaView(Parse(list.Schema), list.SchemaVersion))
                        : Body(new ListView(list.Id, list.Name, list.SchemaVersion, list.Description));
                }

                writes.Add(new BatchChange { Action = intent.Action, List = list });
                return null;
            }
        }
    }

    private static PlatformError? ParentIdError(Op op)
    {
        if (op.Type is not ("list" or "item") || op.ValueError is null)
        {
            return null;
        }

        if (op.WorkspaceId is null && op.ValueError.StartsWith("workspaceId:", StringComparison.Ordinal))
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        if (op.Type == "item" && op.ListId is null && op.ValueError.StartsWith("listId:", StringComparison.Ordinal))
        {
            return PlatformError.BadRequest(op.ValueError);
        }

        return null;
    }

    private async Task<WorkspaceDocument?> LiveWorkspaceAsync(Caller caller, string workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaces.GetAsync(caller.Tenant.Id, workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null || workspace.DeletedAt is not null || !caller.CanSee(workspaceId))
        {
            return null;
        }

        return workspace;
    }

    private (MemberDocument Member, string Token) NewMember(string tenantId, string email, bool isTenantOwner)
    {
        var id = PlatformSupport.NewId();
        var issued = _tokens.Issue(tenantId, id);
        var now = _clock.UtcNow;
        return (new MemberDocument
        {
            Id = id,
            TenantId = tenantId,
            Email = email,
            TokenHash = issued.Hash,
            TokenEncrypted = issued.Encrypted,
            IsTenantOwner = isTenantOwner,
            Created = now,
            LastTokenIssued = now,
        }, issued.Token);
    }

    private void WriteAudit(Caller caller, List<AuditEntry> audits)
    {
        foreach (var entry in audits)
        {
            _audit.Record(entry);
        }

        _ = caller;
    }

    private AuditEntry Audit(Caller caller, string action, params (string Key, string Value)[] targets)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in targets)
        {
            map[key] = value;
        }

        return new AuditEntry(caller.Member.Id, action, _clock.UtcNow, map);
    }

    private static BatchResult Results(PlatformError?[] errors, Prepared prepared)
    {
        TimeSpan? retry = null;
        var results = new BatchOpResult[errors.Length];
        for (var index = 0; index < errors.Length; index++)
        {
            var error = errors[index];
            if (error is null && errors.Any(item => item is not null))
            {
                error = PlatformError.NotApplied;
            }

            if (error is null)
            {
                results[index] = new BatchOpResult(index, 200, prepared.Bodies[index], null);
                continue;
            }

            if (error.RetryAfter is TimeSpan after)
            {
                retry = after;
            }

            results[index] = new BatchOpResult(index, error.StatusCode, null, error.Text);
        }

        return new BatchResult(results, retry);
    }

    private static int BatchBytes(Prepared prepared)
    {
        return prepared.ItemBytes;
    }

    private static JsonElement Body<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, Json);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? OptionalString(Dictionary<string, JsonElement> map, string name, out string? error)
    {
        error = null;
        if (!map.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = name == "email"
                ? "email: invalid format"
                : name + ": expected String, found " + JsonKinds.Of(value);
            return null;
        }

        return value.GetString();
    }

    private static string? RawObject(Dictionary<string, JsonElement> map, string name, out string? error)
    {
        error = null;
        if (!map.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.GetRawText();
    }

    private static OptionalText ReadDescription(Dictionary<string, JsonElement> map)
    {
        if (!map.TryGetValue("description", out var value))
        {
            return new OptionalText(false, null);
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return new OptionalText(true, null);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return new OptionalText(false, null);
        }

        return new OptionalText(true, value.GetString());
    }

    private static string? DescriptionError(Dictionary<string, JsonElement> map)
    {
        if (!map.TryGetValue("description", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Undefined)
        {
            return null;
        }

        return "description: expected String, found " + JsonKinds.Of(value);
    }

    private sealed class Op
    {
        public int Index { get; init; }

        public string Action { get; init; } = "";

        public string Type { get; init; } = "";

        public string? Email { get; set; }

        public string? WorkspaceId { get; set; }

        public string? ListId { get; set; }

        public string? ItemId { get; set; }

        public string? Name { get; set; }

        public bool NameSpecified { get; set; }

        public OptionalText Description { get; set; }

        public string? SchemaJson { get; set; }

        public string? DataJson { get; set; }

        public bool HasFlag { get; set; }

        public bool Flag { get; set; }

        public string? ValueError { get; set; }
    }

    private sealed class Intent
    {
        public int Index { get; init; }

        public string Target { get; init; } = "";

        public string Action { get; init; } = "";

        public string? MemberId { get; init; }

        public string? WorkspaceId { get; init; }

        public string? ListId { get; init; }

        public bool Flag { get; init; }

        public bool SetName { get; init; }

        public string? Name { get; init; }

        public bool SetDescription { get; init; }

        public string? Description { get; init; }

        public bool SetSchema { get; init; }

        public string? Schema { get; init; }
    }

    private sealed class Prepared
    {
        public List<BatchChange> Changes { get; } = [];

        public List<Intent> Intents { get; } = [];

        public List<AuditEntry> Audits { get; } = [];

        public Dictionary<int, JsonElement> Bodies { get; } = [];

        public int ItemBytes { get; set; }

        public int ConflictIndex { get; private set; }

        public void NoteWrite(int index)
        {
            if (ConflictIndex == 0 && Changes.Count == 1)
            {
                ConflictIndex = index;
            }
        }
    }

    #endregion
}
