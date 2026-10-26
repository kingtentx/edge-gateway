using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeGateway.Abstractions;
using EdgeGateway.Application;
using EdgeGateway.Hosting;

namespace EdgeGateway.WinForms;

/// <summary>
/// Local named-pipe RPC boundary. The worker owns GatewayHost and all acquisition resources;
/// the WinForms process only sends application commands and renders returned snapshots.
/// The pipe is restricted to the current Windows user.
/// </summary>
internal class GatewayWorkerClient : DispatchProxy
{
    private string _executable = "";
    private string _pipeName = "";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SemaphoreSlim IpcGate = new(1, 1);

    public static IGatewayApplication ConnectOrStart()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位网关可执行文件。");
        var client = DispatchProxy.Create<IGatewayApplication, GatewayWorkerClient>();
        ((GatewayWorkerClient)(object)client).Initialize(executable);
        return client;
    }

    private void Initialize(string executable)
    {
        _executable = Path.GetFullPath(executable);
        var identity = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        _pipeName = "EdgeGateway-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        EnsureWorkerAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null) throw new ArgumentNullException(nameof(targetMethod));
        args ??= [];
        if (targetMethod.Name == nameof(IAsyncDisposable.DisposeAsync)) return ValueTask.CompletedTask;
        var contract = targetMethod.DeclaringType?.FullName ?? typeof(IGatewayApplication).FullName!;
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task)) return InvokeTaskAsync(contract, targetMethod, args);
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            return GetType().GetMethod(nameof(InvokeTaskOfTAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [contract, targetMethod, args]);
        if (returnType == typeof(ValueTask)) return new ValueTask(InvokeTaskAsync(contract, targetMethod, args));
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            return GetType().GetMethod(nameof(InvokeValueTaskOfTAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [contract, targetMethod, args]);
        if (returnType.IsInterface && returnType == typeof(IDriverCatalog))
            return CreateChild<IDriverCatalog>();
        if (returnType.IsInterface && returnType == typeof(IGatewayLog))
            return CreateChild<IGatewayLog>();
        var payload = SendAsync(contract, targetMethod, args, CancellationToken.None).GetAwaiter().GetResult();
        return payload is null ? null : JsonSerializer.Deserialize(payload, returnType, JsonOptions);
    }

    private T CreateChild<T>() where T : class
    {
        var proxy = DispatchProxy.Create<T, GatewayWorkerClient>();
        var child = (GatewayWorkerClient)(object)proxy;
        child._executable = _executable;
        child._pipeName = _pipeName;
        return proxy;
    }

    private async Task InvokeTaskAsync(string contract, MethodInfo method, object?[] args) =>
        _ = await SendAsync(contract, method, args, CancellationTokenFrom(method, args)).ConfigureAwait(false);

    private async Task<T> InvokeTaskOfTAsync<T>(string contract, MethodInfo method, object?[] args)
    {
        var payload = await SendAsync(contract, method, args, CancellationTokenFrom(method, args)).ConfigureAwait(false);
        return payload is null ? default! : JsonSerializer.Deserialize<T>(payload, JsonOptions)!;
    }

    private async ValueTask<T> InvokeValueTaskOfTAsync<T>(string contract, MethodInfo method, object?[] args) =>
        await InvokeTaskOfTAsync<T>(contract, method, args).ConfigureAwait(false);

    private static CancellationToken CancellationTokenFrom(MethodInfo method, object?[] args)
    {
        var index = Array.FindIndex(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));
        return index >= 0 && args[index] is CancellationToken token ? token : CancellationToken.None;
    }

    private async Task<string?> SendAsync(string contract, MethodInfo method, object?[] args, CancellationToken cancellationToken)
    {
        await IpcGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        await EnsureWorkerAvailableAsync(cancellationToken).ConfigureAwait(false);
        var parameters = method.GetParameters();
        var serializedArguments = new JsonElement[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            serializedArguments[i] = parameters[i].ParameterType == typeof(CancellationToken)
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonSerializer.SerializeToElement(args[i], parameters[i].ParameterType, JsonOptions);
        var request = new PipeRequest(contract, method.Name,
            parameters.Select(parameter => parameter.ParameterType.AssemblyQualifiedName ?? parameter.ParameterType.FullName!).ToArray(),
            serializedArguments);
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("采集服务在返回结果前关闭了 IPC 连接。");
            var response = JsonSerializer.Deserialize<PipeResponse>(line, JsonOptions)
                ?? throw new InvalidDataException("采集服务返回了无效的 IPC 响应。");
            if (response.Error is not null) throw new InvalidOperationException("后台采集服务：" + response.Error);
            return response.Payload;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法连接后台采集服务。请检查 EdgeGateway.Worker 日志及程序目录权限。", exception);
        }
        }
        finally { IpcGate.Release(); }
    }

    private async Task EnsureWorkerAvailableAsync(CancellationToken cancellationToken)
    {
        if (await CanConnectAsync(cancellationToken).ConfigureAwait(false)) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(_executable, "--worker")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
            });
            if (process is null) throw new InvalidOperationException("无法启动后台采集进程。");
        }
        catch (InvalidOperationException)
        {
            if (await CanConnectAsync(cancellationToken).ConfigureAwait(false)) return;
            throw;
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanConnectAsync(cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("后台采集服务启动超时。");
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(150, cancellationToken).ConfigureAwait(false);
            // A health probe uses a real request, because a connected pipe is not enough if the server is still starting.
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest("health", "Ping", [], []), JsonOptions)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return line is not null && JsonSerializer.Deserialize<PipeResponse>(line, JsonOptions)?.Error is null;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        { if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw; return false; }
    }

    internal static async Task<int> RunWorkerAsync()
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var pipeName = "EdgeGateway-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
        await using var host = await GatewayHost.CreateAsync(Path.Combine(root, "AppData"),
            pluginDirectory: Path.Combine(root, "drivers"), runtimeDirectory: root).ConfigureAwait(false);
        var application = host.Application;
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        while (!shutdown.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(shutdown.Token).ConfigureAwait(false);
                if (line is null) continue;
                var request = JsonSerializer.Deserialize<PipeRequest>(line, JsonOptions)
                    ?? throw new InvalidDataException("IPC 请求为空。");
                if (request.Contract == "health") { await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse("{}", null), JsonOptions)); continue; }
                var target = request.Contract switch
                {
                    "EdgeGateway.Application.IGatewayApplication" => (object)application,
                    "EdgeGateway.Application.IDriverCatalog" => application.Drivers,
                    "EdgeGateway.Abstractions.IGatewayLog" => application.Log,
                    _ => throw new InvalidDataException("不允许的 IPC 契约：" + request.Contract)
                };
                var method = target.GetType().GetMethods().Concat(target.GetType().GetInterfaces().SelectMany(type => type.GetMethods()))
                    .FirstOrDefault(candidate => candidate.Name == request.Method
                        && candidate.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName ?? parameter.ParameterType.FullName!)
                            .SequenceEqual(request.ParameterTypes, StringComparer.Ordinal))
                    ?? ResolveContractMethod(request.Contract, request.Method, request.ParameterTypes, target);
                var arguments = new object?[method.GetParameters().Length];
                for (var i = 0; i < arguments.Length; i++)
                {
                    var parameter = method.GetParameters()[i];
                    arguments[i] = parameter.ParameterType == typeof(CancellationToken)
                        ? shutdown.Token
                        : request.Arguments[i].ValueKind == JsonValueKind.Null
                            ? parameter.HasDefaultValue ? parameter.DefaultValue : null
                            : request.Arguments[i].Deserialize(parameter.ParameterType, JsonOptions);
                }
                var result = method.Invoke(target, arguments);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
                }
                var payload = result is null ? null : JsonSerializer.Serialize(result, result.GetType(), JsonOptions);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(payload, null), JsonOptions)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var message = exception is TargetInvocationException { InnerException: not null } tie ? tie.InnerException.Message : exception.Message;
                try
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeResponse(null, message), JsonOptions)).ConfigureAwait(false);
                }
                catch { }
            }
        }
        return 0;
    }

    private static MethodInfo ResolveContractMethod(string contract, string methodName, string[] parameterTypes, object target)
    {
        var type = contract switch
        {
            "EdgeGateway.Application.IGatewayApplication" => typeof(IGatewayApplication),
            "EdgeGateway.Application.IDriverCatalog" => typeof(IDriverCatalog),
            "EdgeGateway.Abstractions.IGatewayLog" => typeof(IGatewayLog),
            _ => target.GetType()
        };
        return type.GetMethods().FirstOrDefault(method => method.Name == methodName
            && method.GetParameters().Select(parameter => parameter.ParameterType.AssemblyQualifiedName ?? parameter.ParameterType.FullName!)
                .SequenceEqual(parameterTypes, StringComparer.Ordinal))
            ?? throw new MissingMethodException(type.FullName, methodName);
    }

    private sealed record PipeRequest(string Contract, string Method, string[] ParameterTypes, JsonElement[] Arguments);
    private sealed record PipeResponse(string? Payload, string? Error);
}
