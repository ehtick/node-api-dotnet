// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Threading;

namespace Microsoft.JavaScript.NodeApi.Interop;

/// <summary>
/// Coordinates in-flight native thread-safe-function (TSFN) calls with TSFN release, so that a
/// caller cannot invoke the TSFN after (or while) it is being released.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JSTsfnSynchronizationContext"/> posts work to the JS thread by calling into a native
/// TSFN, and releases that TSFN from an environment cleanup hook during teardown. Without
/// coordination there is a race: a poster can observe the context as not-yet-disposed, then the
/// cleanup hook can release the TSFN, and then the poster calls into the released TSFN &mdash; a
/// native use-after-release that a managed <c>try/catch</c> cannot guard.
/// </para>
/// <para>
/// This gate closes that window. A caller wraps each native TSFN call in
/// <see cref="TryEnter"/>/<see cref="Exit"/>; the owner calls <see cref="Close"/> before releasing
/// the TSFN. <see cref="Close"/> atomically prevents any further <see cref="TryEnter"/> from
/// succeeding and then waits for all in-flight calls to finish, guaranteeing that no native call
/// is in progress and none can start once <see cref="Close"/> returns.
/// </para>
/// </remarks>
internal sealed class TsfnCallGate
{
    // High bit marks the gate as closed; the remaining bits count in-flight calls. Because Enter
    // never increments once the closed bit is set, and Exit is only called after a successful
    // Enter, the count part never borrows into the closed bit.
    private const int ClosedFlag = unchecked((int)0x80000000);
    private const int CountMask = 0x7FFFFFFF;

    private int _state;

    /// <summary>
    /// Gets a value indicating whether the gate has been closed.
    /// </summary>
    public bool IsClosed => (Volatile.Read(ref _state) & ClosedFlag) != 0;

    /// <summary>
    /// Attempts to enter the gate for a single native call. When this returns true the caller must
    /// pair it with exactly one call to <see cref="Exit"/> once the native call has returned.
    /// </summary>
    /// <returns>True if the call may proceed; false if the gate is closed and the call must be
    /// skipped.</returns>
    public bool TryEnter()
    {
        int state = Volatile.Read(ref _state);
        while ((state & ClosedFlag) == 0)
        {
            int updated = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (updated == state)
            {
                return true;
            }

            state = updated;
        }

        return false;
    }

    /// <summary>
    /// Marks completion of a native call previously admitted by <see cref="TryEnter"/>.
    /// </summary>
    public void Exit() => Interlocked.Decrement(ref _state);

    /// <summary>
    /// Closes the gate so that no further calls can enter, then blocks until all in-flight calls
    /// have exited. After this returns it is safe to release the underlying TSFN. Calling this more
    /// than once is safe.
    /// </summary>
    public void Close()
    {
        int state = Volatile.Read(ref _state);
        while ((state & ClosedFlag) == 0)
        {
            int updated = Interlocked.CompareExchange(
                ref _state, state | ClosedFlag, state);
            if (updated == state)
            {
                break;
            }

            state = updated;
        }

        SpinWait spin = default;
        while ((Volatile.Read(ref _state) & CountMask) != 0)
        {
            spin.SpinOnce();
        }
    }
}
