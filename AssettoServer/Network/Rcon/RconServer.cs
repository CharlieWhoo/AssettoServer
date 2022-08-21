﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AssettoServer.Server.Configuration;
using AssettoServer.Utils;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AssettoServer.Network.Rcon;

public class RconServer : CriticalBackgroundService
{
    private readonly ACServerConfiguration _configuration;
    private readonly Func<TcpClient, RconClient> _rconClientFactory;

    public RconServer(ACServerConfiguration configuration, Func<TcpClient, RconClient> rconClientFactory, IHostApplicationLifetime applicationLifetime) : base(applicationLifetime)
    {
        _configuration = configuration;
        _rconClientFactory = rconClientFactory;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.Server.AdminPassword?.Length < 8)
        {
            throw new ConfigurationException("ADMIN_PASSWORD must be at least 8 characters to enable RCON");
        }
        
        Log.Information("Starting RCON server on port {TcpPort}", _configuration.Extra.RconPort);
        var listener = new TcpListener(IPAddress.Any, _configuration.Extra.RconPort);
        listener.Start();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync(stoppingToken);

                RconClient rconClient = _rconClientFactory(tcpClient);
                await rconClient.StartAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error(ex, "Something went wrong while trying to accept RCON connection");
            }
        }
    }
}
