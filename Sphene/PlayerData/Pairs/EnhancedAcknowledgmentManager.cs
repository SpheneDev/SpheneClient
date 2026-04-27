using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sphene.API.Dto.User;
using Sphene.Services;
using Sphene.Services.Mediator;
using Sphene.Utils;
using Sphene.WebAPI;

namespace Sphene.PlayerData.Pairs;


/// Enhanced acknowledgment manager with timeout management, batching, priority system, and error handling
public class EnhancedAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly AcknowledgmentConfiguration _config;
    private readonly AcknowledgmentMetrics _metrics = new();
    
    // Priority-based queues
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _highPriorityQueue = new();
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _mediumPriorityQueue = new();
    private readonly ConcurrentQueue<EnhancedAcknowledgmentDto> _lowPriorityQueue = new();
    
    // Batch processing
    private readonly ConcurrentDictionary<AcknowledgmentPriority, BatchAcknowledgmentDto> _currentBatches = new();
    private readonly Timer _batchProcessingTimer;
    private readonly Timer _retryTimer;
    
    // Caching
    private readonly ConcurrentDictionary<string, CachedAcknowledgment> _acknowledgmentCache = new(StringComparer.Ordinal);
    private readonly Timer _cacheCleanupTimer;
    private readonly Timer _oldPendingCleanupTimer;
    
    // Session acknowledgment manager for cleanup
    private readonly SessionAcknowledgmentManager _sessionManager;

    private volatile bool _isInDuty;
    
    // Thread safety
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private static readonly TimeSpan DefaultOldPendingMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DutyOldPendingMaxAge = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DefaultSessionMaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DutySessionMaxAge = TimeSpan.FromMinutes(10);
    
    public EnhancedAcknowledgmentManager(ILogger<EnhancedAcknowledgmentManager> logger, 
        ApiController apiController, 
        DalamudUtilService dalamudUtilService,
        SpheneMediator mediator,
        AcknowledgmentConfiguration config,
        SessionAcknowledgmentManager sessionManager) : base(logger, mediator)
    {
        _apiController = apiController;
        _config = config;
        _sessionManager = sessionManager;
        _config.Validate();
        _isInDuty = dalamudUtilService.IsInDuty;
        
        // Initialize timers
        _batchProcessingTimer = new Timer(ProcessBatches, null, 
            TimeSpan.FromMilliseconds(_config.BatchTimeoutMs / 2), 
            TimeSpan.FromMilliseconds(_config.BatchTimeoutMs / 2));
            
        _retryTimer = new Timer(ProcessRetries, null, 
            TimeSpan.FromSeconds(10), 
            TimeSpan.FromSeconds(10));
            
        _cacheCleanupTimer = new Timer(CleanupCache, null, 
            TimeSpan.FromMinutes(_config.CacheExpirationMinutes / 2), 
            TimeSpan.FromMinutes(_config.CacheExpirationMinutes / 2));
            
        // Add cleanup timer for old pending acknowledgments
        _oldPendingCleanupTimer = new Timer(CleanupOldAcknowledgments, null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
        
        // Subscribe to mediator messages
        Mediator.Subscribe<SendCharacterDataAcknowledgmentMessage>(this, (msg) => _ = HandleSendAcknowledgment(msg));
        Mediator.Subscribe<DutyStartMessage>(this, _ =>
        {
            _isInDuty = true;
        });
        Mediator.Subscribe<DutyEndMessage>(this, _ =>
        {
            _isInDuty = false;
        });
    }
    
    
    /// Sends an acknowledgment with priority and error handling
    
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
                await AddToBatchAsync(acknowledgment).ConfigureAwait(false);
                return true;
            }
            else
            {
                return await SendSingleAcknowledgmentAsync(acknowledgment).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send acknowledgment {ackId}", acknowledgment.AcknowledgmentId);
            var errorCode = DetermineErrorCode(ex);
            acknowledgment.MarkAsFailed(errorCode, ex.Message);
            
            // Only retry if the error is retryable
            if (_config.EnableAutoRetry && acknowledgment.RetryCount < _config.MaxRetryAttempts && ShouldRetry(errorCode))
            {
                await ScheduleRetryAsync(acknowledgment).ConfigureAwait(false);
            }
            else if (!ShouldRetry(errorCode))
            {
                Logger.LogWarning("Non-retryable error for acknowledgment {ackId}: {errorCode}, not retrying", 
                    acknowledgment.AcknowledgmentId, errorCode);
            }
            
            return false;
        }
    }
    
    
    /// Adds acknowledgment to batch for processing
    
    private async Task AddToBatchAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        await _batchSemaphore.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
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
                await ProcessBatchAsync(batch).ConfigureAwait(false);
                _currentBatches.TryRemove(acknowledgment.Priority, out _);
            }
        }
        finally
        {
            _batchSemaphore.Release();
        }
    }
    
    
    /// Processes a batch of acknowledgments
    
    private async Task ProcessBatchAsync(BatchAcknowledgmentDto batch)
    {
        try
        {
            Logger.LogDebug("Processing batch {batchId} with {count} acknowledgments", batch.BatchId, batch.Count);
            
            var tasks = batch.Acknowledgments.Select(SendSingleAcknowledgmentAsync);
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            
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
    
    
    /// Sends a single acknowledgment
    
    private async Task<bool> SendSingleAcknowledgmentAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            // Check cache to avoid duplicate sends
            var cacheKey = $"{acknowledgment.User.UID}_{acknowledgment.AcknowledgmentId}";
            if (_acknowledgmentCache.TryGetValue(cacheKey, out _))
            {
                Logger.LogDebug("Acknowledgment {ackId} already cached for user {user}, skipping send", acknowledgment.AcknowledgmentId, acknowledgment.User.AliasOrUID);
                return true;
            }
            
            // Convert to legacy DTO for API compatibility
            var legacyDto = new CharacterDataAcknowledgmentDto(acknowledgment.User, acknowledgment.AcknowledgmentId)
            {
                Success = acknowledgment.Success,
                ErrorCode = (Sphene.API.Dto.User.AcknowledgmentErrorCode)acknowledgment.ErrorCode,
                ErrorMessage = acknowledgment.ErrorMessage,
                AcknowledgedAt = acknowledgment.AcknowledgedAt
            };
            
            // Send acknowledgment
            await _apiController.UserSendCharacterDataAcknowledgment(legacyDto).ConfigureAwait(false);
            
            // Record success
            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _metrics.RecordSuccess(acknowledgment.Priority, responseTime);
            _metrics.RecordSent();
            
            // Remove any existing cache entries for this user (only older acknowledgments, not newer ones)
            var userPrefix = $"{acknowledgment.User.UID}_";
            var currentTimestamp = DateTime.UtcNow;
            var keysToRemove = new List<string>();
            
            foreach (var kvp in _acknowledgmentCache)
            {
                if (kvp.Key.StartsWith(userPrefix, StringComparison.Ordinal) &&
                    !string.Equals(kvp.Key, $"{acknowledgment.User.UID}_{acknowledgment.AcknowledgmentId}", StringComparison.Ordinal) &&
                    kvp.Value.Timestamp < currentTimestamp)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            
            foreach (var keyToRemove in keysToRemove)
            {
                _acknowledgmentCache.TryRemove(keyToRemove, out _);
                Logger.LogDebug("Removed old cached acknowledgment for user {user}: {key}", acknowledgment.User.AliasOrUID, keyToRemove);
            }
            
            // Cache the latest acknowledgment
            _acknowledgmentCache[cacheKey] = new CachedAcknowledgment(acknowledgment, DateTime.UtcNow);
            
            Logger.LogDebug("Cached latest acknowledgment for user {user}: {ackId}", acknowledgment.User.AliasOrUID, acknowledgment.AcknowledgmentId);
            
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
            
            return false;
        }
    }
    
    
    /// Schedules a retry for a failed acknowledgment
    
    private Task ScheduleRetryAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var delay = CalculateRetryDelay(acknowledgment.RetryCount);
        acknowledgment.IncrementRetry(delay);

        Logger.LogInformation("Scheduling retry {retry}/{max} for acknowledgment {ackId} in {delay}ms",
            acknowledgment.RetryCount, _config.MaxRetryAttempts, acknowledgment.AcknowledgmentId, delay.TotalMilliseconds);

        AddToQueue(acknowledgment);
        _metrics.RecordRetry();
        return Task.CompletedTask;
    }
    
    
    /// Calculates retry delay using exponential backoff
    
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
    
    
    /// Adds acknowledgment to appropriate priority queue
    
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
    
    
    /// Determines error code from exception
    
    private static AcknowledgmentErrorCode DetermineErrorCode(Exception ex)
    {
        return ex switch
        {
            TimeoutException => AcknowledgmentErrorCode.Timeout,
            UnauthorizedAccessException => AcknowledgmentErrorCode.AuthenticationFailed,
            ArgumentException => AcknowledgmentErrorCode.InvalidData,
            _ => AcknowledgmentErrorCode.NetworkError
        };
    }
    
    /// Determine if an error is retryable
    private static bool ShouldRetry(AcknowledgmentErrorCode errorCode)
    {
        // Non-retryable errors - these should not be retried
        return errorCode switch
        {
            AcknowledgmentErrorCode.AuthenticationFailed => false,
            AcknowledgmentErrorCode.InsufficientPermissions => false,
            AcknowledgmentErrorCode.InvalidData => false,
            AcknowledgmentErrorCode.HashVerificationFailed => false,
            _ => true // Retry all other errors (network, timeout, etc.)
        };
    }
    
    
    /// Timer callback to process batches
    
    private void ProcessBatches(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _batchSemaphore.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
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
                        await ProcessBatchAsync(batch).ConfigureAwait(false);
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
    

    // Timer callback to cleanup old pending acknowledgments
    private void CleanupOldAcknowledgments(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Cleanup old pending acknowledgments (older than 30 seconds)
                var maxAge = _isInDuty ? DutyOldPendingMaxAge : DefaultOldPendingMaxAge;
                await _sessionManager.CleanupOldPendingAcknowledgments(maxAge).ConfigureAwait(false);
                
                // Cleanup old sessions (older than 2 minutes)
                var sessionMaxAge = _isInDuty ? DutySessionMaxAge : DefaultSessionMaxAge;
                await _sessionManager.CleanupOldSessions(sessionMaxAge).ConfigureAwait(false);
                
                Logger.LogDebug("Completed cleanup of old acknowledgments and sessions");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in acknowledgment cleanup timer");
            }
        });
    }
    

    /// Timer callback to process retries
    
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
                        await SendAcknowledgmentAsync(ack).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in retry processing timer");
            }
        });
    }
    
    
    /// Timer callback to cleanup cache
    
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
    
    
    /// Handles send acknowledgment mediator message
    
    public async Task HandleSendAcknowledgment(SendCharacterDataAcknowledgmentMessage message)
    {
        var enhancedAck = new EnhancedAcknowledgmentDto(
            message.AcknowledgmentDto.User, 
            message.AcknowledgmentDto.DataHash)
        {
            Success = message.AcknowledgmentDto.Success,
            AcknowledgedAt = message.AcknowledgmentDto.AcknowledgedAt
        };
        
        var success = await SendAcknowledgmentAsync(enhancedAck).ConfigureAwait(false);
        
        // Publish UI refresh message to update acknowledgment status in UI
        if (success)
        {
            Mediator.Publish(new RefreshUiMessage());
            Logger.LogDebug("Published UI refresh message after successful acknowledgment Hash: {hash}", 
                message.AcknowledgmentDto.DataHash[..Math.Min(8, message.AcknowledgmentDto.DataHash.Length)]);
        }
    }
    
    
    /// Gets current metrics
    
    public AcknowledgmentMetrics GetMetrics() => _metrics;
    
    
    /// Gets current configuration
    
    public AcknowledgmentConfiguration GetConfiguration() => _config;
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellationTokenSource.Cancel();
            _batchProcessingTimer?.Dispose();
            _retryTimer?.Dispose();
            _cacheCleanupTimer?.Dispose();
            _oldPendingCleanupTimer?.Dispose();
            _batchSemaphore?.Dispose();
            _cancellationTokenSource?.Dispose();
        }
        
        base.Dispose(disposing);
    }

    private sealed class CachedAcknowledgment
    {
        public EnhancedAcknowledgmentDto Acknowledgment { get; }
        public DateTime CachedAt { get; }
        public DateTime Timestamp { get; }
        public CachedAcknowledgment(EnhancedAcknowledgmentDto acknowledgment, DateTime cachedAt)
        {
            Acknowledgment = acknowledgment;
            CachedAt = cachedAt;
            Timestamp = cachedAt;
        }
    }
}
