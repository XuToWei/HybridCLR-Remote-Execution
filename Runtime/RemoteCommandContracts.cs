using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace RemoteExecution
{
    [RequireImplementors]
    public interface IRemoteCommandProvider
    {
        void RegisterCommands(IRemoteCommandRegistry registry);
    }

    public interface IRemoteCommandRegistry
    {
        RemoteCommandDescriptor Register(RemoteCommandDefinition definition,
            Func<RemoteCommandContext, CancellationToken, Task<RemoteCommandResult>> handler);
    }

    public sealed class RemoteCommandDefinition
    {
        public RemoteCommandDefinition(string id, string name, string description = null, string category = null,
            int timeoutSeconds = 30, int maxRequestBytes = RemoteExecutionProtocol.DefaultMaxCommandRequestBytes,
            int maxResponseBytes = RemoteExecutionProtocol.DefaultMaxCommandResponseBytes,
            string requestContentType = "", string responseContentType = "", bool requiresMainThread = true)
        {
            Id = id;
            Name = name;
            Description = description ?? name;
            Category = category ?? string.Empty;
            TimeoutSeconds = timeoutSeconds;
            MaxRequestBytes = maxRequestBytes;
            MaxResponseBytes = maxResponseBytes;
            RequestContentType = requestContentType ?? string.Empty;
            ResponseContentType = responseContentType ?? string.Empty;
            RequiresMainThread = requiresMainThread;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public int TimeoutSeconds { get; }
        public int MaxRequestBytes { get; }
        public int MaxResponseBytes { get; }
        public string RequestContentType { get; }
        public string ResponseContentType { get; }
        public bool RequiresMainThread { get; }
    }

    public sealed class RemoteCommandDescriptor
    {
        internal RemoteCommandDescriptor(RemoteCommandDefinition definition,
            Func<RemoteCommandContext, CancellationToken, Task<RemoteCommandResult>> handler)
        {
            Id = definition.Id;
            Name = definition.Name;
            Description = definition.Description;
            Category = definition.Category;
            TimeoutSeconds = definition.TimeoutSeconds;
            MaxRequestBytes = definition.MaxRequestBytes;
            MaxResponseBytes = definition.MaxResponseBytes;
            RequestContentType = definition.RequestContentType;
            ResponseContentType = definition.ResponseContentType;
            RequiresMainThread = definition.RequiresMainThread;
            Handler = handler;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public int TimeoutSeconds { get; }
        public int MaxRequestBytes { get; }
        public int MaxResponseBytes { get; }
        public string RequestContentType { get; }
        public string ResponseContentType { get; }
        public bool RequiresMainThread { get; }
        public bool IsExecutable => Handler != null;
        internal Func<RemoteCommandContext, CancellationToken, Task<RemoteCommandResult>> Handler { get; }
    }

    public sealed class RemoteCommandContext
    {
        internal RemoteCommandContext(string commandId, string clientId, byte[] payload,
            string contentType, CancellationToken cancellationToken)
        {
            CommandId = commandId;
            ClientId = clientId;
            Payload = payload ?? Array.Empty<byte>();
            ContentType = contentType ?? string.Empty;
            CancellationToken = cancellationToken;
        }

        public string CommandId { get; }
        public string ClientId { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }
        public CancellationToken CancellationToken { get; }
    }

    public sealed class RemoteCommandResult
    {
        private RemoteCommandResult(bool succeeded, string code, string message, byte[] payload, string contentType)
        {
            Succeeded = succeeded;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Payload = payload == null || payload.Length == 0
                ? Array.Empty<byte>() : (byte[])payload.Clone();
            ContentType = contentType ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Code { get; }
        public string Message { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }

        public static RemoteCommandResult Success(string message = "", byte[] payload = null, string contentType = "")
        {
            return new RemoteCommandResult(true, string.Empty, message, payload, contentType);
        }

        public static RemoteCommandResult Failure(string code, string message, byte[] payload = null, string contentType = "")
        {
            return new RemoteCommandResult(false, code, message, payload, contentType);
        }
    }
}
