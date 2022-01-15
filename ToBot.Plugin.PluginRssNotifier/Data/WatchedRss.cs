using System;
using ToBot.Common.Data.Interfaces;

namespace ToBot.Plugin.PluginRssNotifier.Data
{
    public class WatchedRss
        : IObjectId
    {
        public string IdObject { get; set; }

        public string Title { get; set; }

        public string Url { get; set; }

        public int WatchPeriod { get; set; }

        public DateTime LastUpdate { get; set; }

        public DateTime LastPublishDate { get; set; }
    }
}
