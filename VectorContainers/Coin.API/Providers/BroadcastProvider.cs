﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Coin.API.Services;
using Core.API.Consensus;
using Core.API.Helper;
using Core.API.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Coin.API.Providers
{
    public class BroadcastProvider
    {
        private static readonly AsyncLock markStatesAsMutex = new AsyncLock();
        private static readonly AsyncLock markRepliesAsMutex = new AsyncLock();

        private readonly IUnitOfWork unitOfWork;
        private readonly IHttpService httpService;
        private readonly ILogger logger;
        private readonly BlockmainiaOptions blockmainiaOptions;
        private readonly ConcurrentDictionary<ulong, CancellationTokenSource> broadcasts;

        public BroadcastProvider(IUnitOfWork unitOfWork, IHttpService httpService, IOptions<BlockmainiaOptions> blockmainiaOptions, ILogger<BroadcastProvider> logger)
        {
            this.unitOfWork = unitOfWork;
            this.httpService = httpService;
            this.blockmainiaOptions = blockmainiaOptions.Value;
            this.logger = logger;

            broadcasts = new ConcurrentDictionary<ulong, CancellationTokenSource>();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public Task Run()
        {
            try
            {
                MaintainBroadcasts();
            }
            catch (Exception ex)
            {
                logger.LogError($"<<< ReplyProvider.Run >>>: {ex.ToString()}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// 
        /// </summary>
        private void MaintainBroadcasts()
        {
            try
            {
                foreach (var member in httpService.Members
                    .Where(member => !broadcasts.TryGetValue(member.Key, out CancellationTokenSource cancellation)).Select(member => member))
                {
                    var cts = new CancellationTokenSource();
                    Broadcast(member.Key, cts.Token);
                    if (!broadcasts.TryAdd(member.Key, cts))
                    {
                        logger.LogError($"<<< ReplyProvider.MaintainBroadcasts >>>: Failed adding {member.Key}");
                    }
                }

                foreach (var broadcast in broadcasts
                    .Where(broadcast => !httpService.Members.TryGetValue(broadcast.Key, out string url)).Select(broadcast => broadcast))
                {
                    broadcast.Value.Cancel();
                    if (!broadcasts.TryRemove(broadcast.Key, out CancellationTokenSource cancellation))
                    {
                        logger.LogError($"<<< ReplyProvider.MaintainBroadcasts >>>: Failed removing {broadcast.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"<<< ReplyProvider.MaintainBroadcasts >>>: {ex.ToString()}");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private void Broadcast(ulong peer, CancellationToken stoppingToken)
        {
            _ = Task.Factory.StartNew(async () =>
            {
                var initialBackoff = blockmainiaOptions.InitialBackoff;
                var interval = blockmainiaOptions.RoundInterval;
                var backoff = initialBackoff;
                var maxBackoff = blockmainiaOptions.MaxBackoff;
                var retry = false;

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (retry)
                    {
                        backoff *= 2;
                        if (backoff == maxBackoff)
                        {
                            backoff = maxBackoff;
                        }

                        await Task.Delay((int)backoff);
                        retry = false;
                    }

                    try
                    {
                        var responses = await Reply(httpService.Members[peer]);
                        if (responses.Any() != true)
                        {
                            await Task.Delay((int)interval);
                        }

                        foreach (var response in responses)
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                backoff = initialBackoff;
                            }
                        }
                    }
                    catch
                    {
                        retry = true;
                    }
                }
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task<IEnumerable<HttpResponseMessage>> Reply(string uri)
        {
            SemaphoreSlim throttler = null;
            var responses = Enumerable.Empty<HttpResponseMessage>();

            try
            {
                var blockGraphs = await unitOfWork.BlockGraph
                    .GetWhere(x => x.Block.Node.Equals(httpService.NodeIdentity) && x.Included && !x.Replied);

                if (blockGraphs.Any() != true)
                {
                    return responses;
                }

                var numberOfBlocks = 100;
                var tasks = new List<Task<HttpResponseMessage>>();
                var numberOfBatches = (int)Math.Ceiling((double)numberOfBlocks / numberOfBlocks);

                throttler = new SemaphoreSlim(int.MaxValue);

                var series = new long[numberOfBatches];
                foreach (var n in series)
                {
                    var batch = blockGraphs.Skip((int)(n * numberOfBlocks)).Take(numberOfBlocks);
                    if (batch.Any() != true)
                    {
                        continue;
                    }

                    await throttler.WaitAsync();
                    tasks.Add(Replies(throttler, uri, batch));
                }

                try
                {
                    responses = await Task.WhenAll(tasks.ToArray());
                }
                catch (Exception ex)
                {
                    logger.LogError($"<<< ReplyProvider.Reply >>>: {ex.ToString()}");
                }

            }
            catch (Exception ex)
            {
                logger.LogError($"<<< ReplyProvider.Reply >>>: {ex.ToString()}");
            }
            finally
            {
                throttler?.Dispose();
            }

            return responses;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="throttler"></param>
        /// <param name="member"></param>
        /// <param name="batch"></param>
        /// <returns></returns>
        private Task<HttpResponseMessage> Replies(SemaphoreSlim throttler, string member, IEnumerable<BlockGraphProto> batch)
        {
            return Task.Run(async () =>
            {
                HttpResponseMessage response = null;

                try
                {
                    var sending = batch.Select(i => i);
                    var currentBlockGraphs = Util.SerializeProto(sending);
                    var uri = new Uri(new Uri(member), "blockgraphs");

                    response = await httpService.Dial(uri.AbsoluteUri, currentBlockGraphs);
                    response.EnsureSuccessStatusCode();

                    var jToken = Util.ReadJToken(response, "protobufs");
                    var byteArray = Convert.FromBase64String(jToken.Value<string>());

                    if (byteArray.Length > 0)
                    {
                        var blockInfos = Util.DeserializeListProto<BlockInfoProto>(byteArray);
                        if (blockInfos.Any())
                        {
                            await MarkMultipleStatesAs(blockInfos, JobState.Queued);
                            await MarkMultipleRepliesAs(blockInfos, true);
                        }
                    }
                }
                catch
                {
                    var blockLookup = batch.ToLookup(i => i.Block.Hash);
                    var blockInfos = blockLookup.Select(h => new BlockInfoProto { Hash = h.Key });
                    if (blockInfos.Any())
                    {
                        await MarkMultipleStatesAs(blockInfos, JobState.Dead);
                        await MarkMultipleRepliesAs(blockInfos, false);
                    }
                }
                finally
                {
                    throttler.Release();
                }

                return response;
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="blockInfos"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        private async Task MarkMultipleStatesAs(IEnumerable<BlockInfoProto> blockInfos, JobState state)
        {
            if (blockInfos == null)
                throw new ArgumentNullException(nameof(blockInfos));

            try
            {
                using (await markStatesAsMutex.LockAsync())
                {
                    if (blockInfos.Any() != true)
                    {
                        return;
                    }

                    using var session = unitOfWork.Document.OpenSession();

                    var filter = blockInfos.Select(async x => await unitOfWork.Job.GetFirstOrDefault(g => g.Hash.Equals(x.Hash)));
                    for (var i = filter.GetEnumerator(); i.MoveNext();)
                    {
                        var x = i.Current;
                        if (!x.Status.Equals(JobState.Started) &&
                            !x.Status.Equals(JobState.Queued) &&
                            !x.Status.Equals(JobState.Answered) &&
                            !x.Status.Equals(JobState.Dialling))
                        {
                            continue;
                        }

                        x.Result.Status = state;
                        session.Store(x, null, x.Result.Id);
                    }

                    session.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"<<< ReplyProvider.MarkMultipleStatesAs >>>: {ex.ToString()}");
            }

            return;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="replied"></param>
        /// <returns></returns>
        private async Task MarkMultipleRepliesAs(IEnumerable<BlockInfoProto> blockInfos, bool replied = false)
        {
            if (blockInfos == null)
                throw new ArgumentNullException(nameof(blockInfos));

            try
            {
                using (await markRepliesAsMutex.LockAsync())
                {
                    if (blockInfos.Any() != true)
                    {
                        return;
                    }

                    using var session = unitOfWork.Document.OpenSession();

                    foreach (var next in blockInfos)
                    {
                        var blockGraph = await unitOfWork.BlockGraph
                            .GetFirstOrDefault(x => x.Block.Hash.Equals(next.Hash) && x.Block.Node.Equals(httpService.NodeIdentity) && x.Block.Round.Equals(next.Round));
                        if (blockGraph != null)
                        {
                            blockGraph.Replied = replied;
                            session.Store(blockGraph, null, blockGraph.Id);
                        }
                    }

                    session.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"<<< ReplyProvider.MarkMultipleRepliesAs >>>: {ex.ToString()}");
            }

            return;
        }
    }
}
