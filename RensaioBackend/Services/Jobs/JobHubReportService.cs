using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RensaioBackend.Extensions;
using RensaioBackend.Hubs;
using RensaioBackend.Models;
using RensaioBackend.Models.Database;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Jobs.Models;
using RensaioBackend.Services.Jobs.Report;

namespace RensaioBackend.Services.Jobs;

/// <summary>
/// Unified service for reporting both job state changes and progress updates via SignalR
/// </summary>
public class JobHubReportService : IReportProgress
{
    /// <summary>
    /// Ceiling for a single progress broadcast. <see cref="IHubContext{THub}.Clients"/> send to
    /// "All" has no built-in timeout or cancellation; a single wedged client (dead socket, full
    /// pipe, suspended tab) would otherwise block the caller forever with no log. Progress
    /// reporting must never be able to stall a scan it is merely observing.
    /// </summary>
    private static readonly TimeSpan ProgressSendTimeout = TimeSpan.FromSeconds(5);

    private readonly IHubContext<ProgressHub> _hub;
    private readonly ILogger<JobHubReportService> _logger;
    public ThumbCacheService ThumbService { get; }
    public JobHubReportService(IHubContext<ProgressHub> hub, ThumbCacheService thumbService, ILogger<JobHubReportService> logger)
    {
        _hub = hub;
        ThumbService = thumbService;
        _logger = logger;
    }

    /// <summary>
    /// Reports job state changes (queued, running, completed, failed) to SignalR clients
    /// </summary>
    /// <param name="state">The job queue state</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    /*
    public Task ReportJobAsync(Enqueue state, CancellationToken token = default)
    {
        return _hub.Clients.All.SendAsync("Jobs", state.ToJobState(), token);
    }
    */

    /// <summary>
    /// Reports job progress updates to SignalR clients
    /// </summary>
    /// <param name="state">The progress state</param>
    /// <returns>Task representing the async operation</returns>
    public async Task ReportProgressAsync(ProgressState state)
    {
        try
        {
            // Bounded broadcast: a slow/ wedged client must not block the writer.
            await _hub.Clients.All.SendAsync("Progress", state)
                .WaitAsync(ProgressSendTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Progress broadcast for job {JobId} exceeded {Seconds}s; a SignalR client is not draining. Continuing.",
                state?.Id, ProgressSendTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            // Never let a transport hiccup surface as a job/scan failure.
            _logger.LogDebug(ex, "Progress broadcast failed for job {JobId}.", state?.Id);
        }
    }

    /// <summary>
    /// Creates a progress reporter for a specific job
    /// </summary>
    /// <param name="job">Job information</param>
    /// <returns>Progress reporter instance</returns>
    public ProgressReporter CreateReporter(JobInfo job)
    {
        return new ProgressReporter(this, job);
    }
}