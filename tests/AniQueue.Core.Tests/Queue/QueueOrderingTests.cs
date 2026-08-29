using AniQueue.Core.Queue;

namespace AniQueue.Core.Tests.Queue;

/// <summary>
/// The arithmetic behind every reorder. The database does not defend the
/// contiguity invariant for a service that maintains it, which makes these the
/// tests that stand behind that trade — so they are exhaustive where the input
/// space allows it rather than picking a few representative cases.
/// </summary>
public class QueueOrderingTests
{
    [Theory]
    [InlineData(0, QueueMove.Up)]
    [InlineData(0, QueueMove.Top)]
    [InlineData(4, QueueMove.Down)]
    [InlineData(4, QueueMove.Bottom)]
    public void A_move_that_would_change_nothing_returns_null(int fromIndex, QueueMove move) =>
        Assert.Null(QueueOrdering.TargetIndex(fromIndex, count: 5, move));

    [Theory]
    [InlineData(2, QueueMove.Up, 1)]
    [InlineData(2, QueueMove.Down, 3)]
    [InlineData(2, QueueMove.Top, 0)]
    [InlineData(2, QueueMove.Bottom, 4)]
    public void A_move_resolves_to_its_destination(int fromIndex, QueueMove move, int expected) =>
        Assert.Equal(expected, QueueOrdering.TargetIndex(fromIndex, count: 5, move));

    [Fact]
    public void The_only_slot_in_a_queue_cannot_move_anywhere() =>
        Assert.All(
            Enum.GetValues<QueueMove>(),
            move => Assert.Null(QueueOrdering.TargetIndex(0, count: 1, move)));

    [Fact]
    public void An_empty_queue_has_nowhere_to_move_to() =>
        Assert.All(
            Enum.GetValues<QueueMove>(),
            move => Assert.Null(QueueOrdering.TargetIndex(0, count: 0, move)));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void An_index_outside_the_queue_resolves_to_nothing(int fromIndex) =>
        Assert.All(
            Enum.GetValues<QueueMove>(),
            move => Assert.Null(QueueOrdering.TargetIndex(fromIndex, count: 5, move)));

    /// <summary>
    /// A drop past either end is a well-formed gesture, not a bad request — the
    /// browser's idea of the queue length can lag the server's.
    /// </summary>
    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99, 4)]
    public void An_explicit_destination_outside_the_queue_is_clamped(int requested, int expected) =>
        Assert.Equal(expected, QueueOrdering.TargetIndex(fromIndex: 2, count: 5, requested));

    [Fact]
    public void An_explicit_destination_of_where_it_already_is_returns_null() =>
        Assert.Null(QueueOrdering.TargetIndex(fromIndex: 2, count: 5, requestedIndex: 2));

    [Fact]
    public void Clamping_never_invents_a_move_for_a_slot_already_at_that_end()
    {
        Assert.Null(QueueOrdering.TargetIndex(fromIndex: 0, count: 5, requestedIndex: -10));
        Assert.Null(QueueOrdering.TargetIndex(fromIndex: 4, count: 5, requestedIndex: 10));
    }

    [Theory]
    [InlineData(0, 3, "BCDAE")]
    [InlineData(3, 0, "DABCE")]
    [InlineData(1, 2, "ACBDE")]
    [InlineData(4, 0, "EABCD")]
    [InlineData(0, 4, "BCDEA")]
    public void Moving_shifts_everything_between_the_two_positions(int from, int to, string expected)
    {
        var moved = QueueOrdering.Move<char>(['A', 'B', 'C', 'D', 'E'], from, to);

        Assert.Equal(expected, new string([.. moved]));
    }

    [Fact]
    public void Moving_a_slot_onto_itself_leaves_the_order_alone()
    {
        var moved = QueueOrdering.Move<char>(['A', 'B', 'C'], 1, 1);

        Assert.Equal("ABC", new string([.. moved]));
    }

    /// <summary>
    /// The property the service depends on: whatever the move, the result holds
    /// exactly the same items. That is what makes renumbering 0..n-1 from the new
    /// order safe — nothing can be lost or doubled by the reorder itself.
    /// </summary>
    [Fact]
    public void Every_move_produces_a_permutation_of_the_input()
    {
        int[] queue = [10, 20, 30, 40, 50, 60];

        for (var from = 0; from < queue.Length; from++)
        {
            for (var to = 0; to < queue.Length; to++)
            {
                var moved = QueueOrdering.Move(queue, from, to);

                Assert.Equal(queue.Length, moved.Count);
                Assert.Equal(queue.Order(), moved.Order());
                Assert.Equal(queue[from], moved[to]);
            }
        }
    }

    [Fact]
    public void Moving_from_outside_the_queue_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QueueOrdering.Move<char>(['A', 'B'], 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QueueOrdering.Move<char>(['A', 'B'], 0, -1));
    }
}
