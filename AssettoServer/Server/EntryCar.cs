﻿using AssettoServer.Network.Packets;
using AssettoServer.Network.Packets.Incoming;
using AssettoServer.Network.Packets.Shared;
using AssettoServer.Network.Tcp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using AssettoServer.Network.Packets.Outgoing;
using AssettoServer.Server.Ai;
using JPBotelho;
using Serilog;

namespace AssettoServer.Server
{
    public enum AiMode
    {
        Disabled,
        Auto,
        Fixed
    }
    
    public class EntryCar
    {
        public ACServer Server { get; internal set; }
        public ACTcpClient Client { get; internal set; }
        public CarStatus Status { get; private set; } = new CarStatus();

        public bool ForceLights { get; internal set; }
        public int HighPingSeconds { get; internal set; }
        public int LightFlashCount { get; internal set; }

        public long LastActiveTime { get; internal set; }
        public bool HasSentAfkWarning { get; internal set; }
        public bool HasUpdateToSend { get; internal set; }
        public int TimeOffset { get; internal set; }
        public byte SessionId { get; internal set; }
        public uint LastRemoteTimestamp { get; internal set; }
        public int LastPingTime { get; internal set; }
        public int LastPongTime { get; internal set; }
        public ushort Ping { get; internal set; }

        public bool IsSpectator { get; internal set; }
        public string Model { get; internal set; }
        public string Skin { get; internal set; }
        public int SpectatorMode { get; internal set; }
        public int Ballast { get; internal set; }
        public int Restrictor { get; internal set; }

        internal long[] OtherCarsLastSentUpdateTime { get; set; }
        internal Race CurrentRace { get; set; }
        internal EntryCar TargetCar { get; set; }
        internal long LastFallCheckTime{ get; set;}

        private long LastLightFlashTime { get; set; }
        private long LastRaceChallengeTime { get; set; }
        
        public long AiSpawnProtectionEnds { get; set; }
        public bool AiControlled { get; set; }
        public AiMode AiMode { get; set; }
        public float AiSafetyDistanceSquared { get; set; } = 20 * 20;
        public float AiAcceleration { get; set; }
        public float AiCurrentSpeed { get; private set; } = AiMaxSpeed;
        public float AiTargetSpeed { get; private set; } = AiMaxSpeed;

        public const float AiMaxSpeed = 80 / 3.6f;
        public const float AiDefaultDeceleration = -4.5f;
        public const float AiDefaultAcceleration = 4.5f;
        
        private Vector3 _aiStartTangent;
        private Vector3 _aiEndTangent;

        private float _aiCurrentVecLength;
        private float _aiCurrentVecProgress;
        private bool _aiInitialized = false;
        private long _aiLastTick = Environment.TickCount64;
        private bool _aiStoppedForObstacle;
        private long _aiStoppedForObstacleSince;
        private long _aiIgnoreObstaclesUntil;
        private long _aiObstacleHonkStart;
        private long _aiObstacleHonkEnd;
        private TrafficSplinePoint _aiCurrentPoint;

        private Random _random = new Random();

        public void AiTeleport(TrafficSplinePoint point)
        {
            if (point == null || point.Next == null)
            {
                return;
            }

            _aiCurrentPoint = point;
            _aiCurrentVecLength = (_aiCurrentPoint.Next.Point - _aiCurrentPoint.Point).Length();
            _aiCurrentVecProgress = 0;
            
            CalculateTangents();
        }

        private void CalculateTangents()
        {
            if (_aiCurrentPoint.Previous == null)
            {
                _aiStartTangent = (_aiCurrentPoint.Next.Point - _aiCurrentPoint.Point) * 0.5f;
            }
            else
            {
                _aiStartTangent = (_aiCurrentPoint.Point - _aiCurrentPoint.Previous.Point) * 0.5f;
            }

            if (_aiCurrentPoint.Next.Next == null)
            {
                _aiEndTangent = (_aiCurrentPoint.Next.Point - _aiCurrentPoint.Point) * 0.5f;
            }
            else
            {
                _aiEndTangent = (_aiCurrentPoint.Next.Next.Point - _aiCurrentPoint.Point) * 0.5f;
            }
        }

        public bool AiMove(float progress)
        {
            while (progress > _aiCurrentVecLength)
            {
                progress -= _aiCurrentVecLength;
                
                if (_aiCurrentPoint.Next == null 
                    || _aiCurrentPoint.Next.Next == null)
                {
                    Log.Warning("Spline end");
                    return false;
                }
                
                _aiCurrentPoint = _aiCurrentPoint.Next;
                _aiCurrentVecLength = (_aiCurrentPoint.Next.Point - _aiCurrentPoint.Point).Length();
            }

            CalculateTangents();

            _aiCurrentVecProgress = progress;

            return true;
        }

        public void AiDetectCollisions()
        {
            if (Environment.TickCount64 < _aiIgnoreObstaclesUntil)
            {
                AiSetTargetSpeed(AiMaxSpeed);
                return;
            }
            
            float minSpeed = AiMaxSpeed;
            bool hasObstacle = false;
            
            foreach (var car in Server.EntryCars.Where(car => car.AiControlled || (car.Client != null && car.Client.HasSentFirstUpdate)))
            {
                if (car == this) continue;

                float carSpeed = car.Status.Velocity.Length();
                float distance = Vector3.Distance(car.Status.Position, Status.Position);

                if (carSpeed < 0.1f)
                {
                    carSpeed = 0;
                }

                // Check first if car is in front of us
                if (GetAngleToCar(car) is > 165 and < 195)
                {
                    // always brake if the distance is too small
                    if (distance < 10)
                    {
                        minSpeed = 0;
                        hasObstacle = true;
                    }
                    else
                    {
                        // Make full stop if speed is too low. Someone might be trying to turn around
                        if (carSpeed < 20 / 3.6f)
                        {
                            carSpeed = 5 / 3.6f;
                            //carSpeed = Math.Clamp(carSpeed, 0, 5 / 3.6f);
                        }
                        
                        if ((carSpeed + 1 < AiCurrentSpeed || carSpeed == 0)
                            && distance < GetBrakingDistance(carSpeed) * 1.5 + 20)
                        {
                            minSpeed = Math.Min(minSpeed, Math.Max(5 / 3.6f, carSpeed));
                            hasObstacle = true;
                        }
                    }
                }
            }
            
            if (AiCurrentSpeed == 0 && !_aiStoppedForObstacle && hasObstacle)
            {
                _aiStoppedForObstacle = true;
                _aiStoppedForObstacleSince = Environment.TickCount64;
                _aiObstacleHonkStart = _aiStoppedForObstacleSince + _random.Next(3000, 7000);
                _aiObstacleHonkEnd = _aiObstacleHonkStart + _random.Next(500, 1500);
                Log.Debug("AI {0} stopped for obstacle", SessionId);
            }
            else if (_aiStoppedForObstacle && !hasObstacle)
            {
                _aiStoppedForObstacle = false;
                Log.Debug("AI {0} no longer stopped for obstacle", SessionId);
            }

            if (_aiStoppedForObstacle && Environment.TickCount64 - _aiStoppedForObstacleSince > 10_000)
            {
                _aiIgnoreObstaclesUntil = Environment.TickCount64 + 10_000;
                Log.Debug("AI {0} ignoring obstacles until {1}", SessionId, _aiIgnoreObstaclesUntil);
            }
            
            AiSetTargetSpeed(minSpeed);
        }

        public float GetAngleToCar(EntryCar car)
        {
            float challengedAngle = (float) (Math.Atan2(Status.Position.X - car.Status.Position.X, Status.Position.Z - car.Status.Position.Z) * 180 / Math.PI);
            if (challengedAngle < 0)
                challengedAngle += 360;
            float challengedRot = Status.GetRotationAngle();

            challengedAngle += challengedRot;
            challengedAngle %= 360;

            return challengedAngle;
        }

        public float GetBrakingDistance(float targetSpeed)
        {
            return (float) Math.Abs(Math.Pow(targetSpeed - AiCurrentSpeed, 2) / (2 * AiDefaultDeceleration));
        }

        public float GetTyreAngularSpeed(float speed, float wheelDiameter)
        {
            return (float) (speed / (Math.PI * wheelDiameter) * 6);
        }
        
        public void AiSetTargetSpeed(float speed)
        {
            AiTargetSpeed = speed;
            if (speed < AiCurrentSpeed)
            {
                AiAcceleration = AiDefaultDeceleration;
            }
            else if(speed > AiCurrentSpeed)
            {
                AiAcceleration = AiDefaultAcceleration;
            }
            else
            {
                AiAcceleration = 0;
            }
        }

        public void SetAiControl(bool aiControlled)
        {
            if (AiControlled != aiControlled)
            {
                AiControlled = aiControlled;

                if (AiControlled)
                {
                    Log.Debug("Slot {0} is now controlled by AI", SessionId);
                    Reset();
                    
                    Server.BroadcastPacket(new CarConnected
                    {
                        SessionId = SessionId,
                        Name = $"Traffic {SessionId}"
                    });
                }
                else
                {
                    Log.Debug("Slot {0} is no longer controlled by AI", SessionId);
                    Server.BroadcastPacket(new CarDisconnected { SessionId = SessionId });
                }
            }
        }

        public void AiUpdate()
        {
            if (!_aiInitialized) // TODO remove?
            {
                int spawnPos = new Random().Next(0, Server.TrafficMap.Splines[0].Points.Length);
                AiTeleport(Server.TrafficMap.Splines[0].Points[spawnPos]);
                _aiInitialized = true;
            }
            
            long dt = Environment.TickCount64 - _aiLastTick;
            _aiLastTick = Environment.TickCount64;

            if (AiAcceleration != 0)
            {
                AiCurrentSpeed += AiAcceleration * (dt / 1000.0f);
                
                if ((AiAcceleration < 0 && AiCurrentSpeed < AiTargetSpeed) || (AiAcceleration > 0 && AiCurrentSpeed > AiTargetSpeed))
                {
                    AiCurrentSpeed = AiTargetSpeed;
                    AiAcceleration = 0;
                }
            }

            float moveMeters = (dt / 1000.0f) * AiCurrentSpeed;
            if (!AiMove(_aiCurrentVecProgress + moveMeters))
            {
                Log.Debug("Car {0} reached spline end, respawning", SessionId);
                AiTeleport(Server.TrafficMap.Splines[0].Points[0]);
            }

            CatmullRom.CatmullRomPoint smoothPos = CatmullRom.Evaluate(_aiCurrentPoint.Point, _aiCurrentPoint.Next.Point, _aiStartTangent, _aiEndTangent, _aiCurrentVecProgress / _aiCurrentVecLength);
                
            Vector3 rotation = new Vector3()
            {
                X = (float)(Math.Atan2(smoothPos.Tangent.Z, smoothPos.Tangent.X) - Math.PI / 2),
                Y = (float)(Math.Atan2(new Vector2(smoothPos.Tangent.Z, smoothPos.Tangent.X).Length(), smoothPos.Tangent.Y) - Math.PI / 2) * -1f,
                Z = 0
            };

            byte tyreAngularSpeed = (byte) Math.Min(byte.MaxValue, 100 + GetTyreAngularSpeed(AiCurrentSpeed, 0.65f));

            UpdatePosition(new PositionUpdate()
            {
                PakSequenceId = (byte)(Status.PakSequenceId + 1),
                Timestamp = (uint)(Environment.TickCount - Server.StartTime),
                LastRemoteTimestamp = (uint)(Environment.TickCount - Server.StartTime),
                Position = smoothPos.Position,
                Rotation = rotation,
                Velocity = smoothPos.Tangent * AiCurrentSpeed,
                SteerAngle = 127,
                WheelAngle = 127,
                TyreAngularSpeedFL = tyreAngularSpeed,
                TyreAngularSpeedFR = tyreAngularSpeed,
                TyreAngularSpeedRL = tyreAngularSpeed,
                TyreAngularSpeedRR = tyreAngularSpeed,
                EngineRpm = (ushort) MathUtils.Lerp(800, 3000, AiCurrentSpeed / AiMaxSpeed),
                StatusFlag = CarStatusFlags.LightsOn
                             | CarStatusFlags.HighBeamsOff
                             | (AiCurrentSpeed < 20 / 3.6f ? CarStatusFlags.HazardsOn : 0)
                             | (AiCurrentSpeed == 0 || AiAcceleration < 0 ? CarStatusFlags.BrakeLightsOn : 0)
                             | (_aiStoppedForObstacle && Environment.TickCount64 > _aiObstacleHonkStart && Environment.TickCount64 < _aiObstacleHonkEnd ? CarStatusFlags.Horn : 0),
                Gear = 2
            });
        }

        internal void Reset()
        {
            IsSpectator = false;
            SpectatorMode = 0;
            LastActiveTime = 0;
            HasSentAfkWarning = false;
            HasUpdateToSend = false;
            TimeOffset = 0;
            LastRemoteTimestamp = 0;
            HighPingSeconds = 0;
            LastPingTime = 0;
            Ping = 0;
            ForceLights = false;
            Status = new CarStatus();
            CurrentRace = null;
            TargetCar = null;
            AiSpawnProtectionEnds = 0;
            _aiInitialized = false;
        }

        internal void CheckAfk()
        {
            if (!Server.Configuration.Extra.EnableAntiAfk || Client?.IsAdministrator == true)
                return;

            long timeAfk = Environment.TickCount64 - LastActiveTime;
            if (timeAfk > Server.Configuration.Extra.MaxAfkTimeMilliseconds)
                _ = Server.KickAsync(Client, Network.Packets.Outgoing.KickReason.None, $"{Client?.Name} has been kicked for being AFK.");
            else if (!HasSentAfkWarning && Server.Configuration.Extra.MaxAfkTimeMilliseconds - timeAfk < 60000)
            {
                HasSentAfkWarning = true;
                Client?.SendPacket(new ChatMessage { SessionId = 255, Message = "You will be kicked in 1 minute for being AFK." });
            }
        }

        internal void SetActive()
        {
            LastActiveTime = Environment.TickCount64;
            HasSentAfkWarning = false;
        }

        internal void UpdatePosition(PositionUpdate positionUpdate)
        {
            HasUpdateToSend = true;
            LastRemoteTimestamp = positionUpdate.LastRemoteTimestamp;

            if (positionUpdate.StatusFlag != Status.StatusFlag || positionUpdate.Gas != Status.Gas || positionUpdate.SteerAngle != Status.SteerAngle)
                SetActive();

            long currentTick = Environment.TickCount64;
            if(((Status.StatusFlag & CarStatusFlags.LightsOn) == 0 && (positionUpdate.StatusFlag & CarStatusFlags.LightsOn) != 0) || ((Status.StatusFlag & CarStatusFlags.HighBeamsOff) == 0 && (positionUpdate.StatusFlag & CarStatusFlags.HighBeamsOff) != 0))
            {
                LastLightFlashTime = currentTick;
                LightFlashCount++;
            }

            if ((Status.StatusFlag & CarStatusFlags.HazardsOn) == 0 && (positionUpdate.StatusFlag & CarStatusFlags.HazardsOn) != 0)
            {
                if (CurrentRace != null && !CurrentRace.HasStarted && !CurrentRace.LineUpRequired)
                    _ = CurrentRace.StartAsync();
            }

            if (currentTick - LastLightFlashTime > 3000 && LightFlashCount > 0)
            {
                LightFlashCount = 0;
            }

            if (LightFlashCount == 3)
            {
                LightFlashCount = 0;

                if(currentTick - LastRaceChallengeTime > 20000)
                {
                    Task.Run(ChallengeNearbyCar);
                    LastRaceChallengeTime = currentTick;
                }
            }

            /*if (!AiControlled && Status.StatusFlag != positionUpdate.StatusFlag)
            {
                Log.Debug("Status flag from {0:X} to {1:X}", Status.StatusFlag, positionUpdate.StatusFlag);
            }*/


            Status.Timestamp = LastRemoteTimestamp + TimeOffset;
            Status.PakSequenceId = positionUpdate.PakSequenceId;
            Status.Position = positionUpdate.Position;
            Status.Rotation = positionUpdate.Rotation;
            Status.Velocity = positionUpdate.Velocity;
            Status.TyreAngularSpeed[0] = positionUpdate.TyreAngularSpeedFL;
            Status.TyreAngularSpeed[1] = positionUpdate.TyreAngularSpeedFR;
            Status.TyreAngularSpeed[2] = positionUpdate.TyreAngularSpeedRL;
            Status.TyreAngularSpeed[3] = positionUpdate.TyreAngularSpeedRR;
            Status.SteerAngle = positionUpdate.SteerAngle;
            Status.WheelAngle = positionUpdate.WheelAngle;
            Status.EngineRpm = positionUpdate.EngineRpm;
            Status.Gear = positionUpdate.Gear;
            Status.StatusFlag = positionUpdate.StatusFlag;
            Status.PerformanceDelta = positionUpdate.PerformanceDelta;
            Status.Gas = positionUpdate.Gas;
            Status.NormalizedPosition = positionUpdate.NormalizedPosition;
        }

        internal void ChallengeCar(EntryCar car, bool lineUpRequired = true)
        {
            void Reply(string message)
                => Client.SendPacket(new ChatMessage { SessionId = 255, Message = message });

            Race currentRace = CurrentRace;
            if (currentRace != null)
            {
                if (currentRace.HasStarted)
                    Reply("You are currently in a race.");
                else
                    Reply("You have a pending race request.");
            }
            else
            {
                if (car == this)
                    Reply("You cannot challenge yourself to a race.");
                else
                {
                    currentRace = car.CurrentRace;
                    if (currentRace != null)
                    {
                        if (currentRace.HasStarted)
                            Reply("This car is currently in a race.");
                        else
                            Reply("This car has a pending race request.");
                    }
                    else
                    {
                        currentRace = new Race(Server, this, car, lineUpRequired);
                        CurrentRace = currentRace;
                        car.CurrentRace = currentRace;

                        Client.SendPacket(new ChatMessage { SessionId = 255, Message = $"You have challenged {car.Client.Name} to a race." });

                        if (lineUpRequired)
                            car.Client.SendPacket(new ChatMessage { SessionId = 255, Message = $"{Client.Name} has challenged you to a race. Send /accept within 10 seconds to accept." });
                        else
                            car.Client.SendPacket(new ChatMessage { SessionId = 255, Message = $"{Client.Name} has challenged you to a race. Flash your hazard lights or send /accept within 10 seconds to accept." });

                        _ = Task.Delay(10000).ContinueWith(t =>
                        {
                            if (!currentRace.HasStarted)
                            {
                                CurrentRace = null;
                                car.CurrentRace = null;

                                ChatMessage timeoutMessage = new ChatMessage { SessionId = 255, Message = $"Race request has timed out." };
                                Client.SendPacket(timeoutMessage);
                                car.Client.SendPacket(timeoutMessage);
                            }
                        });
                    }
                }
            }
        }

        private void ChallengeNearbyCar()
        {
            EntryCar bestMatch = null;
            float distanceSquared = 30 * 30;

            foreach(EntryCar car in Server.EntryCars)
            {
                ACTcpClient carClient = car.Client;
                if(carClient != null && car != this)
                {
                    float challengedAngle = (float)(Math.Atan2(Status.Position.X - car.Status.Position.X, Status.Position.Z - car.Status.Position.Z) * 180 / Math.PI);
                    if (challengedAngle < 0)
                        challengedAngle += 360;
                    float challengedRot = car.Status.GetRotationAngle();

                    challengedAngle += challengedRot;
                    challengedAngle %= 360;

                    if (challengedAngle > 110 && challengedAngle < 250 && Vector3.DistanceSquared(car.Status.Position, Status.Position) < distanceSquared)
                        bestMatch = car;
                }
            }

            if (bestMatch != null)
                ChallengeCar(bestMatch, false);
        }
    }

    public class CarStatus
    {
        public float[] DamageZoneLevel { get; } = new float[5];
        public short P2PCount { get; internal set; }
        public bool P2PActive { get; internal set; }
        public bool MandatoryPit { get; internal set; }
        public string CurrentTyreCompound { get; internal set; }

        public byte PakSequenceId { get; internal set; }
        public Vector3 Position { get; internal set; }
        public Vector3 Rotation { get; internal set; }
        public Vector3 Velocity { get; internal set; }
        public long Timestamp { get; internal set; }
        public byte[] TyreAngularSpeed { get; } = new byte[4];
        public byte SteerAngle { get; internal set; }
        public byte WheelAngle { get; internal set; }
        public ushort EngineRpm { get; internal set; }
        public byte Gear { get; internal set; }
        public CarStatusFlags StatusFlag { get; internal set; }
        public short PerformanceDelta { get; internal set; }
        public byte Gas { get; internal set; }
        public float NormalizedPosition { get; internal set; }

        public float GetRotationAngle()
        {
            float angle = (float)(Rotation.X * 180 / Math.PI);
            if (angle < 0)
                angle += 360;

            return angle;
        }

        public float GetVelocityAngle()
        {
            if (Math.Abs(Velocity.X) < 1 && Math.Abs(Velocity.Z) < 1)
                return GetRotationAngle();

            Vector3 normalizedVelocity = Vector3.Normalize(Velocity);
            float angle = (float)-(Math.Atan2(normalizedVelocity.X, normalizedVelocity.Z) * 180 / Math.PI);
            if (angle < 0)
                angle += 360;

            return angle;
        }
    }
}
