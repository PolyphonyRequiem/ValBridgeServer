using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ValBridgeServer
{
    public class AttackManager : MonoBehaviour
    {
        private static AttackManager? _instance;

        public static AttackManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ValBridgeServer_AttackManager");
                    _instance = go.AddComponent<AttackManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private GameObject? _target;
        private bool _isAttacking;
        private TaskCompletionSource<object>? _tcs;
        private float _startTime;
        private float _timeout;
        private int _swingCount;

        public bool IsAttacking => _isAttacking;

        public Task<object> StartAttacking(GameObject target, float timeout)
        {
            StopAttacking("Cancelled by new attack request");

            _target = target;
            _timeout = timeout;
            _startTime = Time.time;
            _swingCount = 0;
            _isAttacking = true;
            _tcs = new TaskCompletionSource<object>();

            ValBridgeServerPlugin.ModLogger.LogInfo(
                $"Attack started on {target.name} (id={target.GetInstanceID()}), timeout={timeout}s");

            return _tcs.Task;
        }

        public void StopAttacking(string? reason = null)
        {
            if (!_isAttacking) return;

            _isAttacking = false;
            _target = null;

            if (reason != null)
            {
                ValBridgeServerPlugin.ModLogger.LogInfo($"Attack stopped: {reason}");
            }
        }

        private void FixedUpdate()
        {
            if (!_isAttacking || _tcs == null) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Finish(false, "Player lost");
                return;
            }

            // Check if target is destroyed
            if (_target == null || _target.GetComponent<ZNetView>()?.IsValid() != true)
            {
                Finish(true, $"Target destroyed after {_swingCount} swings");
                return;
            }

            // Check timeout
            if (Time.time - _startTime > _timeout)
            {
                Finish(false, $"Timeout after {_timeout}s ({_swingCount} swings)");
                return;
            }

            // Face the target
            var dir = _target.transform.position - player.transform.position;
            dir.y = 0f;
            if (dir.magnitude > 0.1f)
            {
                dir.Normalize();
                player.SetLookDir(dir);
                player.FaceLookDirection();
            }

            // Attack when not already in an attack animation
            if (!player.InAttack())
            {
                player.StartAttack(null, false);
                _swingCount++;
            }
        }

        private void Finish(bool success, string message)
        {
            var swings = _swingCount;
            StopAttacking(message);

            _tcs?.TrySetResult(new
            {
                success,
                message,
                swings
            });
        }
    }
}
