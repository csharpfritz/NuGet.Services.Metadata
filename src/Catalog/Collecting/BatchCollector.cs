﻿using JsonLD.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace NuGet.Services.Metadata.Catalog.Collecting
{
    public abstract class BatchCollector : Collector
    {
        int _batchSize;

        public BatchCollector(int batchSize)
        {
            _batchSize = batchSize;
        }

        public int BatchCount
        {
            private set;
            get;
        }

        public event Action<CollectorCursor> ProcessedCommit;

        protected override async Task<CollectorCursor> Fetch(CollectorHttpClient client, Uri index, CollectorCursor startFrom)
        {
            CollectorCursor cursor = startFrom;
            
            IList<JObject> items = new List<JObject>();

            JObject root = await client.GetJObjectAsync(index);

            JToken context = null;
            root.TryGetValue("@context", out context);

            IEnumerable<JToken> rootItems = root["items"].OrderBy(item => item["commitTimestamp"].ToObject<DateTime>());

            foreach (JObject rootItem in rootItems)
            {
                CollectorCursor pageCursor = (CollectorCursor)rootItem["commitTimestamp"].ToObject<DateTime>();

                if (pageCursor > startFrom)
                {
                    Uri pageUri = rootItem["url"].ToObject<Uri>();
                    JObject page = await client.GetJObjectAsync(pageUri);

                    IEnumerable<JToken> pageItems = page["items"].OrderBy(item => item["commitTimestamp"].ToObject<DateTime>());

                    foreach (JObject pageItem in pageItems)
                    {
                        CollectorCursor itemCursor = (CollectorCursor)pageItem["commitTimestamp"].ToObject<DateTime>();

                        if (itemCursor > startFrom)
                        {
                            if (itemCursor > cursor)
                            {
                                // Item timestamp is higher than the previous cursor, so report the previous commit as "processed"
                                OnProcessedCommit(cursor);
                            }
                            // Update the cursor
                            cursor = itemCursor;

                            Uri itemUri = pageItem["url"].ToObject<Uri>();

                            items.Add(pageItem);

                            if (items.Count == _batchSize)
                            {
                                await ProcessBatch(client, items, (JObject)context);
                                BatchCount++;
                                items.Clear();
                            }
                        }
                    }
                }
            }

            if (items.Count > 0)
            {
                await ProcessBatch(client, items, (JObject)context);
                BatchCount++;
            }

            return cursor;
        }

        protected virtual void OnProcessedCommit(CollectorCursor cursor)
        {
            var handler = ProcessedCommit;
            if (handler != null)
            {
                handler(cursor);
            }
        }

        protected abstract Task ProcessBatch(CollectorHttpClient client, IList<JObject> items, JObject context);
    }
}
