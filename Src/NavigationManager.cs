using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ValBridgeServer
{
    public class NavigationManager : MonoBehaviour
    {
        private static NavigationManager? _instance;

        public static NavigationManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ValBridgeServer_NavigationManager");
                    _instance = go.AddComponent<NavigationManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private Vector3 _targetPos;
        private readonly List<Vector3> _navPath = new List<Vector3>();
        private bool _isNavigating;
        private TaskCompletionSource<object>? _tcs;
        private float _startTime;
        private float _timeout;
        private float _lastPathCalcTime;

        private const float ArrivalDistXZ = 1.5f;
        private const float WaypointAdvanceDist = 1.5f;
        private const float PathRecalcInterval = 1f;
        private const float RunDistanceThreshold = 5f;

        public bool IsNavigating => _isNavigating;

        public Task<object> StartNavigation(Vector3 target, float timeout)
        {
            StopNavigation("Cancelled by new navigation request");

            _targetPos = target;
            _timeout = timeout;
            _startTime = Time.time;
            _lastPathCalcTime = 0f;
            _navPath.Clear();
            _isNavigating = true;
            _tcs = new TaskCompletionSource<object>();

            ValBridgeServerPlugin.ModLogger.LogInfo(
                $"Navigation started to ({target.x:F1}, {target.z:F1}), timeout={timeout}s");

            return _tcs.Task;
        }

        public void StopNavigation(string? reason = null)
        {
            if (!_isNavigating) return;

            _isNavigating = false;
            _navPath.Clear();

            var player = Player.m_localPlayer;
            if (player != null)
            {
                player.SetMoveDir(Vector3.zero);
                player.SetRun(false);
            }

            if (reason != null)
            {
                ValBridgeServerPlugin.ModLogger.LogInfo($"Navigation stopped: {reason}");
            }
        }

        private void FixedUpdate()
        {
            if (!_isNavigating || _tcs == null) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Finish(false, "Player lost");
                return;
            }

            var pos = player.transform.position;

            // Check timeout
            if (Time.time - _startTime > _timeout)
            {
                var distToTarget = Utils.DistanceXZ(pos, _targetPos);
                Finish(false, $"Timeout after {_timeout}s (distance remaining: {distToTarget:F1}m)");
                return;
            }

            // Check arrival
            var distXZ = Utils.DistanceXZ(pos, _targetPos);
            if (distXZ < ArrivalDistXZ)
            {
                Finish(true, $"Arrived (distance: {distXZ:F2}m)");
                return;
            }

            // Skip movement if player can't move right now
            if (!player.CanMove()) return;

            // Recalculate path periodically
            if (Time.time - _lastPathCalcTime > PathRecalcInterval)
            {
                _lastPathCalcTime = Time.time;
                var pathFound = Pathfinding.instance.GetPath(pos, _targetPos, _navPath, Pathfinding.AgentType.Humanoid);
                if (!pathFound || _navPath.Count == 0)
                {
                    Finish(false, "No path found");
                    return;
                }
            }

            if (_navPath.Count == 0) return;

            // Advance past consumed waypoints
            while (_navPath.Count > 1 && Utils.DistanceXZ(pos, _navPath[0]) < WaypointAdvanceDist)
            {
                _navPath.RemoveAt(0);
            }

            // Move toward next waypoint
            var nextWP = _navPath[0];
            var dir = nextWP - pos;
            dir.y = 0f;
            if (dir.magnitude > 0.1f)
                dir.Normalize();

            player.SetMoveDir(dir);
            player.SetLookDir(dir);
            player.SetRun(distXZ > RunDistanceThreshold);
        }

        private void Finish(bool success, string message)
        {
            var player = Player.m_localPlayer;
            var pos = player != null ? player.transform.position : Vector3.zero;
            var distToTarget = player != null ? Utils.DistanceXZ(pos, _targetPos) : -1f;

            StopNavigation(message);

            _tcs?.TrySetResult(new
            {
                success,
                message,
                finalPosition = new { x = pos.x, y = pos.y, z = pos.z },
                distanceToTarget = (float)Math.Round(distToTarget, 2)
            });
        }
    }
}
