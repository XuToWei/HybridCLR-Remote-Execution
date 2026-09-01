using System;

namespace HybridCLR.RemoteExecution
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RemoteCallableAttribute : Attribute
    {
        public RemoteCallableAttribute(string description, int timeoutSeconds = 30)
        {
            Description = description;
            TimeoutSeconds = timeoutSeconds;
        }

        public string Description { get; }
        public int TimeoutSeconds { get; }
    }
}
