using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VirtoCommerce.SearchModule.Core.Model;

namespace VirtoCommerce.SearchModule.Core.Services
{
    /// <summary>
    /// Responsible for the functionality of indexing
    /// </summary>
    public interface IIndexingManager
    {
        /// <summary>
        /// Return actual index stats for specific document type
        /// </summary>
        /// <param name="documentType"></param>
        /// <returns></returns>
        Task<IndexState> GetIndexStateAsync(string documentType);

        /// <summary>
        /// Return actual index stats for specific document type including backup indices if the Search Providers supports blue-green indexation.
        /// </summary>
        Task<IEnumerable<IndexState>> GetIndicesStateAsync(string documentType);

        /// <summary>
        /// Indexing the specified documents with given options
        /// </summary>
        /// <param name="options"></param>
        /// <param name="progressCallback"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task IndexAsync(IndexingOptions options, Action<IndexingProgress> progressCallback, Platform.Core.Common.ICancellationToken cancellationToken);

        /// <summary>
        /// Indexes a batch of documents immediately. Intended to be used by IndexingJobs.
        /// </summary>
        /// <param name="documentType">Document type to index.</param>
        /// <param name="documentIds">Ids of documents to index.</param>
        /// <param name="builderTypes">Index document builder types to process the changed documents</param>
        /// <returns>Result of indexing operation.</returns>
        Task<IndexingResult> IndexDocumentsAsync(string documentType, string[] documentIds, IEnumerable<string> builderTypes = null);

        /// <summary>
        /// Deletes a batch of documents from the index immediately. Intended to be used by IndexingJobs.
        /// </summary>
        /// <param name="documentType">Document type to delete.</param>
        /// <param name="documentIds">Ids of documents to delete.</param>
        /// <returns>Result of indexing operation.</returns>
        Task<IndexingResult> DeleteDocumentsAsync(string documentType, string[] documentIds);
    }
}
