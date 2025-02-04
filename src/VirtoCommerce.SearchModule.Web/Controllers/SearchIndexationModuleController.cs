using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.Platform.Core.PushNotifications;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SearchModule.Core;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.SearchModule.Core.Services;
using VirtoCommerce.SearchModule.Data.BackgroundJobs;

namespace VirtoCommerce.SearchModule.Web.Controllers
{
    [Route("api/search/indexes")]
    [Produces("application/json")]
    [Authorize]
    public class SearchIndexationModuleController : Controller
    {
        private readonly IEnumerable<IndexDocumentConfiguration> _documentConfigs;
        private readonly ISearchProvider _searchProvider;
        private readonly IIndexingManager _indexingManager;
        private readonly IUserNameResolver _userNameResolver;
        private readonly IPushNotificationManager _pushNotifier;

        public SearchIndexationModuleController(IEnumerable<IndexDocumentConfiguration> documentConfigs, ISearchProvider searchProvider, IIndexingManager indexingManager, IUserNameResolver userNameResolver, IPushNotificationManager pushNotifier)
        {
            _documentConfigs = documentConfigs;
            _searchProvider = searchProvider;
            _indexingManager = indexingManager;
            _userNameResolver = userNameResolver;
            _pushNotifier = pushNotifier;
        }

        [HttpGet]
        [Route("")]
        public async Task<ActionResult<IndexState[]>> GetIndicesAsync()
        {
            var documentTypes = _documentConfigs.Select(c => c.DocumentType).Distinct().ToList();
            var result = await Task.WhenAll(documentTypes.Select(_indexingManager.GetIndexStateAsync));
            return Ok(result);
        }

        [HttpGet]
        [Route("all")]
        public async Task<ActionResult<IndexState[]>> GetAllIndicesAsync()
        {
            var documentTypes = _documentConfigs.Select(c => c.DocumentType).Distinct().ToList();
            var indicesResult = await Task.WhenAll(documentTypes.Select(_indexingManager.GetIndicesStateAsync));
            var results = indicesResult.SelectMany(x => x);
            return Ok(results);
        }

        /// <summary>
        /// Get search index for specified document type and document id.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("index/{documentType}/{documentId}")]
        public async Task<ActionResult<IndexDocument[]>> GetDocumentIndexAsync(string documentType, string documentId)
        {
            var request = new SearchRequest
            {
                Filter = new IdsFilter
                {
                    Values = new[] { documentId },
                },
            };

            var result = await _searchProvider.SearchAsync(documentType, request);
            return Ok(result.Documents);
        }

        /// <summary>
        /// Run indexation process for specified options
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [Route("index")]
        [Authorize(ModuleConstants.Security.Permissions.IndexRebuild)]
        public ActionResult<IndexProgressPushNotification> IndexDocuments([FromBody] IndexingOptions[] options)
        {
            var currentUserName = _userNameResolver.GetCurrentUserName();
            var notification = IndexingJobs.Enqueue(currentUserName, options);
            _pushNotifier.Send(notification);
            return Ok(notification);
        }


        [HttpGet]
        [Route("tasks/{taskId}/cancel")]
        public ActionResult CancelIndexationProcess(string taskId)
        {
            IndexingJobs.CancelIndexation();
            return Ok();
        }

        [HttpGet]
        [Route("swapIndexSupported")]
        public ActionResult GetSwapIndexSupported()
        {
            return Ok(new { Result = _searchProvider is ISupportIndexSwap });
        }

        [HttpPost]
        [Route("swapIndex")]
        [Authorize(ModuleConstants.Security.Permissions.IndexRebuild)]
        public async Task<ActionResult> SwapIndexAsync([FromBody] IndexingOptions option)
        {
            if (_searchProvider is ISupportIndexSwap supportIndexSwapSearchProvider)
            {
                await supportIndexSwapSearchProvider.SwapIndexAsync(option.DocumentType);
            }

            return Ok();
        }
    }
}
