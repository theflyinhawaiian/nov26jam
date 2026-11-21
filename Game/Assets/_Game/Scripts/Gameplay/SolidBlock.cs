using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>Greybox helper: gives a SpriteRenderer the shared white sprite so scenes need no art assets.</summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class SolidBlock : MonoBehaviour
    {
        private void Awake()
        {
            SpriteRenderer sr = GetComponent<SpriteRenderer>();
            if (sr.sprite == null)
                sr.sprite = Fx.WhiteSprite;
        }
    }
}
