﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using NuGet.Versioning;
using System;
using System.Collections.Generic;

namespace NuGet.Indexing
{
    public static class LatestVersionFilter
    {
        public static Filter Create(IndexReader indexReader, bool includePrerelease, bool includeUnlisted)
        {
            IDictionary<string, OpenBitSet> openBitSetLookup = new Dictionary<string, OpenBitSet>();

            foreach (SegmentReader segmentReader in indexReader.GetSequentialSubReaders())
            {
                openBitSetLookup.Add(segmentReader.SegmentName, new OpenBitSet());
            }

            IDictionary<string, Tuple<NuGetVersion, string, int>> lookup = MakeLatestVersionLookup(indexReader, includePrerelease, includeUnlisted);

            foreach (Tuple<NuGetVersion, string, int> entry in lookup.Values)
            {
                string segmentName = entry.Item2;
                int segmentDocumentId = entry.Item3;

                openBitSetLookup[segmentName].Set(segmentDocumentId);
            }

            return new OpenBitSetLookupFilter(openBitSetLookup);
        }

        static IDictionary<string, Tuple<NuGetVersion, string, int>> MakeLatestVersionLookup(IndexReader indexReader, bool includePrerelease, bool includeUnlisted)
        {
            IDictionary<string, Tuple<NuGetVersion, string, int>> lookup = new Dictionary<string, Tuple<NuGetVersion, string, int>>();

            foreach (SegmentReader segmentReader in indexReader.GetSequentialSubReaders())
            {
                for (int n = 0; n < segmentReader.MaxDoc; n++)
                {
                    if (segmentReader.IsDeleted(n))
                    {
                        continue;
                    }

                    Document document = segmentReader.Document(n);

                    NuGetVersion version = GetVersion(document);

                    if (version == null)
                    {
                        continue;
                    }

                    bool isListed = GetListed(document);

                    if (isListed || includeUnlisted)
                    {
                        if (!version.IsPrerelease || includePrerelease)
                        {
                            string id = GetId(document);

                            if (id == null)
                            {
                                continue;
                            }

                            Tuple<NuGetVersion, string, int> existingVersion;
                            if (lookup.TryGetValue(id, out existingVersion))
                            {
                                if (version > existingVersion.Item1)
                                {
                                    lookup[id] = Tuple.Create(version, segmentReader.SegmentName, n);
                                }
                            }
                            else
                            {
                                lookup.Add(id, Tuple.Create(version, segmentReader.SegmentName, n));
                            }
                        }
                    }
                }
            }

            return lookup;
        }

        static NuGetVersion GetVersion(Document document)
        {
            string version = document.Get("Version");
            return (version == null) ? null : new NuGetVersion(version);
        }

        static bool GetListed(Document document)
        {
            string listed = document.Get("Listed");
            return (listed == null) ? false : listed.Equals("true", StringComparison.InvariantCultureIgnoreCase);
        }

        static string GetId(Document document)
        {
            string id = document.Get("Id");
            string ns = document.Get("Namespace");
            string fullname = (ns == null) ? id : string.Format("{0}/{1}", ns, id);
            return (fullname == null) ? null : fullname.ToLowerInvariant();
        }
    }
}
