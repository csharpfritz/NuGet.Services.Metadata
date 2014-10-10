﻿using System;

namespace NuGet.Services.Metadata.Catalog.Maintenance
{
    public abstract class AppendOnlyCatalogItem : CatalogItem
    {
        public Uri GetBaseAddress()
        {
            return new Uri(BaseAddress, "data/" + MakeTimeStampPathComponent(TimeStamp));
        }

        protected virtual string GetItemIdentity()
        {
            return string.Empty;
        }

        public string GetRelativeAddress()
        {
            return GetItemIdentity() + ".json";
        }

        public override Uri GetItemAddress()
        {
            return new Uri(GetBaseAddress(), GetRelativeAddress());
        }

        protected static string MakeTimeStampPathComponent(DateTime timeStamp)
        {
            return string.Format("{0:0000}.{1:00}.{2:00}.{3:00}.{4:00}.{5:00}/", timeStamp.Year, timeStamp.Month, timeStamp.Day, timeStamp.Hour, timeStamp.Minute, timeStamp.Second);
        }
    }
}
