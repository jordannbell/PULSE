using Pulse.Domain;

namespace Pulse.Tests;

public class MonitoredServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MonitoredService NewService(int interval = 60) =>
        MonitoredService.Create(Guid.NewGuid(), "  Website  ", "https://example.com/", interval, 10, 200, true, Now);

    private static CheckResult Ok(MonitoredService s, int secondsLater = 0) =>
        CheckResult.Succeeded(s.Id, Now.AddSeconds(secondsLater), 200, 30);

    private static CheckResult Fail(MonitoredService s, int secondsLater = 0) =>
        CheckResult.Failed(s.Id, Now.AddSeconds(secondsLater), 30, "boom", 500);

    [Fact]
    public void A_new_service_trims_its_name_and_starts_unknown()
    {
        var service = NewService();

        Assert.Equal("Website", service.Name);
        Assert.Equal(ServiceState.Unknown, service.State);
        Assert.Null(service.LastCheckedAt);
    }

    [Theory]
    [InlineData(29, 10)] // interval too short
    [InlineData(86_401, 10)] // interval too long
    [InlineData(60, 0)] // timeout too short
    [InlineData(60, 31)] // timeout too long
    [InlineData(40, 41)] // timeout longer than the interval
    public void Create_refuses_out_of_range_timings(int interval, int timeout)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => MonitoredService.Create(Guid.NewGuid(), "Site", "https://example.com/", interval, timeout, 200, true, Now));
    }

    [Fact]
    public void A_service_never_checked_is_due()
    {
        Assert.True(NewService().IsDue(Now));
    }

    [Fact]
    public void A_service_is_due_once_its_interval_has_passed()
    {
        var service = NewService(interval: 60);
        service.ApplyCheck(Ok(service), 3);

        Assert.False(service.IsDue(Now.AddSeconds(59)));
        Assert.True(service.IsDue(Now.AddSeconds(60)));
    }

    [Fact]
    public void The_first_success_brings_an_unknown_service_up_without_an_incident()
    {
        var service = NewService();

        var transition = service.ApplyCheck(Ok(service), 3);

        Assert.Equal(StateTransition.None, transition);
        Assert.Equal(ServiceState.Up, service.State);
    }

    [Fact]
    public void Failures_below_the_threshold_do_not_open_an_incident()
    {
        var service = NewService();
        service.ApplyCheck(Ok(service), 3);

        Assert.Equal(StateTransition.None, service.ApplyCheck(Fail(service), 3));
        Assert.Equal(StateTransition.None, service.ApplyCheck(Fail(service), 3));
        Assert.Equal(ServiceState.Up, service.State);
        Assert.Equal(2, service.ConsecutiveFailures);
    }

    [Fact]
    public void The_failure_that_reaches_the_threshold_opens_exactly_one_incident()
    {
        var service = NewService();
        service.ApplyCheck(Fail(service), 3);
        service.ApplyCheck(Fail(service), 3);

        Assert.Equal(StateTransition.IncidentOpened, service.ApplyCheck(Fail(service), 3));
        Assert.Equal(ServiceState.Down, service.State);

        // Further failures extend the same incident instead of opening new ones.
        Assert.Equal(StateTransition.None, service.ApplyCheck(Fail(service), 3));
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        var service = NewService();
        service.ApplyCheck(Fail(service), 3);
        service.ApplyCheck(Fail(service), 3);
        service.ApplyCheck(Ok(service), 3);

        Assert.Equal(StateTransition.None, service.ApplyCheck(Fail(service), 3));
        Assert.Equal(StateTransition.None, service.ApplyCheck(Fail(service), 3));
        Assert.Equal(ServiceState.Up, service.State);
    }

    [Fact]
    public void A_success_after_an_outage_resolves_the_incident()
    {
        var service = NewService();
        for (var i = 0; i < 3; i++)
        {
            service.ApplyCheck(Fail(service), 3);
        }

        Assert.Equal(StateTransition.IncidentResolved, service.ApplyCheck(Ok(service), 3));
        Assert.Equal(ServiceState.Up, service.State);
        Assert.Equal(0, service.ConsecutiveFailures);
    }

    [Fact]
    public void A_threshold_of_one_opens_an_incident_on_the_first_failure()
    {
        var service = NewService();

        Assert.Equal(StateTransition.IncidentOpened, service.ApplyCheck(Fail(service), 1));
    }

    [Fact]
    public void An_incident_cannot_be_resolved_twice()
    {
        var incident = Incident.Open(Guid.NewGuid(), Now, "down");
        incident.Resolve(Now.AddMinutes(5));

        Assert.Throws<InvalidOperationException>(() => incident.Resolve(Now.AddMinutes(6)));
    }

    [Fact]
    public void A_resolution_time_before_the_start_is_clamped_to_the_start()
    {
        var incident = Incident.Open(Guid.NewGuid(), Now, "down");

        incident.Resolve(Now.AddMinutes(-5));

        Assert.Equal(Now, incident.ResolvedAt);
    }

    [Fact]
    public void A_long_error_message_is_truncated()
    {
        var check = CheckResult.Failed(Guid.NewGuid(), Now, 10, new string('x', 1000));

        Assert.Equal(CheckResult.ErrorMaxLength, check.Error!.Length);
    }
}
