﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Store.Azure;
using Lucene.Net.Util;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace NuGet.Indexing
{
    public class NuGetSearcherManager : SearcherManager
    {
        Tuple<IDictionary<string, Filter>, IDictionary<string, Filter>> _filters;
        IDictionary<string, Tuple<OpenBitSet, OpenBitSet>> _latestBitSets;
        IDictionary<string, JArray[]> _versionsByDoc;
        JArray[] _versionListsByDoc;

        public static readonly TimeSpan RankingRefreshRate = TimeSpan.FromHours(24);
        public static readonly TimeSpan DownloadCountRefreshRate = TimeSpan.FromHours(1);
        public static readonly TimeSpan FrameworkCompatibilityRefreshRate = TimeSpan.FromHours(24);

        IndexData<IDictionary<string, IDictionary<string, int>>> _currentRankings;
        IndexData<IDictionary<string, IDictionary<string, int>>> _currentDownloadCounts;
        IndexData<IDictionary<string, ISet<string>>> _currentFrameworkCompatibility;

        public Rankings Rankings { get; private set; }
        public DownloadLookup DownloadCounts { get; private set; }
        public FrameworkCompatibility FrameworkCompatibility { get; private set; }

        public string IndexName { get; private set; }
        public IDictionary<string, Uri> RegistrationBaseAddress { get; private set; }

        public DateTime LastReopen { get; private set; }

        public NuGetSearcherManager(string indexName, Lucene.Net.Store.Directory directory, Rankings rankings, DownloadLookup downloadCounts, FrameworkCompatibility frameworkCompatibility)
            : base(directory)
        {
            Rankings = rankings;
            DownloadCounts = downloadCounts;
            IndexName = indexName;
            FrameworkCompatibility = frameworkCompatibility;

            RegistrationBaseAddress = new Dictionary<string, Uri>();

            _currentDownloadCounts = new IndexData<IDictionary<string, IDictionary<string, int>>>(
                "DownloadCounts",
                DownloadCounts.Path,
                DownloadCounts.Load,
                DownloadCountRefreshRate);
            _currentRankings = new IndexData<IDictionary<string, IDictionary<string, int>>>(
                "Rankings",
                Rankings.Path,
                Rankings.Load,
                RankingRefreshRate);
            _currentFrameworkCompatibility = new IndexData<IDictionary<string,ISet<string>>>(
                "FrameworkCompatibility",
                FrameworkCompatibility.Path,
                FrameworkCompatibility.Load,
                FrameworkCompatibilityRefreshRate
                );
        }

        // /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        public static NuGetSearcherManager CreateAzure(
            string storageConnectionString,
            string indexContainer = null,
            string dataContainer = null)
        {
            return CreateAzure(
                CloudStorageAccount.Parse(storageConnectionString),
                indexContainer,
                dataContainer);
        }
        public static NuGetSearcherManager CreateAzure(
            CloudStorageAccount storageAccount,
            string indexContainer = null,
            string dataContainer = null)
        {
            if (String.IsNullOrEmpty(indexContainer))
            {
                indexContainer = "ng-search-index";
            }

            string dataPath = String.Empty;
            if (String.IsNullOrEmpty(dataContainer))
            {
                dataContainer = indexContainer;
                dataPath = "data/";
            }

            return new NuGetSearcherManager(
                indexContainer,
                new AzureDirectory(storageAccount, indexContainer, new RAMDirectory()),
                new StorageRankings(storageAccount, dataContainer, dataPath + Rankings.FileName),
                new StorageDownloadLookup(storageAccount, dataContainer, dataPath + DownloadLookup.FileName),
                new StorageFrameworkCompatibility(storageAccount, dataContainer, dataPath + FrameworkCompatibility.FileName));
        }

        // /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        public static NuGetSearcherManager CreateLocal(string luceneDirectory, string dataDirectory)
        {
            string rankingsFile = Path.Combine(dataDirectory, Rankings.FileName);
            string downloadCountsFile = Path.Combine(dataDirectory, DownloadLookup.FileName);
            string frameworkCompatibilityFile = Path.Combine(dataDirectory, FrameworkCompatibility.FileName);

            return new NuGetSearcherManager(
                luceneDirectory,
                new SimpleFSDirectory(new DirectoryInfo(luceneDirectory)),
                new LocalRankings(rankingsFile),
                new LocalDownloadLookup(downloadCountsFile),
                new LocalFrameworkCompatibility(frameworkCompatibilityFile));
        }

        // ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////// 

        protected override void Warm(IndexSearcher searcher)
        {
            lock (this)
            {
                searcher.Search(new MatchAllDocsQuery(), 1);

                // Reload download counts and rankings synchronously
                _currentDownloadCounts.Reload();
                _currentRankings.Reload();
                _currentFrameworkCompatibility.Reload();

                // Recalculate all the framework compatibility filters
                _filters = Compatibility.Warm(searcher.IndexReader, _currentFrameworkCompatibility.Value);

                // Recalculate all the latest / latestPrerelease bitSets 
                _latestBitSets = CreateLatestBitSets(searcher.IndexReader, _filters);

                // Recalculate precalculated Versions arrays 
                PackageVersions packageVersions = new PackageVersions(searcher.IndexReader);

                _versionsByDoc = new Dictionary<string, JArray[]>();
                _versionsByDoc["http"] = packageVersions.CreateVersionsLookUp(_currentDownloadCounts.Value, RegistrationBaseAddress["http"]);
                _versionsByDoc["https"] = packageVersions.CreateVersionsLookUp(_currentDownloadCounts.Value, RegistrationBaseAddress["https"]);

                _versionListsByDoc = packageVersions.CreateVersionListsLookUp();

                LastReopen = DateTime.UtcNow;
            }
        }

        public IDictionary<string, int> GetRankings(string name = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                name = "Rank";
            }
            
            return _currentRankings.Value[name];
        }

        public Filter GetFilter(bool includePrerelease, string supportedFramework)
        {
            IDictionary<string, Filter> lookUp = includePrerelease ? _filters.Item2 : _filters.Item1;

            string frameworkFullName;

            if (string.IsNullOrEmpty(supportedFramework))
            {
                frameworkFullName = "any";
            }
            else
            {
                FrameworkName frameworkName = VersionUtility.ParseFrameworkName(supportedFramework);
                frameworkFullName = frameworkName.FullName;
                if (frameworkFullName == "Unsupported,Version=v0.0")
                {
                    try
                    {
                        frameworkName = new FrameworkName(supportedFramework);
                        frameworkFullName = frameworkName.FullName;
                    }
                    catch (ArgumentException)
                    {
                        frameworkFullName = "any";
                    }
                }
            }

            Filter filter;
            if (lookUp.TryGetValue(frameworkFullName, out filter))
            {
                return filter;
            }

            return lookUp["any"];
        }

        public Tuple<OpenBitSet, OpenBitSet> GetBitSets(string supportedFramework)
        {
            string frameworkFullName;

            if (string.IsNullOrEmpty(supportedFramework))
            {
                frameworkFullName = "any";
            }
            else
            {
                FrameworkName frameworkName = VersionUtility.ParseFrameworkName(supportedFramework);
                frameworkFullName = frameworkName.FullName;
                if (frameworkFullName == "Unsupported,Version=v0.0")
                {
                    try
                    {
                        frameworkName = new FrameworkName(supportedFramework);
                        frameworkFullName = frameworkName.FullName;
                    }
                    catch (ArgumentException)
                    {
                        frameworkFullName = "any";
                    }
                }
            }

            Tuple<OpenBitSet, OpenBitSet> result;
            if (_latestBitSets.TryGetValue(frameworkFullName, out result))
            {
                return result;
            }

            return _latestBitSets["any"];
        }

        public JArray GetVersions(string scheme, int doc)
        {
            return _versionsByDoc[scheme][doc];
        }

        public JArray GetVersionLists(int doc)
        {
            return _versionListsByDoc[doc];
        }

        public Tuple<int, int> GetDownloadCounts(string id, string version)
        {
            IDictionary<string, IDictionary<string, int>> idLookUp = _currentDownloadCounts.Value;

            IDictionary<string, int> versionsLookUp;
            if (idLookUp.TryGetValue(id.ToLowerInvariant(), out versionsLookUp))
            {
                int packageCount;
                if (!versionsLookUp.TryGetValue(version, out packageCount))
                {
                    packageCount = 0;
                }

                int registrationCount = versionsLookUp.Values.Sum();

                return Tuple.Create(registrationCount, packageCount);
            }

            return Tuple.Create(0, 0);
        }

        static IDictionary<string, Tuple<OpenBitSet, OpenBitSet>> CreateLatestBitSets(IndexReader reader, Tuple<IDictionary<string, Filter>, IDictionary<string, Filter>> filters)
        {
            IDictionary<string, Tuple<OpenBitSet, OpenBitSet>> result = new Dictionary<string, Tuple<OpenBitSet, OpenBitSet>>();

            foreach (KeyValuePair<string, Filter> item in filters.Item1)
            {
                OpenBitSet item1 = BitSetCollector.CreateBitSet(reader, item.Value);
                OpenBitSet item2 = BitSetCollector.CreateBitSet(reader, filters.Item2[item.Key]);

                result.Add(item.Key, Tuple.Create(item1, item2));
            }

            return result;
        }
    }
}