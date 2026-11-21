using UnityEngine;

namespace Jam.Gameplay
{
    public class SpawnPoint : MonoBehaviour
    {
        [Tooltip("0 = Blue (left), 1 = Red (right).")]
        public int Team;

        private void OnDrawGizmos()
        {
            Gizmos.color = Team == 0 ? Color.cyan : Color.red;
            Gizmos.DrawWireCube(transform.position, new Vector3(1f, 1.5f, 0f));
        }
    }
}
