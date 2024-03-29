using Core.API;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NUlid;
using Stateless;
using SwimProtocol.Collections;
using SwimProtocol.Messages;
using SwimProtocol.Repositories;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace SwimProtocol
{
    public class FailureDetection : BackgroundService, ISwimProtocol
    {
        private readonly StateMachine<SwimFailureDetectionState, SwimFailureDetectionTrigger> _stateMachine;
        private readonly ISwimProtocolProvider _swimProtocolProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        private readonly System.Timers.Timer _protocolTimer;
        private readonly System.Timers.Timer _pingTimer;

        private ConcurrentQueue<ISwimNode> _nodes { get; set; } = new ConcurrentQueue<ISwimNode>();

        private ConcurrentDictionaryEx<Ulid, MessageBase> CorrelatedMessages { get; set; } = new ConcurrentDictionaryEx<Ulid, MessageBase>(200);
        private ConcurrentBag<BroadcastableItem> BroadcastQueue { get; set; } = new ConcurrentBag<BroadcastableItem>();

        private readonly object _nodesLock = new object();
        private readonly object _activeNodeLock = new object();
        private readonly object _stateMachineLock = new object();
        private readonly object _broadcastQueueLock = new object();

        private int ProtocolPeriodsComplete { get; set; }

        private NodeRepository _nodeRepository;

        private const int Lambda = 3;

        public FailureDetection(ISwimProtocolProvider swimProtocolProvider, IConfiguration configuration, ILogger<FailureDetection> logger)
        {
            _stateMachine = new StateMachine<SwimFailureDetectionState, SwimFailureDetectionTrigger>(SwimFailureDetectionState.Idle);
            _swimProtocolProvider = swimProtocolProvider;
            _configuration = configuration;
            _logger = logger;

            _nodeRepository = new NodeRepository(swimProtocolProvider.Node.Hostname);

            _protocolTimer = new System.Timers.Timer(7000);
            _pingTimer = new System.Timers.Timer(3500);
            _protocolTimer.AutoReset = false;
            _pingTimer.AutoReset = false;
            _protocolTimer.Elapsed += _protocolTimer_Elapsed;
            _pingTimer.Elapsed += _pingTimer_Elapsed;

            _stateMachine.Configure(SwimFailureDetectionState.Idle)
                .Permit(SwimFailureDetectionTrigger.Ping, SwimFailureDetectionState.Pinged)
                .Ignore(SwimFailureDetectionTrigger.Reset)
                .OnEntry((entryAction) =>
                {
                    Start();
                });

            _stateMachine.Configure(SwimFailureDetectionState.Pinged)
                .Permit(SwimFailureDetectionTrigger.PingExpireLive, SwimFailureDetectionState.Alive)
                .Permit(SwimFailureDetectionTrigger.PingExpireNoResponse, SwimFailureDetectionState.PrePingReq)
                .Permit(SwimFailureDetectionTrigger.ProtocolExpireDead, SwimFailureDetectionState.Expired);

            _stateMachine.Configure(SwimFailureDetectionState.Alive)
                .Permit(SwimFailureDetectionTrigger.ProtocolExpireLive, SwimFailureDetectionState.Expired);

            _stateMachine.Configure(SwimFailureDetectionState.PrePingReq)
                .Permit(SwimFailureDetectionTrigger.PingReq, SwimFailureDetectionState.PingReqed)
                .OnEntry(entryAction =>
                {
                    _stateMachine.Fire(SwimFailureDetectionTrigger.PingReq);
                    BroadcastPingReq();
                });

            _stateMachine.Configure(SwimFailureDetectionState.PingReqed)
                .Permit(SwimFailureDetectionTrigger.ProtocolExpireDead, SwimFailureDetectionState.Expired)
                .Permit(SwimFailureDetectionTrigger.ProtocolExpireLive, SwimFailureDetectionState.Expired);

            _stateMachine.Configure(SwimFailureDetectionState.Expired)
                .Permit(SwimFailureDetectionTrigger.Reset, SwimFailureDetectionState.Idle)
                .OnEntryFrom(SwimFailureDetectionTrigger.ProtocolExpireDead, entryAction =>
                {
                    HandleSuspectNode();
                })
                .OnEntryFrom(SwimFailureDetectionTrigger.ProtocolExpireLive, entryAction =>
                {
                    HandleAliveNode();
                });

            _swimProtocolProvider.ReceivedMessage += _swimProtocolProvider_ReceivedMessage;

            RestoreKnownNodes();
            AddBootstrapNodes();
        }

        private void BroadcastPingReq()
        {
            lock (_nodesLock)
            {
                lock (_activeNodeLock)
                {
                    //  Select k num of nodes.
                    RNGCryptoServiceProvider provider = new RNGCryptoServiceProvider();
                    // Get four random bytes.
                    byte[] four_bytes = new byte[4];
                    provider.GetBytes(four_bytes);

                    // Convert that into an uint.
                    int num = BitConverter.ToInt32(four_bytes, 0);

                    num = num < 0 ? num * -1 : num;

                    int k = 1 + (num % Math.Min(_nodes.Count == 0 ? 1 :_nodes.Count, 8));

                    //  Create list of node candidates, remove active node.
                    var candidates = _nodes.ToList();

                    candidates.Remove(ActiveNode);

                    //  Shuffle candidates
                    candidates.Shuffle();

                    //  Select k nodes
                    var nodesToPingReq = candidates.Take(k);

                    if (!nodesToPingReq.Any())
                    {
                        Debug.WriteLine("No nodes to select from");
                        _logger.LogInformation("No nodes to select from");
                    }

                    foreach (var node in nodesToPingReq)
                    {
                        Debug.WriteLine($"Sending pingreq for {ActiveNode.Endpoint} to {node.Endpoint}");
                        _logger.LogInformation($"Sending pingreq for {ActiveNode.Endpoint} to {node.Endpoint}");

                        _swimProtocolProvider.SendMessage(node,
                            new PingReqMessage(ActiveNodeData.PingCorrelationId, ActiveNode,
                                _swimProtocolProvider.Node));
                    }
                }
            }
        }

        private void RestoreKnownNodes()
        {
            var knownNodes = _nodeRepository.Get();

            foreach (var knownNode in knownNodes)
            {
                AddNode(knownNode);
            }
        }
        private void HandleAliveNode()
        {
            lock (_activeNodeLock)
            {
                if (ActiveNode == null) return;

                ActiveNode.SetStatus(SwimNodeStatus.Alive);

                AddBroadcastMessage(new AliveMessage(Ulid.NewUlid(), _swimProtocolProvider.Node, ActiveNode));

                _nodeRepository.Upsert(ActiveNode);
                AddNode(ActiveNode);
            }
        }

        private void HandleSuspectNode()
        {
            lock (_activeNodeLock)
            {
                if (ActiveNode == null) return;

                ActiveNode.SetStatus(SwimNodeStatus.Suspicious);

                AddBroadcastMessage(new SuspectMessage(Ulid.NewUlid(), _swimProtocolProvider.Node, ActiveNode));

                _nodeRepository.Upsert(ActiveNode);
            }
        }

        public IEnumerable<ISwimNode> Members
        {
            get
            {
                lock (_nodesLock)
                {
                    lock (_activeNodeLock)
                    {
                        var nodes = _nodes.ToList();

                        if (ActiveNode == null) return nodes;

                        if (nodes.All(x => x.Endpoint != ActiveNode.Endpoint))
                        {
                            nodes.Add(ActiveNode);
                        }

                        return nodes;
                    }
                }
            }
        }

        public void Start()
        {
            MarkDeadNodes();
            CleanupDeadNodes();

            _protocolTimer.Start();

            if (ProtocolPeriodsComplete >= InitialNodeCount)
            {
                ShuffleNodes();

                ProtocolPeriodsComplete = 0;
            }

            PingNextNode();

            _pingTimer.Start();
        }

        private void MarkDeadNodes()
        {
            var expiredSuspects = _nodeRepository
                .Get()
                .Where(x => x.Status == SwimNodeStatus.Suspicious && (DateTime.UtcNow - x.SuspiciousTimestamp) > new TimeSpan(0, 1, 0))
                .ToList();

            foreach(var expiredSuspect in expiredSuspects)
            {
                _logger.LogInformation($"Marking {expiredSuspect.Endpoint} as DEAD and Broadcasting");

                expiredSuspect.SetStatus(SwimNodeStatus.Dead);

                AddBroadcastMessage(new DeadMessage(Ulid.NewUlid(), _swimProtocolProvider.Node, expiredSuspect));

                _nodeRepository.Upsert(expiredSuspect);
            }
        }

        private void CleanupDeadNodes()
        {
            var persistedNodes = _nodeRepository
                                    .Get()
                                    .Where(x => x.Status == SwimNodeStatus.Dead)
                                    .ToList();

            foreach (var persistedNode in persistedNodes)
            {
                _logger.LogInformation($"Removing DEAD node {persistedNode.Endpoint} from local list");

                RemoveNode(persistedNode);
            }
        }

        public void ShuffleNodes()
        {
            lock (_nodesLock)
            {
                _logger.LogInformation("Shuffling nodes...");

                var nodes = new List<ISwimNode>(_nodes.ToArray());

                _nodes = new ConcurrentQueue<ISwimNode>();

                nodes.Shuffle();

                foreach (var node in nodes)
                {
                    _nodes.Enqueue(node);
                }
            }
        }

        private void _pingTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                lock (_stateMachineLock)
                {
                    if (_stateMachine.State == SwimFailureDetectionState.Pinged)
                    {
                        lock (_activeNodeLock)
                        {
                            if (ActiveNodeData != null)
                            {
                                _stateMachine.Fire(!ActiveNodeData.ReceivedAck
                                    ? SwimFailureDetectionTrigger.PingExpireNoResponse
                                    : SwimFailureDetectionTrigger.PingExpireLive);
                            }
                            else
                            {
                                _stateMachine.Fire(SwimFailureDetectionTrigger.PingExpireNoResponse);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during ping expiration");
            }
        }

        private void _protocolTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                lock (_stateMachineLock)
                {
                    lock (_activeNodeLock)
                    {
                        if (_stateMachine.State == SwimFailureDetectionState.PingReqed)
                        {
                            if (ActiveNodeData == null)
                            {
                                _stateMachine.Fire(SwimFailureDetectionTrigger.ProtocolExpireDead);
                            }

                            if (ActiveNodeData != null)
                            {
                                if (!ActiveNodeData.ReceivedAck)
                                {
                                    _stateMachine.Fire(SwimFailureDetectionTrigger.ProtocolExpireDead);
                                }
                                else
                                {
                                    _stateMachine.Fire(SwimFailureDetectionTrigger.ProtocolExpireLive);
                                }
                            }
                        }

                        if (_stateMachine.State == SwimFailureDetectionState.Alive)
                        {
                            _stateMachine.Fire(SwimFailureDetectionTrigger.ProtocolExpireLive);
                        }

                        //  Unlikely state but it could happen.
                        if (_stateMachine.State == SwimFailureDetectionState.Pinged)
                        {
                            _stateMachine.Fire(SwimFailureDetectionTrigger.ProtocolExpireDead);
                        }

                        ProtocolPeriodsComplete++;

                        _stateMachine.Fire(SwimFailureDetectionTrigger.Reset);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during protocol expiration");
            }
        }

        private bool AddBroadcastMessage(SignedSwimMessage swimMessage)
        {
            lock (_broadcastQueueLock)
            {
                var item = new BroadcastableItem(swimMessage);

                return ApplyMessageOverrides(item);
            }
        }

        private void AddBroadcastMessage(MessageBase message)
        {
            var signedMessage = _swimProtocolProvider.SignMessage(message);

            AddBroadcastMessage(signedMessage);
        }

        private bool ApplyMessageOverrides(BroadcastableItem item)
        {
            lock (_broadcastQueueLock)
            {
                var items = BroadcastQueue.ToList();

                var toRemove = new List<BroadcastableItem>();

                foreach (var broadcastableItem in items)
                {
                    var m = broadcastableItem.SwimMessage.Message;

                    var w = item.SwimMessage.Message.GetMessageOverrideWeight(m);

                    if (w == 0)
                        continue;

                    if (w == 1)
                        toRemove.Add(broadcastableItem);

                    if (w == -1)
                        return false;
                }

                foreach (var broadcastableItem in toRemove)
                {
                    items.Remove(broadcastableItem);
                }

                items.Add(item);

                BroadcastQueue = new ConcurrentBag<BroadcastableItem>(items
                    .OrderByDescending(x => x.BroadcastCount));
            }

            return true;
        }

        private IEnumerable<SignedSwimMessage> GetBroadcastMessages(int num = 10)
        {
            lock (_broadcastQueueLock)
            {
                //  Prioritize new items and remove items that have hit the Broadcast threshold or expired.
                lock (_nodesLock)
                {
                    BroadcastQueue = new ConcurrentBag<BroadcastableItem>(BroadcastQueue
                        .OrderByDescending(x => x.BroadcastCount)
                        .Where(x => x.BroadcastCount < Math.Max(Lambda * Math.Log(_nodes.Count + 1), 1) && x.SwimMessage.Message.IsValid)
                    );
                }

                var bis = BroadcastQueue.Count < num ? BroadcastQueue.Take(BroadcastQueue.Count) : BroadcastQueue.Take(num);

                foreach (var bi in bis)
                {
                    bi.BroadcastCount++;
                }

                return bis.Select(x => x.SwimMessage);
            }
        }

        private void _swimProtocolProvider_ReceivedMessage(object sender, ReceivedMessageEventArgs e)
        {
            try
            {
                foreach (var signedMessage in e.CompositeMessage.Messages)
                {
                    if (!signedMessage.IsValid())
                    {
                        _logger.LogError("Signature check failed!");
                        continue;
                    }

                    var messageType = signedMessage.Message.MessageType;
                    var message = signedMessage.Message;

                    if (message.IsValid)
                    {
                        switch (messageType)
                        {
                            case MessageType.Alive:
                                {
                                    if (AddBroadcastMessage(signedMessage))
                                    {
                                        var casted = message as AliveMessage;

                                        if (casted != null)
                                        {
                                            _logger.LogInformation(
                                                $"{signedMessage.Message.SourceNode} marked {casted.SubjectNode} ALIVE");
                                            AddNode(casted.SubjectNode);
                                        }
                                    }
                                }
                                break;
                            case MessageType.Dead:
                                {
                                    if (AddBroadcastMessage(signedMessage))
                                    {
                                        var casted = message as DeadMessage;

                                        if (casted != null)
                                        {
                                            _logger.LogInformation(
                                                $"{signedMessage.Message.SourceNode} marked {casted.SubjectNode} SUSPECT");
                                            RemoveNode(casted.SubjectNode);
                                        }
                                    }
                                }
                                break;
                            case MessageType.Ping:
                                {
                                    SendAck(message);
                                    AddNode(message.SourceNode);
                                }
                                break;
                            case MessageType.PingReq:
                                {
                                    RelayPing(message);
                                }
                                break;
                            case MessageType.Suspect:
                                {
                                    if (AddBroadcastMessage(signedMessage))
                                    {
                                        var casted = message as SuspectMessage;
                                        _logger.LogInformation($"{signedMessage.Message.SourceNode} marked {casted.SubjectNode} SUSPECT");
                                        MarkNodeSuspicious(casted.SubjectNode);
                                    }
                                }
                                break;
                            case MessageType.Ack:
                                {
                                    HandleAck(message);
                                    AddNode(message.SourceNode);

                                    MessageBase ms = null;

                                    //  Send swimMessage back to originating node.
                                    if (message.CorrelationId.HasValue && CorrelatedMessages.TryRemove(message.CorrelationId.Value, out ms))
                                    {
                                        var cm = ms as PingReqMessage;
                                        _logger.LogInformation(
                                            $"Relaying ACK {message.SourceNode} -> {cm.SourceNode}");
                                        _swimProtocolProvider.SendMessage(cm.SourceNode, message);
                                    }
                                }

                                break;
                        }
                    }

                    _logger.LogInformation($"Received: {JsonConvert.SerializeObject(message)}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during receive message");
            }
        }

        private void HandleAck(MessageBase message)
        {
            lock (_activeNodeLock)
            {
                if (message.SourceNode.Equals(ActiveNode))
                {
                    if (ActiveNodeData != null && message.CorrelationId == ActiveNodeData.PingCorrelationId)
                    {
                        ActiveNodeData.ReceivedAck = true;
                    }
                }
            }
        }

        private void RelayPing(MessageBase message)
        {
            var m = message as PingReqMessage;

            if (m.CorrelationId.HasValue)
            {
                _logger.LogInformation(
                    $"Relaying PING {m.SourceNode} -> {m.SubjectNode}");

                CorrelatedMessages.TryAdd(m.CorrelationId.Value, m);
                _swimProtocolProvider.SendMessage(m.SubjectNode, new PingMessage(m.CorrelationId.Value) { SourceNode = _swimProtocolProvider.Node });
            }
        }

        private void SendAck(MessageBase message)
        {
            if (message.SourceNode != null && _swimProtocolProvider?.Node?.Endpoint != null && message.CorrelationId.HasValue)
            {
                _swimProtocolProvider.SendMessage(message.SourceNode, new AckMessage(message.CorrelationId.Value, _swimProtocolProvider.Node));
            }
        }

        public void AddNode(ISwimNode node)
        {
            if (node == null)
                return;

            if (node.Endpoint == _swimProtocolProvider.Node.Endpoint)
                return;

            lock (_nodesLock)
            {
                if (_nodes.All(x => x.Endpoint != node.Endpoint))
                {
                    _nodes.Enqueue(node);
                }
            }
        }

        public void RemoveNode(ISwimNode node)
        {
            if (node == null)
                return;

            lock (_nodesLock)
            {
                var nodesFiltered = _nodes.Where(x => x.Endpoint != node.Endpoint);
                _nodes = new ConcurrentQueue<ISwimNode>(nodesFiltered);
            }

            _nodeRepository.Delete(node);
        }

        public void MarkNodeSuspicious(ISwimNode node)
        {
            lock (_nodesLock)
            {
                if (node == null) return;

                var suspiciousNode = _nodes.FirstOrDefault(x => x.Endpoint == node.Endpoint);

                suspiciousNode?.SetStatus(SwimNodeStatus.Suspicious);

                _nodeRepository.Upsert(node);
            }
        }

        private void PingNextNode()
        {
            var members = Members;
            var localNode = _swimProtocolProvider.Node;

            AddBootstrapNodes();

            var output = new StringBuilder();

            output.AppendLine($"Local node {localNode.Endpoint} knows of these nodes:");

            foreach (var member in members)
            {
                output.AppendLine($"\t{member}");
            }

            _logger.LogInformation(output.ToString());

            lock (_nodesLock)
            {
                InitialNodeCount = _nodes.Count;

                ISwimNode node;

                if (_nodes.TryDequeue(out node))
                {
                    lock (_activeNodeLock)
                    {
                        ActiveNode = node;

                        PingNode(ActiveNode);
                    }
                }

                lock (_stateMachineLock)
                {
                    _stateMachine.Fire(SwimFailureDetectionTrigger.Ping);
                }
            }
        }

        private void AddBootstrapNodes()
        {
            //  We will always attempt to connect to the bootstrap nodes, even if they're down.
            var membershipSection = _configuration.GetSection(MembershipConstants.ConfigSection);
            var bootstrapNodes = membershipSection.GetSection(MembershipConstants.BootstrapNodes)
                .GetChildren()
                .ToArray()
                .Select(x => x.Value)
                .ToArray();

            foreach (var bootstrapNode in bootstrapNodes)
            {
                AddNode(new SwimNode
                {
                    Endpoint = bootstrapNode,
                    Status = SwimNodeStatus.Unknown
                });
            }
        }

        private void PingNode(ISwimNode activeNode)
        {
            if (activeNode == _swimProtocolProvider.Node)
            {
                _logger.LogWarning("Node is trying to message itself. Drop it on the floor");
                return;
            }

            var ulid = Ulid.NewUlid();

            lock (_activeNodeLock)
            {
                ActiveNodeData = new NodeData
                {
                    PingCorrelationId = ulid,
                    ReceivedAck = false
                };
            }

            var ping = new PingMessage(ulid)
            {
                SourceNode = _swimProtocolProvider.Node
            };

            var pingSigned = _swimProtocolProvider.SignMessage(ping);

            var messages = GetBroadcastMessages().ToList();

            messages.Insert(0, pingSigned);

            _swimProtocolProvider.SendMessage(activeNode, messages);
        }

        public void OnTransitioned(
          Action<StateMachine<SwimFailureDetectionState, SwimFailureDetectionTrigger>.Transition> onTransitionAction)
        {
            lock (_stateMachineLock)
            {
                _stateMachine.OnTransitioned(onTransitionAction);
            }
        }

        public void Fire(SwimFailureDetectionTrigger trigger)
        {
            lock (_stateMachineLock)
            {
                _stateMachine.Fire(trigger);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Run(() =>
            {
                Start();
            }, stoppingToken);
        }

        public SwimFailureDetectionState State
        {
            get
            {
                lock (_stateMachineLock)
                {
                    return _stateMachine.State;
                }
            }
        }

        public int InitialNodeCount { get; private set; }
        public ISwimNode ActiveNode { get; private set; }

        private NodeData ActiveNodeData { get; set; }

        private class NodeData
        {
            public Ulid PingCorrelationId { get; set; }
            public bool ReceivedAck { get; set; }
        }
    }
}
