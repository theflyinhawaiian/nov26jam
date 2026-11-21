using UnityEngine;

namespace Jam.Gameplay
{
    [RequireComponent(typeof(Camera))]
    public class CameraFollow : MonoBehaviour
    {
        public Transform Target;
        [SerializeField] private float _sharpness = 10f;

        private void LateUpdate()
        {
            if (Target == null)
                return;
            Vector3 goal = new(Target.position.x, Target.position.y, transform.position.z);
            transform.position = Vector3.Lerp(transform.position, goal, 1f - Mathf.Exp(-_sharpness * Time.deltaTime));
        }
    }
}
