// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Microsoft.JSInterop.Infrastructure
{
    /// <summary>
    /// Provides methods that receive incoming calls from JS to .NET.
    /// </summary>
    public static class DotNetDispatcher
    {
        private const string DisposeDotNetObjectReferenceMethodName = "__Dispose";
        internal static readonly JsonEncodedText DotNetObjectRefKey = JsonEncodedText.Encode("__dotNetObject");

        /// <summary>
        /// Receives a call from JS to .NET, locating and invoking the specified method.
        /// </summary>
        /// <param name="jsRuntime">The <see cref="JSRuntime"/>.</param>
        /// <param name="invocationInfo">The <see cref="DotNetInvocationInfo"/>.</param>
        /// <param name="argsJson">A JSON representation of the parameters.</param>
        /// <returns>A JSON representation of the return value, or null.</returns>
        public static string Invoke(JSRuntime jsRuntime, in DotNetInvocationInfo invocationInfo, string argsJson)
        {
            // This method doesn't need [JSInvokable] because the platform is responsible for having
            // some way to dispatch calls here. The logic inside here is the thing that checks whether
            // the targeted method has [JSInvokable]. It is not itself subject to that restriction,
            // because there would be nobody to police that. This method *is* the police.

            IDotNetObjectReference targetInstance = default;
            if (invocationInfo.DotNetObjectId != default)
            {
                targetInstance = jsRuntime.GetObjectReference(invocationInfo.DotNetObjectId);
            }

            var syncResult = InvokeSynchronously(jsRuntime, invocationInfo, targetInstance, argsJson);
            if (syncResult == null)
            {
                return null;
            }

            return JsonSerializer.Serialize(syncResult, jsRuntime.JsonSerializerOptions);
        }

        /// <summary>
        /// Receives a call from JS to .NET, locating and invoking the specified method asynchronously.
        /// </summary>
        /// <param name="jsRuntime">The <see cref="JSRuntime"/>.</param>
        /// <param name="invocationInfo">The <see cref="DotNetInvocationInfo"/>.</param>
        /// <param name="argsJson">A JSON representation of the parameters.</param>
        /// <returns>A JSON representation of the return value, or null.</returns>
        public static void BeginInvokeDotNet(JSRuntime jsRuntime, DotNetInvocationInfo invocationInfo, string argsJson)
        {
            // This method doesn't need [JSInvokable] because the platform is responsible for having
            // some way to dispatch calls here. The logic inside here is the thing that checks whether
            // the targeted method has [JSInvokable]. It is not itself subject to that restriction,
            // because there would be nobody to police that. This method *is* the police.

            // Using ExceptionDispatchInfo here throughout because we want to always preserve
            // original stack traces.

            var callId = invocationInfo.CallId;

            object syncResult = null;
            ExceptionDispatchInfo syncException = null;
            IDotNetObjectReference targetInstance = null;
            try
            {
                if (invocationInfo.DotNetObjectId != default)
                {
                    targetInstance = jsRuntime.GetObjectReference(invocationInfo.DotNetObjectId);
                }

                syncResult = InvokeSynchronously(jsRuntime, invocationInfo, targetInstance, argsJson);
            }
            catch (Exception ex)
            {
                syncException = ExceptionDispatchInfo.Capture(ex);
            }

            // If there was no callId, the caller does not want to be notified about the result
            if (callId == null)
            {
                return;
            }
            else if (syncException != null)
            {
                // Threw synchronously, let's respond.
                jsRuntime.EndInvokeDotNet(invocationInfo, new DotNetInvocationResult(syncException.SourceException, "InvocationFailure"));
            }
            else if (syncResult is Task task)
            {
                // Returned a task - we need to continue that task and then report an exception
                // or return the value.
                task.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        var exceptionDispatchInfo = ExceptionDispatchInfo.Capture(t.Exception.GetBaseException());
                        var dispatchResult = new DotNetInvocationResult(exceptionDispatchInfo.SourceException, "InvocationFailure");
                        jsRuntime.EndInvokeDotNet(invocationInfo, dispatchResult);
                    }

                    var result = TaskGenericsUtil.GetTaskResult(task);
                    jsRuntime.EndInvokeDotNet(invocationInfo, new DotNetInvocationResult(result));
                }, TaskScheduler.Current);
            }
            else
            {
                var dispatchResult = new DotNetInvocationResult(syncResult);
                jsRuntime.EndInvokeDotNet(invocationInfo, dispatchResult);
            }
        }

        private static object InvokeSynchronously(JSRuntime jsRuntime, in DotNetInvocationInfo invocationInfo, IDotNetObjectReference objectReference, string argsJson)
        {
            var assemblyName = invocationInfo.AssemblyName;
            var methodIdentifier = invocationInfo.MethodIdentifier;

            if (objectReference != null && assemblyName != null)
            {
                throw new ArgumentException($"For instance method calls, '{nameof(assemblyName)}' should be null. Value received: '{assemblyName}'.");
            }

            if (objectReference != null && string.Equals(DisposeDotNetObjectReferenceMethodName, methodIdentifier, StringComparison.Ordinal))
            {
                // The client executed dotNetObjectReference.dispose(). Dispose the reference and exit.
                objectReference.Dispose();
                return default;
            }

            var (invoker, parameterTypes) = JSInvokableCache.GetCachedMethodInfo(invocationInfo, objectReference);
            var suppliedArgs = ParseArguments(jsRuntime, methodIdentifier, argsJson, parameterTypes);

            try
            {
                // objectReference will be null if this call invokes a static JSInvokable method.
                return invoker.Invoke(objectReference?.Value, suppliedArgs);
            }
            catch (TargetInvocationException tie) // Avoid using exception filters for AOT runtime support
            {
                if (tie.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw null; // unreached
                }

                throw;
            }
        }

        internal static object[] ParseArguments(JSRuntime jsRuntime, string methodIdentifier, string arguments, Type[] parameterTypes)
        {
            if (parameterTypes.Length == 0)
            {
                return Array.Empty<object>();
            }

            var utf8JsonBytes = Encoding.UTF8.GetBytes(arguments);
            var reader = new Utf8JsonReader(utf8JsonBytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Invalid JSON");
            }

            var suppliedArgs = new object[parameterTypes.Length];

            var index = 0;
            while (index < parameterTypes.Length && reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var parameterType = parameterTypes[index];
                if (reader.TokenType == JsonTokenType.StartObject && IsIncorrectDotNetObjectRefUse(parameterType, reader))
                {
                    throw new InvalidOperationException($"In call to '{methodIdentifier}', parameter of type '{parameterType.Name}' at index {(index + 1)} must be declared as type 'DotNetObjectRef<{parameterType.Name}>' to receive the incoming value.");
                }

                suppliedArgs[index] = JsonSerializer.Deserialize(ref reader, parameterType, jsRuntime.JsonSerializerOptions);
                index++;
            }

            if (index < parameterTypes.Length)
            {
                // If we parsed fewer parameters, we can always make a definitive claim about how many parameters were received.
                throw new ArgumentException($"The call to '{methodIdentifier}' expects '{parameterTypes.Length}' parameters, but received '{index}'.");
            }

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                // Either we received more parameters than we expected or the JSON is malformed.
                throw new JsonException($"Unexpected JSON token {reader.TokenType}. Ensure that the call to `{methodIdentifier}' is supplied with exactly '{parameterTypes.Length}' parameters.");
            }

            return suppliedArgs;

            // Note that the JsonReader instance is intentionally not passed by ref (or an in parameter) since we want a copy of the original reader.
            static bool IsIncorrectDotNetObjectRefUse(Type parameterType, Utf8JsonReader jsonReader)
            {
                // Check for incorrect use of DotNetObjectRef<T> at the top level. We know it's
                // an incorrect use if there's a object that looks like { '__dotNetObject': <some number> },
                // but we aren't assigning to DotNetObjectRef{T}.
                if (jsonReader.Read() &&
                    jsonReader.TokenType == JsonTokenType.PropertyName &&
                    jsonReader.ValueTextEquals(DotNetObjectRefKey.EncodedUtf8Bytes))
                {
                    // The JSON payload has the shape we expect from a DotNetObjectRef instance.
                    return !parameterType.IsGenericType || parameterType.GetGenericTypeDefinition() != typeof(DotNetObjectReference<>);
                }

                return false;
            }
        }

        /// <summary>
        /// Receives notification that a call from .NET to JS has finished, marking the
        /// associated <see cref="Task"/> as completed.
        /// </summary>
        /// <remarks>
        /// All exceptions from <see cref="EndInvokeJS"/> are caught
        /// are delivered via JS interop to the JavaScript side when it requests confirmation, as
        /// the mechanism to call <see cref="EndInvokeJS"/> relies on
        /// using JS->.NET interop. This overload is meant for directly triggering completion callbacks
        /// for .NET -> JS operations without going through JS interop, so the callsite for this
        /// method is responsible for handling any possible exception generated from the arguments
        /// passed in as parameters.
        /// </remarks>
        /// <param name="jsRuntime">The <see cref="JSRuntime"/>.</param>
        /// <param name="arguments">The serialized arguments for the callback completion.</param>
        /// <exception cref="Exception">
        /// This method can throw any exception either from the argument received or as a result
        /// of executing any callback synchronously upon completion.
        /// </exception>
        public static void EndInvokeJS(JSRuntime jsRuntime, string arguments)
        {
            var utf8JsonBytes = Encoding.UTF8.GetBytes(arguments);

            // The payload that we're trying to parse is of the format
            // [ taskId: long, success: boolean, value: string? | object ]
            // where value is the .NET type T originally specified on InvokeAsync<T> or the error string if success is false.
            // We parse the first two arguments and call in to JSRuntimeBase to deserialize the actual value.

            var reader = new Utf8JsonReader(utf8JsonBytes);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Invalid JSON");
            }

            reader.Read();
            var taskId = reader.GetInt64();

            reader.Read();
            var success = reader.GetBoolean();

            reader.Read();
            jsRuntime.EndInvokeJS(taskId, success, ref reader);

            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("Invalid JSON");
            }
        }
    }
}