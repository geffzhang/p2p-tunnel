﻿using client.messengers.clients;
using common.libs;
using common.libs.extends;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace client.service.vea
{
    public class VirtualEthernetAdapterTransfer
    {
        Process Tun2SocksProcess;
        int interfaceNumber = 0;
        const string veaName = "p2p-tunnel";

        private readonly ConcurrentDictionary<IPAddress, IPAddressCacheInfo> ips = new ConcurrentDictionary<IPAddress, IPAddressCacheInfo>();
        private readonly ConcurrentDictionary<int, IPAddressCacheInfo> lanips = new ConcurrentDictionary<int, IPAddressCacheInfo>();
        public ConcurrentDictionary<IPAddress, IPAddressCacheInfo> IPList => ips;
        public ConcurrentDictionary<int, IPAddressCacheInfo> LanIPList => lanips;

        private readonly Config config;
        private readonly IVeaSocks5ClientListener socks5ClientListener;

        public VirtualEthernetAdapterTransfer(Config config, IClientInfoCaching clientInfoCaching, VeaMessengerSender veaMessengerSender, IVeaSocks5ClientListener socks5ClientListener)
        {
            this.config = config;
            this.socks5ClientListener = socks5ClientListener;

            clientInfoCaching.OnOnline.Sub((client) =>
            {
                veaMessengerSender.IP(client.OnlineConnection).ContinueWith((result) =>
                {
                    IPAddress ip = result.Result.Item1;
                    IPAddress lanip = result.Result.Item2;

                    IPAddressCacheInfo cache = new IPAddressCacheInfo { Client = client, IP = ip, LanIP = lanip, Mask = 0 };

                    ips.AddOrUpdate(ip, cache, (a, b) => cache);
                    if (lanip.Equals(IPAddress.Any) == false)
                    {
                        int mask = GetIpMask(lanip);

                        if (lanips.ContainsKey(mask) == false)
                        {
                            cache.Mask = mask;
                            lanips.TryAdd(mask, cache);
                            AddRoute(lanip);
                        }
                    }
                });
            });
            clientInfoCaching.OnOffline.Sub((client) =>
            {
                var value = ips.FirstOrDefault(c => c.Value.Client.Id == client.Id);
                if (ips.TryRemove(value.Key, out IPAddressCacheInfo cache))
                {
                    lanips.TryRemove(cache.Mask, out _);
                }
            });

            AppDomain.CurrentDomain.ProcessExit += (object sender, EventArgs e) => KillWindows();
        }

        public void Run()
        {
            Stop();
            if (config.Enable)
            {
                RunTun2Socks();
                socks5ClientListener.Start(config.SocksPort, config.BufferSize);
            }
        }
        public void Stop()
        {
            socks5ClientListener.Stop();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                KillWindows();
            }
        }

        private void RunTun2Socks()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    RunWindows();
                }
                else
                {
                    throw new Exception($"需要管理员权限");
                }
            }
        }

        private void KillWindows()
        {
            interfaceNumber = 0;
            if (Tun2SocksProcess != null)
            {
                Tun2SocksProcess.Kill();
                Tun2SocksProcess.Close();
                Tun2SocksProcess.Dispose();
                Tun2SocksProcess = null;
            }
        }
        private void RunWindows()
        {
            Tun2SocksProcess = Command.Execute("tun2socks.exe", $" -device {veaName} -proxy socks5://127.0.0.1:{config.SocksPort} -loglevel silent");
            for (int i = 0; i < 60; i++)
            {
                //分配ip
                Command.Execute("cmd.exe", string.Empty, new string[] { $"netsh interface ip set address name=\"{veaName}\" source=static addr={config.IP} mask=255.255.255.0 gateway=none" });
                //网卡编号
                interfaceNumber = GetWindowsInterfaceNum();
                if (interfaceNumber > 0)
                {
                    AddRoute();
                    if (config.ProxyAll) //代理所有
                    {
                        //AddRoute(IPAddress.Any);
                    }
                    break;
                }
                else
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }
        private int GetWindowsInterfaceNum()
        {
            string output = Command.Execute("cmd.exe", string.Empty, new string[] { "route print" });
            foreach (var item in output.Split("\r\n"))
            {
                if (item.Contains("WireGuard Tunnel"))
                {
                    return int.Parse(item.Substring(0, item.IndexOf('.')).Trim());
                }
            }
            return 0;
        }

        private void AddRoute()
        {
            foreach (var item in lanips)
            {
                AddRoute(item.Value.LanIP);
            }
        }
        private void AddRoute(IPAddress ip)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AddRouteWindows(ip);
            }
        }
        private void AddRouteWindows(IPAddress ip)
        {
            if(interfaceNumber > 0)
            {
                Command.Execute("cmd.exe", string.Empty, new string[] { $"route add {ip} mask 0.0.0.0 {config.IP} metric 5 if {interfaceNumber}" });
            }
        }


        byte[] mask = new byte[] { 255, 255, 255, 0 };
        public int GetIpMask(IPAddress ip)
        {
            return GetIpMask(ip.GetAddressBytes());
        }
        public int GetIpMask(Span<byte> ip)
        {
            for (int i = 0; i < ip.Length; i++)
            {
                ip[i] &= mask[i];
            }
            return ip.ToInt32();
        }
    }

    public class IPAddressCacheInfo
    {
        public int Mask { get; set; }
        public IPAddress IP { get; set; }
        public IPAddress LanIP { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public ClientInfo Client { get; set; }
    }
}
