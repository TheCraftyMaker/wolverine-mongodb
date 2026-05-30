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
                    await _parent.RecoverOrphanedIncomingAsync(_runtime, _combined.Token);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellation.Cancel();

        _recoveryTask?.SafeDispose();
        _scheduledJob?.SafeDispose();

        Status = AgentStatus.Stopped;

        return Task.CompletedTask;
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }

    public string Description => $"Wolverine MongoDB durability agent for {Uri} — recovers persisted inbox/outbox messages and runs scheduled jobs against the MongoDB message store.";
}
