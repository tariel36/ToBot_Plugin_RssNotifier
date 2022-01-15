// The MIT License (MIT)
//
// Copyright (c) 2022 tariel36
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ToBot.Common.Attributes;
using ToBot.Common.Extensions;
using ToBot.Common.Maintenance;
using ToBot.Common.Maintenance.Logging;
using ToBot.Common.Pocos;
using ToBot.Common.Watchers;
using ToBot.Communication.Commands;
using ToBot.Communication.Messaging.Formatters;
using ToBot.Communication.Messaging.Providers;
using ToBot.Data.Repositories;
using ToBot.Plugin.PluginRssNotifier.Data;
using ToBot.Plugin.Plugins;
using ToBot.Rss.Clients;
using ToBot.Rss.Pocos;
using System.Net;
using System.IO;

namespace ToBot.Plugin.PluginRssNotifier
{
    public class RssNotifier
        : BasePlugin
    {
        public const string Prefix = "rn";

        private int MinimumPeriod = 1;
        private IReadOnlyList<string> LiveStreamIndicators = new List<string>()
        {
            "transmisja zaczęła się",
            "started streaming"
        };

        public RssNotifier(IRepository repository,
            ILogger logger,
            IMessageFormatter messageFormatter,
            IEmoteProvider emoteProvider,
            string commandsPrefix,
            ExceptionHandler exceptionHandler)
            : base(repository, logger, messageFormatter, emoteProvider, commandsPrefix)
        {
            NotificationSubscribers = new ConcurrentDictionary<ulong, SubscribedChannel>();
            Entries = new ConcurrentDictionary<string, WatchedRss>();
            ContentWatcher = new ContentWatcher(logger, exceptionHandler, ContentWatcherProcedure, TimeSpan.FromMinutes(1.0), Name);

            Repository.PropertyMapper
                .Id<WatchedRss, string>(x => x.IdObject, false)
                .Id<SubscribedChannel, string>(x => x.IdObject, false)
                ;

            repository.AddInvocator<SubscribedChannel>();
            repository.AddInvocator<WatchedRss>();
        }

        private ContentWatcher ContentWatcher { get; }

        private ConcurrentDictionary<ulong, SubscribedChannel> NotificationSubscribers { get; }

        private ConcurrentDictionary<string, WatchedRss> Entries { get; }

        private bool AppendColonToTitle { get; }

        public override void Start()
        {
            Logger.LogMessage(LogLevel.Debug, Name, $"Starting plugin `{Name}`");

            ContentWatcher.Start();

            List<SubscribedChannel> channels = Repository.TryGetItems<SubscribedChannel>(x => string.Equals(x.Source, Name));

            if (channels?.Count > 0)
            {
                foreach (SubscribedChannel channel in channels)
                {
                    NotificationSubscribers[channel.ChannelId] = channel;
                    Logger.LogMessage(LogLevel.Debug, Name, $"Subscribed channel with id `{channel.ChannelId}` for plugin `{Name}`");
                }
            }
            else
            {
                Logger.LogMessage(LogLevel.Debug, Name, $"No registered channels for plugin `{Name}`");
            }

            foreach (WatchedRss entry in Repository.TryGetItems<WatchedRss>(x => true))
            {
                Entries[entry.IdObject] = entry;
            }

            Logger.LogMessage(LogLevel.Debug, Name, $"Plugin `{Name}` started, subscribed channels count: `{NotificationSubscribers.Count}`, entries count: `{Entries.Count}`");
        }

        public override void Stop()
        {
            ContentWatcher.Stop();
        }

        public override void Dispose()
        {
            ContentWatcher.SafeDispose();

            base.Dispose();
        }

        #region Notify

        [IsCommand]
        public async Task Notify(CommandExecutionContext ctx)
        {
            if (ctx == null)
            {
                return;
            }

            ulong channelId = ctx.Context.ChannelId;

            bool isEnabled = false;

            SubscribedChannel channel;

            if (NotificationSubscribers.ContainsKey(channelId))
            {
                NotificationSubscribers.TryRemove(channelId, out channel);

                Repository.DeleteItem(channel);
            }
            else
            {
                isEnabled = true;
                channel = new SubscribedChannel(channelId, Name);
                NotificationSubscribers.TryAdd(channelId, channel);

                Repository.SetItem(channel);
            }

            string channelName = ctx.Context.ChannelName;

            await ctx.Context.RespondStringAsync($"Notifications are now {(isEnabled ? "enabled" : "disabled")} on {MessageFormatter.Bold(channelName)}.");
        }

        #endregion

        #region Test

        [IsCommand]
        public async Task Test(CommandExecutionContext ctx)
        {
            Logger.LogMessage(LogLevel.Debug, Name, $"Plugin `{Name}` invoked `Test` command.");

            ContentWatcherProcedure(null);

            await Task.CompletedTask;
        }

        #endregion

        #region Watch

        [IsCommand(new[] { "string;channelId", "int;period;0" })]
        public async Task Watch(CommandExecutionContext ctx)
        {
            string channelId = ctx.Arguments.Get<string>(nameof(channelId));
            int period = ctx.Arguments.Get<int>(nameof(period));

            WatchedRss watchedRss = new WatchedRss()
            {
                IdObject = channelId,
                WatchPeriod = period < MinimumPeriod ? MinimumPeriod : period,
                LastPublishDate = DateTime.MinValue,
                LastUpdate = DateTime.UtcNow,
                Title = string.Empty,
                Url = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}"
            };

            if (Entries.ContainsKey(watchedRss.IdObject))
            {
                await ctx.Context.RespondStringAsync($"Channel {watchedRss.Title} ({MessageFormatter.Block(watchedRss.IdObject)}) - {MessageFormatter.NoEmbed(watchedRss.Url)} - is already monitored.");
                return;
            }

            RssClient rssClient = new RssClient();

            RssEntryCollection collection = rssClient.Get(watchedRss.Url);
            RssEntry last = collection.Entries
                .OrderByDescending(x => x.PublishDate)
                .FirstOrDefault();

            watchedRss.Title = collection.Title;
            watchedRss.LastPublishDate = last.PublishDate ?? DateTime.UtcNow;

            Repository.SetItem(watchedRss);
            Entries[watchedRss.IdObject] = watchedRss;

            await ctx.Context.RespondStringAsync($"You're monitoring {watchedRss.Title} ({MessageFormatter.Block(watchedRss.IdObject)}) - {MessageFormatter.NoEmbed(watchedRss.Url)} now.");
        }

        #endregion

        #region Remove

        [IsCommand(new[] { "string;channelId" })]
        public async Task Remove(CommandExecutionContext ctx)
        {
            string channelId = ctx.Arguments.Get<string>(nameof(channelId));

            if (!Entries.ContainsKey(channelId))
            {
                await ctx.Context.RespondStringAsync($"Channel {channelId} is not monitored.");
                return;
            }

            Repository.DeleteItem(Entries[channelId]);
            Entries.TryRemove(channelId, out WatchedRss watchedRss);

            await ctx.Context.RespondStringAsync($"Channel {watchedRss.Title} ({MessageFormatter.Block(watchedRss.IdObject)}) - {MessageFormatter.NoEmbed(watchedRss.Url)} - has been removed.");
        }

        #endregion

        #region List

        [IsCommand]
        public async Task List(CommandExecutionContext ctx)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Monitored channels:");

            foreach (WatchedRss entry in Entries.Values)
            {
                sb.AppendLine($"Channel {entry.Title} ({MessageFormatter.Block(entry.IdObject)}) - {MessageFormatter.NoEmbed(entry.Url)} ;");
            }

            await ctx.Context.RespondStringAsync(sb.ToString());
        }

        #endregion

        private void ContentWatcherProcedure(ContentWatcherContext ctx)
        {
            List<Tuple<WatchedRss, RssEntry>> toNotify = new List<Tuple<WatchedRss, RssEntry>>();

            RssClient rssClient = new RssClient();

            foreach (WatchedRss entry in Entries.Values)
            {
                if (entry.LastUpdate.AddMinutes(entry.WatchPeriod) <= DateTime.UtcNow)
                {
                    entry.LastUpdate = DateTime.UtcNow;

                    RssEntryCollection collection = rssClient.Get(entry.Url);
                    RssEntry lastLive = collection.Entries
                        .Where(x => true /* TODO Use IsLive if it will work during testing period. */)
                        .OrderByDescending(x => x.PublishDate)
                        .FirstOrDefault();
                        
                    if (lastLive != null && lastLive.PublishDate > entry.LastPublishDate)
                    {
                        entry.LastPublishDate = lastLive.PublishDate ?? DateTime.UtcNow;

                        toNotify.Add(Tuple.Create(entry, lastLive));
                    }

                    Repository.SetItem(entry);
                }
            }

            Logger.LogMessage(LogLevel.Info, nameof(RssNotifier), $"New `{Name}` entries {toNotify.Count}");

            if (toNotify.Count < 1)
            {
                return;
            }

            StringBuilder sb = new StringBuilder(10240);

            sb.AppendLine("New entries have appeared:")
                .AppendLine();

            foreach (Tuple<WatchedRss, RssEntry> tuple in toNotify)
            {
                bool? isLive = IsLive(tuple.Item2);

                sb.AppendLine($"{tuple.Item1.Title} is streaming (status IsLive: `{(isLive.HasValue ? isLive.Value.ToString() : "NULL")}`): {MessageFormatter.NoEmbed(tuple.Item2.Link)}");
            }

            string message = sb.ToString();

            foreach (SubscribedChannel channel in NotificationSubscribers.Values)
            {
                OnNotification(new NotificationContext() { Message = message, ChannelId = channel.ChannelId });
            }
        }

        private bool? IsLive(RssEntry entry)
        {
            try
            {
                string html = string.Empty;
                string url = entry.Link;

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.AutomaticDecompression = DecompressionMethods.GZip;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (Stream stream = response.GetResponseStream())
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            html = reader.ReadToEnd();
                        }
                    }
                }

                html = html.ToLower();

                return LiveStreamIndicators.Any(x => html.Contains(x));
            }
            catch (Exception ex)
            {
                Logger.LogMessage(LogLevel.Error, nameof(RssNotifier), ex.ToString());
            }

            return null;
        }
    }
}
