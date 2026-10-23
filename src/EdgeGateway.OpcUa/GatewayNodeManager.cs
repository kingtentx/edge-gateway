using EdgeGateway.Model;
using EdgeGateway.Service;
using Opc.Ua;
using Opc.Ua.Server;

namespace EdgeGateway.OpcUa;

/// <summary>Cached UA address space; async writes leave the UA node-manager lock before entering device IO.</summary>
internal sealed class GatewayNodeManager : CustomNodeManager2, IWriteAsyncNodeManager
{
    private readonly GatewayRuntime _runtime;
    private readonly OpcUaServerConfiguration _options;
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<Guid, FolderState> _devices = new();
    private readonly Dictionary<NodeId, TagBinding> _tags = new();
    private FolderState? _root;
    private int _nodeCount;
    public int NodeCount => Volatile.Read(ref _nodeCount);

    public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration, GatewayRuntime runtime,
        OpcUaServerConfiguration options, CancellationToken lifetime) : base(server, configuration, GatewayOpcUaServer.NamespaceUri)
    {
        _runtime = runtime;
        _options = options;
        _lifetime = lifetime;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            _root = Folder(null, "gateway", "EdgeGateway");
            _root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, _root.NodeId));
            AddPredefinedNode(SystemContext, _root);
            ApplyConfiguration(_runtime.GetConfigurationSnapshot());
        }
    }

    public void Refresh(GatewayConfiguration? configuration, IReadOnlyList<TelemetrySample> samples)
    {
        var byId = samples.ToDictionary(sample => sample.TagId);
        lock (Lock)
        {
            if (_root is null || _lifetime.IsCancellationRequested) return;
            if (configuration is not null) ApplyConfiguration(configuration);
            foreach (var binding in _tags.Values)
            {
                byId.TryGetValue(binding.Tag.Id, out var sample);
                var value = CurrentValue(binding, sample);
                binding.Node.Value = value.Value;
                binding.Node.StatusCode = value.StatusCode;
                binding.Node.Timestamp = value.SourceTimestamp;
                binding.Node.ClearChangeMasks(SystemContext, false);
            }
        }
    }

    private void ApplyConfiguration(GatewayConfiguration configuration)
    {
        var devices = configuration.Devices.ToDictionary(device => device.Id);
        var configuredTags = configuration.Devices.SelectMany(device => device.Tags.Select(tag =>
            new KeyValuePair<NodeId, (DeviceConfiguration Device, TagConfiguration Tag)>(TagId(device.Id, tag.Id), (device, tag))))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var removed in _tags.Keys.Where(id => !configuredTags.ContainsKey(id)).ToArray())
        {
            DeleteAndNotify(removed);
            _tags.Remove(removed);
        }
        foreach (var removed in _devices.Keys.Where(id => !devices.ContainsKey(id)).ToArray())
        {
            DeleteAndNotify(_devices[removed].NodeId);
            _devices.Remove(removed);
        }
        foreach (var device in configuration.Devices)
        {
            if (!_devices.TryGetValue(device.Id, out var folder))
            {
                folder = Folder(_root, GatewayOpcUaServer.DeviceNodeIdentifier(device.Id), device.Name);
                AddStringProperty(folder, "DeviceId", device.Id.ToString("D"));
                AddStringProperty(folder, "Driver", device.DriverId);
                _devices[device.Id] = folder;
                AddPredefinedNode(SystemContext, folder);
            }
            folder.DisplayName = device.Name;
            folder.BrowseName = new QualifiedName(device.Name, NamespaceIndex);
            SetStringProperty(folder, "Driver", device.DriverId);
            foreach (var tag in device.Tags)
            {
                var id = TagId(device.Id, tag.Id);
                if (!_tags.TryGetValue(id, out var binding))
                {
                    var variable = new BaseDataVariableState(folder)
                    {
                        NodeId = id, BrowseName = new QualifiedName(tag.Name, NamespaceIndex), DisplayName = tag.Name,
                        TypeDefinitionId = VariableTypeIds.BaseDataVariableType, ReferenceTypeId = ReferenceTypeIds.HasComponent,
                        DataType = GatewayUaValueCodec.DataType(tag), ValueRank = ValueRanks.Scalar,
                        Historizing = false, WriteMask = AttributeWriteMask.None,
                        UserWriteMask = AttributeWriteMask.None, MinimumSamplingInterval = _options.UpdateIntervalMs,
                        StatusCode = StatusCodes.BadWaitingForInitialData, Timestamp = DateTime.UtcNow
                    };
                    folder.AddChild(variable);
                    binding = new(device, tag, variable);
                    variable.Handle = binding;
                    variable.OnReadValue = ReadTagValue;
                    variable.OnReadUserAccessLevel = ReadTagUserAccess;
                    AddStringProperty(variable, "TagId", tag.Id.ToString("D"));
                    AddStringProperty(variable, "Address", tag.Address);
                    AddStringProperty(variable, "Unit", tag.Unit);
                    AddStringProperty(variable, "Description", tag.Description);
                    _tags[id] = binding;
                    AddPredefinedNode(SystemContext, variable);
                }
                binding.Device = device;
                binding.Tag = tag;
                binding.Node.DataType = GatewayUaValueCodec.DataType(tag);
                binding.Node.DisplayName = tag.Name;
                binding.Node.BrowseName = new QualifiedName(tag.Name, NamespaceIndex);
                binding.Node.Description = tag.Description;
                binding.Node.AccessLevel = GatewayUaValueCodec.SupportsWrite(tag) ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;
                binding.Node.UserAccessLevel = binding.Node.AccessLevel;
                SetStringProperty(binding.Node, "Address", tag.Address);
                SetStringProperty(binding.Node, "Unit", tag.Unit);
                SetStringProperty(binding.Node, "Description", tag.Description);
                binding.Node.ClearChangeMasks(SystemContext, true);
            }
            folder.ClearChangeMasks(SystemContext, true);
        }
        _root!.ClearChangeMasks(SystemContext, true);
        Volatile.Write(ref _nodeCount, PredefinedNodes.Count);
    }

    private void DeleteAndNotify(NodeId nodeId)
    {
        // This SDK's event-driven node manager ignores the Deleted change mask for data changes.
        // Explicitly deliver an unknown-node value to existing monitors, including metadata children.
        var prefix = nodeId.Identifier + "/";
        foreach (var item in MonitoredItems.Values.OfType<IDataChangeMonitoredItem2>())
        {
            if (item.NodeId == nodeId || (item.NodeId.NamespaceIndex == NamespaceIndex
                && item.NodeId.Identifier is string identifier && identifier.StartsWith(prefix, StringComparison.Ordinal)))
                item.QueueValue(new DataValue
                {
                    Value = null, StatusCode = StatusCodes.BadNodeIdUnknown,
                    SourceTimestamp = DateTime.UtcNow, ServerTimestamp = DateTime.UtcNow
                }, new ServiceResult(StatusCodes.BadNodeIdUnknown), true);
        }
        DeleteNode(SystemContext, nodeId);
    }

    protected override void AddPredefinedNode(ISystemContext context, NodeState node)
    {
        base.AddPredefinedNode(context, node);
        if (!MonitoredNodes.TryGetValue(node.NodeId, out var monitored) || ReferenceEquals(monitored.Node, node)) return;
        // A configuration restore may recreate the same stable Guid while clients retain their monitored items.
        // The SDK otherwise reuses the deleted NodeState when a new monitor is created for that NodeId.
        monitored.Node.OnStateChanged = null;
        monitored.Node = node;
        node.OnStateChanged = monitored.OnMonitoredNodeChanged;
        if (monitored.DataChangeMonitoredItems is not null)
            foreach (var item in monitored.DataChangeMonitoredItems)
                if (item.ManagerHandle is NodeHandle handle) handle.Node = node;
    }

    private ServiceResult ReadTagValue(ISystemContext context, NodeState node, NumericRange indexRange,
        QualifiedName dataEncoding, ref object value, ref StatusCode status, ref DateTime timestamp)
    {
        if (indexRange != NumericRange.Empty) return StatusCodes.BadIndexRangeInvalid;
        if (!QualifiedName.IsNull(dataEncoding)) return StatusCodes.BadDataEncodingUnsupported;
        var binding = (TagBinding)node.Handle;
        // Query one immutable sample, not a whole-table copy, so a freshly stopped/faulted device cannot stay Good in the UA cache.
        var current = CurrentValue(binding, _runtime.GetLatestSample(binding.Tag.Id));
        value = current.Value;
        status = current.StatusCode;
        timestamp = current.SourceTimestamp;
        return ServiceResult.Good;
    }

    private ServiceResult ReadTagUserAccess(ISystemContext context, NodeState node, ref byte access)
    {
        var binding = (TagBinding)node.Handle;
        access = AccessLevels.CurrentRead;
        if (CanWrite((context as ISessionSystemContext)?.UserIdentity, binding.Device, binding.Tag)) access |= AccessLevels.CurrentWrite;
        return ServiceResult.Good;
    }

    private bool CanWrite(IUserIdentity? identity, DeviceConfiguration device, TagConfiguration tag)
    {
        var applied = _runtime.GetOpcUaConfigurationSnapshot();
        return !_lifetime.IsCancellationRequested && identity?.TokenType == UserTokenType.UserName
            && string.Equals(identity.DisplayName, applied.Username, StringComparison.Ordinal)
            && applied.Enabled && applied.EnableUserAuthentication && applied.AllowRemoteWrite
            && device.Enabled && tag.Enabled && GatewayUaValueCodec.SupportsWrite(tag) && _runtime.IsDeviceRunning(device.Id);
    }

    public async ValueTask WriteAsync(OperationContext context, IList<WriteValue> nodesToWrite,
        IList<ServiceResult> errors, CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        void CancelRequest(RequestManager source, uint requestId, StatusCode status)
        {
            if (requestId == context.RequestId) request.Cancel();
        }
        Server.RequestManager.RequestCancelled += CancelRequest;
        try
        {
            // Register before checking status, covering a Cancel arriving just before the handler was attached.
            if (StatusCode.IsBad(context.OperationStatus)) request.Cancel();
            await WriteRequestAsync(context, nodesToWrite, errors, request.Token).ConfigureAwait(false);
        }
        finally { Server.RequestManager.RequestCancelled -= CancelRequest; }
    }

    private async Task WriteRequestAsync(OperationContext context, IList<WriteValue> nodesToWrite,
        IList<ServiceResult> errors, CancellationToken cancellationToken)
    {
        for (var index = 0; index < nodesToWrite.Count; index++)
        {
            var write = nodesToWrite[index];
            if (write.Processed || write.NodeId.NamespaceIndex != NamespaceIndex) continue;
            write.Processed = true;
            if (StatusCode.IsBad(context.OperationStatus)) { errors[index] = context.OperationStatus; continue; }
            if (cancellationToken.IsCancellationRequested)
            { errors[index] = _lifetime.IsCancellationRequested ? StatusCodes.BadShutdown : StatusCodes.BadRequestCancelledByClient; continue; }
            TagBinding? binding;
            lock (Lock)
            {
                if (!_tags.TryGetValue(write.NodeId, out binding))
                {
                    errors[index] = PredefinedNodes.ContainsKey(write.NodeId) ? StatusCodes.BadNotWritable : StatusCodes.BadNodeIdUnknown;
                    continue;
                }
                if (write.AttributeId != Attributes.Value) { errors[index] = StatusCodes.BadNotWritable; continue; }
                if (!string.IsNullOrEmpty(write.IndexRange)) { errors[index] = StatusCodes.BadIndexRangeInvalid; continue; }
                if (!CanWrite(context.UserIdentity, binding.Device, binding.Tag)) { errors[index] = StatusCodes.BadUserAccessDenied; continue; }
                if (write.Value is null || write.Value.StatusCode != StatusCodes.Good
                    || write.Value.SourceTimestamp != DateTime.MinValue || write.Value.ServerTimestamp != DateTime.MinValue)
                { errors[index] = StatusCodes.BadWriteNotSupported; continue; }
                var type = TypeInfo.IsInstanceOfDataType(write.Value.Value, binding.Node.DataType, ValueRanks.Scalar,
                    SystemContext.NamespaceUris, Server.TypeTree);
                if (type is null || type == TypeInfo.Unknown)
                { errors[index] = StatusCodes.BadTypeMismatch; continue; }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
            var settings = _runtime.GetOpcUaConfigurationSnapshot();
            timeout.CancelAfter(settings.OperationTimeoutMs);
            try
            {
                var engineeringValue = GatewayUaValueCodec.EngineeringText(write.Value.Value);
                var result = await _runtime.WriteVerifiedAsync(binding.Device.Id, binding.Tag.Id, engineeringValue,
                    new WriteVerificationOptions
                    {
                        VerifyReadBack = true, RequireOpcUaWritePermission = true, TimeoutMs = settings.OperationTimeoutMs,
                        Source = $"opcua:{settings.Username}", MaxAttempts = 5, RetryIntervalMs = 100
                    }, timeout.Token).ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested) { errors[index] = StatusCodes.BadShutdown; continue; }
                if (StatusCode.IsBad(context.OperationStatus))
                {
                    errors[index] = new ServiceResult(context.OperationStatus,
                        $"写入命令 {result.CommandId} 已取消或过期；已提交={result.Submitted}；{result.Error}");
                    continue;
                }
                if (result.State == WriteCommandState.Matched && result.ReadBack is { Quality: SampleQuality.Good })
                {
                    // The runtime publishes the actual readback under its device gate. UA always reads that shared sample.
                    errors[index] = ServiceResult.Good;
                }
                else
                {
                    var status = result.State is WriteCommandState.TimedOut || (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        ? StatusCodes.BadTimeout
                        : result.State is WriteCommandState.Cancelled ? StatusCodes.BadRequestCancelledByClient : StatusCodes.BadUnexpectedError;
                    errors[index] = new ServiceResult(status, $"写入命令 {result.CommandId}：{result.State}；已提交={result.Submitted}；{result.Error}");
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
            { errors[index] = new ServiceResult(StatusCodes.BadTypeMismatch, exception.Message); }
            catch (OperationCanceledException)
            {
                errors[index] = _lifetime.IsCancellationRequested ? StatusCodes.BadShutdown
                    : StatusCode.IsBad(context.OperationStatus) ? context.OperationStatus
                    : cancellationToken.IsCancellationRequested ? StatusCodes.BadRequestCancelledByClient : StatusCodes.BadTimeout;
            }
        }
    }

    private DataValue CurrentValue(TagBinding binding, TelemetrySample? sample)
    {
        var timestamp = sample?.Timestamp.UtcDateTime ?? DateTime.UtcNow;
        if (_lifetime.IsCancellationRequested || !binding.Device.Enabled || !binding.Tag.Enabled || !_runtime.IsDeviceRunning(binding.Device.Id))
            return new DataValue { Value = null, StatusCode = StatusCodes.BadOutOfService, SourceTimestamp = timestamp };
        if (sample is null) return new DataValue { Value = null, StatusCode = StatusCodes.BadWaitingForInitialData, SourceTimestamp = timestamp };
        if (sample.Quality == SampleQuality.Bad || sample.Value is null)
            return new DataValue { Value = null, StatusCode = StatusCodes.BadCommunicationError, SourceTimestamp = timestamp };
        try
        {
            return new DataValue
            {
                Value = GatewayUaValueCodec.ConvertValue(binding.Tag, sample.Value), SourceTimestamp = timestamp,
                StatusCode = sample.Quality == SampleQuality.Good ? StatusCodes.Good : StatusCodes.UncertainDataSubNormal
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or InvalidCastException or OverflowException)
        { return new DataValue { Value = null, StatusCode = StatusCodes.BadTypeMismatch, SourceTimestamp = timestamp }; }
    }

    private NodeId TagId(Guid deviceId, Guid tagId) => new(GatewayOpcUaServer.TagNodeIdentifier(deviceId, tagId), NamespaceIndex);
    private FolderState Folder(NodeState? parent, string id, string name)
    {
        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(id, NamespaceIndex), BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name,
            TypeDefinitionId = ObjectTypeIds.FolderType, ReferenceTypeId = ReferenceTypeIds.Organizes,
            WriteMask = AttributeWriteMask.None, UserWriteMask = AttributeWriteMask.None
        };
        parent?.AddChild(folder);
        return folder;
    }
    private void AddStringProperty(NodeState parent, string name, string value)
    {
        var property = new PropertyState<string>(parent)
        {
            NodeId = new NodeId(parent.NodeId.Identifier + "/" + name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex), DisplayName = name,
            ReferenceTypeId = ReferenceTypeIds.HasProperty, TypeDefinitionId = VariableTypeIds.PropertyType,
            DataType = DataTypeIds.String, ValueRank = ValueRanks.Scalar, Value = value,
            AccessLevel = AccessLevels.CurrentRead, UserAccessLevel = AccessLevels.CurrentRead,
            StatusCode = StatusCodes.Good, Timestamp = DateTime.UtcNow
        };
        parent.AddChild(property);
    }
    private void SetStringProperty(NodeState parent, string name, string value)
    {
        if (parent.FindChild(SystemContext, new QualifiedName(name, NamespaceIndex)) is PropertyState<string> property)
        {
            property.Value = value;
            property.Timestamp = DateTime.UtcNow;
            property.ClearChangeMasks(SystemContext, false);
        }
    }
    private sealed class TagBinding(DeviceConfiguration device, TagConfiguration tag, BaseDataVariableState node)
    {
        public DeviceConfiguration Device { get; set; } = device;
        public TagConfiguration Tag { get; set; } = tag;
        public BaseDataVariableState Node { get; } = node;
    }
}
