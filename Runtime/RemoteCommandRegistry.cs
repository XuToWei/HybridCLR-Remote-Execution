using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
            catch { return Array.Empty<Type>(); }
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
