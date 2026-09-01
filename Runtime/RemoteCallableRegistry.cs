using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace HybridCLR.RemoteExecution
{
    public sealed class RemoteCallableDescriptor
    {
        internal RemoteCallableDescriptor(string id, string description, int timeoutSeconds, MethodInfo method)
        {
            Id = id;
            Description = description;
            TimeoutSeconds = timeoutSeconds;
            Method = method;
        }

        public string Id { get; }
        public string Description { get; }
        public int TimeoutSeconds { get; }
        internal MethodInfo Method { get; }
    }

    public static class RemoteCallableRegistry
    {
        public const int MaxDescriptionLength = 1024;
        public const int MaxMethodIdLength = 1024;
        public const int MaxTimeoutSeconds = 3600;

        public static IReadOnlyList<RemoteCallableDescriptor> Discover(IEnumerable<Assembly> assemblies)
        {
            var descriptors = new List<RemoteCallableDescriptor>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Assembly assembly in assemblies ?? Array.Empty<Assembly>())
            {
                if (assembly == null) continue;
                foreach (Type type in GetTypes(assembly))
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        var attribute = method.GetCustomAttribute<RemoteCallableAttribute>(false);
                        if (attribute == null) continue;
                        if (!TryCreate(assembly, method, attribute, out var descriptor, out string error))
                            throw new InvalidOperationException($"Remote callable {type.FullName}.{method.Name} is invalid: {error}");
                        if (!ids.Add(descriptor.Id))
                            throw new InvalidOperationException($"Remote callable ID is duplicated: {descriptor.Id}");
                        descriptors.Add(descriptor);
                    }
                }
            }
            descriptors.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
            return descriptors.AsReadOnly();
        }

        public static bool TryCreate(Assembly assembly, MethodInfo method, RemoteCallableAttribute attribute,
            out RemoteCallableDescriptor descriptor, out string error)
        {
            descriptor = null;
            if (assembly == null || method == null || attribute == null) { error = "assembly, method and attribute are required"; return false; }
            if (!method.IsStatic) { error = "method must be static"; return false; }
            if (method.IsGenericMethod || method.ContainsGenericParameters) { error = "generic methods are not supported"; return false; }
            if (method.DeclaringType == null || string.IsNullOrEmpty(method.DeclaringType.FullName)) { error = "declaring type must have a FullName"; return false; }
            if (method.GetParameters().Length != 0) { error = "method must not have parameters"; return false; }
            if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>(false) != null)
            { error = "async void is not supported"; return false; }
            if (string.IsNullOrWhiteSpace(attribute.Description) || attribute.Description.Length > MaxDescriptionLength)
            { error = $"description must contain 1..{MaxDescriptionLength} characters"; return false; }
            if (attribute.TimeoutSeconds < 1 || attribute.TimeoutSeconds > MaxTimeoutSeconds)
            { error = $"timeout must be in range 1..{MaxTimeoutSeconds}"; return false; }

            string assemblyName = assembly.GetName().Name;
            string id = $"{assemblyName}::{method.DeclaringType.FullName}::{method.Name}";
            if (id.Length > MaxMethodIdLength) { error = $"method ID exceeds {MaxMethodIdLength} characters"; return false; }
            if (!IsSupportedReturnType(method.ReturnType)) { error = "return type must be void, Task or UniTask"; return false; }
            descriptor = new RemoteCallableDescriptor(id, attribute.Description, attribute.TimeoutSeconds, method);
            error = null;
            return true;
        }

        public static async Task InvokeAsync(RemoteCallableDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            try
            {
                object value = descriptor.Method.Invoke(null, null);
                if (value is Task task)
                {
                    await task.ConfigureAwait(false);
                }
                else if (value != null && HasGetAwaiter(value.GetType()))
                {
                    await AwaitableToTask(value).ConfigureAwait(false);
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        private static bool IsSupportedReturnType(Type type)
        {
            if (type == typeof(void) || typeof(Task).IsAssignableFrom(type)) return true;
            return type.FullName == "Cysharp.Threading.Tasks.UniTask" ||
                   (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Cysharp.Threading.Tasks.UniTask`1");
        }

        private static bool HasGetAwaiter(Type type) => type.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null;

        private static Task AwaitableToTask(object awaitable)
        {
            var completion = new TaskCompletionSource<object>();
            try
            {
                MethodInfo getAwaiter = awaitable.GetType().GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object awaiter = getAwaiter.Invoke(awaitable, null);
                Type awaiterType = awaiter.GetType();
                PropertyInfo isCompletedProperty = awaiterType.GetProperty("IsCompleted", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo getResult = awaiterType.GetMethod("GetResult", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                MethodInfo onCompleted = awaiterType.GetMethod("OnCompleted", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
                if (isCompletedProperty == null || getResult == null || onCompleted == null)
                    throw new InvalidOperationException("Awaitable does not implement the required awaiter methods.");

                void Complete()
                {
                    try
                    {
                        getResult.Invoke(awaiter, null);
                        completion.TrySetResult(null);
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException != null) { completion.TrySetException(exception.InnerException); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                }

                if ((bool)isCompletedProperty.GetValue(awaiter, null)) Complete();
                else onCompleted.Invoke(awaiter, new object[] { (Action)Complete });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { completion.TrySetException(exception.InnerException); }
            catch (Exception exception) { completion.TrySetException(exception); }
            return completion.Task;
        }

        private static IEnumerable<Type> GetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
        }
    }
}
