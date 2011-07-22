﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using SignalR.Infrastructure;

namespace SignalR.SignalBuses {
    public class PeerToPeerHttpSignalBus : ISignalBus {
        private static object _peerDiscoveryLocker = new object();
        private readonly ConcurrentDictionary<string, SafeSet<EventHandler<SignaledEventArgs>>> _handlers = new ConcurrentDictionary<string, SafeSet<EventHandler<SignaledEventArgs>>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _peers = new List<string>();
        private bool _peersDiscovered = false;

        public PeerToPeerHttpSignalBus() : this(DependencyResolver.Resolve<IPeerUrlSource>()) { }

        public PeerToPeerHttpSignalBus(IPeerUrlSource discoverer)
            : this(discoverer.GetPeerUrls()) { }

        public PeerToPeerHttpSignalBus(IEnumerable<string> peers) {
            _peers.AddRange(peers);
            Id = Guid.NewGuid();
        }

        private void OnSignaled(string eventKey) {
            SafeSet<EventHandler<SignaledEventArgs>> handlers;
            if (_handlers.TryGetValue(eventKey, out handlers)) {
                Parallel.ForEach(handlers.GetSnapshot(), handler => handler(this, new SignaledEventArgs(eventKey)));
            }
        }

        protected internal Guid Id { get; private set; }

        public virtual Task Signal(string eventKey) {
            // Signal ourselves directly as we don't send the signal via P2P to ourself
            OnSignaled(eventKey);

            EnsurePeersDiscovered();

            return Task.Factory.StartNew(() => SendSignalToPeers(eventKey));
        }

        protected internal virtual void SignalReceived(string eventKey) {
            OnSignaled(eventKey);
        }

        /// <summary>
        /// Override this method to prepare the request before it is sent to peers, e.g. to add authentication credentials
        /// </summary>
        /// <param name="request">The request being sent to peers</param>
        protected virtual void PrepareRequest(WebRequest request) { }

        private void EnsurePeersDiscovered() {
            if (_peersDiscovered) {
                return;
            }

            lock (_peerDiscoveryLocker) {
                if (_peersDiscovered) {
                    return;
                }

                // Loop through peers and send the loopbackTest
                var queryString = "?" + SignalReceiverHandler.QueryStringKeys.LoopbackTest + "=" + HttpUtility.UrlEncode(Id.ToString());
                var peersToRemove = new List<string>();
                Parallel.ForEach(_peers, peer => {
                    try {
                        CreatePrepareAndSendRequestAsync(peer + SignalReceiverHandler.HandlerName + queryString)
                            .ContinueWith(t => {
                                if (t.Exception != null) {
                                    peersToRemove.Add(peer);
                                }
                                else {
                                    using (var sr = new StreamReader(t.Result.GetResponseStream())) {
                                        var result = sr.ReadToEnd();
                                        if (result != SignalReceiverHandler.ResponseValues.Ack) {
                                            // Something wrong with peer, or is ourself, so remove from list
                                            peersToRemove.Add(peer);
                                        }
                                    }
                                }
                            })
                            .Wait();
                    }
                    catch (Exception) {
                        // Problem contacting peer, remove it from the list
                        peersToRemove.Add(peer);
                    }
                });
                if (peersToRemove.Count > 0) {
                    peersToRemove.ForEach(p => _peers.Remove(p));
                }
                _peersDiscovered = true;
            }
        }

        private void SendSignalToPeers(string eventKey) {
            // Loop through peers and send the signal
            var queryString = "?" + SignalReceiverHandler.QueryStringKeys.EventKey + "=" + HttpUtility.UrlEncode(eventKey);
            Parallel.ForEach(_peers, (peer) => {
                CreatePrepareAndSendRequestAsync(peer + SignalReceiverHandler.HandlerName + queryString)
                    .ContinueWith(t => {
                        if (t.Exception == null) {
                            t.Result.Close();
                        }
                    }).Wait();
            });
        }

        private Task<WebResponse> CreatePrepareAndSendRequestAsync(string url) {
            var request = HttpWebRequest.Create(url);
            PrepareRequest(request);
            return request.GetResponseAsync();
        }

        public void AddHandler(string eventKey, EventHandler<SignaledEventArgs> handler) {
            _handlers.AddOrUpdate(eventKey, new SafeSet<EventHandler<SignaledEventArgs>>(new[] { handler }), (key, list) => {
                list.Add(handler);
                return list;
            });
        }

        public void RemoveHandler(string eventKey, EventHandler<SignaledEventArgs> handler) {
            SafeSet<EventHandler<SignaledEventArgs>> handlers;
            if (_handlers.TryGetValue(eventKey, out handlers)) {
                handlers.Remove(handler);
            }
        }
    }
}