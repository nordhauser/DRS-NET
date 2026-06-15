using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Utilities;
using DungeonRunners.Engine;

namespace DungeonRunners.Combat
{
    public sealed class UnitMoverSim
    {
        private const int TwoPiRaw = 0x16800;
        private const int HalfPiRaw = TwoPiRaw / 4;

        public enum MoveStateEnum : byte
        {
            Idle = 0,
            RotatePrimary = 1,
            PathFollow = 2,
            RotateSecondary = 3,
        }


        public Fixed32 PosX, PosY, PosZ;

        public int HeadingReset;

        public byte Flags;

        public MoveStateEnum State;

        public int HeadingCurrent;

        public int HeadingTargetSecondary;

        public int HeadingDelta;

        public int PathRequestId = -1;

        public byte ReplanCooldown;

        public List<(int X, int Y)> Waypoints = new List<(int X, int Y)>();

        public int SteeringAccumX, SteeringAccumY;

        public PathMap PathMap;

        public Pathfinder Pathfinder;

        public Fixed32 Speed;

        public Fixed32 ArriveRadius = Fixed32.FromInt(5);

        public System.Action OnArrived;

        private int _goalX, _goalY;
        private bool _goalSet;

        private int _chaseLastPlannedGoalX, _chaseLastPlannedGoalY;
        private bool _chaseLastPlannedGoalSet;
        private byte _chaseReplanCooldownTicks;


        public void UpdateMovement()
        {
            UpdateSteering();
            MoveUnit();
        }

        public void UpdateSteering()
        {
            switch (State)
            {
                case MoveStateEnum.Idle:
                    return;

                case MoveStateEnum.RotatePrimary:
                    HeadingCurrent = NormalizeAngle(HeadingCurrent + HeadingDelta);
                    return;

                case MoveStateEnum.RotateSecondary:
                    HeadingTargetSecondary = NormalizeAngle(HeadingTargetSecondary + HeadingDelta);
                    HeadingCurrent = HeadingTargetSecondary;
                    return;

                case MoveStateEnum.PathFollow:
                    UpdateSteeringPathFollow();
                    return;
            }
        }

        private void UpdateSteeringPathFollow()
        {
            if (PathRequestId != -1) return;

            while (Waypoints.Count > 0)
            {
                var wp = Waypoints[Waypoints.Count - 1];
                long dx0 = wp.X - PosX.RawValue;
                long dy0 = wp.Y - PosY.RawValue;
                if (dx0 * dx0 + dy0 * dy0 != 0) break;
                Flags = (byte)(Flags & 0xFB);
                Waypoints.RemoveAt(Waypoints.Count - 1);
            }

            if (Waypoints.Count == 0)
            {
                ReplanCooldown = 0;
                State = MoveStateEnum.Idle;
                OnArrived?.Invoke();
                return;
            }

            var target = Waypoints[Waypoints.Count - 1];
            int deltaXRaw = target.X - PosX.RawValue;
            int deltaYRaw = target.Y - PosY.RawValue;
            long lenSq = (long)deltaXRaw * deltaXRaw + (long)deltaYRaw * deltaYRaw;

            HeadingCurrent = Fixed32Math.HeadingFromVector(deltaXRaw, deltaYRaw).RawValue;

            if (PathMap != null && _goalSet)
            {
                bool reachable = PathMap.IsWalkable(
                    _goalX / (float)Fixed32.OneRaw,
                    _goalY / (float)Fixed32.OneRaw);
                if (reachable)
                {
                    ReplanCooldown = 0;
                }
                else if (ReplanCooldown == 0)
                {
                    ReplanCooldown = 0xF;
                }
                else
                {
                    ReplanCooldown = (byte)(ReplanCooldown - 1);
                    if (ReplanCooldown == 0)
                    {
                        ReissuePathRequest();
                        if (PathRequestId != -1) return;
                    }
                }
            }

            long arriveRaw = ArriveRadius.RawValue;
            long arriveRadiusSq = arriveRaw * arriveRaw;
            bool arrived = lenSq <= arriveRadiusSq;
            if (arrived) Flags |= 0x04;
            else Flags = (byte)(Flags & 0xFB);

            if (!arrived) return;

            SteeringAccumX += deltaXRaw;
            SteeringAccumY += deltaYRaw;
            Flags = (byte)(Flags & 0xFB);
            Waypoints.RemoveAt(Waypoints.Count - 1);

            if (Waypoints.Count > 0)
            {
                var next = Waypoints[Waypoints.Count - 1];
                HeadingCurrent = Fixed32Math.HeadingFromVector(
                    next.X - PosX.RawValue,
                    next.Y - PosY.RawValue).RawValue;
            }
            else
            {
                ReplanCooldown = 0;
                State = MoveStateEnum.Idle;
                OnArrived?.Invoke();
            }
        }

        private void ReissuePathRequest()
        {
            if (Pathfinder == null || PathMap == null || !_goalSet) return;
            var startNode = PathMap.GetNodeAtWorld(PosX.ToFloat(), PosY.ToFloat());
            var goalNode = PathMap.GetNodeAtWorld(
                _goalX / (float)Fixed32.OneRaw,
                _goalY / (float)Fixed32.OneRaw);
            if (startNode == null || goalNode == null) return;
            Pathfinder.RequestPath(startNode, goalNode);
            while (!Pathfinder.IsDone && Pathfinder.NodesExpanded < 32768)
                Pathfinder.UpdateRequest(64);
            if (Pathfinder.GetPath(out var waypointNodes))
            {
                OnPathRequestComplete(0, waypointNodes);
            }
        }

        public void MoveUnit()
        {
            if (State != MoveStateEnum.PathFollow && State != MoveStateEnum.RotatePrimary && State != MoveStateEnum.RotateSecondary)
                return;
            if (Speed.RawValue == 0) return;

            var (vx, vy) = VectorType2D.FromHeading(new Fixed32(HeadingCurrent));

            int stepX = (int)(((long)vx.RawValue * Speed.RawValue) >> 8);
            int stepY = (int)(((long)vy.RawValue * Speed.RawValue) >> 8);

            ResolveMovement(stepX, stepY);
        }

        public bool ResolveMovement(int dxRaw, int dyRaw)
        {
            if (PathMap == null)
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            float maxAxisStep = (PathMap.NodeResolution * 0.5f);
            int maxStepRaw = Fixed32.FromFloat(maxAxisStep).RawValue;
            int axisMag = Math.Max(Math.Abs(dxRaw), Math.Abs(dyRaw));
            int subSteps = Math.Max(1, (axisMag + maxStepRaw - 1) / maxStepRaw);

            int dxSub = dxRaw / subSteps;
            int dySub = dyRaw / subSteps;
            int dxRem = dxRaw - dxSub * subSteps;
            int dyRem = dyRaw - dySub * subSteps;

            bool anyMoved = false;
            for (int subStepIndex = 0; subStepIndex < subSteps; subStepIndex++)
            {
                int stepX = dxSub + (subStepIndex == 0 ? dxRem : 0);
                int stepY = dySub + (subStepIndex == 0 ? dyRem : 0);
                if (DoSingleStep(stepX, stepY)) anyMoved = true;
                else break;
            }
            return anyMoved;
        }

        private bool DoSingleStep(int dxRaw, int dyRaw)
        {
            float curX = PosX.ToFloat();
            float curY = PosY.ToFloat();
            float proposedX = curX + dxRaw / (float)Fixed32.OneRaw;
            float proposedY = curY + dyRaw / (float)Fixed32.OneRaw;

            if (PathMap.IsWalkable(proposedX, proposedY))
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            if (dxRaw != 0 && PathMap.IsWalkable(proposedX, curY))
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                return true;
            }

            if (dyRaw != 0 && PathMap.IsWalkable(curX, proposedY))
            {
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            return false;
        }


        public void MoveToPoint(int targetX, int targetY)
        {
            if (PosX.RawValue == targetX && PosY.RawValue == targetY) return;

            if (State == MoveStateEnum.PathFollow
                && Waypoints.Count > 0
                && Waypoints[0].X == targetX
                && Waypoints[0].Y == targetY)
            {
                return;
            }

            ClearMoveState();
            State = MoveStateEnum.PathFollow;
            _goalX = targetX;
            _goalY = targetY;
            _goalSet = true;

            if (Pathfinder != null && PathMap != null)
            {
                var startNode = PathMap.GetNodeAtWorld(
                    PosX.ToFloat(),
                    PosY.ToFloat());
                var goalNode = PathMap.GetNodeAtWorld(
                    targetX / (float)Fixed32.OneRaw,
                    targetY / (float)Fixed32.OneRaw);

                if (startNode != null && goalNode != null)
                {
                    Pathfinder.RequestPath(startNode, goalNode);
                    while (!Pathfinder.IsDone && Pathfinder.NodesExpanded < 32768)
                        Pathfinder.UpdateRequest(64);

                    if (Pathfinder.GetPath(out var waypointNodes))
                    {
                        OnPathRequestComplete(0, waypointNodes);
                        return;
                    }
                }
            }

            Waypoints.Clear();
            Waypoints.Add((targetX, targetY));
        }

        public void OnPathRequestComplete(int requestId, List<PathNode> pathWaypoints)
        {
            PathRequestId = -1;
            Waypoints.Clear();
            if (pathWaypoints == null || pathWaypoints.Count == 0) return;
            for (int waypointIndex = pathWaypoints.Count - 1; waypointIndex >= 1; waypointIndex--)
            {
                var waypoint = pathWaypoints[waypointIndex];
                Waypoints.Add((Fixed32.FromFloat(waypoint.WorldX).RawValue,
                               Fixed32.FromFloat(waypoint.WorldY).RawValue));
            }
        }

        public void ClearMoveState()
        {
            Flags &= 0xFB;
            State = MoveStateEnum.Idle;
            HeadingCurrent = HeadingReset;
            HeadingTargetSecondary = HeadingReset;
            HeadingDelta = 0;
            Waypoints.Clear();
            PathRequestId = -1;
            ReplanCooldown = 0;
            _goalSet = false;
            SteeringAccumX = 0;
            SteeringAccumY = 0;
            _chaseLastPlannedGoalSet = false;
            _chaseReplanCooldownTicks = 0;
        }

        public void SetChaseTarget(int targetXRaw, int targetYRaw, int arriveRadiusRaw,
                                   byte replanCooldownTicks, int replanThresholdRaw)
        {
            ArriveRadius = new Fixed32(arriveRadiusRaw);

            bool needsReplan = false;
            if (!_chaseLastPlannedGoalSet || State != MoveStateEnum.PathFollow || Waypoints.Count == 0)
            {
                needsReplan = true;
            }
            else
            {
                long dx = (long)targetXRaw - _chaseLastPlannedGoalX;
                long dy = (long)targetYRaw - _chaseLastPlannedGoalY;
                long distSq = dx * dx + dy * dy;
                long thresholdSq = (long)replanThresholdRaw * replanThresholdRaw;
                if (distSq > thresholdSq)
                {
                    needsReplan = true;
                }
                else if (_chaseReplanCooldownTicks > 0)
                {
                    _chaseReplanCooldownTicks--;
                }
                else
                {
                    needsReplan = true;
                }
            }

            if (!needsReplan) return;

            ClearMoveState();
            State = MoveStateEnum.PathFollow;
            _goalX = targetXRaw;
            _goalY = targetYRaw;
            _goalSet = true;
            Waypoints.Clear();
            Waypoints.Add((targetXRaw, targetYRaw));

            _chaseLastPlannedGoalX = targetXRaw;
            _chaseLastPlannedGoalY = targetYRaw;
            _chaseLastPlannedGoalSet = true;
            _chaseReplanCooldownTicks = replanCooldownTicks;
        }

        public void EndChaseTracking()
        {
            _chaseLastPlannedGoalSet = false;
            _chaseReplanCooldownTicks = 0;
        }


        public static int NormalizeAngle(int angle)
        {
            if (angle < 0)
            {
                while (angle < 0) angle += TwoPiRaw;
                return angle;
            }
            if (angle >= TwoPiRaw)
            {
                while (angle >= TwoPiRaw) angle -= TwoPiRaw;
                return angle;
            }
            return angle;
        }
    }
}
