﻿using Newtonsoft.Json.Linq;
using NuGet.Services.Metadata.Catalog.Persistence;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using VDS.RDF;

namespace NuGet.Services.Metadata.Catalog.Maintenance
{
    public abstract class CatalogWriterBase : IDisposable
    {
        List<CatalogItem> _batch;
        bool _open;

        public CatalogWriterBase(Storage storage, CatalogContext context = null)
        {
            Options.InternUris = false;

            Storage = storage;
            _batch = new List<CatalogItem>();
            _open = true;

            Context = context ?? new CatalogContext();

            RootUri = Storage.ResolveUri("index.json");
        }
        public void Dispose()
        {
            _batch.Clear();
            _open = false;
        }

        public Storage Storage { get; private set; }

        public Uri RootUri { get; private set; }

        public CatalogContext Context { get; private set; }

        public int Count { get { return _batch.Count; } }

        public void Add(CatalogItem item)
        {
            if (!_open)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            _batch.Add(item);
        }
        public Task Commit(IGraph commitMetadata = null)
        {
            return Commit(DateTime.UtcNow, commitMetadata);
        }

        public async Task Commit(DateTime commitTimeStamp, IGraph commitMetadata = null)
        {
            if (!_open)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (_batch.Count == 0)
            {
                return;
            }

            Guid commitId = Guid.NewGuid();

            IDictionary<string, CatalogItemSummary> newItemEntries = await SaveItems(commitId, commitTimeStamp);

            IDictionary<string, CatalogItemSummary> pageEntries = await SavePages(commitId, commitTimeStamp, newItemEntries);

            await SaveRoot(commitId, commitTimeStamp, pageEntries, commitMetadata);

            _batch.Clear();
        }

        async Task<IDictionary<string, CatalogItemSummary>> SaveItems(Guid commitId, DateTime commitTimeStamp)
        {
            IDictionary<string, CatalogItemSummary> pageItems = new Dictionary<string, CatalogItemSummary>();
            List<Task> tasks = null;

            Uri resourceUri = null;
            int batchIndex = 0;

            foreach (CatalogItem item in _batch)
            {
                try
                {
                    item.SetTimeStamp(commitTimeStamp);
                    item.SetCommitId(commitId);
                    item.SetBaseAddress(Storage.BaseAddress);

                    StorageContent content = item.CreateContent(Context);
                    IGraph pageContent = item.CreatePageContent(Context);

                    resourceUri = item.GetItemAddress();

                    if (content != null)
                    {
                        if (tasks == null)
                        {
                            tasks = new List<Task>();
                        }

                        tasks.Add(Storage.Save(resourceUri, content));
                    }

                    pageItems.Add(resourceUri.ToString(), new CatalogItemSummary(item.GetItemType(), commitId, commitTimeStamp, null, pageContent));

                    batchIndex++;
                }
                catch (Exception e)
                {
                    throw new Exception(string.Format("item uri: {0} batch index: {1}", resourceUri, batchIndex), e);
                }
            }

            if (tasks != null)
            {
                await Task.WhenAll(tasks.ToArray());
            }

            return pageItems;
        }

        async Task SaveRoot(Guid commitId, DateTime commitTimeStamp, IDictionary<string, CatalogItemSummary> pageEntries, IGraph commitMetadata)
        {
            await SaveIndexResource(RootUri, Schema.DataTypes.CatalogRoot, commitId, commitTimeStamp, pageEntries, commitMetadata);
        }

        protected abstract Task<IDictionary<string, CatalogItemSummary>> SavePages(Guid commitId, DateTime commitTimeStamp, IDictionary<string, CatalogItemSummary> itemEntries);

        protected virtual StorageContent CreateIndexContent(IGraph graph, Uri type)
        {
            JObject frame = Context.GetJsonLdContext("context.Container.json", type);
            return new StringStorageContent(Utils.CreateJson(graph, frame), "application/json", "no-store");
        }

        protected async Task SaveIndexResource(Uri resourceUri, Uri typeUri, Guid commitId, DateTime commitTimeStamp, IDictionary<string, CatalogItemSummary> entries, IGraph extra)
        {
            IGraph graph = new Graph();

            INode resourceNode = graph.CreateUriNode(resourceUri);
            INode itemPredicate = graph.CreateUriNode(Schema.Predicates.CatalogItem);
            INode typePredicate = graph.CreateUriNode(Schema.Predicates.Type);
            INode timeStampPredicate = graph.CreateUriNode(Schema.Predicates.CatalogTimeStamp);
            INode commitIdPredicate = graph.CreateUriNode(Schema.Predicates.CatalogCommitId);
            INode countPredicate = graph.CreateUriNode(Schema.Predicates.CatalogCount);

            graph.Assert(resourceNode, typePredicate, graph.CreateUriNode(typeUri));
            graph.Assert(resourceNode, commitIdPredicate, graph.CreateLiteralNode(commitId.ToString()));
            graph.Assert(resourceNode, timeStampPredicate, graph.CreateLiteralNode(commitTimeStamp.ToString("O"), Schema.DataTypes.DateTime));
            graph.Assert(resourceNode, countPredicate, graph.CreateLiteralNode(entries.Count.ToString(), Schema.DataTypes.Integer));

            foreach (KeyValuePair<string, CatalogItemSummary> itemEntry in entries)
            {
                INode itemNode = graph.CreateUriNode(new Uri(itemEntry.Key));

                graph.Assert(resourceNode, itemPredicate, itemNode);
                graph.Assert(itemNode, typePredicate, graph.CreateUriNode(itemEntry.Value.Type));
                graph.Assert(itemNode, commitIdPredicate, graph.CreateLiteralNode(itemEntry.Value.CommitId.ToString()));
                graph.Assert(itemNode, timeStampPredicate, graph.CreateLiteralNode(itemEntry.Value.CommitTimeStamp.ToString("O"), Schema.DataTypes.DateTime));

                if (itemEntry.Value.Count != null)
                {
                    graph.Assert(itemNode, countPredicate, graph.CreateLiteralNode(itemEntry.Value.Count.ToString(), Schema.DataTypes.Integer));
                }

                if (itemEntry.Value.Content != null)
                {
                    graph.Merge(itemEntry.Value.Content, true);
                }
            }

            if (extra != null)
            {
                graph.Merge(extra, true);
            }

            await SaveGraph(resourceUri, graph, typeUri);
        }

        protected async Task<IDictionary<string, CatalogItemSummary>> LoadIndexResource(Uri resourceUri)
        {
            IDictionary<string, CatalogItemSummary> entries = new Dictionary<string, CatalogItemSummary>();

            IGraph graph = await LoadGraph(resourceUri);

            if (graph == null)
            {
                return entries;
            }

            INode typePredicate = graph.CreateUriNode(Schema.Predicates.Type);
            INode itemPredicate = graph.CreateUriNode(Schema.Predicates.CatalogItem);
            INode timeStampPredicate = graph.CreateUriNode(Schema.Predicates.CatalogTimeStamp);
            INode commitIdPredicate = graph.CreateUriNode(Schema.Predicates.CatalogCommitId);
            INode countPredicate = graph.CreateUriNode(Schema.Predicates.CatalogCount);

            INode resourceNode = graph.CreateUriNode(resourceUri);

            foreach (INode itemNode in graph.GetTriplesWithSubjectPredicate(resourceNode, itemPredicate).Select((t) => t.Object))
            {
                Triple typeTriple = graph.GetTriplesWithSubjectPredicate(itemNode, typePredicate).First();
                Uri type = ((IUriNode)typeTriple.Object).Uri;

                Triple commitIdTriple = graph.GetTriplesWithSubjectPredicate(itemNode, commitIdPredicate).First();
                Guid commitId = Guid.Parse(((ILiteralNode)commitIdTriple.Object).Value);

                Triple timeStampTriple = graph.GetTriplesWithSubjectPredicate(itemNode, timeStampPredicate).First();
                DateTime timeStamp = DateTime.Parse(((ILiteralNode)timeStampTriple.Object).Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                Triple countTriple = graph.GetTriplesWithSubjectPredicate(itemNode, countPredicate).FirstOrDefault();
                int? count = (countTriple != null) ? int.Parse(((ILiteralNode)countTriple.Object).Value) : (int?)null;

                IGraph itemContent = null;
                INode itemContentSubjectNode = null;
                foreach (Triple itemContentTriple in graph.GetTriplesWithSubject(itemNode))
                {
                    if (itemContentTriple.Predicate.Equals(typePredicate))
                    {
                        continue;
                    }
                    if (itemContentTriple.Predicate.Equals(timeStampPredicate))
                    {
                        continue;
                    }
                    if (itemContentTriple.Predicate.Equals(commitIdPredicate))
                    {
                        continue;
                    }
                    if (itemContentTriple.Predicate.Equals(countPredicate))
                    {
                        continue;
                    }

                    if (itemContent == null)
                    {
                        itemContent = new Graph();
                        itemContentSubjectNode = itemContentTriple.Subject.CopyNode(itemContent, false);
                    }

                    INode itemContentPredicateNode = itemContentTriple.Predicate.CopyNode(itemContent, false);
                    INode itemContentObjectNode = itemContentTriple.Object.CopyNode(itemContent, false);

                    itemContent.Assert(itemContentSubjectNode, itemContentPredicateNode, itemContentObjectNode);

                    if (itemContentTriple.Object is IUriNode)
                    {
                        CopyGraph(itemContentTriple.Object, graph, itemContent);
                    }
                }

                entries.Add(itemNode.ToString(), new CatalogItemSummary(type, commitId, timeStamp, count, itemContent));
            }

            return entries;
        }

        void CopyGraph(INode sourceNode, IGraph source, IGraph target)
        {
            foreach (Triple triple in source.GetTriplesWithSubject(sourceNode))
            {
                if (target.Assert(triple.CopyTriple(target)) && triple.Object is IUriNode)
                {
                    CopyGraph(triple.Object, source, target);
                }
            }
        }

        protected virtual async Task SaveGraph(Uri resourceUri, IGraph graph, Uri typeUri)
        {
            await Storage.Save(resourceUri, CreateIndexContent(graph, typeUri));
        }

        protected virtual async Task<IGraph> LoadGraph(Uri resourceUri)
        {
            string content = await Storage.LoadString(resourceUri);

            if (content == null)
            {
                return null;
            }

            return Utils.CreateGraph(content);
        }
    }
}
