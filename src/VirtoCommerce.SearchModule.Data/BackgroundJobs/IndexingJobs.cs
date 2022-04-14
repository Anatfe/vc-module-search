// This indexing jobs implementation allows only one job to perform indexing.
// If some job is started successfully, all other jobs will terminate with "Indexation is already in progress" error until the first job is finished.
// The synchronization is done by using the Hangfire distributed lock infrastructure.
// This supports scaled-out scenarios.
//
// This class also supports queueing index batches through the Hangfire job scheduler,
// so that we can spread the indexation work over the entire web farm.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Hangfire;
using VirtoCommerce.SearchModule.Core;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.SearchModule.Core.Services;
using Job = Hangfire.Common.Job;

namespace VirtoCommerce.SearchModule.Data.BackgroundJobs
{
    public sealed class IndexingJobs
    {
        private static readonly MethodInfo _indexChangesJobMethod = typeof(IndexingJobs).GetMethod("IndexChangesJob");
        private static readonly MethodInfo _manualIndexAllJobMethod = typeof(IndexingJobs).GetMethod("IndexAllDocumentsJob");

        private readonly IEnumerable<IndexDocumentConfiguration> _documentsConfigs;
        private readonly IIndexingManager _indexingManager;
        private readonly ISettingsManager _settingsManager;
        private readonly IndexProgressHandler _progressHandler;
        private readonly IIndexingInterceptor[] _interceptors;

        public IndexingJobs(IEnumerable<IndexDocumentConfiguration> documentsConfigs, IIndexingManager indexingManager, ISettingsManager settingsManager,
            IndexProgressHandler progressHandler, IIndexingInterceptor[] interceptors = null)
        {
            _documentsConfigs = documentsConfigs;
            _indexingManager = indexingManager;
            _settingsManager = settingsManager;
            _progressHandler = progressHandler;
            _interceptors = interceptors;
        }

        // Enqueue a background job with single notification object for all given options
        public static IndexProgressPushNotification Enqueue(string currentUserName, IndexingOptions[] options)
        {
            var notification = IndexProgressHandler.CreateNotification(currentUserName, null);

            // Hangfire will set cancellation token.
            BackgroundJob.Enqueue<IndexingJobs>(j => j.IndexAllDocumentsJob(currentUserName, notification.Id, options, JobCancellationToken.Null));

            return notification;
        }

        // Cancel current indexation if there is one
        public static void CancelIndexation()
        {
            var processingJob = JobStorage.Current.GetMonitoringApi().ProcessingJobs(0, int.MaxValue)
                .FirstOrDefault(x => x.Value.Job.Method == _indexChangesJobMethod || x.Value.Job.Method == _manualIndexAllJobMethod);

            if (!string.IsNullOrEmpty(processingJob.Key))
            {
                try
                {
                    BackgroundJob.Delete(processingJob.Key);
                }
                catch
                {
                    // Ignore concurrency exceptions, when somebody else cancelled it as well.
                }
            }
        }

        // One-time job for manual indexation
        [Queue(JobPriority.Normal)]
        public Task IndexAllDocumentsJob(string userName, string notificationId, IndexingOptions[] options, IJobCancellationToken cancellationToken)
        {
            return WithInterceptorsAsync(options, async o =>
             {
                 try
                 {
                     var success = await RunIndexJobAsync(userName, notificationId, false, o, IndexAllDocumentsAsync, cancellationToken);

                     // Indexation manager might re-use the jobs to scale out indexation.
                     // Wait for all indexing jobs to complete, before telling interceptors we're ready.
                     // This method is running as a job as well, so skip this job.
                     // Scale out background indexation jobs are scheduled as low.
                     await WaitForIndexationJobsToBeReadyAsync(JobPriority.Low, x => x.Method != _manualIndexAllJobMethod && x.Method != _indexChangesJobMethod);

                     return success;
                 }
                 finally
                 {
                     // Report indexation summary
                     _progressHandler.Finish();
                 }
             });
        }

        // Recurring job for automatic changes indexation.
        // It should push separate notification for each document type if any changes were indexed for this type
        [Queue(JobPriority.Normal)]
        public Task IndexChangesJob(string documentType, IJobCancellationToken cancellationToken)
        {
            var allOptions = GetAllIndexingOptions(documentType);

            return WithInterceptorsAsync(allOptions, async o =>
             {
                 // Create different notification for each option (document type)
                 var success = true;

                 foreach (var options in o)
                 {
                     success = success && await RunIndexJobAsync(null, null, true, new[] { options }, IndexChangesAsync, cancellationToken);
                 }

                 // Indexation manager might re-use the jobs to scale out indexation.
                 // Wait for all indexing jobs to complete, before telling interceptors we're ready.
                 // This method is running as a job as well, so skip this job.
                 // Scale out background indexation jobs are scheduled as low.
                 await WaitForIndexationJobsToBeReadyAsync(JobPriority.Low, x => x.Method != _manualIndexAllJobMethod && x.Method != _indexChangesJobMethod);

                 return success;
             });
        }

        #region Scale-out indexation actions for indexing worker

        [Obsolete("Method is obsolete. Use EnqueueIndexDocuments(string documentType, string[] documentIds, string priority = JobPriority.Normal, IList<IIndexDocumentBuilder> builders = null) instead.")]
        public static void EnqueueIndexDocuments(string documentType, string[] documentIds, string priority = JobPriority.Normal)
        {
            EnqueueIndexDocuments(documentType, documentIds, priority, null);
        }

        public static void EnqueueIndexDocuments(string documentType, string[] documentIds, string priority = JobPriority.Normal, IList<IIndexDocumentBuilder> builders = null)
        {
            var buildersTypes = builders?.Select(x => x.GetType().FullName);

            switch (priority)
            {
                case JobPriority.High:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.IndexDocumentsHighPriorityAsync(documentType, documentIds, buildersTypes));
                    break;
                case JobPriority.Normal:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.IndexDocumentsNormalPriorityAsync(documentType, documentIds, buildersTypes));
                    break;
                case JobPriority.Low:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.IndexDocumentsLowPriorityAsync(documentType, documentIds, buildersTypes));
                    break;
                default:
                    throw new ArgumentException($@"Unknown priority: {priority}", nameof(priority));
            }
        }

        public static void EnqueueDeleteDocuments(string documentType, string[] documentIds, string priority = JobPriority.Normal)
        {
            switch (priority)
            {
                case JobPriority.High:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.DeleteDocumentsHighPriorityAsync(documentType, documentIds));
                    break;
                case JobPriority.Normal:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.DeleteDocumentsNormalPriorityAsync(documentType, documentIds));
                    break;
                case JobPriority.Low:
                    BackgroundJob.Enqueue<IndexingJobs>(x => x.DeleteDocumentsLowPriorityAsync(documentType, documentIds));
                    break;
                default:
                    throw new ArgumentException($@"Unknown priority: {priority}", nameof(priority));
            }
        }

        [Obsolete("Method is obsolete. Use EnqueueIndexAndDeleteDocuments(IndexEntry[] indexEntries, string priority = JobPriority.Normal, IList<IIndexDocumentBuilder> builders = null) instead.")]
        public static void EnqueueIndexAndDeleteDocuments(IndexEntry[] indexEntries, string priority = JobPriority.Normal)
        {
            EnqueueIndexAndDeleteDocuments(indexEntries, priority, null);
        }

        public static void EnqueueIndexAndDeleteDocuments(IndexEntry[] indexEntries, string priority = JobPriority.Normal, IList<IIndexDocumentBuilder> builders = null)
        {
            var groupedEntriesByType = GetGroupedByTypeAndDistinctedByChangeTypeIndexEntries(indexEntries);

            foreach (var groupedEntryByType in groupedEntriesByType)
            {
                var addedEntries = groupedEntryByType.Where(x => x.EntryState == EntryState.Added).ToList();
                var modifiedEntries = groupedEntryByType.Where(x => x.EntryState == EntryState.Modified).ToList();
                var deletedEntries = groupedEntryByType.Where(x => x.EntryState == EntryState.Deleted).ToList();

                if (addedEntries.Any())
                {
                    EnqueueIndexDocuments(groupedEntryByType.Key, addedEntries.Select(x => x.Id).ToArray(), priority, null);
                }

                if (modifiedEntries.Any())
                {
                    EnqueueIndexDocuments(groupedEntryByType.Key, modifiedEntries.Select(x => x.Id).ToArray(), priority, builders);
                }

                if (deletedEntries.Any())
                {
                    EnqueueDeleteDocuments(groupedEntryByType.Key, deletedEntries.Select(x => x.Id).ToArray(), priority);
                }
            }
        }

        public static IEnumerable<IGrouping<string, IndexEntry>> GetGroupedByTypeAndDistinctedByChangeTypeIndexEntries(IEnumerable<IndexEntry> indexEntries)
        {
            var indexEntriesFilteredFromEmptyIds = indexEntries.Where(x => !string.IsNullOrEmpty(x.Id)).ToList();

            var result = new List<IndexEntry>();

            foreach (var indexEntryGroupedByType in indexEntriesFilteredFromEmptyIds.GroupBy(x => x.Type))
            {
                foreach (var indexEntryGroupedById in indexEntryGroupedByType.GroupBy(x => x.Id))
                {
                    var entryWasAdded = indexEntryGroupedById.Any(x => x.EntryState is EntryState.Added);
                    var entryWasModified = indexEntryGroupedById.Any(x => x.EntryState is EntryState.Modified);
                    var entryWasDeleted = indexEntryGroupedById.Any(x => x.EntryState is EntryState.Deleted);

                    if (entryWasDeleted)
                    {
                        result.Add(indexEntryGroupedById.First(x => x.EntryState is EntryState.Deleted));
                    }
                    else if (entryWasAdded)
                    {
                        result.Add(indexEntryGroupedById.First(x => x.EntryState is EntryState.Added));
                    }
                    else if (entryWasModified)
                    {
                        result.Add(indexEntryGroupedById.First(x => x.EntryState is EntryState.Modified));
                    }
                }
            }

            return result.GroupBy(x => x.Type);
        }

        // Use hard-code methods to easily set queue for Hangfire.
        // Make sure we wait for async methods to end, so that Hangfire retries if an exception occurs.

        [Queue(JobPriority.High)]
        public async Task IndexDocumentsHighPriorityAsync(string documentType, string[] documentIds, IEnumerable<string> builderTypes)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.IndexDocumentsAsync(documentType, documentIds, builderTypes);
            }
        }

        [Queue(JobPriority.Normal)]
        public async Task IndexDocumentsNormalPriorityAsync(string documentType, string[] documentIds, IEnumerable<string> builderTypes)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.IndexDocumentsAsync(documentType, documentIds, builderTypes);
            }
        }

        [Queue(JobPriority.Low)]
        public async Task IndexDocumentsLowPriorityAsync(string documentType, string[] documentIds, IEnumerable<string> builderTypes)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.IndexDocumentsAsync(documentType, documentIds, builderTypes);
            }
        }

        [Queue(JobPriority.High)]
        public async Task DeleteDocumentsHighPriorityAsync(string documentType, string[] documentIds)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.DeleteDocumentsAsync(documentType, documentIds);
            }
        }

        [Queue(JobPriority.Normal)]
        public async Task DeleteDocumentsNormalPriorityAsync(string documentType, string[] documentIds)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.DeleteDocumentsAsync(documentType, documentIds);
            }
        }

        [Queue(JobPriority.Low)]
        public async Task DeleteDocumentsLowPriorityAsync(string documentType, string[] documentIds)
        {
            if (!documentIds.IsNullOrEmpty())
            {
                await _indexingManager.DeleteDocumentsAsync(documentType, documentIds);
            }
        }

        #endregion

        private Task<bool> RunIndexJobAsync(string currentUserName, string notificationId, bool suppressInsignificantNotifications,
            IEnumerable<IndexingOptions> allOptions, Func<IndexingOptions, ICancellationToken, Task> indexationFunc,
            IJobCancellationToken cancellationToken)
        {
            var success = false;

            // Reset progress handler to initial state
            _progressHandler.Start(currentUserName, notificationId, suppressInsignificantNotifications);

            // Make sure only one indexation job can run in the cluster.
            // CAUTION: locking mechanism assumes single threaded execution.
            try
            {
                using (JobStorage.Current.GetConnection().AcquireDistributedLock("IndexationJob", TimeSpan.Zero))
                {
                    try
                    {
                        var tasks = allOptions.Select(x => indexationFunc(x, new JobCancellationTokenWrapper(cancellationToken)));
                        Task.WaitAll(tasks.ToArray());

                        success = true;
                    }
                    catch (OperationCanceledException)
                    {
                        _progressHandler.Cancel();
                    }
                    catch (Exception ex)
                    {
                        _progressHandler.Exception(ex);
                    }
                    finally
                    {
                        // Report indexation summary only for "Recurring job for automatic changes indexation."
                        if (notificationId.IsNullOrEmpty())
                        {
                            _progressHandler.Finish();
                        }
                    }
                }
            }
            catch
            {
                // TODO: Check wait in calling method
                _progressHandler.AlreadyInProgress();
            }

            return Task.FromResult(success);
        }

        private async Task IndexAllDocumentsAsync(IndexingOptions options, ICancellationToken cancellationToken)
        {
            var oldIndexationDate = GetLastIndexationDate(options.DocumentType);
            var newIndexationDate = DateTime.UtcNow;

            await _indexingManager.IndexAsync(options, _progressHandler.Progress, cancellationToken);

            // Save indexation date to prevent changes from being indexed again
            SetLastIndexationDate(options.DocumentType, oldIndexationDate, newIndexationDate);
        }

        private async Task IndexChangesAsync(IndexingOptions options, ICancellationToken cancellationToken)
        {
            var oldIndexationDate = options.StartDate;
            var newIndexationDate = DateTime.UtcNow;

            options.EndDate = oldIndexationDate == null ? null : (DateTime?)newIndexationDate;

            await _indexingManager.IndexAsync(options, _progressHandler.Progress, cancellationToken);

            // Save indexation date. It will be used as a start date for the next indexation
            SetLastIndexationDate(options.DocumentType, oldIndexationDate, newIndexationDate);
        }

        private async Task<bool> WithInterceptorsAsync(ICollection<IndexingOptions> options, Func<ICollection<IndexingOptions>, Task<bool>> action)
        {
            try
            {
                if (!_interceptors.IsNullOrEmpty())
                {
                    foreach (var interceptor in _interceptors)
                    {
                        interceptor.OnBegin(options.ToArray());
                    }
                }

                var result = await action(options);

                if (!_interceptors.IsNullOrEmpty())
                {
                    foreach (var interceptor in _interceptors)
                    {
                        interceptor.OnEnd(options.ToArray(), result);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                if (!_interceptors.IsNullOrEmpty())
                {
                    foreach (var interceptor in _interceptors)
                    {
                        interceptor.OnEnd(options.ToArray(), false, ex);
                    }
                }
                throw;
            }
        }

        private IList<IndexingOptions> GetAllIndexingOptions(string documentType)
        {
            var configs = _documentsConfigs.AsQueryable();

            if (!string.IsNullOrEmpty(documentType))
            {
                configs = configs.Where(c => c.DocumentType.EqualsInvariant(documentType));
            }

            var result = configs.Select(c => GetIndexingOptions(c.DocumentType)).ToList();
            return result;
        }

        private IndexingOptions GetIndexingOptions(string documentType)
        {
            return new IndexingOptions
            {
                DocumentType = documentType,
                DeleteExistingIndex = false,
                StartDate = GetLastIndexationDate(documentType),
                BatchSize = GetBatchSize(),
            };
        }

        private DateTime? GetLastIndexationDate(string documentType)
        {
            var result = _indexingManager.GetIndexStateAsync(documentType).GetAwaiter().GetResult().LastIndexationDate;
            if (result != null)
            {
                //need to take the older date from the dates loaded from the index and settings.
                //Because the actual last indexation date stored in the index may be later than last job run are stored in the settings. e.g after data import or direct database changes
                result = new DateTime(Math.Min(result.Value.Ticks, _settingsManager.GetValue(GetLastIndexationDateName(documentType), DateTime.MaxValue).Ticks));
            }
            return result;
        }

        private void SetLastIndexationDate(string documentType, DateTime? oldValue, DateTime newValue)
        {
            var currentValue = GetLastIndexationDate(documentType);
            if (currentValue == oldValue)
            {
                _settingsManager.SetValue(GetLastIndexationDateName(documentType), newValue);
            }
        }

        private static string GetLastIndexationDateName(string documentType)
        {
            return $"VirtoCommerce.Search.IndexingJobs.IndexationDate.{documentType}";
        }

        private int GetBatchSize()
        {
            return _settingsManager.GetValue(ModuleConstants.Settings.General.IndexPartitionSize.Name, 50);
        }

        private static async Task WaitForIndexationJobsToBeReadyAsync(string queue, Func<Job, bool> jobPredicate)
        {
            var monitoringApi = JobStorage.Current.GetMonitoringApi();

            while (true)
            {
                var hasQueuedIndexingJobs = monitoringApi.Queues()
                    .FirstOrDefault(x => x.Name.Equals(queue, StringComparison.OrdinalIgnoreCase))
                    ?.FirstJobs
                    .Where(x => jobPredicate == null || jobPredicate(x.Value.Job))
                    .Any(x => x.Value.Job.Method.DeclaringType == typeof(IndexingJobs));

                if (!hasQueuedIndexingJobs.GetValueOrDefault())
                {
                    var hasFetchedIndexingJobs = monitoringApi.FetchedJobs(queue, 0, int.MaxValue)
                        ?.Where(x => jobPredicate == null || jobPredicate(x.Value.Job))
                        .Any(x => x.Value.Job.Method.DeclaringType == typeof(IndexingJobs));

                    if (!hasFetchedIndexingJobs.GetValueOrDefault())
                    {
                        var hasProcessingIndexingJobs = monitoringApi.ProcessingJobs(0, int.MaxValue)
                            ?.Where(x => jobPredicate == null || jobPredicate(x.Value.Job))
                            .Any(x => x.Value.Job.Method.DeclaringType == typeof(IndexingJobs));

                        if (!hasProcessingIndexingJobs.GetValueOrDefault())
                        {
                            break;
                        }
                    }
                }

                await Task.Delay(100);
            }
        }
    }
}
