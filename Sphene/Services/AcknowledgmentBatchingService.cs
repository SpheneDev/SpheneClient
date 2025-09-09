using Microsoft.Extensions.Logging;
using Sphene.API.Data;
using Sphene.Services.Events;
using Sphene.Services.Mediator;
using Sphene.SpheneConfiguration.Models;
using System.Collections.Concurrent;
using System.Timers;

namespace Sphene.Services;

// Batching service for acknowledgments to improve performance
public class AcknowledgmentBatchingService : IDisposable
{
    private readonly ILogger<AcknowledgmentBatchingService> _logger;
    private readonly SpheneMediator _mediator;
    private readonly MessageService _messageService;
    private readonly ConcurrentDictionary<string, BatchedAcknowledgment> _pendingBatches = new();
    private readonly System.Timers.Timer _batchTimer;
    private readonly TimeSpan _batchWindow = TimeSpan.FromMilliseconds(500); // 500ms batch window
    private readonly int _maxBatchSize = 10; // Maximum items per batch
    private bool _disposed = false;

    public AcknowledgmentBatchingService(
        ILogger<AcknowledgmentBatchingService> logger,
        SpheneMediator mediator,
        MessageService messageService)
    {
        _logger = logger;
        _mediator = mediator;
        _messageService = messageService;
        
        // Timer to process batches periodically
        _batchTimer = new System.Timers.Timer(100); // Check every 100ms
        _batchTimer.Elapsed += ProcessBatches;
        _batchTimer.AutoReset = true;
        _batchTimer.Start();
    }

    // Add an acknowledgment to a batch
    public void AddToBatch(string batchKey, UserData user, string acknowledgmentId, Action<List<UserData>, string> processBatch)
    {
        if (_disposed) return;

        var batch = _pendingBatches.AddOrUpdate(batchKey,
            new BatchedAcknowledgment
            {
                BatchKey = batchKey,
                Users = new List<UserData> { user },
                AcknowledgmentId = acknowledgmentId,
                ProcessBatch = processBatch,
                CreatedAt = DateTime.UtcNow,
                LastUpdated = DateTime.UtcNow
            },
            (key, existing) =>
            {
                existing.Users.Add(user);
                existing.LastUpdated = DateTime.UtcNow;
                return existing;
            });

        _logger.LogDebug("Added user {user} to batch {batchKey}, total users: {count}", 
            user.AliasOrUID, batchKey, batch.Users.Count);

        // Process immediately if batch is full
        if (batch.Users.Count >= _maxBatchSize)
        {
            ProcessBatch(batchKey, batch);
        }
    }

    // Process batches that are ready
    private void ProcessBatches(object? sender, ElapsedEventArgs e)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var batchesToProcess = new List<(string key, BatchedAcknowledgment batch)>();

        foreach (var kvp in _pendingBatches)
        {
            var batch = kvp.Value;
            var age = now - batch.CreatedAt;
            
            // Process if batch window has elapsed or batch is full
            if (age >= _batchWindow || batch.Users.Count >= _maxBatchSize)
            {
                batchesToProcess.Add((kvp.Key, batch));
            }
        }

        foreach (var (key, batch) in batchesToProcess)
        {
            ProcessBatch(key, batch);
        }
    }

    // Process a single batch
    private void ProcessBatch(string batchKey, BatchedAcknowledgment batch)
    {
        if (_disposed) return;

        if (_pendingBatches.TryRemove(batchKey, out _))
        {
            try
            {
                _logger.LogInformation("Processing batch {batchKey} with {count} users", 
                    batchKey, batch.Users.Count);

                // Execute the batch processing function
                batch.ProcessBatch(batch.Users, batch.AcknowledgmentId);

                // Add batch processing notification
                _messageService.AddTaggedMessage(
                    $"batch_processed_{batchKey}",
                    $"Processed acknowledgment batch with {batch.Users.Count} users",
                    NotificationType.Info,
                    "Batch Processed",
                    TimeSpan.FromSeconds(2)
                );

                // Publish batch processing event
                _mediator.Publish(new AcknowledgmentBatchProcessedMessage(
                    batchKey,
                    batch.Users,
                    batch.AcknowledgmentId,
                    DateTime.UtcNow
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch {batchKey}", batchKey);
                
                // Add error notification
                _messageService.AddTaggedMessage(
                    $"batch_error_{batchKey}",
                    $"Error processing acknowledgment batch: {ex.Message}",
                    NotificationType.Error,
                    "Batch Error",
                    TimeSpan.FromSeconds(5)
                );
            }
        }
    }

    // Get current batch statistics
    public BatchStatistics GetStatistics()
    {
        var now = DateTime.UtcNow;
        var totalBatches = _pendingBatches.Count;
        var totalUsers = _pendingBatches.Values.Sum(b => b.Users.Count);
        var oldestBatch = _pendingBatches.Values.MinBy(b => b.CreatedAt);
        var oldestAge = oldestBatch != null ? now - oldestBatch.CreatedAt : TimeSpan.Zero;

        return new BatchStatistics
        {
            PendingBatches = totalBatches,
            TotalPendingUsers = totalUsers,
            OldestBatchAge = oldestAge,
            BatchWindow = _batchWindow,
            MaxBatchSize = _maxBatchSize
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _batchTimer?.Stop();
        _batchTimer?.Dispose();

        // Process any remaining batches
        foreach (var kvp in _pendingBatches)
        {
            ProcessBatch(kvp.Key, kvp.Value);
        }
        
        _pendingBatches.Clear();
    }
}

// Batched acknowledgment data structure
public class BatchedAcknowledgment
{
    public string BatchKey { get; set; } = string.Empty;
    public List<UserData> Users { get; set; } = new();
    public string AcknowledgmentId { get; set; } = string.Empty;
    public Action<List<UserData>, string> ProcessBatch { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdated { get; set; }
}

// Batch statistics for monitoring
public class BatchStatistics
{
    public int PendingBatches { get; set; }
    public int TotalPendingUsers { get; set; }
    public TimeSpan OldestBatchAge { get; set; }
    public TimeSpan BatchWindow { get; set; }
    public int MaxBatchSize { get; set; }
}

// Event for when a batch is processed
public record AcknowledgmentBatchProcessedMessage(
    string BatchKey,
    List<UserData> Users,
    string AcknowledgmentId,
    DateTime Timestamp
) : MessageBase;