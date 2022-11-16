﻿using client.messengers.clients;
using client.messengers.punchHole;
using client.messengers.punchHole.tcp;
using client.messengers.punchHole.udp;
using client.messengers.register;
using client.messengers.relay;
using client.realize.messengers.heart;
using client.realize.messengers.punchHole;
using client.realize.messengers.relay;
using common.libs;
using common.server;
using common.server.model;
using common.server.servers.rudp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace client.realize.messengers.clients
{
    public class ClientsTransfer : IClientsTransfer
    {
        private BoolSpace firstClients = new BoolSpace(true);

        private readonly IPunchHoleUdp punchHoleUdp;
        private readonly IPunchHoleTcp punchHoleTcp;
        private readonly RegisterStateInfo registerState;
        private readonly IClientInfoCaching clientInfoCaching;
        private readonly PunchHoleMessengerSender punchHoleMessengerSender;
        private readonly Config config;
        private readonly IUdpServer udpServer;
        private readonly HeartMessengerSender heartMessengerSender;
        private readonly RelayMessengerSender relayMessengerSender;
        private readonly IConnecRouteCaching connecRouteCaching;

        private const byte TryReverseMaxValue = 2;
        private object lockObject = new();

        public ClientsTransfer(ClientsMessengerSender clientsMessengerSender,
            IPunchHoleUdp punchHoleUdp, IPunchHoleTcp punchHoleTcp, IClientInfoCaching clientInfoCaching,
            RegisterStateInfo registerState, PunchHoleMessengerSender punchHoleMessengerSender, Config config,
            IUdpServer udpServer, ITcpServer tcpServer, HeartMessengerSender heartMessengerSender,
            RelayMessengerSender relayMessengerSender, IClientsTunnel clientsTunnel, IConnecRouteCaching connecRouteCaching
        )
        {
            this.punchHoleUdp = punchHoleUdp;
            this.punchHoleTcp = punchHoleTcp;
            this.registerState = registerState;
            this.clientInfoCaching = clientInfoCaching;
            this.config = config;
            this.udpServer = udpServer;
            this.heartMessengerSender = heartMessengerSender;
            this.relayMessengerSender = relayMessengerSender;
            this.connecRouteCaching = connecRouteCaching;


            punchHoleUdp.OnStep1Handler.Sub((e) =>
            {
                if (config.Client.UseUdp == true)
                {
                    clientInfoCaching.Connecting(e.RawData.FromId, true, ServerType.UDP);
                }
            });
            punchHoleUdp.OnStep2FailHandler.Sub((e) =>
            {
                if (clientInfoCaching.Get(e.RawData.FromId, out ClientInfo client))
                {
                    clientInfoCaching.Connecting(e.RawData.FromId, false, ServerType.UDP);
                    if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                    {
                        clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                        clientInfoCaching.RemoveUdpserver(e.RawData.TunnelName, true);
                        _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);
                    }
                }

            });
            punchHoleUdp.OnStep3Handler.Sub((e) =>
            {
                clientInfoCaching.Online(e.Data.FromId, e.Connection, ClientConnectTypes.P2P);
                if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                {
                    clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                    //  clientInfoCaching.RemoveUdpserver(e.RawData.TunnelName);
                    _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);
                }
            });
            punchHoleUdp.OnStep4Handler.Sub((e) =>
            {
                clientInfoCaching.Online(e.Data.FromId, e.Connection, ClientConnectTypes.P2P);
                if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                {
                    clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                    // clientInfoCaching.RemoveUdpserver(e.RawData.TunnelName);
                    _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);
                }
            });

            punchHoleTcp.OnStep1Handler.Sub((e) =>
            {
                if (config.Client.UseTcp == true)
                {
                    clientInfoCaching.Connecting(e.RawData.FromId, true, ServerType.TCP);
                }
            });
            punchHoleTcp.OnStep2FailHandler.Sub((e) =>
            {
                clientInfoCaching.Connecting(e.RawData.FromId, false, ServerType.TCP);
                if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                {
                    clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                    _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);
                }
            });
            punchHoleTcp.OnStep3Handler.Sub((e) =>
            {
                clientInfoCaching.Online(e.Data.FromId, e.Connection, ClientConnectTypes.P2P);
                if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                {
                    clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                    _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);

                }
            });
            punchHoleTcp.OnStep4Handler.Sub((e) =>
            {
                clientInfoCaching.Online(e.Data.FromId, e.Connection, ClientConnectTypes.P2P);
                if (e.RawData.TunnelName > (ulong)TunnelDefaults.MAX)
                {
                    clientInfoCaching.RemoveTunnelPort(e.RawData.TunnelName);
                    _ = clientsMessengerSender.RemoveTunnel(registerState.OnlineConnection, e.RawData.TunnelName);
                }
            });

            //调试注释
            tcpServer.OnDisconnect.Sub((connection) => Disconnect(connection, registerState.TcpConnection));
            udpServer.OnDisconnect.Sub((connection) => Disconnect(connection, registerState.UdpConnection));
            clientsTunnel.OnDisConnect = Disconnect;

            //中继连线
            relayMessengerSender.OnRelay.Sub((param) =>
            {
                if (clientInfoCaching.Get(param.FromId, out ClientInfo client))
                {
                    _ = Relay1(client, param.Connection);
                }
            });

            this.punchHoleMessengerSender = punchHoleMessengerSender;
            //有人要求反向链接
            punchHoleMessengerSender.OnReverse.Sub(OnReverse);
            registerState.OnRegisterBind.Sub(OnRegisterBind);
            //收到来自服务器的 在线客户端 数据
            clientsMessengerSender.OnServerClientsData.Sub(OnServerSendClients);

            Logger.Instance.Info("获取外网距离ing...");
            registerState.LocalInfo.RouteLevel = NetworkHelper.GetRouteLevel();
        }

        public void Disconnect(IConnection connection, IConnection regConnection)
        {
            if (ReferenceEquals(regConnection, connection))
            {
                return;
            }
            clientInfoCaching.Offline(connection.ConnectId, connection.ServerType);

            //Logger.Instance.DebugDebug($"客户端掉线");
            //ulong connectId = connection.ConnectId;
            //Task.Run(async () =>
            //{
            //    await Task.Delay(1000);
            //    if (clientInfoCaching.Get(connectId, out ClientInfo client))
            //    {
            //        Logger.Instance.DebugDebug($"客户端掉线:重连:{client.Name}");
            //        ConnectClient(client);
            //    }
            //});
        }

        public void ConnectClient(ulong id)
        {
            if (clientInfoCaching.Get(id, out ClientInfo client))
            {
                ConnectClient(client);
            }
        }
        public void ConnectClient(ClientInfo client)
        {
            if (client.Id == registerState.ConnectId)
            {
                return;
            }
            if (registerState.LocalInfo.IsConnecting)
            {
                return;
            }
            Task.Run(async () =>
            {
                EnumConnectResult udp = await ConnectUdp(client).ConfigureAwait(false);
                EnumConnectResult tcp = await ConnectTcp(client).ConfigureAwait(false);

                //任一失败
                if (((udp | tcp) & EnumConnectResult.Fail) == EnumConnectResult.Fail)
                {
                    if (client.TryReverseValue < TryReverseMaxValue)
                    {
                        client.TryReverseValue++;
                        ConnectReverse(client);
                    }
                    else
                    {
                        if (udp == EnumConnectResult.Fail)
                        {
                            //不启用tcp时，才中继udp
                            if ((config.Client.UseTcp | client.UseTcp) == false)
                            {
                                _ = Relay(client, ServerType.UDP, true);
                            }
                        }
                        if (tcp == EnumConnectResult.Fail)
                        {
                            _ = Relay(client, ServerType.TCP, true);
                        }
                    }
                }
                client.TryReverseValue = 1;
            });
        }

        public void ConnectReverse(ulong id)
        {
            if (clientInfoCaching.Get(id, out ClientInfo client))
            {
                ConnectReverse(client);
            }
        }
        private void ConnectReverse(ClientInfo info)
        {
            punchHoleMessengerSender.SendReverse(info).ConfigureAwait(false);
        }
        private void OnReverse(OnPunchHoleArg arg)
        {
            if (clientInfoCaching.Get(arg.Data.FromId, out ClientInfo client))
            {
                PunchHoleReverseInfo model = new PunchHoleReverseInfo();
                model.DeBytes(arg.Data.Data);
                client.TryReverseValue = model.TryReverse;
                ConnectClient(client);
            }
        }

        public void Reset(ulong id)
        {
            punchHoleMessengerSender.SendReset(id).ConfigureAwait(false);

        }
        public void ConnectStop(ulong id)
        {
            punchHoleTcp.SendStep2Stop(id);
        }

        private async Task<EnumConnectResult> ConnectUdp(ClientInfo client)
        {
            if (client.UdpConnected)
            {
                return EnumConnectResult.Success;
            }
            if ((config.Client.UseUdp & client.UseUdp) == false)
            {
                return EnumConnectResult.BreakOff;
            }
            if (client.UdpConnecting)
            {
                return EnumConnectResult.BreakOff;
            }

            clientInfoCaching.Connecting(client.Id, true, ServerType.UDP);

            ulong[] tunnelNames = new ulong[] { (ulong)TunnelDefaults.UDP, (ulong)TunnelDefaults.MIN };
            if (config.Client.UseOriginPort == false)
            {
                tunnelNames[0] = (ulong)TunnelDefaults.MIN;
            }

            for (int i = 0; i < tunnelNames.Length; i++)
            {
                ConnectResultModel result = await punchHoleUdp.Send(new ConnectParams
                {
                    Id = client.Id,
                    TunnelName = tunnelNames[i],
                    TryTimes = 2,
                    LocalPort = registerState.LocalInfo.UdpPort
                }).ConfigureAwait(false);
                if (result.State)
                {
                    return EnumConnectResult.Success;
                }
                Logger.Instance.Error((result.Result as ConnectFailModel).Msg);
            }

            clientInfoCaching.Offline(client.Id, ServerType.UDP);
            return EnumConnectResult.Fail;
        }
        private async Task<EnumConnectResult> ConnectTcp(ClientInfo client)
        {
            if (client.TcpConnected)
            {
                return EnumConnectResult.Success;
            }
            if ((config.Client.UseTcp & client.UseTcp) == false)
            {
                return EnumConnectResult.BreakOff;
            }
            if (client.TcpConnecting)
            {
                return EnumConnectResult.BreakOff;
            }

            clientInfoCaching.Connecting(client.Id, true, ServerType.TCP);

            ulong[] tunnelNames = new ulong[] { (ulong)TunnelDefaults.TCP, (ulong)TunnelDefaults.MIN };
            if (config.Client.UseOriginPort == false)
            {
                tunnelNames[0] = (ulong)TunnelDefaults.MIN;
            }
            for (int i = 0; i < tunnelNames.Length; i++)
            {
                ConnectResultModel result = await punchHoleTcp.Send(new ConnectParams
                {
                    Id = client.Id,
                    TunnelName = tunnelNames[i],
                    TryTimes = 2,
                    LocalPort = registerState.LocalInfo.UdpPort
                }).ConfigureAwait(false);
                if (result.State)
                {
                    return EnumConnectResult.Success;
                }
                Logger.Instance.Error((result.Result as ConnectFailModel).Msg);
            }

            clientInfoCaching.Offline(client.Id, ServerType.TCP);
            return EnumConnectResult.Fail;
        }

        public async Task Ping()
        {
            if (config.Client.UseUdp)
            {
                var udps = clientInfoCaching.All().Where(c => c.UseUdp && config.Client.UseUdp);
                foreach (var item in udps)
                {
                    try
                    {

                        var start = DateTime.Now;
                        var res = await heartMessengerSender.Heart(item.UdpConnection);
                        if (res)
                        {
                            item.UdpConnection.RoundTripTime = (ushort)(DateTime.Now - start).TotalMilliseconds;
                        }
                        else
                        {
                            item.UdpConnection.RoundTripTime = -1;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            if (config.Client.UseTcp)
            {
                var tcps = clientInfoCaching.All().Where(c => c.UseTcp && config.Client.UseTcp && c.TcpConnected);
                foreach (var item in tcps)
                {
                    try
                    {
                        var start = DateTime.Now;
                        var res = await heartMessengerSender.Heart(item.TcpConnection);
                        if (res)
                        {
                            item.TcpConnection.RoundTripTime = (ushort)(DateTime.Now - start).TotalMilliseconds;
                        }
                        else
                        {
                            item.TcpConnection.RoundTripTime = -1;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }


        private void OnRegisterBind(bool state)
        {
            firstClients.Reset();
            clientInfoCaching.Clear();
            if (state)
            {
                clientInfoCaching.AddTunnelPort((ulong)TunnelDefaults.UDP, registerState.LocalInfo.UdpPort);
                clientInfoCaching.AddUdpserver((ulong)TunnelDefaults.UDP, udpServer as UdpServer);
                clientInfoCaching.AddTunnelPort((ulong)TunnelDefaults.TCP, registerState.LocalInfo.TcpPort);
            }
            else
            {
                clientInfoCaching.RemoveTunnelPort((ulong)TunnelDefaults.UDP);
                clientInfoCaching.RemoveUdpserver((ulong)TunnelDefaults.UDP);
                clientInfoCaching.RemoveTunnelPort((ulong)TunnelDefaults.TCP);
            }
        }

        private void OnServerSendClients(ClientsInfo clients)
        {
            try
            {
                if (registerState.OnlineConnection == null || clients.Clients == null)
                {
                    return;
                }
                lock (lockObject)
                {
                    IEnumerable<ulong> remoteIds = clients.Clients.Select(c => c.Id);
                    //下线了的
                    IEnumerable<ulong> offlines = clientInfoCaching.AllIds().Except(remoteIds).Where(c => c != registerState.ConnectId);
                    foreach (ulong offid in offlines)
                    {
                        clientInfoCaching.Offline(offid);
                        clientInfoCaching.Remove(offid);
                    }
                    //新上线的
                    IEnumerable<ulong> upLines = remoteIds.Except(clientInfoCaching.AllIds());
                    IEnumerable<ClientsClientInfo> upLineClients = clients.Clients.Where(c => upLines.Contains(c.Id) && c.Id != registerState.ConnectId);

                    foreach (ClientsClientInfo item in upLineClients)
                    {
                        EnumClientAccess enumClientAccess = (EnumClientAccess)item.ClientAccess;
                        ClientInfo client = new ClientInfo
                        {
                            Id = item.Id,
                            Name = item.Name,
                            UseTcp = (enumClientAccess & EnumClientAccess.UseTcp) == EnumClientAccess.UseTcp,
                            UseUdp = (enumClientAccess & EnumClientAccess.UseUdp) == EnumClientAccess.UseUdp,
                            UsePunchHole = (enumClientAccess & EnumClientAccess.UsePunchHole) == EnumClientAccess.UsePunchHole,
                            UseRelay = (enumClientAccess & EnumClientAccess.UseRelay) == EnumClientAccess.UseRelay,
                        };
                        clientInfoCaching.Add(client);
                        if (firstClients.IsDefault)
                        {
                            if (config.Client.UsePunchHole && client.UsePunchHole)
                            {
                                if (registerState.LocalInfo.TcpPort == registerState.RemoteInfo.TcpPort || registerState.LocalInfo.UdpPort == registerState.RemoteInfo.UdpPort)
                                {
                                    ConnectClient(client);
                                }
                                else
                                {
                                    ConnectReverse(client);
                                }
                            }
                            else
                            {
                                if (config.Client.UseTcp | client.UseTcp == false)
                                {
                                    _ = Relay(client, ServerType.UDP, true);
                                }
                                _ = Relay(client, ServerType.TCP, true);
                            }
                        }
                    }

                    firstClients.Reverse();
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex);
            }
        }

        public async Task<ConcurrentDictionary<ulong, ConnectInfo[]>> Connects()
        {
            await relayMessengerSender.AskConnects();
            return connecRouteCaching.Connects;
        }
        public async Task<Dictionary<ulong, int[]>> Delay(ulong[] toid)
        {
            Dictionary<ulong, int[]> data = new Dictionary<ulong, int[]>();
            bool res = false;
            DateTime current;

            foreach (var item in clientInfoCaching.All().Where(c => c.UseRelay && c.Id != toid && c.Id != registerState.ConnectId))
            {
                if (data.ContainsKey(item.Id)) continue;

                int[] result = new int[] { -1, -1 };
                data.Add(item.Id, result);

                if (config.Client.UseTcp && item.UseTcp && item.TcpConnected && item.TcpConnectType != ClientConnectTypes.RelayServer)
                {
                    current = DateTime.Now;
                    res = await relayMessengerSender.Delay(toid, item.TcpConnection);
                    if (res)
                    {
                        result[0] = (int)(DateTime.Now - current).TotalMilliseconds;
                    }
                }

                if (config.Client.UseUdp && item.UseUdp && item.UdpConnected && item.UdpConnectType != ClientConnectTypes.RelayServer)
                {
                    current = DateTime.Now;
                    res = await relayMessengerSender.Delay(toid, item.UdpConnection);
                    if (res)
                    {
                        result[1] = (int)(DateTime.Now - current).TotalMilliseconds;
                    }
                }
            }


            int[] resultServer = new int[] { -1, -1 };
            data.Add(0, resultServer);

            if (config.Client.UseTcp)
            {
                current = DateTime.Now;
                res = await relayMessengerSender.Delay(toid, registerState.TcpConnection);
                if (res)
                {
                    resultServer[0] = (int)(DateTime.Now - current).TotalMilliseconds;
                }
            }
            if (config.Client.UseUdp)
            {
                current = DateTime.Now;
                res = await relayMessengerSender.Delay(toid, registerState.UdpConnection);
                if (res)
                {
                    resultServer[1] = (int)(DateTime.Now - current).TotalMilliseconds;
                }
            }


            return data;
        }
        private async Task Relay(ClientInfo client, ServerType serverType, bool notify = false)
        {
            if (registerState.RemoteInfo.Relay == false)
            {
                return;
            }
            IConnection sourceConnection = serverType switch
            {
                ServerType.TCP => registerState.TcpConnection,
                ServerType.UDP => registerState.UdpConnection,
                _ => throw new NotImplementedException(),
            };
            await Relay1(client, sourceConnection, notify);
        }
        private async Task Relay1(ClientInfo client, IConnection sourceConnection, bool notify = false)
        {
            bool connected = sourceConnection.ServerType switch
            {
                ServerType.TCP => client.TcpConnected,
                ServerType.UDP => client.UdpConnected,
                _ => false,
            };
            if (connected) return;

            await Relay(client, sourceConnection, notify);
        }
        public async Task Relay(ClientInfo client, IConnection sourceConnection, bool notify = false)
        {
            if (sourceConnection == null || sourceConnection.Connected == false) return;

            IConnection connection = sourceConnection.Clone();
            connection.Relay = true;
            //connection.RelayId = sourceConnection.ConnectId;

            bool isServer = sourceConnection.ServerType switch
            {
                ServerType.TCP => ReferenceEquals(registerState.TcpConnection, sourceConnection),
                ServerType.UDP => ReferenceEquals(registerState.UdpConnection, sourceConnection),
                ServerType.TCPUDP => true,
                _ => true,
            };
            ClientConnectTypes relayType = isServer ? ClientConnectTypes.RelayServer : ClientConnectTypes.RelayNode;
            clientInfoCaching.Online(client.Id, connection, relayType);

            if (notify)
            {
                await relayMessengerSender.Relay(client.Id, sourceConnection);
            }
        }

        [Flags]
        enum EnumConnectResult : byte
        {
            Fail = 1,
            Success = 2,
            BreakOff = 4
        }
    }

}
