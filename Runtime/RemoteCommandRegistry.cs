using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public static class RemoteCommandRegistry
    {
        public const int MaxIdLength = 1024;
        public const int MaxDescriptionLength = 1024;
        public const int MaxContentTypeLength = 256;
        public const int MaxTimeoutSeconds = 3600;

        private static readonly object s_Lock = new object();
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);
        private static readonly Dictionary<string, RemoteCommandDescriptor> s_Commands =
            new Dictionary<string, RemoteCommandDescriptor>(StringComparer.Ordinal);

        public static RemoteCommandDescriptor Register(RemoteCommandDefinition definition,
            Func<RemoteCommandContext, CancellationToken, Task<RemoteCommandResult>> handler)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            string error = ValidateDefinition(definition);
            if (error != null) throw new ArgumentException(error, nameof(definition));
            var descriptor = new RemoteCommandDescriptor(definition, handler);
            lock (s_Lock)
            {
                if (s_Commands.ContainsKey(descriptor.Id))
                    throw new InvalidOperationException($"Remote command ID is duplicated: {descriptor.Id}");
                s_Commands.Add(descriptor.Id, descriptor);
            }
            return descriptor;
        }

        public static IReadOnlyList<RemoteCommandDescriptor> RegisterProvider(
            IRemoteCommandProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            var staged = new StagedRegistry();
            provider.RegisterCommands(staged);
            RemoteCommandDescriptor[] descriptors = staged.Descriptors.ToArray();
            lock (s_Lock)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (RemoteCommandDescriptor descriptor in descriptors)
                {
                    if (!ids.Add(descriptor.Id))
                        throw new InvalidOperationException($"Remote command ID is duplicated in the provider: {descriptor.Id}");
                    if (s_Commands.ContainsKey(descriptor.Id))
                        throw new InvalidOperationException($"Remote command ID is already registered: {descriptor.Id}");
                }
                foreach (RemoteCommandDescriptor descriptor in descriptors)
                    s_Commands.Add(descriptor.Id, descriptor);
            }
            return descriptors;
        }

        public static IReadOnlyList<RemoteCommandDescriptor> Snapshot()
        {
            lock (s_Lock)
                return s_Commands.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }

        public static bool TryGet(string id, out RemoteCommandDescriptor descriptor)
        {
            lock (s_Lock) return s_Commands.TryGetValue(id, out descriptor);
        }

        public static void Unregister(IReadOnlyList<RemoteCommandDescriptor> descriptors)
        {
            if (descriptors == null) throw new ArgumentNullException(nameof(descriptors));
            lock (s_Lock)
            {
                foreach (RemoteCommandDescriptor descriptor in descriptors)
                {
                    if (descriptor == null ||
                        !s_Commands.TryGetValue(descriptor.Id, out RemoteCommandDescriptor existing) ||
                        !ReferenceEquals(existing, descriptor))
                        throw new InvalidOperationException("Remote command registration no longer matches the requested rollback.");
                }
                foreach (RemoteCommandDescriptor descriptor in descriptors)
                    s_Commands.Remove(descriptor.Id);
            }
        }

        public static async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandDescriptor descriptor,
            RemoteCommandContext context, CancellationToken cancellationToken)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (!descriptor.IsExecutable) throw new InvalidOperationException("Remote command is not executable.");
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (context.CancellationToken != cancellationToken)
                throw new InvalidOperationException("Command context cancellation token does not match the execution token.");
            if (context.Payload.Length > descriptor.MaxRequestBytes)
                throw new InvalidDataException($"Command request exceeds {descriptor.MaxRequestBytes} bytes.");
            RemoteCommandResult result = await descriptor.Handler(context, cancellationToken);
            if (result == null) throw new InvalidOperationException("Remote command returned no result.");
            if (result.Payload.Length > descriptor.MaxResponseBytes)
                throw new InvalidDataException($"Command response exceeds {descriptor.MaxResponseBytes} bytes.");
            return result;
        }

        public static IReadOnlyList<RemoteCommandDescriptor> DiscoverAttributes(IEnumerable<Assembly> assemblies)
        {
            var found = new List<RemoteCommandDescriptor>();
            foreach (Assembly assembly in assemblies ?? Array.Empty<Assembly>())
            {
                if (assembly == null) continue;
                foreach (Type type in GetLoadableTypes(assembly))
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        var attribute = method.GetCustomAttribute<RemoteCommandAttribute>(false);
                        if (attribute == null) continue;
                        if (!TryCreateAttributeCommand(assembly, method, attribute, out RemoteCommandDescriptor descriptor,
                            out string error))
                            throw new InvalidOperationException($"Remote command {type.FullName}.{method.Name} is invalid: {error}");
                        found.Add(descriptor);
                    }
                }
            }
            return found.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }

        public static IReadOnlyList<RemoteCommandDescriptor> RegisterAttributeCommands(
            IEnumerable<Assembly> assemblies)
        {
            RemoteCommandDescriptor[] discovered = DiscoverAttributes(assemblies).ToArray();
            lock (s_Lock)
            {
                ValidateAttributeBatch(discovered);
                RemoteCommandDescriptor[] additions = discovered
                    .Where(descriptor => !s_Commands.ContainsKey(descriptor.Id))
                    .ToArray();
                foreach (RemoteCommandDescriptor descriptor in additions)
                    s_Commands.Add(descriptor.Id, descriptor);
                return additions;
            }
        }

        private static void ValidateAttributeBatch(IEnumerable<RemoteCommandDescriptor> descriptors)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (RemoteCommandDescriptor descriptor in descriptors)
            {
                if (!ids.Add(descriptor.Id))
                    throw new InvalidOperationException($"Remote command ID is duplicated in the registration batch: {descriptor.Id}");
                if (s_Commands.TryGetValue(descriptor.Id, out RemoteCommandDescriptor existing) &&
                    !DescriptorsMatch(existing, descriptor))
                    throw new InvalidOperationException($"Remote command ID is already registered with different metadata: {descriptor.Id}");
            }
        }

        private static bool DescriptorsMatch(RemoteCommandDescriptor left, RemoteCommandDescriptor right)
        {
            return left != null && right != null &&
                string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
                string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
                string.Equals(left.Category, right.Category, StringComparison.Ordinal) &&
                left.TimeoutSeconds == right.TimeoutSeconds &&
                left.MaxRequestBytes == right.MaxRequestBytes &&
                left.MaxResponseBytes == right.MaxResponseBytes &&
                string.Equals(left.RequestContentType, right.RequestContentType, StringComparison.Ordinal) &&
                string.Equals(left.ResponseContentType, right.ResponseContentType, StringComparison.Ordinal) &&
                left.RequiresMainThread == right.RequiresMainThread;
        }

        private static bool TryCreateAttributeCommand(Assembly assembly, MethodInfo method,
            RemoteCommandAttribute attribute, out RemoteCommandDescriptor descriptor, out string error)
        {
            descriptor = null;
            error = null;
            if (!method.IsStatic) { error = "method must be static"; return false; }
            if (method.IsGenericMethod || method.ContainsGenericParameters) { error = "generic methods are not supported"; return false; }
            if (method.DeclaringType == null || string.IsNullOrEmpty(method.DeclaringType.FullName)) { error = "declaring type must have a FullName"; return false; }
            if (method.GetParameters().Length != 0) { error = "method must not have parameters"; return false; }
            if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>(false) != null)
            { error = "async void is not supported"; return false; }
            if (string.IsNullOrWhiteSpace(attribute.Description) ||
                GetUtf8ByteCount(attribute.Description) > MaxDescriptionLength)
            { error = $"description must contain 1..{MaxDescriptionLength} UTF-8 bytes"; return false; }
            if (attribute.TimeoutSeconds < 1 || attribute.TimeoutSeconds > MaxTimeoutSeconds)
            { error = $"timeout must be in range 1..{MaxTimeoutSeconds}"; return false; }
            if (!IsSupportedReturnType(method.ReturnType)) { error = "return type must be void, Task or UniTask"; return false; }

            string id = $"{assembly.GetName().Name}::{method.DeclaringType.FullName}::{method.Name}";
            if (GetUtf8ByteCount(id) > MaxIdLength)
            { error = $"command ID exceeds {MaxIdLength} UTF-8 bytes"; return false; }
            var definition = new RemoteCommandDefinition(id, method.Name, attribute.Description, method.DeclaringType.FullName,
                attribute.TimeoutSeconds, 0, 0, string.Empty, string.Empty);
            descriptor = new RemoteCommandDescriptor(definition,
                (context, cancellationToken) => InvokeAttributeAsync(method, cancellationToken));
            return true;
        }

        private static async Task<RemoteCommandResult> InvokeAttributeAsync(MethodInfo method,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                object value = method.Invoke(null, null);
                if (value is Task task) await task.ConfigureAwait(false);
                else if (value != null && HasGetAwaiter(value.GetType())) await AwaitableToTask(value).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return RemoteCommandResult.Success();
            }
            catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
            {
                throw exception.InnerException;
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                return RemoteCommandResult.Failure("COMMAND_EXECUTION_FAILED", exception.InnerException.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return RemoteCommandResult.Failure("COMMAND_EXECUTION_FAILED", exception.Message);
            }
        }

        private static int GetUtf8ByteCount(string value)
        {
            try { return s_Utf8.GetByteCount(value ?? string.Empty); }
            catch (EncoderFallbackException) { return int.MaxValue; }
        }

        private static string ValidateDefinition(RemoteCommandDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Id) ||
                GetUtf8ByteCount(definition.Id) > MaxIdLength) return "invalid command ID";
            if (string.IsNullOrWhiteSpace(definition.Name) ||
                GetUtf8ByteCount(definition.Name) > MaxDescriptionLength) return "invalid command name";
            if (string.IsNullOrWhiteSpace(definition.Description) ||
                GetUtf8ByteCount(definition.Description) > MaxDescriptionLength) return "invalid command description";
            if (GetUtf8ByteCount(definition.Category) > MaxDescriptionLength) return "invalid command category";
            if (definition.TimeoutSeconds < 1 || definition.TimeoutSeconds > MaxTimeoutSeconds) return "invalid command timeout";
            if (definition.MaxRequestBytes < 0 || definition.MaxRequestBytes > RemoteExecutionProtocol.MaxCommandRequestBytes) return "invalid request size";
            if (definition.MaxResponseBytes < 0 || definition.MaxResponseBytes > RemoteExecutionProtocol.MaxCommandResponseBytes) return "invalid response size";
            if (GetUtf8ByteCount(definition.RequestContentType) > MaxContentTypeLength ||
                GetUtf8ByteCount(definition.ResponseContentType) > MaxContentTypeLength) return "invalid content type";
            return null;
        }

        private static bool IsSupportedReturnType(Type type)
        {
            if (type == typeof(void) || typeof(Task).IsAssignableFrom(type)) return true;
            return type.FullName == "Cysharp.Threading.Tasks.UniTask" ||
                (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Cysharp.Threading.Tasks.UniTask`1");
        }

        private static bool HasGetAwaiter(Type type) => type.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null) != null;

        private static Task AwaitableToTask(object awaitable)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                MethodInfo getAwaiter = awaitable.GetType().GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                object awaiter = getAwaiter.Invoke(awaitable, null);
                Type awaiterType = awaiter.GetType();
                PropertyInfo isCompleted = awaiterType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo getResult = awaiterType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                MethodInfo onCompleted = awaiterType.GetMethod("OnCompleted", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Action) }, null);
                if (isCompleted == null || getResult == null || onCompleted == null)
                    throw new InvalidOperationException("Awaitable does not implement the required awaiter methods.");
                void Complete()
                {
                    try { getResult.Invoke(awaiter, null); completion.TrySetResult(null); }
                    catch (TargetInvocationException exception) when (exception.InnerException != null) { completion.TrySetException(exception.InnerException); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                }
                if ((bool)isCompleted.GetValue(awaiter, null)) Complete();
                else onCompleted.Invoke(awaiter, new object[] { (Action)Complete });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { completion.TrySetException(exception.InnerException); }
            catch (Exception exception) { completion.TrySetException(exception); }
            return completion.Task;
        }

        internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
            catch { return Array.Empty<Type>(); }
        }

        private sealed class StagedRegistry : IRemoteCommandRegistry
        {
            internal List<RemoteCommandDescriptor> Descriptors { get; } =
                new List<RemoteCommandDescriptor>();

            public RemoteCommandDescriptor Register(RemoteCommandDefinition definition,
                Func<RemoteCommandContext, CancellationToken, Task<RemoteCommandResult>> handler)
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                string error = ValidateDefinition(definition);
                if (error != null) throw new ArgumentException(error, nameof(definition));
                var descriptor = new RemoteCommandDescriptor(definition, handler);
                Descriptors.Add(descriptor);
                return descriptor;
            }
        }
    }
}
