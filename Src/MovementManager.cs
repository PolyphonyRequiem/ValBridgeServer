using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ValBridgeServer
{
    public class MovementManager : MonoBehaviour
    {
        private static MovementManager? _instance;

        public static MovementManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ValBridgeServer_MovementManager");
                    _instance = go.AddComponent<MovementManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private bool _isMoving;
        private TaskCompletionSource<object>? _tcs;
        private float _startTime;
        private float _duration;
        private Vector3 _moveDir;
        private bool _run;

        public bool IsMoving => _isMoving;

        public Task<object> StartMoving(Vector3 direction, bool run, float duration)
        {
            StopMoving();

            _moveDir = direction.normalized;
            _run = run;
            _duration = duration;
            _startTime = Time.time;
            _isMoving = true;
            _tcs = new TaskCompletionSource<object>();

            return _tcs.Task;
        }

        public void StopMoving()
        {
            if (!_isMoving) return;

            _isMoving = false;

            var player = Player.m_localPlayer;
            if (player != null)
            {
                player.SetMoveDir(Vector3.zero);
                player.SetRun(false);
            }
        }

        private void FixedUpdate()
        {
            if (!_isMoving || _tcs == null) return;

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Finish("Player lost");
                return;
            }

            if (Time.time - _startTime >= _duration)
            {
                Finish("Completed");
                return;
            }

            player.SetMoveDir(_moveDir);
            player.SetLookDir(_moveDir);
            player.SetRun(_run);
        }

        private void Finish(string message)
        {
            var player = Player.m_localPlayer;
            var pos = player != null ? player.transform.position : Vector3.zero;

            StopMoving();

            _tcs?.TrySetResult(new
            {
                success = true,
                message,
                position = new { x = pos.x, y = pos.y, z = pos.z }
            });
        }
    }
}
