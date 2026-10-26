namespace Pulse.Application.Alerts;

/// <summary>A way to tell an organization's owner something: an e-mail, a webhook...</summary>
public interface IAlertChannel
{
    /// <summary>Stable name shown to the owner ("Webhook", "E-mail").</summary>
    string Name { get; }

    /// <summary>Delivers the alert, or throws <see cref="AlertDeliveryException"/> with a message safe to show.</summary>
    Task SendAsync(AlertEvent alert, CancellationToken cancellationToken);
}

/// <summary>Builds the channels an organization has set up, ready to deliver.</summary>
public interface IAlertChannelFactory
{
    Task<IReadOnlyList<IAlertChannel>> CreateAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Where the checks hand their events. Never blocks and never throws: delivery happens elsewhere.</summary>
public interface IAlertPublisher
{
    void Publish(AlertEvent alert);
}

/// <summary>A delivery failed. The message is short, written by hand, and contains no secret.</summary>
public sealed class AlertDeliveryException : Exception
{
    public AlertDeliveryException(string message)
        : base(message)
    {
    }

    public AlertDeliveryException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public AlertDeliveryException()
    {
    }
}
