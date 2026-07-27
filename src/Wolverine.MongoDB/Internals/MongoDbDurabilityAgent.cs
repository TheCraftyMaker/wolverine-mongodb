using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public class MongoDbDurabilityAgent : IAgent
{
    private readonly IWolverineRuntime _runtime;
    private readonly MongoDbMessageStore _parent;
    private readonly DurabilitySettings _settings;
    private readonly ILogger<MongoDbDurabilityAgent> _logger;

    private Task? _recoveryTask;
    private Task? _scheduledJob;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _combined;

    public MongoDbDurabilityAgent(IWolverineRuntime runtime, MongoDbMessageStore parent)
    {
        _runtime = runtime;
        _parent = parent;
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{PersistenceConstants.AgentScheme}://mongodb/durability");

        _logger = runtime.LoggerFactory.CreateLogger<MongoDbDurabilityAgent>();

        _combined = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation, _cancellation.Token);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartTimers();
        Status = AgentStatus.Running;
        return Task.CompletedTask;
    }

    internal void StartTimers()
    {
        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTask = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            while (!_combined.IsCancellationRequested)
            {
                try
                {
                    if (_settings.Mode != DurabilityMode.Solo)
                    {
                        await _parent.ReleaseDeadNodeOwnershipAsync(_combined.Token);
                    }
                    await _parent.RecoverOrphanedIncomingAsync(_runtime, _combined.Token);
                    await _parent.RecoverOrphanedOutgoingAsync(_runtime, _combined.Token);
                    await _parent.ReplayDeadLettersAsync(_combined.Token);
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _logger.LogError(e, "Recovery loop tick failed");
                }

                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);

        _scheduledJob = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            while (!_combined.IsCancellationRequested)
            {
                try
                {
                    await runScheduledJobs();
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _logger.LogError(e, "Scheduled-job loop tick failed");
                }

                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);
    }

    private async Task runScheduledJobs()
    {
        try
        {
            if (!await _parent.TryAttainScheduledJobLockAsync(_combined.Token))
            {
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain the scheduled job lock");
            return;
        }

        try
        {
            await _parent.PublishDueScheduledMessagesAsync(_runtime, _combined.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process scheduled messages");
        }
        finally
        {
            try
            {
                await _parent.ReleaseScheduledJobLockAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release the scheduled job lock");
            }
        }
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private int _stopping;

    /// <summary>
    /// Cancels, then awaits both loops (bounded) before disposing their CancellationTokenSources.
    /// Once this returns, no new recovery-tick claim writes will be issued, so the node document
    /// delete that follows (NodeAgentController.StopAsync -> stopAllAgentsAsync,
    /// external/wolverine/src/Wolverine/Runtime/Agents/NodeAgentController.cs:120,136) is safe.
    ///
    /// This shrinks but cannot close the shutdown-ordering window: WolverineRuntime.HostService
    /// releases this node's ownership (ReleaseAllOwnershipAsync,
    /// external/wolverine/src/Wolverine/Runtime/WolverineRuntime.HostService.cs:390) BEFORE
    /// tearing down agents (:412), so a tick already in flight at :390 can still re-claim
    /// envelopes before it observes cancellation here. That ordering belongs to Wolverine core,
    /// not this provider — document it, don't try to compensate for it.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;

        try
        {
            await _cancellation.CancelAsync();

            var loops = new[] { _recoveryTask, _scheduledJob }.Where(t => t is not null).Select(t => t!).ToArray();
            if (loops.Length > 0)
            {
                try
                {
                    await Task.WhenAll(loops).WaitAsync(StopTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "MongoDB durability loops did not observe cancellation within {Timeout}; " +
                        "continuing shutdown. In-flight recovery writes may still be in progress.",
                        StopTimeout);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the loops await PeriodicTimer/Task.Delay on the linked token, so
                    // they complete in the Canceled state. Also covers an aborting caller's token.
                }
            }
        }
        finally
        {
            // Dispose the linked source before the source it links, and only after the loops
            // have stopped touching _combined.Token / _combined.IsCancellationRequested.
            _combined.Dispose();
            _cancellation.Dispose();
            Status = AgentStatus.Stopped;
        }
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }

    public string Description => $"Wolverine MongoDB durability agent for {Uri} — recovers persisted inbox/outbox messages and runs scheduled jobs against the MongoDB message store.";
}
