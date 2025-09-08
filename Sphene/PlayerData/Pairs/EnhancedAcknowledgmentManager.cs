using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.Services.Mediator;
using Sphene.WebAPI;
using Sphene.Utils;
using Sphene.WebAPI;

namespace Sphene.PlayerData.Pairs;

/// <summary>
/// Enhanced acknowledgment manager with timeout management, batching, priority system, and error handling
/// </summary>
public class EnhancedAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    // Removed DalamudUtilService dependency - not needed for acknowledgment management
    private readonly AcknowledgmentConfiguration _config;
    private readonly AcknowledgmentMetrics _metrics = new();
    
    // Priority-based queues
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _highPriorityQueue = new();
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _mediumPriorityQueue = new();
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _lowPriorityQueue = new();
    
    // Pending acknowledgments with timeout tracking
    private readonly ConcurrentDictionary<string, PendingAcknowledgment> _pendingAcknowledgments = new();
    
    // Batch processing
    private readonly ConcurrentDictionary<AcknowledgmentPriority, BatchAcknowledgmentDto> _currentBatches = new();
    private readonly Timer _batchProcessingTimer;
    private readonly Timer _timeoutCheckTimer;
    private readonly Timer _retryTimer;
    
    // Caching
    private readonly ConcurrentDictionary<string, CachedAcknowledgment> _acknowledgmentCache = new();
    private readonly Timer _cacheCleanupTimer;
    
    // Thread safety
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    public EnhancedAcknowledgmentManager(ILogger<EnhancedAcknowledgmentManager> logger, 
        ApiController apiController, 
        // DalamudUtilService dalamudUtil, // Removed - not needed
        SpheneMediator mediator,
        AcknowledgmentConfiguration config) : base(logger, mediator)
    {
        _apiController = apiController;
        // _dalamudUtil = dalamudUtil; // Removed - not needed
        _config = config;
        _config.Validate();
        
        // Initialize timers
        _batchProcessingTimer = new Timer(ProcessBatches, null, 
            TimeSpan.FromMilliseconds(_config.BatchTimeoutMs / 2), 
            TimeSpan.FromMilliseconds(_config.BatchTimeoutMs / 2));
            
        _timeoutCheckTimer = new Timer(CheckTimeouts, null, 
            TimeSpan.FromSeconds(5), 
            TimeSpan.FromSeconds(5));
            
        _retryTimer = new Timer(ProcessRetries, null, 
            TimeSpan.FromSeconds(10), 
            TimeSpan.FromSeconds(10));
            
        _cacheCleanupTimer = new Timer(CleanupCache, null, 
            TimeSpan.FromMinutes(_config.CacheExpirationMinutes / 2), 
            TimeSpan.FromMinutes(_config.CacheExpirationMinutes / 2));
        
        // Subscribe to mediator messages
        Mediator.Subscribe<SendCharacterDataAcknowledgmentMessage>(this, (msg) => _ = HandleSendAcknowledgment(msg));
    }
    
    /// <summary>
    /// Sends an acknowledgment with priority and error handling
    /// </summary>
    public async Task<bool> SendAcknowledgmentAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        try
        {
            // Check cache first
            var cacheKey = $"{acknowledgment.User.UID}_{acknowledgment.AcknowledgmentId}";
            if (_acknowledgmentCache.ContainsKey(cacheKey))
            {
                Logger.LogDebug("Acknowledgment already processed (cached): {ackId}", acknowledgment.AcknowledgmentId);
                return true;
            }
            
            // Add to appropriate priority queue
            if (_config.EnableBatching)
            {
                await AddToBatchAsync(acknowledgment);
                return true;
            }
            else
            {
                return await SendSingleAcknowledgmentAsync(acknowledgment);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send acknowledgment {ackId}", acknowledgment.AcknowledgmentId);
            acknowledgment.MarkAsFailed(AcknowledgmentErrorCode.NetworkError, ex.Message);
            
            if (_config.EnableAutoRetry && acknowledgment.RetryCount < _config.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(acknowledgment);
            }
            
            return false;
        }
    }
    
    /// <summary>
    /// Adds acknowledgment to batch for processing
    /// </summary>
    private async Task AddToBatchAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        await _batchSemaphore.WaitAsync(_cancellationTokenSource.Token);
        try
        {
            if (!_currentBatches.TryGetValue(acknowledgment.Priority, out var batch))
            {
                batch = new BatchAcknowledgmentDto { Priority = acknowledgment.Priority };
                _currentBatches[acknowledgment.Priority] = batch;
            }
            
            batch.AddAcknowledgment(acknowledgment);
            
            // Check if batch is ready to send
            if (batch.IsReadyToSend(_config.MaxBatchSize, TimeSpan.FromMilliseconds(_config.BatchTimeoutMs)))
            {
                await ProcessBatchAsync(batch);
                _currentBatches.TryRemove(acknowledgment.Priority, out _);
            }
        }
        finally
        {
            _batchSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Processes a batch of acknowledgments
    /// </summary>
    private async Task ProcessBatchAsync(BatchAcknowledgmentDto batch)
    {
        try
        {
            Logger.LogDebug("Processing batch {batchId} with {count} acknowledgments", batch.BatchId, batch.Count);
            
            var tasks = batch.Acknowledgments.Select(SendSingleAcknowledgmentAsync);
            var results = await Task.WhenAll(tasks);
            
            batch.MarkAsProcessed();
            
            var successCount = results.Count(r => r);
            Logger.LogInformation("Batch {batchId} processed: {success}/{total} successful", 
                batch.BatchId, successCount, batch.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process batch {batchId}", batch.BatchId);
        }
    }
    
    /// <summary>
    /// Sends a single acknowledgment
    /// </summary>
    private async Task<bool> SendSingleAcknowledgmentAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(_config.GetTimeoutForPriority(acknowledgment.Priority));
        
        try
        {
            // Add to pending acknowledgments for timeout tracking
            var pendingAck = new PendingAcknowledgment(acknowledgment, DateTime.UtcNow.Add(timeout));
            _pendingAcknowledgments[acknowledgment.AcknowledgmentId] = pendingAck;
            
            // Convert to legacy DTO for API compatibility
            var legacyDto = new CharacterDataAcknowledgmentDto(acknowledgment.User, acknowledgment.AcknowledgmentId)
            {
                Success = acknowledgment.Success,
                AcknowledgedAt = acknowledgment.AcknowledgedAt
            };
            
            // Send acknowledgment
            await _apiController.UserSendCharacterDataAcknowledgment(legacyDto);
            
            // Record success
            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _metrics.RecordSuccess(acknowledgment.Priority, responseTime);
            _metrics.RecordSent();
            
            // Cache the acknowledgment
            var cacheKey = $"{acknowledgment.User.UID}_{acknowledgment.AcknowledgmentId}";
            _acknowledgmentCache[cacheKey] = new CachedAcknowledgment(acknowledgment, DateTime.UtcNow);
            
            // Remove from pending
            _pendingAcknowledgments.TryRemove(acknowledgment.AcknowledgmentId, out _);
            
            Logger.LogDebug("Successfully sent acknowledgment {ackId} in {ms}ms", 
                acknowledgment.AcknowledgmentId, responseTime);
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send acknowledgment {ackId}", acknowledgment.AcknowledgmentId);
            
            var errorCode = DetermineErrorCode(ex);
            acknowledgment.MarkAsFailed(errorCode, ex.Message);
            _metrics.RecordFailure(acknowledgment.Priority, errorCode);
            
            // Remove from pending
            _pendingAcknowledgments.TryRemove(acknowledgment.AcknowledgmentId, out _);
            
            return false;
        }
    }
    
    /// <summary>
    /// Schedules a retry for a failed acknowledgment
    /// </summary>
    private async Task ScheduleRetryAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var delay = CalculateRetryDelay(acknowledgment.RetryCount);
        acknowledgment.IncrementRetry(delay);
        
        Logger.LogInformation("Scheduling retry {retry}/{max} for acknowledgment {ackId} in {delay}ms", 
            acknowledgment.RetryCount, _config.MaxRetryAttempts, acknowledgment.AcknowledgmentId, delay.TotalMilliseconds);
        
        // Add back to appropriate queue for retry
        AddToQueue(acknowledgment);
        _metrics.RecordRetry();
    }
    
    /// <summary>
    /// Calculates retry delay using exponential backoff
    /// </summary>
    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        var delay = Math.Min(
            _config.BaseRetryDelayMs * Math.Pow(2, retryCount),
            _config.MaxRetryDelayMs
        );
        
        // Add jitter to prevent thundering herd
        var jitter = new Random().NextDouble() * 0.1 * delay;
        return TimeSpan.FromMilliseconds(delay + jitter);
    }
    
    /// <summary>
    /// Adds acknowledgment to appropriate priority queue
    /// </summary>
    private void AddToQueue(EnhancedAcknowledgmentDto acknowledgment)
    {
        switch (acknowledgment.Priority)
        {
            case AcknowledgmentPriority.High:
                _highPriorityQueue.Enqueue(acknowledgment);
                break;
            case AcknowledgmentPriority.Medium:
                _mediumPriorityQueue.Enqueue(acknowledgment);
                break;
            case AcknowledgmentPriority.Low:
                _lowPriorityQueue.Enqueue(acknowledgment);
                break;
        }
    }
    
    /// <summary>
    /// Determines error code from exception
    /// </summary>
    private AcknowledgmentErrorCode DetermineErrorCode(Exception ex)
    {
        return ex switch
        {
            TimeoutException => AcknowledgmentErrorCode.Timeout,
            UnauthorizedAccessException => AcknowledgmentErrorCode.AuthenticationFailed,
            ArgumentException => AcknowledgmentErrorCode.InvalidData,
            _ => AcknowledgmentErrorCode.NetworkError
        };
    }
    
    /// <summary>
    /// Timer callback to process batches
    /// </summary>
    private void ProcessBatches(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _batchSemaphore.WaitAsync(_cancellationTokenSource.Token);
                try
                {
                    var batchesToProcess = new List<(AcknowledgmentPriority, BatchAcknowledgmentDto)>();
                    
                    foreach (var kvp in _currentBatches)
                    {
                        var batch = kvp.Value;
                        if (batch.IsReadyToSend(_config.MaxBatchSize, TimeSpan.FromMilliseconds(_config.BatchTimeoutMs)))
                        {
                            batchesToProcess.Add((kvp.Key, batch));
                        }
                    }
                    
                    foreach (var (priority, batch) in batchesToProcess)
                    {
                        await ProcessBatchAsync(batch);
                        _currentBatches.TryRemove(priority, out _);
                    }
                }
                finally
                {
                    _batchSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in batch processing timer");
            }
        });
    }
    
    /// <summary>
    /// Timer callback to check for timeouts
    /// </summary>
    private void CheckTimeouts(object? state)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                var timedOutAcks = new List<string>();
                
                foreach (var kvp in _pendingAcknowledgments)
                {
                    if (now >= kvp.Value.TimeoutAt)
                    {
                        timedOutAcks.Add(kvp.Key);
                    }
                }
                
                foreach (var ackId in timedOutAcks)
                {
                    if (_pendingAcknowledgments.TryRemove(ackId, out var pendingAck))
                    {
                        Logger.LogWarning("Acknowledgment {ackId} timed out", ackId);
                        pendingAck.Acknowledgment.MarkAsFailed(AcknowledgmentErrorCode.Timeout);
                        _metrics.RecordFailure(pendingAck.Acknowledgment.Priority, AcknowledgmentErrorCode.Timeout);
                        
                        if (_config.EnableAutoRetry && pendingAck.Acknowledgment.RetryCount < _config.MaxRetryAttempts)
                        {
                            _ = ScheduleRetryAsync(pendingAck.Acknowledgment);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in timeout check timer");
            }
        });
    }
    
    /// <summary>
    /// Timer callback to process retries
    /// </summary>
    private void ProcessRetries(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var retryQueues = new[] { _highPriorityQueue, _mediumPriorityQueue, _lowPriorityQueue };
                
                foreach (var queue in retryQueues)
                {
                    var retryAcks = new List<EnhancedAcknowledgmentDto>();
                    
                    // Collect ready retries
                    while (queue.TryPeek(out var ack) && ack.IsReadyForRetry())
                    {
                        if (queue.TryDequeue(out var dequeuedAck))
                        {
                            retryAcks.Add(dequeuedAck);
                        }
                    }
                    
                    // Process retries
                    foreach (var ack in retryAcks)
                    {
                        await SendAcknowledgmentAsync(ack);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in retry processing timer");
            }
        });
    }
    
    /// <summary>
    /// Timer callback to cleanup cache
    /// </summary>
    private void CleanupCache(object? state)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                var expiredKeys = new List<string>();
                
                foreach (var kvp in _acknowledgmentCache)
                {
                    if (now - kvp.Value.CachedAt > TimeSpan.FromMinutes(_config.CacheExpirationMinutes))
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }
                
                foreach (var key in expiredKeys)
                {
                    _acknowledgmentCache.TryRemove(key, out _);
                }
                
                // Limit cache size
                if (_acknowledgmentCache.Count > _config.MaxCacheSize)
                {
                    var oldestEntries = _acknowledgmentCache
                        .OrderBy(kvp => kvp.Value.CachedAt)
                        .Take(_acknowledgmentCache.Count - _config.MaxCacheSize)
                        .Select(kvp => kvp.Key)
                        .ToList();
                        
                    foreach (var key in oldestEntries)
                    {
                        _acknowledgmentCache.TryRemove(key, out _);
                    }
                }
                
                Logger.LogDebug("Cache cleanup completed. Removed {expired} expired entries. Current size: {size}", 
                    expiredKeys.Count, _acknowledgmentCache.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in cache cleanup timer");
            }
        });
    }
    
    /// <summary>
    /// Handles send acknowledgment mediator message
    /// </summary>
    public async Task HandleSendAcknowledgment(SendCharacterDataAcknowledgmentMessage message)
    {
        var enhancedAck = new EnhancedAcknowledgmentDto(
            message.AcknowledgmentDto.User, 
            message.AcknowledgmentDto.AcknowledgmentId)
        {
            Success = message.AcknowledgmentDto.Success,
            AcknowledgedAt = message.AcknowledgmentDto.AcknowledgedAt
        };
        
        var success = await SendAcknowledgmentAsync(enhancedAck);
        
        // Publish UI refresh message to update acknowledgment status in UI
        if (success)
        {
            Mediator.Publish(new RefreshUiMessage());
            Logger.LogDebug("Published UI refresh message after successful acknowledgment {ackId}", 
                message.AcknowledgmentDto.AcknowledgmentId);
        }
    }
    
    /// <summary>
    /// Gets current metrics
    /// </summary>
    public AcknowledgmentMetrics GetMetrics() => _metrics;
    
    /// <summary>
    /// Gets current configuration
    /// </summary>
    public AcknowledgmentConfiguration GetConfiguration() => _config;
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellationTokenSource.Cancel();
            _batchProcessingTimer?.Dispose();
            _timeoutCheckTimer?.Dispose();
            _retryTimer?.Dispose();
            _cacheCleanupTimer?.Dispose();
            _batchSemaphore?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
        
        base.Dispose(disposing);
    }
}

/// <summary>
/// Represents a pending acknowledgment with timeout tracking
/// </summary>
internal class PendingAcknowledgment
{
    public EnhancedAcknowledgmentDto Acknowledgment { get; }
    public DateTime TimeoutAt { get; }
    
    public PendingAcknowledgment(EnhancedAcknowledgmentDto acknowledgment, DateTime timeoutAt)
    {
        Acknowledgment = acknowledgment;
        TimeoutAt = timeoutAt;
    }
}

/// <summary>
/// Represents a cached acknowledgment
/// </summary>
internal class CachedAcknowledgment
{
    public EnhancedAcknowledgmentDto Acknowledgment { get; }
    public DateTime CachedAt { get; }
    
    public CachedAcknowledgment(EnhancedAcknowledgmentDto acknowledgment, DateTime cachedAt)
    {
        Acknowledgment = acknowledgment;
        CachedAt = cachedAt;
    }
}