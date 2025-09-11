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
using Sphene.SpheneConfiguration;

namespace Sphene.PlayerData.Pairs;


/// Enhanced acknowledgment manager with timeout management, batching, priority system, and error handling
public class EnhancedAcknowledgmentManager : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    // Removed DalamudUtilService dependency - not needed for acknowledgment management
    private readonly AcknowledgmentConfigService _configService;
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
    
    // Session acknowledgment manager for cleanup
    private readonly SessionAcknowledgmentManager _sessionManager;
    
    // Thread safety
    private readonly SemaphoreSlim _batchSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    
    public EnhancedAcknowledgmentManager(ILogger<EnhancedAcknowledgmentManager> logger, 
        ApiController apiController, 
        // DalamudUtilService dalamudUtil, // Removed - not needed
        SpheneMediator mediator,
        AcknowledgmentConfigService configService,
        SessionAcknowledgmentManager sessionManager) : base(logger, mediator)
    {
        _apiController = apiController;
        // _dalamudUtil = dalamudUtil; // Removed - not needed
        _configService = configService;
        _sessionManager = sessionManager;
        _configService.Current.Validate();
        
        // Initialize timers
        _batchProcessingTimer = new Timer(ProcessBatches, null, 
            TimeSpan.FromMilliseconds(_configService.Current.BatchTimeoutMs / 2), 
            TimeSpan.FromMilliseconds(_configService.Current.BatchTimeoutMs / 2));
            
        _timeoutCheckTimer = new Timer(CheckTimeouts, null, 
            TimeSpan.FromSeconds(5), 
            TimeSpan.FromSeconds(5));
            
        _retryTimer = new Timer(ProcessRetries, null, 
            TimeSpan.FromSeconds(10), 
            TimeSpan.FromSeconds(10));
            
        _cacheCleanupTimer = new Timer(CleanupCache, null, 
            TimeSpan.FromMinutes(_configService.Current.CacheExpirationMinutes / 2), 
            TimeSpan.FromMinutes(_configService.Current.CacheExpirationMinutes / 2));
            
        // Add cleanup timer for old pending acknowledgments
        var cleanupTimer = new Timer(CleanupOldAcknowledgments, null,
            TimeSpan.FromMinutes(2), // First cleanup after 2 minutes
            TimeSpan.FromMinutes(5)); // Then every 5 minutes
        
        // Subscribe to mediator messages
        Mediator.Subscribe<SendCharacterDataAcknowledgmentMessage>(this, (msg) => _ = HandleSendAcknowledgment(msg));
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
                return true;
            }
            
            // Add to appropriate priority queue
            if (_configService.Current.EnableBatching)
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
            Logger.LogError(ex, "Failed to send acknowledgment");
            acknowledgment.MarkAsFailed(AcknowledgmentErrorCode.NetworkError, ex.Message);
            
            if (_configService.Current.EnableAutoRetry && acknowledgment.RetryCount < _configService.Current.MaxRetryAttempts)
            {
                await ScheduleRetryAsync(acknowledgment);
            }
            
            return false;
        }
    }
    
    
    /// Adds acknowledgment to batch for processing
    
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
            
            // Check if this acknowledgment is already in the current batch
            if (batch.ContainsAcknowledgment(acknowledgment.AcknowledgmentId))
            {
                return;
            }
            
            batch.AddAcknowledgment(acknowledgment);

            
            // Check if batch is ready to send
            if (batch.IsReadyToSend(_configService.Current.MaxBatchSize, TimeSpan.FromMilliseconds(_configService.Current.BatchTimeoutMs)))
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
    
    
    /// Processes a batch of acknowledgments
    
    private async Task ProcessBatchAsync(BatchAcknowledgmentDto batch)
    {
        try
        {

            
            var tasks = batch.Acknowledgments.Select(SendSingleAcknowledgmentAsync);
            var results = await Task.WhenAll(tasks);
            
            batch.MarkAsProcessed();
            
            var successCount = results.Count(r => r);

        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to process batch");
        }
    }
    
    
    /// Sends a single acknowledgment
    
    private async Task<bool> SendSingleAcknowledgmentAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var startTime = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(_configService.Current.GetTimeoutForPriority(acknowledgment.Priority));
        
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
            

            
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send acknowledgment");
            
            var errorCode = DetermineErrorCode(ex);
            acknowledgment.MarkAsFailed(errorCode, ex.Message);
            _metrics.RecordFailure(acknowledgment.Priority, errorCode);
            
            // Remove from pending
            _pendingAcknowledgments.TryRemove(acknowledgment.AcknowledgmentId, out _);
            
            return false;
        }
    }
    
    
    /// Schedules a retry for a failed acknowledgment
    
    private async Task ScheduleRetryAsync(EnhancedAcknowledgmentDto acknowledgment)
    {
        var delay = CalculateRetryDelay(acknowledgment.RetryCount);
        acknowledgment.IncrementRetry(delay);
        

        
        // Add back to appropriate queue for retry
        AddToQueue(acknowledgment);
        _metrics.RecordRetry();
    }
    
    
    /// Calculates retry delay using exponential backoff
    
    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        var delay = Math.Min(
            _configService.Current.BaseRetryDelayMs * Math.Pow(2, retryCount),
            _configService.Current.MaxRetryDelayMs
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
    
    
    /// Checks if an acknowledgment is already in any priority queue
    
    private bool IsAcknowledgmentInQueues(string acknowledgmentId)
    {
        var queues = new[] { _highPriorityQueue, _mediumPriorityQueue, _lowPriorityQueue };
        
        foreach (var queue in queues)
        {
            // Create a temporary list to check queue contents without dequeuing
            var tempList = new List<EnhancedAcknowledgmentDto>();
            var found = false;
            
            // Dequeue all items to check them
            while (queue.TryDequeue(out var item))
            {
                tempList.Add(item);
                if (item.AcknowledgmentId == acknowledgmentId)
                {
                    found = true;
                }
            }
            
            // Re-enqueue all items
            foreach (var item in tempList)
            {
                queue.Enqueue(item);
            }
            
            if (found)
            {
                return true;
            }
        }
        
        return false;
    }
    
    
    /// Determines error code from exception
    
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
    
    
    /// Timer callback to process batches
    
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
                        if (batch.IsReadyToSend(_configService.Current.MaxBatchSize, TimeSpan.FromMilliseconds(_configService.Current.BatchTimeoutMs)))
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
    

    // Timer callback to cleanup old pending acknowledgments
    private void CleanupOldAcknowledgments(object? state)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Cleanup old pending acknowledgments (older than 10 minutes)
                var maxAge = TimeSpan.FromMinutes(10);
                _sessionManager.CleanupOldPendingAcknowledgments(maxAge);
                
                // Cleanup old sessions (older than 30 minutes)
                var sessionMaxAge = TimeSpan.FromMinutes(30);
                _sessionManager.CleanupOldSessions(sessionMaxAge);
                

            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in acknowledgment cleanup timer");
            }
        });
    }
    

    // Timer callback to check for timeouts
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

                        pendingAck.Acknowledgment.MarkAsFailed(AcknowledgmentErrorCode.Timeout);
                        _metrics.RecordFailure(pendingAck.Acknowledgment.Priority, AcknowledgmentErrorCode.Timeout);
                        
                        if (_configService.Current.EnableAutoRetry && pendingAck.Acknowledgment.RetryCount < _configService.Current.MaxRetryAttempts)
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
                    if (now - kvp.Value.CachedAt > TimeSpan.FromMinutes(_configService.Current.CacheExpirationMinutes))
                    {
                        expiredKeys.Add(kvp.Key);
                    }
                }
                
                foreach (var key in expiredKeys)
                {
                    _acknowledgmentCache.TryRemove(key, out _);
                }
                
                // Limit cache size
                if (_acknowledgmentCache.Count > _configService.Current.MaxCacheSize)
                {
                    var oldestEntries = _acknowledgmentCache
                        .OrderBy(kvp => kvp.Value.CachedAt)
                        .Take(_acknowledgmentCache.Count - _configService.Current.MaxCacheSize)
                        .Select(kvp => kvp.Key)
                        .ToList();
                        
                    foreach (var key in oldestEntries)
                    {
                        _acknowledgmentCache.TryRemove(key, out _);
                    }
                }
                

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
        var ackId = message.AcknowledgmentDto.AcknowledgmentId;
        var userUid = message.AcknowledgmentDto.User.UID;
        
        // Enhanced duplicate detection - check multiple sources
        var cacheKey = $"{userUid}_{ackId}";
        
        // Check if already cached (successfully processed)
        if (_acknowledgmentCache.ContainsKey(cacheKey))
        {
            return;
        }
        
        // Check if currently pending (being processed)
        if (_pendingAcknowledgments.ContainsKey(ackId))
        {
            return;
        }
        
        // Check if already in any priority queue
        if (IsAcknowledgmentInQueues(ackId))
        {
            return;
        }
        
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
        }
    }
    
    
    /// Gets current metrics
    
    public AcknowledgmentMetrics GetMetrics() => _metrics;
    
    
    /// Gets current configuration
    
    public AcknowledgmentConfiguration GetConfiguration() => _configService.Current;
    
    
    /// Gets the configuration service for persistence
    
    public AcknowledgmentConfigService GetConfigurationService() => _configService;
    
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


/// Represents a pending acknowledgment with timeout tracking
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


/// Represents a cached acknowledgment
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