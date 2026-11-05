using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public class BracketLifecycleTests
{
    [Fact]
    public void Delayed_acceptance_never_forgets_protection_or_cancellation_before_acceptance()
    {
        var account = new Account { DelayAcceptance = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        var stop = Assert.Single(account.Submitted);
        stop.Time = DateTime.UtcNow.AddDays(-1); // Beyond the former eight-second placement window, without sleeping.
        account.Publish();

        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.Equal(4m, stop.Unfilled);
        Assert.True(stop.ReduceOnly);
        brackets.CancelBracket(stop);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Empty(account.Working);

        account.Accept(stop);
        Assert.Contains(stop, account.CancelRequests);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        account.AcknowledgeCancel(stop);
        Assert.Null(brackets.StopLoss);
        Assert.Empty(account.Working);
        Assert.Single(account.Submitted);
        Assert.Equal(4m, account.Position.Volume);
    }

    [Fact]
    public void Cancel_receipt_does_not_authorize_replacement_or_release_the_owned_order()
    {
        var account = new Account();
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        var original = brackets.StopLoss!;

        brackets.Move(original, 95);
        account.Publish();
        Assert.Same(original, Assert.Single(account.Working));
        Assert.Same(original, brackets.StopLoss);
        Assert.Equal(90m, original.TriggerPrice);
        Assert.Equal(4m, original.Unfilled);
        Assert.Single(account.Submitted);

        account.AcknowledgeCancel(original);
        var replacement = Assert.Single(account.Working);
        Assert.NotSame(original, replacement);
        Assert.Same(replacement, brackets.StopLoss);
        Assert.Equal(95m, replacement.TriggerPrice);
        Assert.Equal(4m, replacement.Unfilled);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(replacement));
        Assert.True(replacement.ReduceOnly);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("rejected")]
    [InlineData("absent")]
    public void Non_execution_target_changes_and_manual_reduction_do_not_move_stop_to_breakeven(string outcome)
    {
        var account = new Account { ImmediateCancellation = true, LastPrice = 110 };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(120);
        var target = brackets.Targets.Single();

        account.Working.Remove(target);
        if (outcome != "absent")
        {
            target.State = outcome == "rejected" ? OrderStates.Failed : OrderStates.Done;
            target.Status = outcome == "rejected" ? OrderStatus.Rejected : OrderStatus.Canceled;
        }
        account.Position.Volume = 2;
        account.Publish();

        Assert.Equal(2m, account.Position.Volume);
        var stop = brackets.StopLoss!;
        Assert.Equal(90m, stop.TriggerPrice);
        Assert.Equal(2m, stop.Unfilled);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.Equal(0m, target.Filled);
        Assert.All(account.Working, order => Assert.True(order.ReduceOnly));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Incomplete_snapshots_cannot_flatten_forget_or_replace_owned_protection(bool positionReady, bool ordersReady)
    {
        var account = new Account();
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(120);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();
        account.PositionReady = positionReady;
        account.OrdersReady = ordersReady;
        account.Position.Volume = 0;
        account.Working.Clear();
        account.Publish();

        Assert.Same(stop, brackets.StopLoss);
        Assert.Same(target, Assert.Single(brackets.Targets));
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.Equal(BracketRole.Target, brackets.RoleOf(target));
        Assert.Empty(account.CancelRequests);
        Assert.Contains("synchronizing", brackets.ApplyTemplate());
        Assert.Equal(2, account.Submitted.Count);

        account.Position.Volume = 4;
        account.Working.AddRange(new[] { stop, target });
        account.PositionReady = true;
        account.OrdersReady = true;
        account.Publish();
        Assert.Same(stop, brackets.StopLoss);
        Assert.Same(target, Assert.Single(brackets.Targets));
        Assert.Equal(4m, stop.Unfilled);
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Split_waits_for_target_cancel_and_keeps_working_stop_until_that_acknowledgment()
    {
        var account = new Account();
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();
        brackets.AddTarget(120);

        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(4m, stop.Unfilled);
        Assert.DoesNotContain(stop, account.CancelRequests);
        Assert.Same(target, Assert.Single(brackets.Targets));
        Assert.Equal(2, account.Submitted.Count);
        account.Fill(target, 1);
        Assert.Equal(3m, account.Position.Volume);
        Assert.Same(stop, brackets.StopLoss);
        Assert.DoesNotContain(stop, account.CancelRequests);
        Assert.Equal(90m, stop.TriggerPrice);

        account.AcknowledgeCancel(target);
        Assert.Contains(stop, account.CancelRequests);
        var targets = brackets.Targets.OrderBy(order => order.Price).ToArray();
        Assert.Equal(2, targets.Length);
        Assert.Equal(110m, targets[0].Price);
        Assert.Equal(1m, targets[0].Unfilled);
        Assert.Equal(120m, targets[1].Price);
        Assert.Equal(2m, targets[1].Unfilled);
        Assert.Same(stop, brackets.StopLoss);

        account.AcknowledgeCancel(stop);
        Assert.Equal(3m, brackets.StopLoss!.Unfilled);
        Assert.Equal(100m, brackets.StopLoss.TriggerPrice);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.All(account.Working, order => Assert.True(order.ReduceOnly));
    }

    [Fact]
    public void Late_execution_after_cancel_ack_is_accounted_once_and_resizes_current_protection()
    {
        var account = new Account { ImmediateCancellation = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var original = brackets.Targets.Single();
        brackets.AddTarget(120);
        Assert.Equal(OrderStatus.Canceled, original.Status);
        Assert.Equal(4m, brackets.Targets.Sum(order => order.Unfilled));

        account.Fill(original, 1);
        Assert.Equal(3m, account.Position.Volume);
        Assert.Equal(3m, brackets.StopLoss!.Unfilled);
        Assert.Equal(100m, brackets.StopLoss.TriggerPrice);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        account.Publish();
        account.Publish();
        Assert.Equal(3m, brackets.StopLoss.Unfilled);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Equal(1m, original.Filled);
    }

    [Fact]
    public void Flatten_and_reopen_waits_for_old_cancellations_and_late_fill_cannot_activate_new_breakeven()
    {
        var account = new Account();
        using var brackets = new BracketManager(account);
        brackets.Template = new BracketTemplate { Enabled = true, StopTicks = 10 };
        brackets.Template.Targets.Clear();
        brackets.Template.Targets.Add(new BracketTarget { Ticks = 10, Percent = 100 });
        brackets.ApplyTemplate();
        var oldStop = brackets.StopLoss!;
        var oldTarget = brackets.Targets.Single();
        var manual = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Sell, 1, 160, "manual");

        account.Position.Volume = 0;
        account.Publish();
        account.Position.Volume = 4;
        account.Position.AveragePrice = 120;
        account.LastPrice = 120;
        account.Publish();
        Assert.Equal(3, account.Submitted.Count);
        Assert.Same(oldStop, brackets.StopLoss);
        Assert.Same(oldTarget, Assert.Single(brackets.Targets));
        Assert.DoesNotContain(manual, account.CancelRequests);

        account.Fill(oldTarget, 1, marketPrice: 125); // A delayed old execution is not the reopened position's current market.
        Assert.Equal(3m, account.Position.Volume);
        account.AcknowledgeCancel(oldTarget);
        Assert.Equal(3, account.Submitted.Count);
        account.AcknowledgeCancel(oldStop);

        Assert.Equal(3m, brackets.StopLoss!.Unfilled);
        Assert.Equal(110m, brackets.StopLoss.TriggerPrice);
        var target = Assert.Single(brackets.Targets);
        Assert.Equal(130m, target.Price);
        Assert.Equal(3m, target.Unfilled);
        Assert.Contains(manual, account.Working);
        Assert.Equal(BracketRole.None, brackets.RoleOf(manual));
        Assert.DoesNotContain(manual, account.CancelRequests);
        Assert.Equal(3, account.Working.Count);
    }

    [Fact]
    public void Attached_entry_disappearance_or_unrelated_position_is_not_an_entry_fill()
    {
        var account = new Account { ImmediateCancellation = true };
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        var entry = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Buy, 2, 95);
        brackets.AttachToEntry(entry, 90);
        brackets.AttachToEntry(entry, 105);
        account.Working.Remove(entry);
        account.Position.Volume = 1; // A different/manual entry is not evidence that this order filled.
        account.Position.AveragePrice = 100;
        account.Publish();
        Assert.Null(brackets.StopLoss);
        Assert.Empty(brackets.Targets);
        Assert.NotNull(brackets.PendingFor(entry));
        Assert.Single(account.Submitted);

        account.Position.Volume = 0;
        account.Publish();
        account.Fill(entry, 1);
        Assert.Equal(1m, account.Position.Volume);
        Assert.Equal(1m, brackets.StopLoss!.Unfilled);
        Assert.Equal(90m, brackets.StopLoss.TriggerPrice);
        Assert.Equal(105m, Assert.Single(brackets.Targets).Price);
        Assert.Equal(1m, brackets.Targets.Single().Unfilled);
        account.Fill(entry, 1);
        Assert.Equal(2m, account.Position.Volume);
        Assert.Equal(2m, brackets.StopLoss!.Unfilled);
        Assert.Equal(2m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Empty(brackets.Pending);
    }

    [Fact]
    public void Startup_waits_for_both_replays_before_protecting_the_reported_position()
    {
        var account = new Account { PositionReady = false, OrdersReady = false };
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        brackets.Template.Enabled = true;
        brackets.Template.StopTicks = 10;
        brackets.Template.Targets.Single().Ticks = 20;
        account.Position.Volume = 4;
        account.PositionReady = true;
        account.Publish();
        Assert.Empty(account.Submitted);
        Assert.Null(brackets.StopLoss);
        account.OrdersReady = true;
        account.Publish();
        Assert.Equal(4m, brackets.StopLoss!.Unfilled);
        Assert.Equal(90m, brackets.StopLoss.TriggerPrice);
        Assert.Equal(4m, brackets.Targets.Single().Unfilled);
        Assert.Equal(120m, brackets.Targets.Single().Price);
        Assert.Equal(2, account.Working.Count);
    }

    [Fact]
    public void Partial_stop_execution_resizes_target_without_inventing_a_target_hit_or_replacing_working_stop()
    {
        var account = new Account();
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(120);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();
        account.Fill(stop, 1);

        Assert.Equal(3m, account.Position.Volume);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(90m, stop.TriggerPrice);
        Assert.Contains(target, account.CancelRequests);
        Assert.DoesNotContain(stop, account.CancelRequests);
        Assert.Same(target, Assert.Single(brackets.Targets));
        account.AcknowledgeCancel(target);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(3m, brackets.Targets.Single().Unfilled);
        Assert.Equal(120m, brackets.Targets.Single().Price);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.Equal(2, account.Working.Count);
    }

    [Fact]
    public void Attached_entry_cancel_before_acceptance_keeps_late_partial_fill_protected()
    {
        var account = new Account { DelayAcceptance = true };
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        brackets.Template.StopTicks = 10;
        brackets.Template.Targets.Single().Ticks = 20;
        var entry = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Buy, 2, 95);
        brackets.AttachTemplate(entry);
        brackets.CancelBracket(entry);
        entry.Time = DateTime.UtcNow.AddDays(-1);
        account.Publish();
        Assert.NotNull(brackets.PendingFor(entry));
        account.Accept(entry);
        Assert.Contains(entry, account.CancelRequests);
        account.Fill(entry, 1);

        Assert.Equal(1m, account.Position.Volume);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();
        Assert.Equal(85m, stop.TriggerPrice);
        Assert.Equal(1m, stop.Unfilled);
        Assert.Equal(115m, target.Price);
        Assert.Equal(1m, target.Unfilled);
        Assert.True(stop.ReduceOnly);
        Assert.True(target.ReduceOnly);
        account.AcknowledgeCancel(entry);
        account.Accept(stop);
        account.Accept(target);
        Assert.Empty(brackets.Pending);
        Assert.Equal(1m, account.Position.Volume);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.Equal(BracketRole.Target, brackets.RoleOf(target));
        Assert.DoesNotContain(stop, account.CancelRequests);
        Assert.DoesNotContain(target, account.CancelRequests);
        Assert.Equal(2, account.Working.Count);
    }

    [Fact]
    public void Attached_entry_fill_reported_after_terminal_cancel_still_gets_its_attached_protection()
    {
        var account = new Account { ImmediateCancellation = true };
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        var entry = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Buy, 2, 95);
        brackets.AttachToEntry(entry, 90);
        brackets.AttachToEntry(entry, 105);
        brackets.CancelBracket(entry);
        Assert.Empty(brackets.Pending);
        Assert.Empty(account.Working);

        account.Fill(entry, 1);
        Assert.Equal(1m, account.Position.Volume);
        Assert.Equal(90m, brackets.StopLoss!.TriggerPrice);
        Assert.Equal(1m, brackets.StopLoss.Unfilled);
        Assert.Equal(105m, brackets.Targets.Single().Price);
        Assert.Equal(1m, brackets.Targets.Single().Unfilled);
        account.Fill(entry, 1);
        Assert.Equal(2m, account.Position.Volume);
        Assert.Equal(2m, brackets.StopLoss.Unfilled);
        Assert.Equal(2m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Equal(2, account.Working.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tracked_amendment_does_not_release_split_capacity_until_broker_confirmation(bool optimisticQuantity)
    {
        var account = new AmendingAccount { OptimisticQuantity = optimisticQuantity };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var stop = brackets.StopLoss!;
        var original = brackets.Targets.Single();
        brackets.AddTarget(120);
        account.Publish();
        account.Publish();

        Assert.True(account.IsReplacementPending(original));
        Assert.Equal(4m, account.BrokerUnfilled(original));
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(90m, account.BrokerPrice(stop));
        Assert.Single(brackets.Targets);
        Assert.Equal(2, account.Submitted.Count);
        Assert.Empty(account.CancelRequests);
        account.Confirm(original);

        Assert.False(account.IsReplacementPending(original));
        Assert.Same(original, brackets.Targets.First(order => order.Price == 110));
        Assert.Equal(2m, account.BrokerUnfilled(original));
        Assert.Equal(2m, brackets.Targets.Single(order => order.Price == 120).Unfilled);
        Assert.Equal(4m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Same(stop, brackets.StopLoss);
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Rejected_tracked_resize_retains_actual_target_capacity_until_an_explicit_new_edit()
    {
        var account = new AmendingAccount { OptimisticQuantity = true };
        using var brackets = new BracketManager(account);
        var messages = new List<string>();
        brackets.Message += messages.Add;
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var stop = brackets.StopLoss!;
        var original = brackets.Targets.Single();
        brackets.AddTarget(120);
        account.Reject(original);
        account.Publish();

        Assert.False(account.IsReplacementPending(original));
        Assert.Equal(4m, original.Unfilled);
        Assert.Same(original, Assert.Single(brackets.Targets));
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(2, account.Submitted.Count);
        Assert.Empty(account.CancelRequests);
        Assert.Contains(messages, message => message.Contains("not confirmed", StringComparison.Ordinal));

        brackets.SetTargetFraction(original, 0.5m);
        Assert.True(account.IsReplacementPending(original));
        account.Confirm(original);
        Assert.Equal(4m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Equal(2m, brackets.Targets.Single(order => order.Price == 120).Unfilled);
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Fill_during_tracked_resize_updates_protection_without_duplicate_amendments_or_exposure()
    {
        var account = new AmendingAccount { OptimisticQuantity = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var stop = brackets.StopLoss!;
        var original = brackets.Targets.Single();
        brackets.AddTarget(120);
        account.Fill(original, 1);

        Assert.Equal(3m, account.Position.Volume);
        Assert.Equal(3m, account.BrokerUnfilled(original));
        Assert.Equal(4m, account.BrokerUnfilled(stop));
        Assert.False(account.IsReplacementPending(stop));
        Assert.Equal(2, account.Submitted.Count);
        account.Confirm(original);
        Assert.True(account.IsReplacementPending(stop));
        Assert.Equal(90m, account.BrokerPrice(stop));
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        account.Confirm(stop);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(3m, account.BrokerUnfilled(stop));
        Assert.Equal(100m, account.BrokerPrice(stop));
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Native_amendment_waits_for_delayed_original_acceptance_and_keeps_one_protection_handle()
    {
        var account = new AmendingAccount { DelayAcceptance = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        var stop = brackets.StopLoss!;
        stop.Time = DateTime.UtcNow.AddDays(-1);
        brackets.SetStopLoss(95);
        account.Publish();
        Assert.Single(account.Submitted);
        Assert.Equal(90m, stop.TriggerPrice);
        account.Accept(stop);
        Assert.True(account.IsReplacementPending(stop));
        Assert.Equal(90m, account.BrokerPrice(stop));
        Assert.Same(stop, brackets.StopLoss);
        account.Confirm(stop);
        Assert.Same(stop, Assert.Single(account.Working));
        Assert.Equal(95m, account.BrokerPrice(stop));
        Assert.Equal(4m, account.BrokerUnfilled(stop));
        Assert.Single(account.Submitted);
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Attached_entry_move_waits_for_native_acceptance_and_keeps_offsets_after_fill()
    {
        var account = new AmendingAccount { DelayAcceptance = true };
        account.Position.Volume = 0;
        using var brackets = new BracketManager(account);
        var entry = OrderActions.Manual(account, OrderTypes.Limit, OrderDirections.Buy, 2, 95);
        brackets.AttachToEntry(entry, 90);
        brackets.AttachToEntry(entry, 105);
        brackets.Move(entry, 96);
        Assert.Equal(95m, entry.Price);
        account.Accept(entry);
        Assert.True(account.IsReplacementPending(entry));
        Assert.Equal(95m, account.BrokerPrice(entry));
        account.Confirm(entry);
        account.DelayAcceptance = false;
        account.Fill(entry, 2);
        Assert.Equal(2m, account.Position.Volume);
        Assert.Equal(96m, account.Position.AveragePrice);
        Assert.Equal(91m, brackets.StopLoss!.TriggerPrice);
        Assert.Equal(2m, brackets.StopLoss.Unfilled);
        Assert.Equal(106m, brackets.Targets.Single().Price);
        Assert.Equal(2m, brackets.Targets.Single().Unfilled);
        Assert.Empty(brackets.Pending);
        Assert.Empty(account.CancelRequests);
    }

    [Fact]
    public void Explicit_dispose_keeps_existing_cancel_and_late_fill_obligations_until_replacement_is_protecting()
    {
        var account = new Account();
        var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();
        brackets.CancelBracket(target);
        brackets.Dispose();
        account.Fill(target, 1);
        Assert.Equal(3m, account.Position.Volume);
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(90m, stop.TriggerPrice);
        account.AcknowledgeCancel(target);
        Assert.Contains(stop, account.CancelRequests);
        Assert.Same(stop, brackets.StopLoss);
        account.AcknowledgeCancel(stop);
        Assert.Equal(3m, brackets.StopLoss!.Unfilled);
        Assert.Equal(100m, brackets.StopLoss.TriggerPrice);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(brackets.StopLoss));
        Assert.Empty(brackets.Targets);
        Assert.Same(brackets.StopLoss, Assert.Single(account.Working));
        Assert.True(brackets.StopLoss.ReduceOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_execution_before_position_report_never_recreates_the_executed_unit(bool trackedAmendment)
    {
        Account account = trackedAmendment
            ? new AmendingAccount { OptimisticQuantity = true }
            : new Account { ImmediateCancellation = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(120);
        var stop = brackets.StopLoss!;
        var target = brackets.Targets.Single();

        account.Fill(stop, 1, updatePosition: false);
        Assert.Equal(4m, account.Position.Volume); // Independent PnL still reports the older long-four position.
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(4m, stop.QuantityToFill);
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(1m, stop.Filled);
        Assert.Equal(90m, stop.TriggerPrice);
        Assert.DoesNotContain(stop, account.CancelRequests);
        if (account is AmendingAccount native) native.Confirm(target);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));

        account.Position.Volume = 3;
        account.Publish();
        account.Publish();
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(4m, stop.QuantityToFill);
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));
        Assert.DoesNotContain(stop, account.CancelRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Target_execution_before_position_report_sizes_breakeven_to_the_execution_bound(bool trackedAmendment)
    {
        Account account = trackedAmendment
            ? new AmendingAccount { OptimisticQuantity = true }
            : new Account { ImmediateCancellation = true };
        using var brackets = new BracketManager(account);
        brackets.SetStopLoss(90);
        brackets.AddTarget(110);
        var originalStop = brackets.StopLoss!;
        var target = brackets.Targets.Single();

        account.Fill(target, 1, updatePosition: false);
        Assert.Equal(4m, account.Position.Volume);
        if (account is AmendingAccount native)
        {
            Assert.True(native.IsReplacementPending(originalStop));
            native.Confirm(originalStop);
        }
        var stop = brackets.StopLoss!;
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(100m, stop.TriggerPrice);
        Assert.Equal(3m, target.Unfilled);
        Assert.Equal(1m, target.Filled);
        Assert.Equal(BracketRole.StopLoss, brackets.RoleOf(stop));

        account.Position.Volume = 3;
        account.Publish();
        account.Publish();
        Assert.Same(stop, brackets.StopLoss);
        Assert.Equal(3m, stop.Unfilled);
        Assert.Equal(100m, stop.TriggerPrice);
        Assert.Equal(3m, brackets.Targets.Sum(order => order.Unfilled));
        Assert.Equal(3m, account.Position.Volume);
    }

    private sealed class AmendingAccount : Account, ITradingAccount, ITrackedOrderReplacement
    {
        private sealed record Amendment(decimal OldPrice, decimal OldTotal, decimal Price, decimal Total);
        private readonly Dictionary<Order, Amendment> _amendments = new();
        public bool OptimisticQuantity { get; init; }

        public bool IsReplacementPending(Order order) => order.ExchangeOrderId.Length == 0 || _amendments.ContainsKey(order);
        public decimal BrokerUnfilled(Order order) => _amendments.TryGetValue(order, out var amendment)
            ? amendment.OldTotal - order.Filled : order.Unfilled;
        public decimal BrokerPrice(Order order) => _amendments.TryGetValue(order, out var amendment)
            ? amendment.OldPrice : BracketManager.PriceOf(order);

        public override Order Replace(Order order, PreparedOrderIntent intent)
        {
            decimal price = intent.Request.Price, quantity = intent.Request.Quantity;
            OrderContext.BindOrder(order, intent);
            _amendments.Add(order, new Amendment(BracketManager.PriceOf(order), order.QuantityToFill, price, order.Filled + quantity));
            if (OptimisticQuantity)
            {
                order.QuantityToFill = order.Filled + quantity;
                order.Unfilled = quantity;
            }
            if (order.Type == OrderTypes.Stop) order.TriggerPrice = price;
            else order.Price = price;
            Publish();
            return order;
        }

        public void Confirm(Order order) => Resolve(order, accepted: true);
        public void Reject(Order order) => Resolve(order, accepted: false);

        private void Resolve(Order order, bool accepted)
        {
            var amendment = _amendments[order];
            decimal filled = order.Filled;
            order.QuantityToFill = accepted ? amendment.Total : amendment.OldTotal;
            order.Unfilled = Math.Max(0, order.QuantityToFill - filled);
            decimal price = accepted ? amendment.Price : amendment.OldPrice;
            if (order.Type == OrderTypes.Stop) order.TriggerPrice = price;
            else order.Price = price;
            _amendments.Remove(order);
            Publish();
        }
    }

    private class Account : ITradingAccount
    {
        public string Name => "Bracket lifecycle fixture";
        public bool IsReal => true;
        public Instrument Instrument { get; } = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
        public Position Position { get; } = new() { Volume = 4m, AveragePrice = 100m };
        public List<Order> Submitted { get; } = new();
        public List<Order> Working { get; } = new();
        public List<Order> CancelRequests { get; } = new();
        public IEnumerable<Order> ActiveOrders => Working.ToArray();
        public bool PositionReady { get; set; } = true;
        public bool OrdersReady { get; set; } = true;
        public bool DelayAcceptance { get; set; }
        public bool ImmediateCancellation { get; set; }
        public decimal LastPrice { get; set; } = 100m;
        public string? LastError => null;
        public OrderContext OrderContext { get; } = OrderContext.EphemeralSimulation("Bracket lifecycle fixture");
        public IExecutionSource Executions { get; } = new UnavailableExecutionSource();
        public event Action? Changed;

        public void Publish() => Changed?.Invoke();

        public void Accept(Order order)
        {
            order.ExchangeOrderId = order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Working.Add(order);
            Publish();
        }

        public void AcknowledgeCancel(Order order)
        {
            order.State = OrderStates.Done;
            order.Status = OrderStatus.Canceled;
            Working.Remove(order);
            Publish();
        }

        public void Fill(Order order, decimal quantity, decimal? marketPrice = null, bool updatePosition = true)
        {
            bool alreadyCanceled = order.Status == OrderStatus.Canceled;
            order.Unfilled -= quantity;
            order.Status = alreadyCanceled ? OrderStatus.Canceled : order.Unfilled == 0 ? OrderStatus.Filled : OrderStatus.PartlyFilled;
            order.State = alreadyCanceled || order.Unfilled == 0 ? OrderStates.Done : OrderStates.Active;
            if (alreadyCanceled || order.Unfilled == 0) Working.Remove(order);
            if (updatePosition)
            {
                decimal before = Position.Volume;
                decimal signed = order.Direction == OrderDirections.Buy ? quantity : -quantity;
                if (before == 0) Position.AveragePrice = BracketManager.PriceOf(order);
                else if (Math.Sign(before) == Math.Sign(signed))
                    Position.AveragePrice = (Position.AveragePrice * Math.Abs(before) + BracketManager.PriceOf(order) * quantity) / (Math.Abs(before) + quantity);
                Position.Volume += signed;
            }
            LastPrice = marketPrice ?? BracketManager.PriceOf(order);
            Publish();
        }

        public Order Submit(PreparedOrderIntent intent)
        {
            var request = intent.Request;
            var type = request.Type;
            var direction = request.Direction;
            var quantity = request.Quantity;
            var price = request.Price;
            var comment = request.Comment;
            var reduceOnly = request.ReduceOnly;
            var order = new Order
            {
                Type = type, Direction = direction, QuantityToFill = quantity, Unfilled = quantity,
                Price = type == OrderTypes.Limit ? price : 0, TriggerPrice = type == OrderTypes.Stop ? price : 0,
                State = OrderStates.Active, Status = OrderStatus.Placed, Comment = comment, ReduceOnly = reduceOnly,
            };
            OrderContext.BindOrder(order, intent);
            Submitted.Add(order);
            if (!DelayAcceptance) Accept(order);
            else Publish();
            return order;
        }

        public virtual Order Replace(Order order, PreparedOrderIntent intent) => throw new NotSupportedException();
        public void Cancel(Order order)
        {
            if (order.ExchangeOrderId.Length == 0) return; // No receipt is possible until the placement is accepted.
            CancelRequests.Add(order);
            if (ImmediateCancellation) AcknowledgeCancel(order);
            else Publish(); // A cancel receipt is not a terminal order acknowledgment.
        }
        public void CancelAt(decimal price) => throw new NotSupportedException();
        public void CancelAll() => throw new NotSupportedException();
    }
}
