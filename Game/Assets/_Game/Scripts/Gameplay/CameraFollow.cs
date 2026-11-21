using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Smoothly follows the local player and never shows outside the arena.</summary>
    [RequireComponent(typeof(Camera))]
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private float _sharpness = 10f;

        private Camera _camera;
        private Transform _target;
        private bool _snap;

        public Transform Target
        {
            get => _target;
            set
            {
                _target = value;
                _snap = true;
            }
        }

        private void Awake() => _camera = GetComponent<Camera>();

        private void LateUpdate()
        {
            if (_target == null)
                return;

            Vector3 goal = Clamp(_target.position);
            goal.z = transform.position.z;
            transform.position = _snap ? goal : Vector3.Lerp(transform.position, goal, 1f - Mathf.Exp(-_sharpness * Time.deltaTime));
            _snap = false;
        }

        private Vector3 Clamp(Vector3 p)
        {
            Rect r = ArenaLayout.Bounds;
            float halfH = _camera.orthographicSize;
            float halfW = halfH * _camera.aspect;
            p.x = r.width > halfW * 2f ? Mathf.Clamp(p.x, r.xMin + halfW, r.xMax - halfW) : r.center.x;
            p.y = r.height > halfH * 2f ? Mathf.Clamp(p.y, r.yMin + halfH, r.yMax - halfH) : r.center.y;
            return p;
        }
    }
}
