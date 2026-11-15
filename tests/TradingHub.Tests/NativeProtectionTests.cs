using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Chart protection against a fake venue that models a documented linked stop/target (OCO) contract: one request sends
/// both legs, a full fill of one leg makes the venue cancel the other, legs are amended natively, and the venue reports the
/// group of each working leg. Venue behaviour the contract leaves open (partial fills) is modelled both ways. This is
/// fake-venue conformance, not evidence of a real venue's behaviour.
/// </summary>
public class NativeProtectionTests
{
    private static BracketManager Brackets(OcoVenue venue, int targets = 1)
    {
        var template = new BracketTemplate { Enabled = true, StopTicks = 10, BreakevenAfterFirstTarget = false };
        template.Targets.Clear();
        for (int i = 0; i < targets; i++) template.Targets.Add(new BracketTarget { Ticks = 20 + 10 * i, Percent = 100m / targets });
        return new BracketManager(venue) { Template = template };
    }

    [Fact]
    public void Switched_off_keeps_two_separate_app_managed_orders()
    {
        var venue = new OcoVenue { Enabled = false };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        Assert.Equal(0, venue.LinkedSubmissions);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.All(venue.Working, o => Assert.Null(venue.ProtectionGroupOf(o)));
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Null(brackets.ProtectionNote);
        Assert.Equal(90m, brackets.StopLoss!.TriggerPrice);
        Assert.Equal(120m, Assert.Single(brackets.Targets).Price);
    }

    [Fact]
    public void Opted_in_pair_is_one_linked_submission_and_a_target_fill_leaves_nothing_behind()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        Assert.Equal(1, venue.LinkedSubmissions);
        Assert.Equal(2, venue.Submitted.Count);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);
        Assert.NotNull(venue.ProtectionGroupOf(stop));
        Assert.Equal(venue.ProtectionGroupOf(stop), venue.ProtectionGroupOf(target));
        Assert.Equal(ProtectionMode.ExchangeSide, brackets.Mode);
        Assert.True(stop.ReduceOnly && target.ReduceOnly);
        Assert.Equal(4m, stop.Unfilled);
        Assert.Equal(4m, target.Unfilled);

        venue.Fill(target, 4);

        Assert.Equal(OrderStatus.Canceled, stop.Status); // cancelled by the venue, not by this app
        Assert.Empty(venue.CancelRequests);
        Assert.Empty(venue.Working);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Equal(ProtectionMode.None, brackets.Mode);
    }

    [Fact]
    public void Partial_target_fill_resizes_the_linked_stop_natively_when_the_venue_keeps_it()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);

        venue.Fill(target, 1);

        Assert.Equal(3m, venue.Position.Volume);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(3m, stop.Unfilled);
        Assert.Contains(stop, venue.ReplaceRequests);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Equal(ProtectionMode.ExchangeSide, brackets.Mode);
    }

    [Fact]
    public void Partial_target_fill_that_cancels_the_linked_stop_restores_an_app_managed_stop()
    {
        var venue = new OcoVenue { PartialFillCancelsSibling = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);

        venue.Fill(target, 1);

        Assert.Equal(OrderStatus.Canceled, stop.Status);
        var restored = brackets.StopLoss!;
        Assert.NotSame(stop, restored);
        Assert.Equal(3m, restored.Unfilled);
        Assert.Equal(90m, restored.TriggerPrice);
        Assert.True(restored.ReduceOnly);
        Assert.Null(venue.ProtectionGroupOf(restored));
        Assert.Equal(1, venue.LinkedSubmissions);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Contains("cancelled one linked leg", brackets.ProtectionNote);
    }

    [Fact]
    public void Moving_a_linked_leg_is_a_native_amendment_of_the_same_order()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;

        brackets.Move(stop, 95);

        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(95m, stop.TriggerPrice);
        Assert.Equal(4m, stop.Unfilled);
        Assert.Single(venue.ReplaceRequests);
        Assert.Empty(venue.CancelRequests);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Equal(ProtectionMode.ExchangeSide, brackets.Mode);
    }

    [Fact]
    public void A_refused_link_falls_back_to_app_managed_protection_at_once()
    {
        var venue = new OcoVenue { RefuseLinks = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        Assert.Equal(1, venue.LinkedSubmissions);
        Assert.Equal(4, venue.Submitted.Count);
        Assert.Equal(2, venue.Submitted.Count(o => o.Status == OrderStatus.Rejected));
        Assert.Equal(90m, brackets.StopLoss!.TriggerPrice);
        Assert.Equal(4m, brackets.StopLoss!.Unfilled);
        Assert.Equal(120m, Assert.Single(brackets.Targets).Price);
        Assert.Equal(2, venue.Working.Count);
        Assert.All(venue.Working, o => Assert.Null(venue.ProtectionGroupOf(o)));
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Contains("refused", brackets.ProtectionNote);
    }

    [Fact]
    public void An_unknown_linked_submission_is_never_duplicated_and_binds_when_the_venue_reports_it()
    {
        var venue = new OcoVenue { HoldLinks = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        Assert.Equal(1, venue.LinkedSubmissions);
        for (int i = 0; i < 3; i++) venue.Publish();
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Empty(venue.Working);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode); // unproven until the venue reports the group

        venue.ResolveHeld(accepted: true);

        Assert.Equal(2, venue.Submitted.Count);
        Assert.Equal(2, venue.Working.Count);
        Assert.Equal(ProtectionMode.ExchangeSide, brackets.Mode);
    }

    [Fact]
    public void An_unknown_linked_submission_later_refused_still_ends_protected()
    {
        var venue = new OcoVenue { HoldLinks = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        venue.ResolveHeld(accepted: false);

        Assert.Equal(4, venue.Submitted.Count);
        Assert.Equal(2, venue.Working.Count);
        Assert.Equal(4m, brackets.StopLoss!.Unfilled);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
    }

    [Fact]
    public void A_restarted_manager_adopts_the_venue_pair_instead_of_placing_template_protection()
    {
        var venue = new OcoVenue();
        var first = Brackets(venue);
        venue.Open(4, 100);
        var stop = first.StopLoss!;
        var target = Assert.Single(first.Targets);
        first.Dispose();

        // Reopened before the account snapshot completes: the existing position then looks new.
        venue.PositionReady = false;
        using var second = Brackets(venue);
        venue.PositionReady = true;
        venue.Publish();

        Assert.Equal(2, venue.Submitted.Count);
        Assert.Same(stop, second.StopLoss);
        Assert.Same(target, Assert.Single(second.Targets));
        Assert.Equal(ProtectionMode.ExchangeSide, second.Mode);

        venue.Fill(stop, 4);
        Assert.Equal(OrderStatus.Canceled, target.Status);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Empty(venue.Working);
    }

    [Fact]
    public void A_steady_reopened_manager_adopts_and_resizes_the_pair_to_the_position()
    {
        var venue = new OcoVenue();
        var first = Brackets(venue);
        venue.Open(4, 100);
        var stop = first.StopLoss!;
        first.Dispose();

        using var second = Brackets(venue);
        venue.Publish();
        Assert.Same(stop, second.StopLoss);

        venue.Position.Volume = 3; // a manual partial exit outside the brackets
        venue.Publish();
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(3m, Assert.Single(second.Targets).Unfilled);
        Assert.Equal(2, venue.Submitted.Count);
    }

    [Fact]
    public void Partial_targets_are_not_representable_and_stay_app_managed()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue, targets: 2);
        venue.Open(4, 100);

        Assert.Equal(0, venue.LinkedSubmissions);
        Assert.Equal(3, venue.Submitted.Count);
        Assert.Equal(ProtectionMode.AppManaged, brackets.Mode);
        Assert.Contains("exactly one stop and one target", brackets.ProtectionNote);
    }

    [Fact]
    public void Removing_the_target_when_the_venue_also_cancels_the_stop_keeps_the_position_protected()
    {
        var venue = new OcoVenue { CancelCancelsSibling = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);

        brackets.CancelBracket(target);

        Assert.Equal(OrderStatus.Canceled, stop.Status);
        var restored = brackets.StopLoss!;
        Assert.NotSame(stop, restored);
        Assert.Equal(4m, restored.Unfilled);
        Assert.Empty(brackets.Targets);
    }

    [Fact]
    public void A_cancellation_nobody_here_requested_is_respected_as_for_app_managed_protection()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);

        venue.CancelEverything();

        Assert.Empty(venue.Working);
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Null(brackets.StopLoss);
    }

    [Fact]
    public void A_stop_fill_closes_the_position_and_the_venue_cancels_the_target()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var target = Assert.Single(brackets.Targets);

        venue.Fill(brackets.StopLoss!, 4);

        Assert.Equal(0m, venue.Position.Volume);
        Assert.Equal(OrderStatus.Canceled, target.Status);
        Assert.Empty(venue.Working);
        Assert.Equal(2, venue.Submitted.Count);
    }

    // ===================================================================== separately published venue events

    [Fact]
    public void A_stop_cancelled_before_the_target_fill_is_reported_is_replaced_once_the_position_confirms_it()
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);

        venue.ReportCancel(stop); // the venue cancelled the stop because the target filled 1; that fill is not reported yet
        Assert.Equal(2, venue.Submitted.Count);
        Assert.Contains("Protection gap", brackets.ProtectionNote);

        venue.ReportExecution(target, 1); // fill reported, position report still 4
        Assert.Equal(2, venue.Submitted.Count);

        venue.ReportPosition(3);
        var restored = brackets.StopLoss!;
        Assert.NotSame(stop, restored);
        Assert.Equal(3m, restored.Unfilled);
        Assert.Equal(90m, restored.TriggerPrice);
        Assert.Equal(3, venue.Submitted.Count);
        Assert.DoesNotContain("Protection gap", brackets.ProtectionNote ?? "");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_all_or_close_position_after_an_earlier_partial_target_fill_never_re_sends_a_stop(bool closePosition)
    {
        var venue = new OcoVenue();
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);
        venue.Fill(target, 1); // the venue keeps the stop; it is resized to 3
        Assert.Equal(3m, stop.Unfilled);
        int submitted = venue.Submitted.Count;

        if (closePosition) OrderActions.Flatten(venue); // Cancel all, then a market close for 3
        else venue.CancelAll();
        submitted = venue.Submitted.Count;
        venue.ReportCancel(stop); // acknowledged while the position still reads 3
        venue.ReportCancel(target);
        Assert.Equal(submitted, venue.Submitted.Count);
        Assert.Null(brackets.StopLoss);
        if (closePosition)
        {
            venue.Fill(venue.Working.Single(o => o.Type == OrderTypes.Market), 3);
            Assert.Equal(0m, venue.Position.Volume);
        }
        Assert.Equal(submitted, venue.Submitted.Count);
        Assert.DoesNotContain(venue.Working, o => o.Type == OrderTypes.Stop);
    }

    [Fact]
    public void Removing_the_target_while_it_fills_completely_never_re_sends_a_stop()
    {
        var venue = new OcoVenue { DeferCancels = true };
        using var brackets = Brackets(venue);
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;
        var target = Assert.Single(brackets.Targets);

        brackets.CancelBracket(target); // requested; the venue fills it before the cancellation arrives
        venue.ReportCancel(stop); // OCO cancellation of the stop, reported before the target's fill
        Assert.Equal(2, venue.Submitted.Count);
        venue.ReportExecution(target, 4);
        Assert.Equal(2, venue.Submitted.Count);
        venue.ReportPosition(0);

        Assert.Equal(2, venue.Submitted.Count);
        Assert.Empty(venue.Working);
        Assert.Null(brackets.StopLoss);
    }

    [Fact]
    public void An_unexplained_cancellation_ends_in_a_visible_protection_gap_after_the_wait_bound()
    {
        var venue = new OcoVenue();
        var now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        using var brackets = Brackets(venue);
        brackets.Clock = () => now;
        venue.Open(4, 100);
        var stop = brackets.StopLoss!;

        venue.ReportCancel(stop); // nobody here asked; the target has not filled
        Assert.Contains("Protection gap", brackets.ProtectionNote);
        now = now.AddMinutes(5);
        venue.Publish();

        Assert.Equal(2, venue.Submitted.Count);
        Assert.Null(brackets.StopLoss);
        Assert.Contains("Protection gap", brackets.ProtectionNote);
        Assert.Contains("no stop", brackets.ProtectionNote);
    }

    /// <summary>Fake venue with a linked stop/target contract and native amendments.</summary>
    private sealed class OcoVenue : ITradingAccount, ITrackedOrderReplacement, INativeProtection
    {
        private readonly Dictionary<Order, string> _groups = new();
        private List<Order>? _scope;
        private (List<Order> Legs, string Group)? _held;
        private int _nextGroup;

        public bool Enabled { get; set; } = true;
        public bool RefuseLinks { get; set; }
        public bool HoldLinks { get; set; }
        public bool PartialFillCancelsSibling { get; set; }
        public bool CancelCancelsSibling { get; set; }
        public int LinkedSubmissions { get; private set; }

        public string Name => "Native protection fixture";
        public bool IsReal => true;
        public Instrument Instrument { get; } = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
        public Position Position { get; } = new();
        public List<Order> Submitted { get; } = new();
        public List<Order> Working { get; } = new();
        public List<Order> CancelRequests { get; } = new();
        public List<Order> ReplaceRequests { get; } = new();
        public IEnumerable<Order> ActiveOrders => Working.ToArray();
        public bool PositionReady { get; set; } = true;
        public bool OrdersReady { get; set; } = true;
        public decimal LastPrice { get; set; } = 100m;
        public string? LastError => null;
        public OrderContext OrderContext { get; } = OrderContext.EphemeralSimulation("Native protection fixture");
        public IExecutionSource Executions { get; } = new UnavailableExecutionSource();
        public event Action? Changed;

        public NativeProtectionFeatures NativeProtection => Enabled && NativeProtectionFault == null
            ? NativeProtectionFeatures.LinkedStopAndTarget | NativeProtectionFeatures.NativeAmendment
            : NativeProtectionFeatures.None;
        public string? NativeProtectionFault { get; private set; }

        public IDisposable LinkProtection(OrderOwner owner)
        {
            Assert.Null(_scope);
            var scope = _scope = new List<Order>();
            return new Scope(() =>
            {
                _scope = null;
                if (scope.Count != 2) { foreach (var leg in scope) Accept(leg); return; }
                LinkedSubmissions++;
                string group = "oco-" + ++_nextGroup;
                if (RefuseLinks)
                {
                    NativeProtectionFault = "Fixture venue refused the linked pair.";
                    foreach (var leg in scope) Reject(leg);
                    Publish();
                    return;
                }
                if (HoldLinks)
                {
                    _held = (scope, group);
                    Publish();
                    return;
                }
                foreach (var leg in scope) _groups[leg] = group;
                foreach (var leg in scope) Accept(leg);
            });
        }

        public string? ProtectionGroupOf(Order order) =>
            order.ExchangeOrderId.Length > 0 && _groups.TryGetValue(order, out var group) ? group : null;

        public void ResolveHeld(bool accepted)
        {
            var (legs, group) = _held!.Value;
            _held = null;
            if (accepted)
            {
                foreach (var leg in legs) _groups[leg] = group;
                foreach (var leg in legs) Accept(leg);
                return;
            }
            RefuseLinks = true;
            NativeProtectionFault = "Fixture venue refused the linked pair.";
            foreach (var leg in legs) Reject(leg);
            Publish();
        }

        public void Publish() => Changed?.Invoke();

        public void Open(decimal volume, decimal price)
        {
            Position.Volume = volume;
            Position.AveragePrice = price;
            Publish();
        }

        private void Accept(Order order)
        {
            order.ExchangeOrderId = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Working.Add(order);
            Publish();
        }

        private static void Reject(Order order)
        {
            order.State = OrderStates.Failed;
            order.Status = OrderStatus.Rejected;
        }

        private void VenueCancel(Order order)
        {
            if (order.State != OrderStates.Active) return;
            order.State = OrderStates.Done;
            order.Status = OrderStatus.Canceled;
            Working.Remove(order);
        }

        private Order? Sibling(Order order) => _groups.TryGetValue(order, out var group)
            ? _groups.FirstOrDefault(g => g.Value == group && !ReferenceEquals(g.Key, order)).Key : null;

        public void Fill(Order order, decimal quantity)
        {
            order.Unfilled -= quantity;
            bool full = order.Unfilled == 0;
            order.Status = full ? OrderStatus.Filled : OrderStatus.PartlyFilled;
            order.State = full ? OrderStates.Done : OrderStates.Active;
            if (full) Working.Remove(order);
            Position.Volume += order.Direction == OrderDirections.Buy ? quantity : -quantity;
            LastPrice = BracketManager.PriceOf(order);
            if ((full || PartialFillCancelsSibling) && Sibling(order) is { } sibling) VenueCancel(sibling);
            Publish();
        }

        public void CancelEverything()
        {
            foreach (var order in Working.ToArray()) VenueCancel(order);
            Publish();
        }

        public Order Submit(PreparedOrderIntent intent)
        {
            var request = intent.Request;
            var order = new Order
            {
                Type = request.Type, Direction = request.Direction, QuantityToFill = request.Quantity, Unfilled = request.Quantity,
                Price = request.Type == OrderTypes.Limit ? request.Price : 0, TriggerPrice = request.Type == OrderTypes.Stop ? request.Price : 0,
                State = OrderStates.Active, Status = OrderStatus.Placed, ReduceOnly = request.ReduceOnly,
            };
            Assert.Null(OrderContext.BindOrder(order, intent));
            Submitted.Add(order);
            if (_scope != null && _scope.Count < 2) _scope.Add(order);
            else Accept(order);
            return order;
        }

        public bool IsReplacementPending(Order order) => false; // amendments are acknowledged synchronously

        public Order Replace(Order order, PreparedOrderIntent intent)
        {
            Assert.Null(OrderContext.BindOrder(order, intent));
            ReplaceRequests.Add(order);
            order.QuantityToFill = order.Filled + intent.Request.Quantity;
            order.Unfilled = intent.Request.Quantity;
            if (order.Type == OrderTypes.Stop) order.TriggerPrice = intent.Request.Price;
            else order.Price = intent.Request.Price;
            Publish();
            return order;
        }

        public void Cancel(Order order)
        {
            if (order.ExchangeOrderId.Length == 0 || order.State != OrderStates.Active) return;
            CancelRequests.Add(order);
            CancelRequestedOrders.Add(order);
            if (DeferCancels) { Publish(); return; }
            var sibling = CancelCancelsSibling ? Sibling(order) : null;
            VenueCancel(order);
            if (sibling != null) VenueCancel(sibling);
            Publish();
        }

        public void CancelAt(decimal price) => throw new NotSupportedException();

        /// <summary>Cancel all requests every working order; acknowledgments arrive later, one event each.</summary>
        public void CancelAll()
        {
            foreach (var order in Working.ToArray())
            {
                CancelRequests.Add(order);
                CancelRequestedOrders.Add(order);
            }
            Publish();
        }

        // ---- venue events published one at a time, as a real venue's separate order/fill/position reports arrive
        public bool DeferCancels { get; set; }
        public HashSet<Order> CancelRequestedOrders { get; } = new();
        public bool CancelRequested(Order order) => CancelRequestedOrders.Contains(order);

        public void ReportCancel(Order order)
        {
            VenueCancel(order);
            Publish();
        }

        public void ReportExecution(Order order, decimal quantity)
        {
            order.Unfilled -= quantity;
            bool full = order.Unfilled == 0;
            order.Status = full ? OrderStatus.Filled : OrderStatus.PartlyFilled;
            order.State = full ? OrderStates.Done : OrderStates.Active;
            if (full) Working.Remove(order);
            LastPrice = BracketManager.PriceOf(order);
            Publish();
        }

        public void ReportPosition(decimal volume)
        {
            Position.Volume = volume;
            Publish();
        }

        private sealed class Scope(Action close) : IDisposable
        {
            public void Dispose() => close();
        }
    }
}
