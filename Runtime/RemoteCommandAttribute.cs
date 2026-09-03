using System;

namespace RemoteExecution
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RemoteCommandAttribute : Attribute
    {
        public RemoteCommandAttribute(string description, int timeoutSeconds = 30)
        {
            Description = description;
            TimeoutSeconds = timeoutSeconds;
        }

        public string Description { get; }
        public int TimeoutSeconds { get; }
    }
}
