﻿using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SearchModule.Core.Model.Filters;
using VirtoCommerce.SearchModule.Core.Model.Indexing;
using VirtoCommerce.SearchModule.Core.Model.Search;
using VirtoCommerce.SearchModule.Core.Model.Search.Criterias;
using Xunit;

namespace VirtoCommerce.SearchModule.Test
{
    [CLSCompliant(false)]
    [Collection("Search")]
    [Trait("Category", "CI")]
    public class SearchTests : SearchTestsBase
    {
        private const string _scope = "test";
        private const string _documentType = "item";

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanCreateIndex(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanUpdateIndex(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType, true);
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanGetOutlines(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(6, results.DocCount);
            Assert.Equal(6, results.TotalCount);

            int outlineCount;
            var outlineObject = results.Documents.First()["__outline"]; // can be JArray or object[] depending on provider used
            if (outlineObject is JArray)
                outlineCount = (outlineObject as JArray).Count;
            else
                outlineCount = ((object[])outlineObject).Length;

            Assert.True(outlineCount == 2, $"Returns {outlineCount} outlines instead of 2");
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanSort(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                Sort = new SearchSort("name"),
                RecordsToRetrieve = 1,
            };

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(1, results.DocCount);
            Assert.Equal(6, results.TotalCount);

            var productName = results.Documents.First()["name"] as string;
            Assert.Equal("Black Sox", productName);

            criteria = new KeywordSearchCriteria(_documentType)
            {
                Sort = new SearchSort("name", true),
                RecordsToRetrieve = 1,
            };

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(1, results.DocCount);
            Assert.Equal(6, results.TotalCount);

            productName = results.Documents.First()["name"] as string;
            Assert.Equal("Sample Product", productName);
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanSearchByIds(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                Ids = new[] { "red3", "another" },
                RecordsToRetrieve = 10,
            };

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(2, results.DocCount);
            Assert.Equal(2, results.TotalCount);

            Assert.True(results.Documents.Any(d => (string)d.Id == "red3"), "Cannot find 'red3'");
            Assert.True(results.Documents.Any(d => (string)d.Id == "another"), "Cannot find 'another'");
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanSearchByPhrase(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                SearchPhrase = " shirt ",
                RecordsToRetrieve = 10,
            };

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(3, results.DocCount);
            Assert.Equal(3, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                SearchPhrase = "red shirt",
                RecordsToRetrieve = 1,
            };

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(1, results.DocCount);
            Assert.Equal(2, results.TotalCount);
        }

        [Theory]
        [InlineData("Lucene", 5)]
        [InlineData("Elastic", 5)]
        [InlineData("Azure", 3)] // Azure does not support collections with non-string elements
        public void CanFilterByPriceWithoutAnyPricelist(string providerType, long expectedDocumentsCount)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                RecordsToRetrieve = 10,
            };

            var priceRangefilter = new PriceRangeFilter
            {
                Currency = "USD",
                Values = new[]
                {
                    new RangeFilterValue { Upper = "100" },
                    new RangeFilterValue { Lower = "700" },
                }
            };

            criteria.Apply(priceRangefilter);

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(expectedDocumentsCount, results.DocCount);
            Assert.Equal(expectedDocumentsCount, results.TotalCount);
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanFilter(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            var stringFilter = new AttributeFilter
            {
                Key = "Color",
                Values = new[]
                {
                    new AttributeFilterValue { Value = "White" }, // Non-existent value
                    new AttributeFilterValue { Value = "Green" }, // Non-existent value
                }
            };

            criteria.Apply(stringFilter);

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(0, results.DocCount);
            Assert.Equal(0, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            stringFilter = new AttributeFilter
            {
                Key = "Color",
                Values = new[]
              {
                    new AttributeFilterValue { Value = "Red" },
                    new AttributeFilterValue { Value = "Blue" },
                    new AttributeFilterValue { Value = "Black" },
                }
            };

            criteria.Apply(stringFilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(5, results.DocCount);
            Assert.Equal(5, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            var numericFilter = new AttributeFilter
            {
                Key = "Size",
                Values = new[]
                {
                    new AttributeFilterValue { Value = "1" },
                    new AttributeFilterValue { Value = "2" },
                    new AttributeFilterValue { Value = "3" },
                }
            };

            criteria.Apply(numericFilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(2, results.DocCount);
            Assert.Equal(2, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            var rangefilter = new RangeFilter
            {
                Key = "Size",
                Values = new[]
                {
                    new RangeFilterValue { Lower = "0", Upper = "5" },
                    new RangeFilterValue { Lower = "5", Upper = "10" },
                }
            };

            criteria.Apply(rangefilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(4, results.DocCount);
            Assert.Equal(4, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "default" },
                RecordsToRetrieve = 10,
            };

            var priceRangefilter = new PriceRangeFilter
            {
                Currency = "USD",
                Values = new[]
                {
                    new RangeFilterValue { Upper = "100" },
                    new RangeFilterValue { Lower = "700" },
                }
            };

            criteria.Apply(priceRangefilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(3, results.DocCount);
            Assert.Equal(3, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "default", "sale" },
                RecordsToRetrieve = 10,
            };

            criteria.Apply(priceRangefilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(3, results.DocCount);
            Assert.Equal(3, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "sale", "default" },
                RecordsToRetrieve = 10,
            };

            criteria.Apply(priceRangefilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(4, results.DocCount);
            Assert.Equal(4, results.TotalCount);


            criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "supersale", "sale", "default" },
                RecordsToRetrieve = 10,
            };

            criteria.Apply(priceRangefilter);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(5, results.DocCount);
            Assert.Equal(5, results.TotalCount);
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        [InlineData("Azure")]
        public void CanGetFacets(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "default" },
                RecordsToRetrieve = 0,
            };

            var attributeFacet = new AttributeFilter
            {
                Key = "Color",
                Values = new[]
                {
                    new AttributeFilterValue { Id = "Red", Value = "Red" },
                    new AttributeFilterValue { Id = "Blue", Value = "Blue" },
                    new AttributeFilterValue { Id = "Black", Value = "Black" },
                }
            };

            var rangeFacet = new RangeFilter
            {
                Key = "Size",
                Values = new[]
                {
                    new RangeFilterValue { Id = "5_to_10", Lower = "5", Upper = "10" },
                    new RangeFilterValue { Id = "0_to_5", Lower = "0", Upper = "5" },
                }
            };

            var priceRangeFacet = new PriceRangeFilter
            {
                Currency = "USD",
                Values = new[]
                {
                    new RangeFilterValue { Id = "0_to_100", Lower = "0", Upper = "100" },
                    new RangeFilterValue { Id = "100_to_700", Lower = "100", Upper = "700" },
                    new RangeFilterValue { Id = "over_700", Lower = "700" },
                    new RangeFilterValue { Id = "under_100", Upper = "100" },
                }
            };

            criteria.Add(attributeFacet);
            criteria.Add(rangeFacet);
            criteria.Add(priceRangeFacet);

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(0, results.DocCount);

            var redCount = GetFacetCount(results, "Color", "Red");
            Assert.True(redCount == 3, $"Returns {redCount} facets of red instead of 3");

            var sizeCount = GetFacetCount(results, "Size", "0_to_5");
            Assert.True(sizeCount == 3, $"Returns {sizeCount} facets of 0_to_5 size instead of 3");

            var sizeCount2 = GetFacetCount(results, "Size", "5_to_10");
            Assert.True(sizeCount2 == 1, $"Returns {sizeCount2} facets of 5_to_10 size instead of 1"); // only 1 result because upper bound is not included

            var priceCount = GetFacetCount(results, "Price", "0_to_100");
            Assert.True(priceCount == 2, $"Returns {priceCount} facets of 0_to_100 prices instead of 2");

            var priceCount2 = GetFacetCount(results, "Price", "100_to_700");
            Assert.True(priceCount2 == 3, $"Returns {priceCount2} facets of 100_to_700 prices instead of 3");

            var priceCount3 = GetFacetCount(results, "Price", "over_700");
            Assert.True(priceCount3 == 1, $"Returns {priceCount3} facets of over_700 prices instead of 1");

            var priceCount4 = GetFacetCount(results, "Price", "under_100");
            Assert.True(priceCount4 == 2, $"Returns {priceCount4} facets of priceCount4 prices instead of 2");
        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        //[InlineData("Azure")] // Azure does not support filters for individual facets
        public void CanGetPriceFacetsForMultiplePricelists(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "default", "sale" },
                RecordsToRetrieve = 10,
            };

            var priceRangeFacet = new PriceRangeFilter
            {
                Currency = "USD",
                Values = new[]
                {
                    new RangeFilterValue {Id = "0_to_100", Lower = "0", Upper = "100"},
                    new RangeFilterValue {Id = "100_to_700", Lower = "100", Upper = "700"},
                }
            };

            criteria.Add(priceRangeFacet);

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.True(results.DocCount == 6, $"Returns {results.DocCount} instead of 6");

            var priceCount = GetFacetCount(results, "Price", "0_to_100");
            Assert.True(priceCount == 2, $"Returns {priceCount} facets of 0_to_100 prices instead of 2");

            var priceCount2 = GetFacetCount(results, "Price", "100_to_700");
            Assert.True(priceCount2 == 3, $"Returns {priceCount2} facets of 100_to_700 prices instead of 3");


            criteria = new KeywordSearchCriteria(_documentType)
            {
                Currency = "USD",
                Pricelists = new[] { "sale", "default" },
                RecordsToRetrieve = 10,
            };

            criteria.Add(priceRangeFacet);

            results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.True(results.DocCount == 6, $"\"Sample Product\" search returns {results.DocCount} instead of 6");

            var priceSaleCount = GetFacetCount(results, "Price", "0_to_100");
            Assert.True(priceSaleCount == 3, $"Returns {priceSaleCount} facets of 0_to_100 prices instead of 2");

            var priceSaleCount2 = GetFacetCount(results, "Price", "100_to_700");
            Assert.True(priceSaleCount2 == 2, $"Returns {priceSaleCount2} facets of 100_to_700 prices instead of 3");

        }

        [Theory]
        [InlineData("Lucene")]
        [InlineData("Elastic")]
        //[InlineData("Azure")] // Azure applies filters before calculating facets
        public void CanGetAllFacetValuesWhenFilterIsApplied(string providerType)
        {
            var provider = GetSearchProvider(providerType, _scope);
            SearchTestsHelper.CreateSampleIndex(provider, _scope, _documentType);

            var criteria = new KeywordSearchCriteria(_documentType)
            {
                RecordsToRetrieve = 10,
            };

            var facet = new AttributeFilter
            {
                Key = "Color",
                Values = new[]
                {
                    new AttributeFilterValue {Id = "Red", Value = "Red"},
                    new AttributeFilterValue {Id = "Blue", Value = "Blue"},
                    new AttributeFilterValue {Id = "Black", Value = "Black"}
                }
            };

            var filter = new AttributeFilter
            {
                Key = "Color",
                Values = new[]
                {
                    new AttributeFilterValue {Id = "Red", Value = "Red"}
                }
            };

            criteria.Add(facet);
            criteria.Apply(filter);

            var results = provider.Search<DocumentDictionary>(_scope, criteria);

            Assert.Equal(3, results.DocCount);
            Assert.Equal(3, results.TotalCount);

            var redCount = GetFacetCount(results, "Color", "Red");
            Assert.True(redCount == 3, $"Returns {redCount} facets of Red instead of 3");

            var blueCount = GetFacetCount(results, "Color", "Blue");
            Assert.True(blueCount == 1, $"Returns {blueCount} facets of Blue instead of 1");

            var blackCount = GetFacetCount(results, "Color", "Black");
            Assert.True(blackCount == 1, $"Returns {blackCount} facets of Black instead of 1");
        }
    }
}
