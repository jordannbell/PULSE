using Pulse.Contracts;

namespace Pulse.Application.Alerts;

/// <summary>Lets an owner see which alert channels are set up and check that each one works.</summary>
public sealed class AlertTester(IAlertChannelFactory channels, TimeProvider clock)
{
    public async Task<IReadOnlyList<AlertChannelDto>> ListAsync(Guid organizationId, CancellationToken cancellationToken) =>
        [.. (await channels.CreateAsync(organizationId, cancellationToken)).Select(c => new AlertChannelDto(c.Name))];

    /// <summary>Sends one test message through each channel of the organization and reports each outcome.</summary>
    public async Task<IReadOnlyList<AlertTestResultDto>> TestAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var message = AlertEvent.TestMessage(organizationId, clock.GetUtcNow());
        var results = new List<AlertTestResultDto>();
        foreach (var channel in await channels.CreateAsync(organizationId, cancellationToken))
        {
            try
            {
                await channel.SendAsync(message, cancellationToken);
                results.Add(new AlertTestResultDto(channel.Name, true, null));
            }
            catch (AlertDeliveryException ex)
            {
                results.Add(new AlertTestResultDto(channel.Name, false, ex.Message));
            }
        }

        return results;
    }
}
