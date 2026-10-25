using System;
using System.Threading;

namespace VirtualPlayerEngineProbe;

// One reservation, published BEFORE native StartServer can start either listener.
// No late claims, re-arming, Player references or engine writes. Readers on accept,
// network and game threads see one immutable snapshot, not mutable collections.
public sealed class StartupReservation
{
    public sealed record View(int Slot, int Capacity, Guid Session);
    private View current;
    private int started;
    public int OwnerThread { get; private set; }
    public View Snapshot => Volatile.Read(ref current);
    public bool IsReserved(int slot) => Snapshot is { } view && view.Slot == slot;
    public static bool ConnectionIdle(bool connected, bool active, int state, bool pending, bool terminationApproved)
        => !connected && !active && state == 0 && !pending && !terminationApproved;
    public bool MaySkipDisconnectedSlot(int slot, bool connected, bool active, int state, bool pending, bool terminationApproved)
        => IsReserved(slot) && ConnectionIdle(connected, active, state, pending, terminationApproved);

    public bool ArmBeforeListening(bool alreadyListening, int capacity)
    {
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return false;
        OwnerThread = Environment.CurrentManagedThreadId;
        if (alreadyListening || capacity < 2 || capacity > 255) return false;
        Volatile.Write(ref current, new View(capacity - 1, capacity, Guid.NewGuid()));
        return true;
    }

    public void AssertOwner()
    {
        if (OwnerThread == 0 || OwnerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Player lifecycle must run on the server simulation thread.");
    }

    public void AssertOwner(bool engineMainThread)
    {
        if (!engineMainThread) throw new InvalidOperationException("Engine Program.IsMainThread is false.");
        AssertOwner();
    }

    public void Release()
    {
        AssertOwner();
        Volatile.Write(ref current, null);
    }
}
