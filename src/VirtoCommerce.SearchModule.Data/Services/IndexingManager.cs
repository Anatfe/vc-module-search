using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SearchModule.Core;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.SearchModule.Core.Services;

namespace VirtoCommerce.SearchModule.Data.Services
{
    /// <summary>
    /// Implement the functionality of indexing
    /// </summary>
    public class IndexingManager : IIndexingManager
    {
        private readonly ISearchProvider _searchProvider;
        private readonly IEnumerable<IndexDocumentConfiguration> _configs;
        private readonly ISettingsManager _settingsManager;
        private readonly IIndexingWorker _backgroundWorker;
        private readonly SearchOptions _searchOptions;

        public IndexingManager(ISearchProvider searchProvider, IEnumerable<IndexDocumentConfiguration> configs,
            IOptions<SearchOptions> searchOptions,
            ISettingsManager settingsManager = null, IIndexingWorker backgroundWorker = null)
        {
            if (searchProvider == null)
                throw new ArgumentNullException(nameof(searchProvider));
            if (configs == null)
                throw new ArgumentNullException(nameof(configs));

            _searchOptions = searchOptions.Value;
            _searchProvider = searchProvider;
            _configs = configs;
            _settingsManager = settingsManager;
            _backgroundWorker = backgroundWorker;
        }

        public virtual async Task<IndexState> GetIndexStateAsync(string documentType)
        {
            var result = await GetIndexStateAsync(documentType, getBackupIndexState: false);

            return result;
        }

        public virtual async Task<IEnumerable<IndexState>> GetIndicesStateAsync(string documentType)
        {
            var result = new List<IndexState>();

            result.Add(await GetIndexStateAsync(documentType, getBackupIndexState: false));

            if (_searchProvider is ISupportIndexSwap)
            {
                result.Add(await GetIndexStateAsync(documentType, getBackupIndexState: true));
            }

            return result;
        }

        public virtual async Task IndexAsync(IndexingOptions options, Action<IndexingProgress> progressCallback,
            ICancellationToken cancellationToken)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.DocumentType))
                throw new ArgumentNullException($"{nameof(options)}.{nameof(options.DocumentType)}");

            if (options.BatchSize == null)
                options.BatchSize =
                    _settingsManager?.GetValue(ModuleConstants.Settings.General.IndexPartitionSize.Name, 50) ?? 50;
            if (options.BatchSize < 1)
                throw new ArgumentException(@$"{nameof(options.BatchSize)} {options.BatchSize} cannon be less than 1",
                    $"{nameof(options)}");

            cancellationToken.ThrowIfCancellationRequested();

            var documentType = options.DocumentType;

            // each Search Engine implementation has its own way of handing index rebuild 
            if (options.DeleteExistingIndex)
            {
                progressCallback?.Invoke(new IndexingProgress($"{documentType}: deleting index", documentType));
                await _searchProvider.DeleteIndexAsync(documentType);
            }

            var configs = _configs.Where(c => c.DocumentType.EqualsInvariant(documentType)).ToArray();

            foreach (var config in configs)
            {
                await ProcessConfigurationAsync(config, options, progressCallback, cancellationToken);
            }
        }

        public virtual async Task<IndexingResult> IndexDocumentsAsync(string documentType, string[] documentIds, IEnumerable<string> builderTypes = null)
        {
            // Todo: reuse general index api?
            var configs = _configs.Where(c => c.DocumentType.EqualsInvariant(documentType)).ToArray();
            var result = new IndexingResult { Items = new List<IndexingResultItem>() };

            var partialUpdate = false;

            foreach (var config in configs)
            {
                var primaryDocumentBuilder = config.DocumentSource.DocumentBuilder;

                var additionalDocumentBuilders = config.RelatedSources?
                    .Where(s => s.DocumentBuilder != null)
                    .Select(s => s.DocumentBuilder)
                    .ToList() ?? new List<IIndexDocumentBuilder>();

                if ((builderTypes?.Any() ?? false) && additionalDocumentBuilders.Any() && _searchProvider is ISupportPartialUpdate)
                {
                    additionalDocumentBuilders = additionalDocumentBuilders.Where(x => builderTypes.Contains(x.GetType().FullName))
                        .ToList();

                    // In case of changing main object itself, there would be only primary document builder,
                    // but in the other cases, when changed additional dependent objects, primary builder should be nulled.
                    if (!builderTypes.Contains(primaryDocumentBuilder.GetType().FullName))
                    {
                        primaryDocumentBuilder = null;
                    }

                    partialUpdate = true;
                }

                var documents = await GetDocumentsAsync(documentIds, primaryDocumentBuilder, additionalDocumentBuilders, new CancellationTokenWrapper(CancellationToken.None));

                IndexingResult indexingResult;

                if (partialUpdate && _searchProvider is ISupportPartialUpdate supportPartialUpdateProvider)
                {
                    indexingResult = await supportPartialUpdateProvider.IndexPartialAsync(documentType, documents);
                }
                else
                {
                    indexingResult = await _searchProvider.IndexAsync(documentType, documents);
                }

                result.Items.AddRange(indexingResult.Items ?? Enumerable.Empty<IndexingResultItem>());
            }

            return result;
        }

        public virtual async Task<IndexingResult> DeleteDocumentsAsync(string documentType, string[] documentIds)
        {
            var documents = documentIds.Select(id => new IndexDocument(id)).ToList();
            return await _searchProvider.RemoveAsync(documentType, documents);
        }

        protected virtual async Task ProcessConfigurationAsync(IndexDocumentConfiguration configuration,
            IndexingOptions options, Action<IndexingProgress> progressCallback, ICancellationToken cancellationToken)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
            if (string.IsNullOrEmpty(configuration.DocumentType))
                throw new ArgumentNullException($"{nameof(configuration)}.{nameof(configuration.DocumentType)}");
            if (configuration.DocumentSource == null)
                throw new ArgumentNullException($"{nameof(configuration)}.{nameof(configuration.DocumentSource)}");
            if (configuration.DocumentSource.ChangesProvider == null)
                throw new ArgumentNullException(
                    nameof(configuration),
                    $"{nameof(configuration)}.{nameof(configuration.DocumentSource)}.{nameof(configuration.DocumentSource.ChangesProvider)} cannot be null");
            if (configuration.DocumentSource.DocumentBuilder == null)
                throw new ArgumentNullException(
                    nameof(configuration),
                    $"{nameof(configuration)}.{nameof(configuration.DocumentSource)}.{nameof(configuration.DocumentSource.DocumentBuilder)} cannot be null");

            cancellationToken.ThrowIfCancellationRequested();

            var documentType = options.DocumentType;

            progressCallback?.Invoke(new IndexingProgress($"{documentType}: calculating total count", documentType));

            var batchOptions = new BatchIndexingOptions
            {
                DocumentType = options.DocumentType,
                Reindex = options.DeleteExistingIndex,
                PrimaryDocumentBuilder = configuration.DocumentSource.DocumentBuilder,
                SecondaryDocumentBuilders = configuration.RelatedSources
                    ?.Where(s => s.DocumentBuilder != null)
                    .Select(s => s.DocumentBuilder)
                    .ToList(),
            };

            var feeds = await GetChangeFeeds(configuration, options);

            // Try to get total count to indicate progress. Some feeds don't have a total count.
            var totalCount = feeds.Any(x => x.TotalCount == null)
                ? (long?)null
                : feeds.Sum(x => x.TotalCount ?? 0);

            long processedCount = 0;

            var changes = await GetNextChangesAsync(feeds);
            while (changes.Any())
            {
                IList<string> errors = null;

                if (_backgroundWorker == null)
                {
                    var indexingResult = await ProcessChangesAsync(changes, batchOptions, cancellationToken);
                    errors = GetIndexingErrors(indexingResult);
                }
                else
                {
                    // We're executing a job to index all documents or the changes since a specific time.
                    // Priority for this indexation work should be quite low.
                    var documentIds = changes
                        .Select(x => x.DocumentId)
                        .Distinct()
                        .ToArray();

                    _backgroundWorker.IndexDocuments(configuration.DocumentType, documentIds,
                        IndexingPriority.Background);
                }

                processedCount += changes.Count;

                var description = totalCount != null
                    ? $"{documentType}: {processedCount} of {totalCount} have been indexed"
                    : $"{documentType}: {processedCount} have been indexed";

                progressCallback?.Invoke(new IndexingProgress(description, documentType, totalCount, processedCount,
                    errors));

                cancellationToken.ThrowIfCancellationRequested();

                changes = await GetNextChangesAsync(feeds);
            }

            // indexation complete, swap indexes back
            await SwapIndices(options);

            progressCallback?.Invoke(new IndexingProgress($"{documentType}: indexation finished", documentType, totalCount ?? processedCount, processedCount));
        }

        protected virtual async Task<IList<IndexDocumentChange>> GetNextChangesAsync(
            IList<IIndexDocumentChangeFeed> feeds)
        {
            var batches = await Task.WhenAll(feeds.Select(f => f.GetNextBatch()));

            var changes = batches
                .Where(b => b != null)
                .SelectMany(b => b)
                .ToList();

            return changes;
        }

        protected virtual async Task<IndexingResult> ProcessChangesAsync(IEnumerable<IndexDocumentChange> changes,
            BatchIndexingOptions batchOptions, ICancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new IndexingResult { Items = new List<IndexingResultItem>() };

            var indexDocumentChanges = changes as IndexDocumentChange[] ?? changes.ToArray();

            // Full changes don't have changes provider specified because we don't set it for manual indexation.
            var fullChanges = _searchProvider is ISupportPartialUpdate ? indexDocumentChanges
                .Where(x =>
                    x.ChangeType is IndexDocumentChangeType.Deleted or IndexDocumentChangeType.Created ||
                    !_configs.GetBuildersForProvider(x.Provider?.GetType()).Any()
                )
                .ToArray() : indexDocumentChanges;

            var partialChanges = indexDocumentChanges.Except(fullChanges);

            var partialResult = await ProcessPartialDocumentsAsync(partialChanges, batchOptions, cancellationToken);

            var groups = GetLatestChangesForEachDocumentGroupedByChangeType(fullChanges);

            foreach (var group in groups)
            {
                var changeType = group.Key;
                var changesGroup = group.Value;

                var groupResult =
                    await ProcessDocumentsAsync(changeType, changesGroup, batchOptions, cancellationToken);

                if (groupResult?.Items != null)
                {
                    result.Items.AddRange(groupResult.Items);
                }
            }

            result.Items.AddRange(partialResult.Items);

            return result;
        }

        protected virtual async Task<IndexingResult> ProcessPartialDocumentsAsync(
            IEnumerable<IndexDocumentChange> changes,
            BatchIndexingOptions batchOptions,
            ICancellationToken cancellationToken)
        {
            var result = new IndexingResult { Items = new List<IndexingResultItem>() };

            var indexDocumentChanges = changes as IndexDocumentChange[] ?? changes.ToArray();

            var changeIds = indexDocumentChanges.Select(x => x.DocumentId).Distinct();

            foreach (var id in changeIds)
            {
                var builders = indexDocumentChanges
                    .Where(x => x.DocumentId == id)
                    .SelectMany(x => _configs.GetBuildersForProvider(x.Provider.GetType()));

                var documents = await GetDocumentsAsync(new[] { id }, null, builders, cancellationToken);

                IndexingResult indexingResult;

                indexingResult = await ((ISupportPartialUpdate)_searchProvider).IndexPartialAsync(batchOptions.DocumentType, documents);

                result.Items.AddRange(indexingResult.Items);
            }

            return result;
        }

        protected virtual async Task<IndexingResult> ProcessDocumentsAsync(IndexDocumentChangeType changeType,
            string[] changedIds, BatchIndexingOptions batchOptions, ICancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IndexingResult result = null;

            if (changeType == IndexDocumentChangeType.Deleted)
            {
                result = await DeleteDocumentsAsync(batchOptions.DocumentType, changedIds);
            }
            else if (changeType is IndexDocumentChangeType.Modified or IndexDocumentChangeType.Created)
            {
                var documents = await GetDocumentsAsync(changedIds, batchOptions.PrimaryDocumentBuilder, batchOptions.SecondaryDocumentBuilders, cancellationToken);

                if (batchOptions.Reindex && _searchProvider is ISupportIndexSwap supportIndexSwapProvider)
                {
                    result = await supportIndexSwapProvider.IndexWithBackupAsync(batchOptions.DocumentType, documents);
                }
                else
                {
                    result = await _searchProvider.IndexAsync(batchOptions.DocumentType, documents);
                }
            }

            return result;
        }

        protected virtual async Task<IIndexDocumentChangeFeed[]> GetChangeFeeds(
            IndexDocumentConfiguration configuration, IndexingOptions options)
        {
            // Return in-memory change feed for specific set of document ids.
            if (options.DocumentIds != null)
            {
                return new IIndexDocumentChangeFeed[]
                {
                    new InMemoryIndexDocumentChangeFeed(options.DocumentIds.ToArray(),
                        IndexDocumentChangeType.Modified, options.BatchSize ?? 50)
                };
            }

            // Support old ChangesProvider.
            if (configuration.DocumentSource.ChangeFeedFactory == null)
            {
                configuration.DocumentSource.ChangeFeedFactory =
                    new IndexDocumentChangeFeedFactoryAdapter(configuration.DocumentSource.ChangesProvider);
            }

            var factories = new List<IIndexDocumentChangeFeedFactory>
            {
                configuration.DocumentSource.ChangeFeedFactory
            };

            // In case of 'full' re-index we don't want to include the related sources,
            // because that would double the indexation work.
            // E.g. All products would get indexed for the primary document source
            // and afterwards all products would get re-indexed for all the prices as well.
            if (options.StartDate != null || options.EndDate != null)
            {
                foreach (var related in configuration.RelatedSources ?? Enumerable.Empty<IndexDocumentSource>())
                {
                    // Support old ChangesProvider.
                    if (related.ChangeFeedFactory == null)
                        related.ChangeFeedFactory = new IndexDocumentChangeFeedFactoryAdapter(related.ChangesProvider);

                    factories.Add(related.ChangeFeedFactory);
                }
            }

            return await Task.WhenAll(factories.Select(x =>
                x.CreateFeed(options.StartDate, options.EndDate, options.BatchSize ?? 50)));
        }

        protected virtual IList<string> GetIndexingErrors(IndexingResult indexingResult)
        {
            var errors = indexingResult?.Items?
                .Where(i => !i.Succeeded)
                .Select(i => $"ID: {i.Id}, Error: {i.ErrorMessage}")
                .ToList();

            return errors?.Any() == true ? errors : null;
        }

        protected virtual IDictionary<IndexDocumentChangeType, string[]> GetLatestChangesForEachDocumentGroupedByChangeType(IEnumerable<IndexDocumentChange> changes)
        {
            var result = changes
                .GroupBy(c => c.DocumentId)
                .Select(g => g.OrderByDescending(o => o.ChangeDate).First())
                .GroupBy(c => c.ChangeType)
                .ToDictionary(g => g.Key, g => g.Select(c => c.DocumentId).ToArray());

            return result;
        }

        protected virtual async Task<IList<IndexDocument>> GetDocumentsAsync(IList<string> documentIds, IIndexDocumentBuilder primaryDocumentBuilder,
            IEnumerable<IIndexDocumentBuilder> additionalDocumentBuilders, ICancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<IndexDocument> primaryDocuments;

            if (primaryDocumentBuilder == null)
            {
                primaryDocuments = documentIds.Select(x => new IndexDocument(x)).ToList();
            }
            else
            {
                primaryDocuments = (await primaryDocumentBuilder.GetDocumentsAsync(documentIds))
                    ?.Where(x => x != null)
                    .ToList();
            }

            if (primaryDocuments?.Any() == true)
            {
                if (additionalDocumentBuilders != null)
                {
                    var primaryDocumentIds = primaryDocuments.Select(d => d.Id).ToArray();
                    var secondaryDocuments =
                        await GetSecondaryDocumentsAsync(additionalDocumentBuilders, primaryDocumentIds, cancellationToken);

                    MergeDocuments(primaryDocuments, secondaryDocuments);
                }

                // Add system fields
                foreach (var document in primaryDocuments)
                {
                    document.Add(new IndexDocumentField(KnownDocumentFields.IndexationDate, DateTime.UtcNow)
                    {
                        IsRetrievable = true,
                        IsFilterable = true
                    });
                }
            }

            return primaryDocuments;
        }

        protected virtual async Task<IList<IndexDocument>> GetSecondaryDocumentsAsync(
            IEnumerable<IIndexDocumentBuilder> secondaryDocumentBuilders, IList<string> documentIds,
            ICancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tasks = secondaryDocumentBuilders.Select(p => p.GetDocumentsAsync(documentIds));
            var results = await Task.WhenAll(tasks);

            var result = results
                .Where(r => r != null)
                .SelectMany(r => r.Where(d => d != null))
                .ToList();

            return result;
        }

        protected virtual void MergeDocuments(IList<IndexDocument> primaryDocuments,
            IList<IndexDocument> secondaryDocuments)
        {
            if (primaryDocuments?.Any() == true && secondaryDocuments?.Any() == true)
            {
                var secondaryDocumentGroups = secondaryDocuments
                    .GroupBy(d => d.Id)
                    .ToDictionary(g => g.Key, g => g, StringComparer.OrdinalIgnoreCase);

                foreach (var primaryDocument in primaryDocuments)
                {
                    if (secondaryDocumentGroups.ContainsKey(primaryDocument.Id))
                    {
                        var secondaryDocumentGroup = secondaryDocumentGroups[primaryDocument.Id];

                        foreach (var secondaryDocument in secondaryDocumentGroup)
                        {
                            primaryDocument.Merge(secondaryDocument);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Swap between active and backup indeces, if supported
        /// </summary>
        protected virtual async Task SwapIndices(IndexingOptions options)
        {
            if (options.DeleteExistingIndex && _searchProvider is ISupportIndexSwap swappingSupportedSearchProvider)
            {
                await swappingSupportedSearchProvider.SwapIndexAsync(options.DocumentType);
            }
        }

        private async Task<IndexState> GetIndexStateAsync(string documentType, bool getBackupIndexState)
        {
            var result = new IndexState
            {
                DocumentType = documentType,
                Provider = _searchOptions.Provider,
                Scope = _searchOptions.Scope,
                IsActive = !getBackupIndexState,
            };

            var searchRequest = new SearchRequest
            {
                UseBackupIndex = getBackupIndexState,
                Sorting = new[] { new SortingField { FieldName = KnownDocumentFields.IndexationDate, IsDescending = true } },
                Take = 1,
            };

            try
            {
                var searchResponse = await _searchProvider.SearchAsync(documentType, searchRequest);

                result.IndexedDocumentsCount = searchResponse.TotalCount;
                if (searchResponse.Documents?.Any() == true)
                {
                    var indexationDate = searchResponse.Documents[0].FirstOrDefault(kvp => kvp.Key.EqualsInvariant(KnownDocumentFields.IndexationDate));
                    if (DateTimeOffset.TryParse(indexationDate.Value.ToString(), out var lastIndexationDateTime))
                    {
                        result.LastIndexationDate = lastIndexationDateTime.DateTime;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return result;
        }
    }
}
