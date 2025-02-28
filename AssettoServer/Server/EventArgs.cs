﻿using System;
using System.ComponentModel;
using System.Numerics;
using AssettoServer.Network.Tcp;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Shared.Network.Packets.Outgoing.Handshake;
using AssettoServer.Shared.Network.Packets.Shared;
using CommunityToolkit.Common.Deferred;

namespace AssettoServer.Server;

public delegate void EventHandler<TSender, TArgs>(TSender sender, TArgs args) where TArgs : EventArgs;
public delegate void EventHandlerIn<TSender, TArg>(TSender sender, in TArg args) where TArg : struct;

public class HandshakeAcceptedEventArgs : DeferredEventArgs
{
    public HandshakeResponse HandshakeResponse { get; init; }
}

public class ClientAuditEventArgs : EventArgs
{
    public KickReason Reason { get; init; }
    public string? ReasonStr { get; init; }
    public ACTcpClient? Admin { get; init; }
}

public class ChatEventArgs : CancelEventArgs
{
    public string Message { get; }

    public ChatEventArgs(string message)
    {
        Message = message;
    }
}

public class ChatMessageEventArgs : EventArgs
{
    public ChatMessage ChatMessage { get; init; }
}

public class SessionChangedEventArgs : EventArgs
{
    public SessionState? PreviousSession { get; }
    public SessionState NextSession { get; }

    public SessionChangedEventArgs(SessionState? previousSession, SessionState nextSession)
    {
        PreviousSession = previousSession;
        NextSession = nextSession;
    }
}

public class CollisionEventArgs : EventArgs
{
    public EntryCar? TargetCar { get; }
    public float Speed { get; }
    public Vector3 Position { get; }
    public Vector3 RelPosition { get; }

    public CollisionEventArgs(EntryCar? targetCar, float speed, Vector3 position, Vector3 relPosition)
    {
        TargetCar = targetCar;
        Speed = speed;
        Position = position;
        RelPosition = relPosition;
    }
}

public class LapCompletedEventArgs : EventArgs
{
    public LapCompletedOutgoing Packet { get; }
    
    public LapCompletedEventArgs(LapCompletedOutgoing packet)
    {
        Packet = packet;
    }
}
