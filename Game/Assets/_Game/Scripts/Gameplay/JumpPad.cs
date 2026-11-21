using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Trigger on the JumpPad layer; PlatformerMotor launches anyone overlapping it.</summary>
    [RequireComponent(typeof(BoxCollider2D))]
    public class JumpPad : MonoBehaviour
    {
        [Tooltip("Apex height above the pad, in world units.")]
        public float LaunchHeight = 20f;
    }
}
