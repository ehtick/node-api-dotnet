// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using Microsoft.JavaScript.NodeApi.Interop;
using Xunit;
using static Microsoft.JavaScript.NodeApi.Runtime.JSRuntime;

namespace Microsoft.JavaScript.NodeApi.Test;

public class JSReferenceTests
{
    private readonly MockJSRuntime _mockRuntime = new();

    private JSValueScope TestScope(JSValueScopeType scopeType)
        => TestScope(scopeType, new MockJSRuntime.SynchronizationContext());

    private JSValueScope TestScope(
        JSValueScopeType scopeType, JSSynchronizationContext synchronizationContext)
    {
        napi_env env = new(Environment.CurrentManagedThreadId);
        return new(scopeType, env, _mockRuntime, synchronizationContext);
    }

    [Fact]
    public void GetReferenceFromSameScope()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        JSReference reference = new(value);
        Assert.True(reference.GetValue().IsObject());
    }

    [Fact]
    public void GetReferenceFromParentScope()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSReference reference;
        using (JSValueScope handleScope = new(JSValueScopeType.Handle))
        {
            JSValue value = JSValue.CreateObject();
            reference = new JSReference(value);
        }

        Assert.True(reference.GetValue().IsObject());
    }

    [Fact]
    public void GetReferenceFromDifferentThread()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        JSReference reference = new(value);

        // Run in a new thread which will not have any current scope.
        TestUtils.RunInThread(() =>
        {
            Assert.Throws<JSInvalidThreadAccessException>(() => reference.GetValue());
        }).Wait();
    }

    [Fact]
    public void GetReferenceFromDifferentRootScope()
    {
        using JSValueScope rootScope1 = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        JSReference reference = new(value);

        // Run in a new thread and establish another root scope there.
        TestUtils.RunInThread(() =>
        {
            using JSValueScope rootScope2 = TestScope(JSValueScopeType.Root);
            Assert.Throws<JSInvalidThreadAccessException>(() => reference.GetValue());
        }).Wait();
    }

    [Fact]
    public void GetWeakReferenceUnavailable()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        var reference = new JSReference(value, isWeak: true);

        _mockRuntime.MockReleaseWeakReferenceValue(reference.Handle);
        Assert.Throws<NullReferenceException>(() => reference.GetValue());
    }

    [Fact]
    public void TryGetWeakReferenceValue()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        JSReference reference = new(value);
        Assert.True(reference.TryGetValue(out JSValue result));
        Assert.True(result.IsObject());
    }

    [Fact]
    public void TryGetWeakReferenceUnavailable()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        JSValue value = JSValue.CreateObject();
        var reference = new JSReference(value, isWeak: true);

        _mockRuntime.MockReleaseWeakReferenceValue(reference.Handle);
        Assert.False(reference.TryGetValue(out _));
    }

    // A reference created from a NoContext scope (as the native host does) has a null runtime
    // context, so its finalizer takes the branch that previously asserted thread access. The GC
    // finalizer runs on a thread with no JS scope, so that assertion threw
    // JSInvalidThreadAccessException out of the finalizer, which terminates the process (the
    // reported worker-teardown crash). The finalizer must instead complete without throwing.
    [Fact]
    public void FinalizeNoContextReferenceFromDifferentThreadDoesNotThrow()
    {
        using JSValueScope noContextScope = TestScope(JSValueScopeType.NoContext);

        JSValue value = JSValue.CreateObject();
        var reference = new FinalizerTestReference(value);

        // Run on a new thread that has no current scope, simulating the GC finalizer thread.
        TestUtils.RunInThread(() => reference.SimulateFinalize()).Wait();

        Assert.True(reference.IsDisposed);
    }

    // A no-context reference finalized off the JS thread cannot be deleted inline (there is no
    // synchronization context to marshal the delete back to the JS thread). Instead of leaking the
    // napi_ref until the environment is destroyed, the finalizer defers the deletion, which is then
    // performed the next time a scope for the same environment is active on its JS thread.
    [Fact]
    public void FinalizeNoContextReferenceDefersDeletionUntilNextScope()
    {
        napi_env env = new(Environment.CurrentManagedThreadId);
        using JSValueScope noContextScope = new(
            JSValueScopeType.NoContext, env, _mockRuntime,
            new MockJSRuntime.SynchronizationContext());

        JSValue value = JSValue.CreateObject();
        var reference = new FinalizerTestReference(value);
        napi_ref handle = reference.Handle;
        Assert.True(_mockRuntime.HasReference(handle));

        // Simulate the GC finalizer thread: no current scope, so the delete cannot run inline.
        TestUtils.RunInThread(() => reference.SimulateFinalize()).Wait();
        Assert.True(reference.IsDisposed);

        // The delete is deferred, not run on the finalizer thread, so the reference still exists.
        Assert.True(_mockRuntime.HasReference(handle));

        // Entering a scope for the same environment on the JS thread drains the deferred deletion.
        using (JSValueScope drainScope = new(JSValueScopeType.NoContext))
        {
        }

        Assert.False(_mockRuntime.HasReference(handle));
    }

    // When the environment-scoped scope that owns a deferred no-context deletion is disposed, a
    // final drain runs so the napi_ref is released even if no further scope is entered.
    [Fact]
    public void DisposingScopeDrainsDeferredNoContextDeletion()
    {
        napi_env env = new(Environment.CurrentManagedThreadId);
        napi_ref handle;

        var noContextScope = new JSValueScope(
            JSValueScopeType.NoContext, env, _mockRuntime,
            new MockJSRuntime.SynchronizationContext());
        try
        {
            JSValue value = JSValue.CreateObject();
            var reference = new FinalizerTestReference(value);
            handle = reference.Handle;

            TestUtils.RunInThread(() => reference.SimulateFinalize()).Wait();
            Assert.True(_mockRuntime.HasReference(handle));
        }
        finally
        {
            // Disposing the environment-scoped scope performs the final drain.
            noContextScope.Dispose();
        }

        Assert.False(_mockRuntime.HasReference(handle));
    }
    // inline. The finalizer must never throw when it runs on a thread with no current scope, and
    // the posted delete must actually release the native reference once the JS thread pumps it.
    [Fact]
    public void FinalizeContextReferenceFromDifferentThreadDoesNotThrow()
    {
        var syncContext = new MockJSRuntime.RecordingSynchronizationContext();
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root, syncContext);

        JSValue value = JSValue.CreateObject();
        var reference = new FinalizerTestReference(value);
        napi_ref handle = reference.Handle;
        Assert.True(_mockRuntime.HasReference(handle));

        TestUtils.RunInThread(() => reference.SimulateFinalize()).Wait();

        Assert.True(reference.IsDisposed);

        // The delete is deferred to the JS thread, not run inline on the finalizer thread.
        Assert.True(_mockRuntime.HasReference(handle));
        Assert.Equal(1, syncContext.PendingCount);

        // Pumping the sync context runs the posted delete, releasing the native reference.
        Assert.Equal(1, syncContext.RunPendingCallbacks());
        Assert.False(_mockRuntime.HasReference(handle));
    }

    // Explicit disposal (disposing: true) preserves the documented behavior of asserting thread
    // access for a no-context reference; only the finalizer path is made non-throwing.
    [Fact]
    public void DisposeNoContextReferenceFromDifferentThreadThrows()
    {
        using JSValueScope noContextScope = TestScope(JSValueScopeType.NoContext);

        JSValue value = JSValue.CreateObject();
        JSReference reference = new(value);

        TestUtils.RunInThread(() =>
        {
            Assert.Throws<JSInvalidThreadAccessException>(() => reference.Dispose());
        }).Wait();
    }

    // The finalizer invokes the virtual Dispose(bool), so a derived override can throw before or
    // after the base implementation runs. ~JSReference() must catch at its entry point, otherwise
    // the exception escapes the finalizer and terminates the process. This drives real GC
    // finalization of an override that throws; if the guarantee held only for the base method, the
    // test host would crash instead of completing.
    [Fact]
    public void FinalizerSwallowsExceptionsFromDerivedDisposeOverride()
    {
        using JSValueScope rootScope = TestScope(JSValueScopeType.Root);

        CreateAndAbandonThrowingReference();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Creates a throwing reference in a separate non-inlined frame and keeps no reference to it, so
    // it becomes eligible for finalization once this method returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndAbandonThrowingReference()
    {
        JSValue value = JSValue.CreateObject();
        _ = new ThrowingFinalizerReference(value);
    }

    // Exposes the protected finalizer code path (Dispose(disposing: false)) so a test can invoke it
    // directly on a non-JS thread, deterministically reproducing what the GC finalizer does.
    private sealed class FinalizerTestReference : JSReference
    {
        public FinalizerTestReference(JSValue value) : base(value) { }

        // Invokes the finalizer code path (Dispose(disposing: false)) on this instance and returns
        // whether it completed. Reads instance state so it is not flagged as a static candidate.
        public bool SimulateFinalize()
        {
            Dispose(disposing: false);
            return IsDisposed;
        }
    }

    // A reference whose Dispose(bool) override throws, to verify the finalizer entry point catches
    // exceptions from derived overrides and not just from the base implementation.
    private sealed class ThrowingFinalizerReference : JSReference
    {
        public ThrowingFinalizerReference(JSValue value) : base(value) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new InvalidOperationException("Simulated failure in a derived finalizer.");
        }
    }
}
