// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JavaScript.NodeApi.Interop;
using Xunit;

namespace Microsoft.JavaScript.NodeApi.Test;

public class TsfnCallGateTests
{
    [Fact]
    public void EnterAndExitWhileOpen()
    {
        var gate = new TsfnCallGate();

        Assert.True(gate.TryEnter());
        gate.Exit();

        Assert.False(gate.IsClosed);
    }

    [Fact]
    public void CloseRejectsFurtherEntry()
    {
        var gate = new TsfnCallGate();

        gate.Close();

        Assert.True(gate.IsClosed);
        Assert.False(gate.TryEnter());
    }

    [Fact]
    public void CloseIsIdempotent()
    {
        var gate = new TsfnCallGate();

        gate.Close();
        gate.Close();

        Assert.True(gate.IsClosed);
        Assert.False(gate.TryEnter());
    }

    // Close must not return while a call is in flight: it has to wait for the corresponding Exit,
    // guaranteeing no native TSFN call is in progress once the TSFN is released.
    [Fact]
    public void CloseWaitsForInFlightCallToExit()
    {
        var gate = new TsfnCallGate();

        // Simulate an in-flight native call that has entered but not yet exited.
        Assert.True(gate.TryEnter());

        Task closeTask = Task.Run(() => gate.Close());

        // Close cannot complete while the call is in flight.
        Assert.False(closeTask.Wait(TimeSpan.FromMilliseconds(200)));

        // Once the in-flight call exits, Close completes.
        gate.Exit();
        Assert.True(closeTask.Wait(TimeSpan.FromSeconds(5)));

        // After closing, no further calls are admitted.
        Assert.False(gate.TryEnter());
    }

    // Once Close has set the closed flag, an entry that races with it must be rejected, so the
    // in-flight count cannot rise again after Close begins draining.
    [Fact]
    public void EntryDoesNotSucceedAfterCloseFlagSet()
    {
        var gate = new TsfnCallGate();

        gate.Close();

        // A burst of concurrent entry attempts after Close must all fail.
        Parallel.For(0, 1000, _ => Assert.False(gate.TryEnter()));
    }

    // Stress the gate with concurrent enter/exit callers while another thread closes it, then
    // assert the invariant that after Close returns no caller is inside the gate and none can
    // enter.
    [Fact]
    public void ConcurrentCallersDrainBeforeCloseCompletes()
    {
        var gate = new TsfnCallGate();
        using var start = new ManualResetEventSlim(false);

        Task[] callers = new Task[8];
        for (int i = 0; i < callers.Length; i++)
        {
            callers[i] = Task.Run(() =>
            {
                start.Wait();
                for (int j = 0; j < 5000; j++)
                {
                    if (gate.TryEnter())
                    {
                        // Represents a brief native call.
                        Thread.SpinWait(10);
                        gate.Exit();
                    }
                }
            });
        }

        start.Set();

        // Close concurrently with the callers; it must wait for any in-flight call to exit.
        gate.Close();

        // After Close returns, no caller may enter and the callers finish without error.
        Assert.False(gate.TryEnter());
        Assert.True(Task.WaitAll(callers, TimeSpan.FromSeconds(30)));
        Assert.True(gate.IsClosed);
    }
}
